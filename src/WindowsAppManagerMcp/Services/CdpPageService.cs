using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The CDP side of the DOM bridge (roadmap M5.8), over <see cref="ICdpTransport"/>: one connection to the tab per call,
/// closed before it returns, as the reads and the tab tools do (M5.2).
/// </summary>
/// <remarks>
/// <para>
/// Every call sends Page.enable first without waiting for it (a page blocked by a dialog may never answer it), so the
/// browser reports a JavaScript dialog the page opens, and a command that would wait on it ends with CdpDialogOpen; a page
/// that answers nothing is CdpTimeout with the advice to answer a dialog with browser_handle_dialog, since Edge does not
/// report a dialog that was open before the connection.
/// </para>
/// <para>
/// Elements: a selector or a text is found by one script (<see cref="BrowserPageScripts.BuildFindExpression"/>, its input
/// as JSON literals), a role and name through the browser's accessibility tree (Accessibility.queryAXTree, then
/// DOM.resolveNode). Several matches without an index are AmbiguousElement, so an action never picks the first of several
/// look-alikes. The box is DOM.getContentQuads', in CSS pixels of the viewport: the coordinates Input.dispatchMouseEvent
/// takes.
/// </para>
/// </remarks>
public sealed class CdpPageService : ICdpPageService
{
    /// <summary>The longest one command (other than the script and the capture) may take.</summary>
    public static readonly TimeSpan MaxCommandTime = TimeSpan.FromSeconds(5);

    /// <summary>How long a mouse or key event, or the text, may take to be confirmed inside the gate before the next is sent.</summary>
    public static readonly TimeSpan InputAckTime = TimeSpan.FromSeconds(1);

    /// <summary>How long browser_handle_dialog waits for the browser to report a dialog that is open after Page.enable.</summary>
    public static readonly TimeSpan DialogReportWait = TimeSpan.FromMilliseconds(500);

    /// <summary>How long browser_handle_dialog waits for the page to answer a check when the browser said no dialog is shown.</summary>
    public static readonly TimeSpan DialogProbeTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How much longer than its time limit a script's call waits: the browser stops a script at the limit (Runtime.evaluate's
    /// timeout) and answers, which this leaves the time to arrive.
    /// </summary>
    public static readonly TimeSpan ScriptGrace = TimeSpan.FromSeconds(1);

    /// <summary>A script's exception text is cut to this many characters.</summary>
    public const int MaxExceptionLength = 1_000;

    /// <summary>What a page that answers nothing probably has, and what to do.</summary>
    internal const string UnansweredAdvice =
        "It probably has a JavaScript dialog open (answer it deliberately with browser_handle_dialog), is paused in the " +
        "debugger, or is busy.";

    private readonly ICdpTransport _transport;

    /// <param name="transport">The DevTools connections.</param>
    public CdpPageService(ICdpTransport transport)
    {
        _transport = transport;
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpEvaluation>> EvaluateAsync(
        CdpTarget target, string expression, bool awaitPromise, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expression);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpEvaluation>.From(connected);

        await using var connection = connected.Value!;
        var total = timeout + ScriptGrace;
        EnablePage(connection, total, cancellationToken);

        // The browser stops the script at its limit and answers; the call waits a little longer for that answer
        var reply = await CdpGatedSend.SendAsync(connection, gate, "Runtime.evaluate", new
        {
            expression,
            returnByValue = true,
            awaitPromise,
            timeout = Math.Max(1, (long)Left(timeout, stopwatch).TotalMilliseconds)
        }, Left(total, stopwatch), cancellationToken).ConfigureAwait(false);

        if (!reply.Success)
        {
            return reply.ErrorCode == BrowserErrorCode.CdpTimeout
                ? CdpPageResult<CdpEvaluation>.Fail(BrowserErrorCode.CdpTimeout,
                    $"The script in tab {target.Id} gave no result within {FormatMs(timeout)}" +
                    (awaitPromise ? " (its promise was not settled)" : string.Empty) + $". {UnansweredAdvice}")
                : CdpPageResult<CdpEvaluation>.From(reply);
        }

        if (reply.Value!.Error is { } fault)
        {
            return fault.Message.Contains("terminated", StringComparison.OrdinalIgnoreCase)
                ? CdpPageResult<CdpEvaluation>.Fail(BrowserErrorCode.CdpTimeout,
                    $"The script in tab {target.Id} ran longer than {FormatMs(timeout)}, so the browser stopped it ({fault.Message}). " +
                    "What it did before that stays done.")
                : CdpPageResult<CdpEvaluation>.From(CdpReplies.ToFailure<bool>("Runtime.evaluate", fault));
        }

        var result = reply.Value.Result;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("exceptionDetails", out var details) &&
            details.ValueKind == JsonValueKind.Object)
        {
            return CdpPageResult<CdpEvaluation>.Ok(new CdpEvaluation(null, "exception", ExceptionOf(details)));
        }

        if (!result.TryGetProperty("result", out var remote) || remote.ValueKind != JsonValueKind.Object)
        {
            return CdpPageResult<CdpEvaluation>.Fail(BrowserErrorCode.CdpProtocolError,
                $"Tab {target.Id} answered Runtime.evaluate without a result.");
        }

        var type = CdpReplies.GetString(remote, "type");
        var subtype = CdpReplies.GetString(remote, "subtype");
        JsonElement? value = null;
        if (remote.TryGetProperty("value", out var given))
            value = given.Clone();
        else if (CdpReplies.GetString(remote, "unserializableValue") is { Length: > 0 } unserializable)
            value = JsonSerializer.SerializeToElement(unserializable);

        return CdpPageResult<CdpEvaluation>.Ok(new CdpEvaluation(value, subtype.Length > 0 ? subtype : type.Length > 0 ? type : "undefined"));
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpDialogOutcome>> HandleDialogAsync(
        CdpTarget target, bool accept, string? promptText, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpDialogOutcome>.From(connected);

        await using var connection = connected.Value!;

        // Chrome reports a dialog that is open again after Page.enable; Edge 155 does not, so the dialog is answered either
        // way. Page.enable is sent while the dialog blocks the page, and only waited for briefly
        using (var opening = connection.ExpectEvent("Page.javascriptDialogOpening"))
        {
            var enable = connection.SendAsync("Page.enable", null, timeout, cancellationToken, CdpSendOptions.AllowWhileDialogOpen);
            _ = enable.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            var reported = opening.WaitAsync(Min(DialogReportWait, Left(timeout, stopwatch)), cancellationToken);
            await Task.WhenAny(enable, reported).ConfigureAwait(false);
            if (!reported.IsCompleted && enable.IsCompleted)
            {
                // The report comes before Page.enable's reply when it comes at all; let the reader hand it over
                await Task.WhenAny(reported, Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)).ConfigureAwait(false);
            }
        }

        // Read before it is answered: Page.javascriptDialogClosed, which follows the answer at once, clears it
        var dialog = connection.OpenDialog;
        var parameters = new Dictionary<string, object> { ["accept"] = accept };
        if (promptText != null)
            parameters["promptText"] = promptText;

        var reply = await CdpGatedSend.SendAsync(connection, gate, "Page.handleJavaScriptDialog", parameters,
            Bounded(Left(timeout, stopwatch)), cancellationToken, CdpSendOptions.AllowWhileDialogOpen).ConfigureAwait(false);
        if (!reply.Success)
            return CdpPageResult<CdpDialogOutcome>.From(reply);

        if (reply.Value!.Error is not { } fault)
            return CdpPageResult<CdpDialogOutcome>.Ok(new CdpDialogOutcome(dialog));

        if (dialog != null)
            return CdpPageResult<CdpDialogOutcome>.From(CdpReplies.ToFailure<bool>("Page.handleJavaScriptDialog", fault));

        // "No dialog is showing": a page that answers has none; one that does not may have a dialog the browser does not
        // hand to a new connection
        var probe = await connection.SendAsync("Runtime.evaluate", new { expression = "1", returnByValue = true },
            Min(DialogProbeTime, Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (probe.Success)
        {
            return CdpPageResult<CdpDialogOutcome>.Fail(nameof(UIElementErrorCode.DialogNotFound),
                $"Tab {target.Id} has no JavaScript dialog open (the browser: {fault.Message}), and its page answers. Nothing was done.");
        }

        return CdpPageResult<CdpDialogOutcome>.Fail(BrowserErrorCode.CdpDialogOpen,
            $"Tab {target.Id} does not answer, so a JavaScript dialog is probably open, but the browser did not hand it to this " +
            $"DevTools connection ({fault.Message}). Nothing was done. Answer it through UI Automation instead: respond_to_dialog " +
            "on the browser window reads it and names its buttons; decide deliberately which to press, or ask the user.");
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpElementBox>> LocateAsync(
        CdpTarget target, BrowserElementLocator locator, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(locator);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpElementBox>.From(connected);

        await using var connection = connected.Value!;
        EnablePage(connection, timeout, cancellationToken);

        var element = await FindAsync(connection, target, locator, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!element.Success)
            return element.FailAs<CdpElementBox>();

        var box = await BoxAsync(connection, target, element.Value!, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        return box.Success ? CdpPageResult<CdpElementBox>.Ok(box.Value!.Box) : box.FailAs<CdpElementBox>();
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpInputOutcome>> ClickAsync(
        CdpTarget target, BrowserElementLocator locator, MouseButton button, int clickCount, CdpGate gate, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(locator);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpInputOutcome>.From(connected);

        await using var connection = connected.Value!;
        EnablePage(connection, timeout, cancellationToken);

        var element = await FindAsync(connection, target, locator, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!element.Success)
            return element.FailAs<CdpInputOutcome>();

        var found = element.Value!;
        var state = await StateAsync(connection, target, found.ObjectId, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!state.Success)
            return state.FailAs<CdpInputOutcome>();

        if (state.Value!.Disabled)
        {
            return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.ElementNotEnabled),
                $"The element ({Describe(state.Value.Info)}) that {locator.Describe()} names is disabled. Nothing was clicked.");
        }

        // Scrolled into view first: the click goes to a point in the viewport. A browser without the command scrolls nothing,
        // and an element outside the viewport is then ElementOffscreen
        var scrolled = await connection.SendAsync("DOM.scrollIntoViewIfNeeded", new { objectId = found.ObjectId },
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!scrolled.Success)
            return Unanswered<CdpInputOutcome>("DOM.scrollIntoViewIfNeeded", scrolled, target);

        var box = await BoxAsync(connection, target, found, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!box.Success)
            return box.FailAs<CdpInputOutcome>();

        var (_, quads) = box.Value!;
        var viewport = box.Value.Box;
        if (ClickPoint(quads, viewport.ViewportWidth, viewport.ViewportHeight) is not { } point)
        {
            return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.ElementOffscreen),
                $"The element ({Describe(viewport.Element)}) is outside the page's viewport after scrolling it into view. Nothing was clicked.");
        }

        var hit = await CallOnAsync(connection, found.ObjectId, BrowserPageScripts.HitTestFunction, [point.X, point.Y], byValue: true,
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!hit.Success)
            return Unanswered<CdpInputOutcome>("Runtime.callFunctionOn", hit, target);

        if (ValueOf(hit.Value!.Result) is { } hitTest && hitTest.TryGetProperty("hit", out var reached) &&
            reached.ValueKind == JsonValueKind.False)
        {
            return CdpPageResult<CdpInputOutcome>.Fail(nameof(BrowserErrorCode.ElementObscured),
                $"Another element ({CdpReplies.GetString(hitTest, "by")}, written by the page) covers the middle of the element " +
                $"({Describe(viewport.Element)}) at ({Css(point.X)}, {Css(point.Y)}) CSS pixels, so the click would reach it instead. " +
                "Nothing was clicked. Close what covers it first (a banner or dialog of the page), or capture the page to see it.");
        }

        // The page takes the events as if it had the focus, also in a window behind others; ends with the connection
        await SendQuietlyAsync(connection, "Emulation.setFocusEmulationEnabled", new { enabled = true }, Min(InputAckTime, Left(timeout, stopwatch)),
            cancellationToken).ConfigureAwait(false);

        var (name, buttons) = ButtonOf(button);
        using var opening = connection.ExpectEvent("Page.javascriptDialogOpening");
        var dialogOpened = opening.WaitAsync(Left(timeout, stopwatch), cancellationToken);
        var unconfirmed = false;
        using (await gate(cancellationToken).ConfigureAwait(false))
        {
            var events = new List<Dictionary<string, object>>
            {
                MouseEvent("mouseMoved", point, "none", 0, 0)
            };
            for (var click = 1; click <= clickCount; click++)
            {
                events.Add(MouseEvent("mousePressed", point, name, buttons, click));
                events.Add(MouseEvent("mouseReleased", point, name, 0, click));
            }

            foreach (var mouse in events)
            {
                var sent = await SendUntilDialogAsync(connection, "Input.dispatchMouseEvent", mouse,
                    Min(InputAckTime, Left(timeout, stopwatch)), dialogOpened, cancellationToken).ConfigureAwait(false);
                if (sent is { Success: true, Value.Error: { } refused })
                    return CdpPageResult<CdpInputOutcome>.From(CdpReplies.ToFailure<bool>("Input.dispatchMouseEvent", refused));

                if (sent is { Success: false } failed && failed.ErrorCode != BrowserErrorCode.CdpTimeout)
                    return CdpPageResult<CdpInputOutcome>.From(failed);

                unconfirmed |= sent is { Success: false };
                if (DialogOf(dialogOpened) != null)
                    break;
            }
        }

        var dialog = DialogOf(dialogOpened);
        return CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(viewport.Element, found.MatchCount, point,
            Dialog: dialog,
            Warning: unconfirmed && dialog == null
                ? "The page did not confirm the click within a second (it may be busy); it was sent. Check the page before you click again."
                : null));
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpInputOutcome>> TypeAsync(
        CdpTarget target, BrowserTypeOptions options, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpInputOutcome>.From(connected);

        await using var connection = connected.Value!;
        EnablePage(connection, timeout, cancellationToken);

        BrowserElementInfo? info = null;
        int? matches = null;
        if (options.Selector != null)
        {
            var element = await FindAsync(connection, target, new BrowserElementLocator(options.Selector, null, null, null), timeout, stopwatch,
                cancellationToken).ConfigureAwait(false);
            if (!element.Success)
                return element.FailAs<CdpInputOutcome>();

            var state = await StateAsync(connection, target, element.Value!.ObjectId, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
            if (!state.Success)
                return state.FailAs<CdpInputOutcome>();

            info = state.Value!.Info;
            matches = element.Value.MatchCount;
            var focused = await connection.SendAsync("DOM.focus", new { objectId = element.Value.ObjectId }, Bounded(Left(timeout, stopwatch)),
                cancellationToken).ConfigureAwait(false);
            if (!focused.Success)
                return Unanswered<CdpInputOutcome>("DOM.focus", focused, target);

            if (focused.Value!.Error is { } unfocusable)
            {
                return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.FocusNotOnElement),
                    $"The element ({Describe(info)}) that selector '{options.Selector}' names cannot take the focus ({unfocusable.Message}). " +
                    "Nothing was typed.");
            }
        }

        var field = await FieldAsync(connection, target, options.Replace, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!field.Success)
            return field.FailAs<CdpInputOutcome>();

        if (!field.Value!.Editable)
        {
            var what = field.Value.Tag is { } tag ? $"the focused element is a {tag} that does not take text" : "no element of the page has the focus";
            return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.FocusNotOnElement),
                $"Nothing was typed: {what}. Pass the field's selector, or click it first with browser_click.");
        }

        await SendQuietlyAsync(connection, "Emulation.setFocusEmulationEnabled", new { enabled = true }, Min(InputAckTime, Left(timeout, stopwatch)),
            cancellationToken).ConfigureAwait(false);

        using var opening = connection.ExpectEvent("Page.javascriptDialogOpening");
        var dialogOpened = opening.WaitAsync(Left(timeout, stopwatch), cancellationToken);
        var unconfirmed = false;
        using (await gate(cancellationToken).ConfigureAwait(false))
        {
            var commands = new List<(string Method, object Parameters)> { ("Input.insertText", new { text = options.Text }) };
            if (options.PressEnter)
            {
                commands.Add(("Input.dispatchKeyEvent", new
                {
                    type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = "\r",
                    unmodifiedText = "\r"
                }));
                commands.Add(("Input.dispatchKeyEvent", new
                {
                    type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13
                }));
            }

            foreach (var (method, parameters) in commands)
            {
                var sent = await SendUntilDialogAsync(connection, method, parameters, Min(InputAckTime, Left(timeout, stopwatch)), dialogOpened,
                    cancellationToken).ConfigureAwait(false);
                if (sent is { Success: true, Value.Error: { } refused })
                    return CdpPageResult<CdpInputOutcome>.From(CdpReplies.ToFailure<bool>(method, refused));

                if (sent is { Success: false } failed && failed.ErrorCode != BrowserErrorCode.CdpTimeout)
                    return CdpPageResult<CdpInputOutcome>.From(failed);

                unconfirmed |= sent is { Success: false };
                if (DialogOf(dialogOpened) != null)
                    break;
            }
        }

        var dialog = DialogOf(dialogOpened);
        string? warning = unconfirmed && dialog == null
            ? "The page did not confirm the text within a second (it may be busy); it was sent. Read the field before you type again."
            : null;

        // A page that has the focus takes the text; one that does not (a frame's, or focus emulation missing) leaves the
        // field as it was, which the field's length tells when Enter did not leave the page
        if (warning == null && dialog == null && !options.PressEnter && !field.Value.Frame && options.Text.Length > 0 &&
            field.Value.Length is int before)
        {
            var after = await FieldAsync(connection, target, replace: false, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
            if (after is { Success: true, Value.Length: int now } && now == before && !(options.Replace && before == options.Text.Length))
            {
                warning = "The focused field's length did not change, so the page may not have taken the text (the field may refuse " +
                          "it, or the focus moved). Read the field before you type again.";
            }
        }

        return CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(info ?? (field.Value.Tag is { } t ? new BrowserElementInfo(t) : null),
            matches, TypedLength: options.Text.Length, PressedEnter: options.PressEnter ? true : null, Dialog: dialog, Warning: warning));
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpScreenshot>> CaptureAsync(
        CdpTarget target, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpScreenshot>.From(connected);

        await using var connection = connected.Value!;
        EnablePage(connection, timeout, cancellationToken);

        var view = await connection.SendAsync("Runtime.evaluate", new { expression = BrowserPageScripts.ViewportExpression, returnByValue = true },
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!view.Success)
            return Unanswered<CdpScreenshot>("Runtime.evaluate", view, target);

        if (view.Value!.Error is { } viewFault)
            return CdpPageResult<CdpScreenshot>.From(CdpReplies.ToFailure<bool>("Runtime.evaluate", viewFault));

        var page = ValueOf(view.Value.Result);
        var url = page is { } p ? CdpReplies.GetString(p, "url") : target.Url;
        var title = page is { } q ? CdpReplies.GetString(q, "title") : target.Title;

        // A page that is not shown (a tab behind another, a covered or minimized window) produces no frames to capture
        var activated = false;
        if (page is not { } shown || CdpReplies.GetString(shown, "visibility") != "visible")
        {
            var front = await CdpGatedSend.SendAsync(connection, gate, "Page.bringToFront", null, Bounded(Left(timeout, stopwatch)),
                cancellationToken).ConfigureAwait(false);
            if (!front.Success)
                return Unanswered<CdpScreenshot>("Page.bringToFront", front, target);

            if (front.Value!.Error is { } frontFault)
                return CdpPageResult<CdpScreenshot>.From(CdpReplies.ToFailure<bool>("Page.bringToFront", frontFault));

            activated = true;
        }

        var shot = await connection.SendAsync("Page.captureScreenshot", new { format = "png", fromSurface = true, captureBeyondViewport = false },
            Left(timeout, stopwatch), cancellationToken).ConfigureAwait(false);
        if (!shot.Success)
        {
            return shot.ErrorCode == BrowserErrorCode.CdpTimeout
                ? CdpPageResult<CdpScreenshot>.Fail(BrowserErrorCode.CdpTimeout,
                    $"Tab {target.Id} produced no frame to capture within {FormatMs(timeout)}. Its window may be minimized or covered by " +
                    "other windows (a covered browser stops drawing): bring it to the front (focus_window) and capture again, or " +
                    "use capture_window. " + UnansweredAdvice)
                : CdpPageResult<CdpScreenshot>.From(shot);
        }

        if (shot.Value!.Error is { } shotFault)
            return CdpPageResult<CdpScreenshot>.From(CdpReplies.ToFailure<bool>("Page.captureScreenshot", shotFault));

        byte[] png;
        try
        {
            png = Convert.FromBase64String(CdpReplies.GetString(shot.Value.Result, "data"));
        }
        catch (FormatException)
        {
            png = [];
        }

        return png.Length == 0
            ? CdpPageResult<CdpScreenshot>.Fail(BrowserErrorCode.CdpProtocolError, $"Tab {target.Id} answered Page.captureScreenshot without an image.")
            : CdpPageResult<CdpScreenshot>.Ok(new CdpScreenshot(png, url, title, activated));
    }

    /// <summary>
    /// The element <paramref name="locator"/> names, as a remote object of the connection, and how many matched:
    /// ElementNotFound, AmbiguousElement (several and no index, with the first ones described), CdpEvaluationFailed (a
    /// selector that is not valid CSS), or the failure of a command.
    /// </summary>
    private static async Task<CdpPageResult<FoundElement>> FindAsync(
        ICdpConnection connection, CdpTarget target, BrowserElementLocator locator, TimeSpan timeout, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        return locator.ByAccessibility
            ? await FindByAccessibilityAsync(connection, target, locator, timeout, stopwatch, cancellationToken).ConfigureAwait(false)
            : await FindByScriptAsync(connection, target, locator, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CdpPageResult<FoundElement>> FindByScriptAsync(
        ICdpConnection connection, CdpTarget target, BrowserElementLocator locator, TimeSpan timeout, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var found = await connection.SendAsync("Runtime.evaluate", new
        {
            expression = BrowserPageScripts.BuildFindExpression(locator),
            returnByValue = false
        }, Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!found.Success)
            return Unanswered<FoundElement>("Runtime.evaluate", found, target);

        if (found.Value!.Error is { } fault)
            return CdpPageResult<FoundElement>.From(CdpReplies.ToFailure<bool>("Runtime.evaluate", fault));

        if (ObjectIdOf(found.Value.Result) is not { } holder)
        {
            return CdpPageResult<FoundElement>.Fail(BrowserErrorCode.CdpEvaluationFailed,
                $"The page in tab {target.Id} could not be searched for {locator.Describe()} (it may be loading a new document). Try again.");
        }

        var properties = await connection.SendAsync("Runtime.getProperties", new { objectId = holder, ownProperties = true },
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!properties.Success)
            return Unanswered<FoundElement>("Runtime.getProperties", properties, target);

        if (properties.Value!.Error is { } propertiesFault)
            return CdpPageResult<FoundElement>.From(CdpReplies.ToFailure<bool>("Runtime.getProperties", propertiesFault));

        int count = 0;
        string? element = null, first = null, error = null;
        if (properties.Value.Result.TryGetProperty("result", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in list.EnumerateArray())
            {
                if (!property.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                    continue;

                switch (CdpReplies.GetString(property, "name"))
                {
                    case "count" when value.TryGetProperty("value", out var n) && n.ValueKind == JsonValueKind.Number:
                        count = n.TryGetInt32(out var c) ? c : 0;
                        break;
                    case "el":
                        element = CdpReplies.GetString(value, "objectId") is { Length: > 0 } id ? id : null;
                        break;
                    case "first":
                        first = CdpReplies.GetString(value, "value");
                        break;
                    case "error":
                        error = CdpReplies.GetString(value, "value");
                        break;
                }
            }
        }

        if (error != null)
        {
            return CdpPageResult<FoundElement>.Fail(BrowserErrorCode.CdpEvaluationFailed,
                $"The selector '{locator.Selector}' is not valid CSS ({error}). Nothing was done.");
        }

        return element != null
            ? CdpPageResult<FoundElement>.Ok(new FoundElement(element, count))
            : NoSingleMatch(locator, count, first);
    }

    private static async Task<CdpPageResult<FoundElement>> FindByAccessibilityAsync(
        ICdpConnection connection, CdpTarget target, BrowserElementLocator locator, TimeSpan timeout, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var document = await connection.SendAsync("Runtime.evaluate", new { expression = "document", returnByValue = false },
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!document.Success)
            return Unanswered<FoundElement>("Runtime.evaluate", document, target);

        if (document.Value!.Error is { } documentFault)
            return CdpPageResult<FoundElement>.From(CdpReplies.ToFailure<bool>("Runtime.evaluate", documentFault));

        if (ObjectIdOf(document.Value.Result) is not { } documentId)
        {
            return CdpPageResult<FoundElement>.Fail(BrowserErrorCode.CdpEvaluationFailed,
                $"The page in tab {target.Id} has no document to search (it may be loading a new one). Try again.");
        }

        var query = new Dictionary<string, object> { ["objectId"] = documentId };
        if (locator.Role != null)
            query["role"] = locator.Role;
        if (locator.Name != null)
            query["accessibleName"] = locator.Name;

        var tree = await connection.SendAsync("Accessibility.queryAXTree", query, Bounded(Left(timeout, stopwatch)), cancellationToken)
            .ConfigureAwait(false);
        if (!tree.Success)
            return Unanswered<FoundElement>("Accessibility.queryAXTree", tree, target);

        if (tree.Value!.Error is { } treeFault)
            return CdpPageResult<FoundElement>.From(CdpReplies.ToFailure<bool>("Accessibility.queryAXTree", treeFault));

        var matches = new List<(int BackendNodeId, string Description)>();
        if (tree.Value.Result.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.True)
                    continue;

                if (!node.TryGetProperty("backendDOMNodeId", out var backend) || !backend.TryGetInt32(out var backendId))
                    continue;

                var role = AxValue(node, "role");
                var name = AxValue(node, "name");
                matches.Add((backendId, role + (name.Length > 0 ? $" '{Cut(name, 40)}'" : string.Empty)));
            }
        }

        var index = locator.Index ?? 0;
        if (matches.Count == 0 || (locator.Index == null && matches.Count > 1) || index >= matches.Count)
            return NoSingleMatch(locator, matches.Count, string.Join("; ", matches.Take(5).Select(m => m.Description)));

        var resolved = await connection.SendAsync("DOM.resolveNode", new { backendNodeId = matches[index].BackendNodeId },
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!resolved.Success)
            return Unanswered<FoundElement>("DOM.resolveNode", resolved, target);

        if (resolved.Value!.Error is { } resolveFault)
            return CdpPageResult<FoundElement>.From(CdpReplies.ToFailure<bool>("DOM.resolveNode", resolveFault));

        return resolved.Value.Result.TryGetProperty("object", out var remote) && CdpReplies.GetString(remote, "objectId") is { Length: > 0 } objectId
            ? CdpPageResult<FoundElement>.Ok(new FoundElement(objectId, matches.Count))
            : CdpPageResult<FoundElement>.Fail(BrowserErrorCode.CdpProtocolError, $"Tab {target.Id} answered DOM.resolveNode without an object.");
    }

    /// <summary>ElementNotFound (none, or an index past the matches) or AmbiguousElement (several and no index).</summary>
    private static CdpPageResult<FoundElement> NoSingleMatch(BrowserElementLocator locator, int count, string? first)
    {
        if (count > 1 && locator.Index == null)
        {
            return CdpPageResult<FoundElement>.Fail(nameof(UIElementErrorCode.AmbiguousElement),
                $"{count} elements match {locator.Describe()} (the first: {first}; text from the page). Nothing was done. Pass index " +
                "(from 0, in document order) once you know which you mean, or narrow it.");
        }

        return CdpPageResult<FoundElement>.Fail(nameof(UIElementErrorCode.ElementNotFound),
            count == 0
                ? $"No element of the page matches {locator.Describe()}" +
                  (locator.Text != null ? " among its shown elements" : string.Empty) + ". Nothing was done. Wait for it with " +
                  "wait_for_browser, or read the page (get_browser_content) to see what it has."
                : $"Only {count} element(s) match {locator.Describe()}, so index {locator.Index} names none. Nothing was done.");
    }

    /// <summary>The element's description and the page's viewport (<see cref="BrowserPageScripts.ElementStateFunction"/>).</summary>
    private static async Task<CdpPageResult<ElementState>> StateAsync(
        ICdpConnection connection, CdpTarget target, string objectId, TimeSpan timeout, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var state = await CallOnAsync(connection, objectId, BrowserPageScripts.ElementStateFunction, null, byValue: true,
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!state.Success)
            return Unanswered<ElementState>("Runtime.callFunctionOn", state, target);

        if (state.Value!.Error is { } fault)
            return CdpPageResult<ElementState>.From(CdpReplies.ToFailure<bool>("Runtime.callFunctionOn", fault));

        if (ValueOf(state.Value.Result) is not { } value)
        {
            return CdpPageResult<ElementState>.Fail(BrowserErrorCode.CdpEvaluationFailed,
                $"The element in tab {target.Id} could not be read (the page may have replaced it). Try again.");
        }

        var text = CdpReplies.GetString(value, "text");
        return CdpPageResult<ElementState>.Ok(new ElementState(
            new BrowserElementInfo(CdpReplies.GetString(value, "tag"), text.Length > 0 ? text : null),
            IsTrue(value, "disabled"),
            !value.TryGetProperty("top", out var top) || top.ValueKind != JsonValueKind.False,
            Number(value, "dpr") is > 0 and var dpr ? dpr : 1,
            Number(value, "vw") ?? 0,
            Number(value, "vh") ?? 0,
            Number(value, "scale") is > 0 and var scale ? scale : 1,
            CdpReplies.GetString(value, "visibility")));
    }

    /// <summary>The element's state and its boxes (DOM.getContentQuads): ElementOffscreen when it has none (not rendered).</summary>
    private static async Task<CdpPageResult<(CdpElementBox Box, IReadOnlyList<CssRect> Quads)>> BoxAsync(
        ICdpConnection connection, CdpTarget target, FoundElement found, TimeSpan timeout, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var state = await StateAsync(connection, target, found.ObjectId, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!state.Success)
            return state.FailAs<(CdpElementBox, IReadOnlyList<CssRect>)>();

        var quads = await connection.SendAsync("DOM.getContentQuads", new { objectId = found.ObjectId }, Bounded(Left(timeout, stopwatch)),
            cancellationToken).ConfigureAwait(false);
        if (!quads.Success)
            return Unanswered<(CdpElementBox, IReadOnlyList<CssRect>)>("DOM.getContentQuads", quads, target);

        var s = state.Value!;
        var rects = quads.Value!.Error == null ? QuadsOf(quads.Value.Result) : [];
        if (rects.Count == 0)
        {
            return CdpPageResult<(CdpElementBox, IReadOnlyList<CssRect>)>.Fail(nameof(UIElementErrorCode.ElementOffscreen),
                $"The element ({Describe(s.Info)}) has no box on the page: it is hidden (display: none) or not rendered" +
                (quads.Value.Error is { } fault ? $" ({fault.Message})" : string.Empty) + ".");
        }

        var left = rects.Min(r => r.X);
        var top = rects.Min(r => r.Y);
        var union = new CssRect(left, top, rects.Max(r => r.X + r.Width) - left, rects.Max(r => r.Y + r.Height) - top);
        return CdpPageResult<(CdpElementBox, IReadOnlyList<CssRect>)>.Ok((
            new CdpElementBox(s.Info, found.MatchCount, union, s.DevicePixelRatio, s.ViewportWidth, s.ViewportHeight, s.Scale, s.Visibility,
                s.InMainDocument),
            rects));
    }

    /// <summary>
    /// Where a click on the element lands: the middle of its first box (with an area) inside the viewport, else of the
    /// first box's part inside it; null when no box reaches into the viewport.
    /// </summary>
    public static CssPoint? ClickPoint(IReadOnlyList<CssRect> quads, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(quads);
        foreach (var quad in quads.Where(q => q.Width >= 1 && q.Height >= 1).Concat(quads))
        {
            var left = Math.Max(quad.X, 0);
            var top = Math.Max(quad.Y, 0);
            var right = Math.Min(quad.X + quad.Width, viewportWidth);
            var bottom = Math.Min(quad.Y + quad.Height, viewportHeight);
            if (right > left && bottom > top)
                return new CssPoint(Math.Round((left + right) / 2, 2), Math.Round((top + bottom) / 2, 2));
        }

        return null;
    }

    /// <summary>The focused field, after selecting its content with <paramref name="replace"/>.</summary>
    private static async Task<CdpPageResult<FocusedField>> FieldAsync(
        ICdpConnection connection, CdpTarget target, bool replace, TimeSpan timeout, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var field = await connection.SendAsync("Runtime.evaluate", new
        {
            expression = BrowserPageScripts.BuildFocusedFieldExpression(replace),
            returnByValue = true
        }, Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!field.Success)
            return Unanswered<FocusedField>("Runtime.evaluate", field, target);

        if (field.Value!.Error is { } fault)
            return CdpPageResult<FocusedField>.From(CdpReplies.ToFailure<bool>("Runtime.evaluate", fault));

        if (ValueOf(field.Value.Result) is not { } value)
        {
            return CdpPageResult<FocusedField>.Fail(BrowserErrorCode.CdpEvaluationFailed,
                $"The page in tab {target.Id} could not be read for its focused field (it may be loading a new document). Nothing was typed.");
        }

        var tag = CdpReplies.GetString(value, "tag");
        return CdpPageResult<FocusedField>.Ok(new FocusedField(
            IsTrue(value, "editable"), tag.Length > 0 ? tag : null, IsTrue(value, "frame"), Number(value, "length") is { } length ? (int)length : null));
    }

    /// <summary>
    /// Sends an input command (allowed while a dialog is open, so the release of a click that opened one still goes out)
    /// and waits for its reply, or for a dialog the page opens meanwhile, whichever comes first; null for the dialog.
    /// </summary>
    private static async Task<CdpResult<CdpReply>?> SendUntilDialogAsync(
        ICdpConnection connection, string method, object parameters, TimeSpan timeout, Task<CdpResult<JsonElement>> dialogOpened,
        CancellationToken cancellationToken)
    {
        var sent = connection.SendAsync(method, parameters, timeout, cancellationToken, CdpSendOptions.AllowWhileDialogOpen);
        var first = await Task.WhenAny(sent, DialogOnly(dialogOpened)).ConfigureAwait(false);
        if (first == sent)
            return await sent.ConfigureAwait(false);

        _ = sent.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        return null;
    }

    /// <summary>A task that completes only when a dialog was reported (a timeout or a closed connection never completes it).</summary>
    private static async Task DialogOnly(Task<CdpResult<JsonElement>> dialogOpened)
    {
        var result = await dialogOpened.ConfigureAwait(false);
        if (!result.Success)
            await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
    }

    /// <summary>The dialog an action opened: reported by the page's Page.javascriptDialogOpening, else null.</summary>
    private static CdpDialog? DialogOf(Task<CdpResult<JsonElement>> dialogOpened) =>
        dialogOpened is { IsCompletedSuccessfully: true, Result: { Success: true } opened } ? CdpDialogText.FromEvent(opened.Value) : null;

    private static Dictionary<string, object> MouseEvent(string type, CssPoint point, string button, int buttons, int clickCount) => new()
    {
        ["type"] = type,
        ["x"] = point.X,
        ["y"] = point.Y,
        ["button"] = button,
        ["buttons"] = buttons,
        ["clickCount"] = clickCount
    };

    /// <summary>
    /// CDP's name and buttons bit of a button. CDP names the page's buttons, which Windows' swapped buttons do not change:
    /// primary is the page's left button.
    /// </summary>
    private static (string Name, int Buttons) ButtonOf(MouseButton button) => button switch
    {
        MouseButton.Secondary => ("right", 2),
        MouseButton.Middle => ("middle", 4),
        MouseButton.X1 => ("back", 8),
        MouseButton.X2 => ("forward", 16),
        _ => ("left", 1)
    };

    /// <summary>Runtime.callFunctionOn on an object, with arguments (numbers) when given.</summary>
    private static Task<CdpResult<CdpReply>> CallOnAsync(
        ICdpConnection connection, string objectId, string function, double[]? arguments, bool byValue, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object>
        {
            ["objectId"] = objectId,
            ["functionDeclaration"] = function,
            ["returnByValue"] = byValue
        };
        if (arguments != null)
            parameters["arguments"] = arguments.Select(a => new Dictionary<string, object> { ["value"] = a }).ToArray();

        return connection.SendAsync("Runtime.callFunctionOn", parameters, timeout, cancellationToken);
    }

    /// <summary>Sends a command whose failure changes nothing (focus emulation), waiting for it at most its time.</summary>
    private static async Task SendQuietlyAsync(
        ICdpConnection connection, string method, object parameters, TimeSpan timeout, CancellationToken cancellationToken) =>
        _ = await connection.SendAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Page.enable without waiting for it: the browser then reports a dialog the page opens, which ends the commands waiting
    /// on the page with CdpDialogOpen. A page blocked by a dialog may never answer it.
    /// </summary>
    private static void EnablePage(ICdpConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var enable = connection.SendAsync("Page.enable", null, timeout, cancellationToken);
        _ = enable.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }

    /// <summary>A failed command: a timeout with the advice for a page that answers nothing, any other failure as it is.</summary>
    private static CdpPageResult<T> Unanswered<T>(string method, CdpResult<CdpReply> failed, CdpTarget target) =>
        failed.ErrorCode == BrowserErrorCode.CdpTimeout
            ? CdpPageResult<T>.Fail(BrowserErrorCode.CdpTimeout, $"Tab {target.Id} did not answer {method} in time. {UnansweredAdvice} Nothing was done.")
            : CdpPageResult<T>.From(failed);

    /// <summary>What a script threw: the exception's description (with its stack), else the details' text, and where.</summary>
    private static BrowserScriptException ExceptionOf(JsonElement details)
    {
        var text = details.TryGetProperty("exception", out var exception) && CdpReplies.GetString(exception, "description") is { Length: > 0 } description
            ? description
            : exception.ValueKind == JsonValueKind.Object && exception.TryGetProperty("value", out var thrown) && thrown.ValueKind != JsonValueKind.Undefined
                ? $"Uncaught {thrown.GetRawText()}"
                : CdpReplies.GetString(details, "text");
        return new BrowserScriptException(Cut(text.Length > 0 ? text : "The script threw.", MaxExceptionLength),
            Number(details, "lineNumber") is { } line ? (int)line : null,
            Number(details, "columnNumber") is { } column ? (int)column : null);
    }

    /// <summary>The objectId of a Runtime.evaluate reply's result, when nothing threw.</summary>
    private static string? ObjectIdOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && !result.TryGetProperty("exceptionDetails", out _) &&
        result.TryGetProperty("result", out var remote) && CdpReplies.GetString(remote, "objectId") is { Length: > 0 } id
            ? id
            : null;

    /// <summary>The value of a reply's result with returnByValue, when it is an object and nothing threw.</summary>
    private static JsonElement? ValueOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        !result.TryGetProperty("exceptionDetails", out _) &&
        result.TryGetProperty("result", out var remote) && remote.ValueKind == JsonValueKind.Object &&
        remote.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>DOM.getContentQuads' quads (eight numbers each) as the rectangles around them.</summary>
    private static IReadOnlyList<CssRect> QuadsOf(JsonElement result)
    {
        var rects = new List<CssRect>();
        if (!result.TryGetProperty("quads", out var quads) || quads.ValueKind != JsonValueKind.Array)
            return rects;

        foreach (var quad in quads.EnumerateArray())
        {
            if (quad.ValueKind != JsonValueKind.Array || quad.GetArrayLength() != 8)
                continue;

            var points = quad.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.Number ? p.GetDouble() : double.NaN).ToArray();
            if (points.Any(p => !double.IsFinite(p)))
                continue;

            var xs = new[] { points[0], points[2], points[4], points[6] };
            var ys = new[] { points[1], points[3], points[5], points[7] };
            rects.Add(new CssRect(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min()));
        }

        return rects;
    }

    private static string AxValue(JsonElement node, string property) =>
        node.TryGetProperty(property, out var holder) && holder.ValueKind == JsonValueKind.Object &&
        holder.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsTrue(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        double.IsFinite(property.GetDouble())
            ? property.GetDouble()
            : null;

    private static string Describe(BrowserElementInfo info) => info.Text is { } text ? $"{info.Tag} '{Cut(text, 40)}'" : info.Tag;

    private static string Css(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Cut(string text, int max) => text.Length > max ? text[..(max - 3)] + "..." : text;

    private static TimeSpan Left(TimeSpan timeout, Stopwatch stopwatch)
    {
        var left = timeout - stopwatch.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    private static TimeSpan Bounded(TimeSpan left) => left < MaxCommandTime ? left : MaxCommandTime;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string FormatMs(TimeSpan timeout) => $"{(long)timeout.TotalMilliseconds} ms";

    /// <summary>The tab's own DevTools connection, at the WebSocket URL its port listed.</summary>
    private async Task<CdpResult<ICdpConnection>> ConnectAsync(CdpTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var label = $"tab {target.Id}";
        var webSocketUrl = CdpEndpointValidator.ValidateWebSocketUrl(target.WebSocketDebuggerUrl, target.Port, label);
        if (!webSocketUrl.Success)
            return webSocketUrl.FailAs<ICdpConnection>();

        return await _transport.ConnectAsync(webSocketUrl.Value!, target.Port, label, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An element found in the page: its remote object and how many elements matched.</summary>
    private sealed record FoundElement(string ObjectId, int MatchCount);

    /// <summary>What <see cref="BrowserPageScripts.ElementStateFunction"/> read.</summary>
    private sealed record ElementState(
        BrowserElementInfo Info, bool Disabled, bool InMainDocument, double DevicePixelRatio, double ViewportWidth, double ViewportHeight,
        double Scale, string Visibility);

    /// <summary>The page's focused element: whether it takes text, its tag, whether it is a frame, and its content's length.</summary>
    private sealed record FocusedField(bool Editable, string? Tag, bool Frame, int? Length);
}
