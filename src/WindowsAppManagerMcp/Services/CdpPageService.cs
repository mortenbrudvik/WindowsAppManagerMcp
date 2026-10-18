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
/// that answers nothing is CdpTimeout with the advice to read a dialog with browser_handle_dialog first: Chrome and Edge
/// (155) neither report nor answer a dialog that was open before the connection, on any DevTools connection (roadmap M5.8,
/// dialogs). With a dialog answer, a click or a typing answers the dialog it opens on its own connection
/// (<see cref="ICdpConnection.AnswerNextDialog"/>). Every call but the dialog's then reads the page in its main frame
/// (<see cref="BrowserPageScripts.ViewportExpression"/>, in an isolated world the page's scripts cannot reach,
/// <see cref="CdpIsolatedWorld"/>): its visibility and viewport, which an element inside a frame shares, and its address,
/// refused when it is not a web page (the tab may have navigated since it was listed).
/// </para>
/// <para>
/// Elements: a selector or a text is found by one script (<see cref="BrowserPageScripts.BuildFindExpression"/>, its input
/// as JSON literals), a role and name through the browser's accessibility tree (Accessibility.queryAXTree, then
/// DOM.resolveNode). Several matches without an index are AmbiguousElement, so an action never picks the first of several
/// look-alikes. The box is DOM.getContentQuads', in CSS pixels of the main frame's viewport (also for an element inside a
/// frame): the coordinates Input.dispatchMouseEvent takes.
/// </para>
/// </remarks>
public sealed class CdpPageService : ICdpPageService
{
    /// <summary>The longest one command (other than the script and the capture) may take.</summary>
    public static readonly TimeSpan MaxCommandTime = TimeSpan.FromSeconds(5);

    /// <summary>How long a mouse or key event, or the text, may take to be confirmed inside the gate before the next is sent.</summary>
    public static readonly TimeSpan InputAckTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How much longer than its time limit a script's call waits: the browser stops a script at the limit (Runtime.evaluate's
    /// timeout) and answers, which this leaves the time to arrive.
    /// </summary>
    public static readonly TimeSpan ScriptGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a command sent inside the gate before the input (scrolling the element into view, its box, the hit test,
    /// the focus, the selection) may take: the gate is never held long for a slow page.
    /// </summary>
    public static readonly TimeSpan GatedCommandTime = TimeSpan.FromSeconds(2);

    /// <summary>How long the release of a press that opened a dialog is given to go out (the page cannot answer it).</summary>
    public static readonly TimeSpan ReleaseAfterDialogTime = TimeSpan.FromMilliseconds(250);

    /// <summary>A script's exception text is cut to this many characters.</summary>
    public const int MaxExceptionLength = 1_000;

    /// <summary>What a page that answers nothing probably has, and what to do.</summary>
    internal const string UnansweredAdvice =
        "It probably has a JavaScript dialog open, which DevTools cannot answer once it is open: read it first with " +
        "browser_handle_dialog (the browser window's handle, no accept), then answer it deliberately (browser_handle_dialog " +
        "with accept), or ask the user. Or it is paused in the debugger, or busy.";

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

        // The page the connection shows now, not only the one the tab was listed with, must be a web page
        var page = await ReadPageAsync(connection, target, total, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!page.Success)
            return page.FailAs<CdpEvaluation>();

        // The script's own time limit starts with the script: the page's read does not shorten it
        stopwatch.Restart();

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

        var page = await ReadPageAsync(connection, target, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!page.Success)
            return page.FailAs<CdpElementBox>();

        var element = await FindAsync(connection, target, locator, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!element.Success)
            return element.FailAs<CdpElementBox>();

        var state = await StateAsync(connection, target, element.Value!.ObjectId, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!state.Success)
            return state.FailAs<CdpElementBox>();

        return await BoxAsync(connection, target, element.Value, state.Value!.Info, page.Value!, Bounded(Left(timeout, stopwatch)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpInputOutcome>> ClickAsync(
        CdpTarget target, BrowserElementLocator locator, MouseButton button, int clickCount, CdpGate gate, TimeSpan timeout,
        CancellationToken cancellationToken = default, BrowserDialogAnswer? dialogAnswer = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(locator);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpInputOutcome>.From(connected);

        await using var connection = connected.Value!;
        EnablePage(connection, timeout, cancellationToken);

        var page = await ReadPageAsync(connection, target, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!page.Success)
            return page.FailAs<CdpInputOutcome>();

        var element = await FindAsync(connection, target, locator, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!element.Success)
            return element.FailAs<CdpInputOutcome>();

        var found = element.Value!;
        var state = await StateAsync(connection, target, found.ObjectId, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!state.Success)
            return state.FailAs<CdpInputOutcome>();

        var info = state.Value!.Info;
        if (state.Value.Disabled)
        {
            return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.ElementNotEnabled),
                $"The element ({Describe(info)}) that {locator.Describe()} names is disabled (or inside a disabled control). Nothing was clicked.");
        }

        var (name, buttons) = ButtonOf(button);
        using var opening = connection.ExpectEvent("Page.javascriptDialogOpening");
        var dialogOpened = opening.WaitAsync(Left(timeout, stopwatch), cancellationToken);
        Task<CdpResult<CdpAnsweredDialog>>? answered = null;
        var unconfirmed = false;
        CssPoint point;

        // Everything that changes the page is sent inside the gate, so a pause or another input call never meets it half
        // done: the scroll, then the box and the hit test right before the events, so the page has the least time to move
        using (var hold = await gate(cancellationToken).ConfigureAwait(false))
        {
            // Scrolled into view first: the click goes to a point in the viewport. A browser without the command scrolls
            // nothing, and an element outside the viewport is then ElementOffscreen
            var scrolled = await connection.SendAsync("DOM.scrollIntoViewIfNeeded", new { objectId = found.ObjectId },
                Gated(timeout, stopwatch), cancellationToken).ConfigureAwait(false);
            if (!scrolled.Success)
                return Unanswered<CdpInputOutcome>("DOM.scrollIntoViewIfNeeded", scrolled, target);

            var box = await BoxAsync(connection, target, found, info, page.Value!, Gated(timeout, stopwatch), cancellationToken)
                .ConfigureAwait(false);
            if (!box.Success)
                return box.FailAs<CdpInputOutcome>();

            if (ClickPoint(box.Value!.Quads, page.Value!.ViewportWidth, page.Value.ViewportHeight) is not { } inside)
            {
                return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.ElementOffscreen),
                    $"The element ({Describe(info)}) is outside the page's viewport after scrolling it into view. Nothing was clicked.");
            }

            point = inside;
            var hit = await CallOnAsync(connection, found.ObjectId, BrowserPageScripts.HitTestFunction, [point.X, point.Y], byValue: true,
                Gated(timeout, stopwatch), cancellationToken).ConfigureAwait(false);
            if (!hit.Success)
                return Unanswered<CdpInputOutcome>("Runtime.callFunctionOn", hit, target);

            if (ValueOf(hit.Value!.Result) is { } hitTest && hitTest.TryGetProperty("hit", out var reached) &&
                reached.ValueKind == JsonValueKind.False)
            {
                return CdpPageResult<CdpInputOutcome>.Fail(nameof(BrowserErrorCode.ElementObscured), IsTrue(hitTest, "frame")
                    ? $"The element ({Describe(info)}) is inside a frame of another origin, so whether a click at its middle reaches " +
                      "it cannot be checked. Nothing was clicked. Place it with browser_element_rect and check the point with " +
                      "get_element_at_point before you click it with the input tools, or ask the user."
                    : $"Another element ({CdpReplies.GetString(hitTest, "by")}, written by the page) covers the middle of the element " +
                      $"({Describe(info)}) at ({Css(point.X)}, {Css(point.Y)}) CSS pixels, so the click would reach it instead. " +
                      "Nothing was clicked. Close what covers it first (a banner or dialog of the page), or capture the page to see it.");
            }

            if (Paused(hold))
                return PausedFail("The element was scrolled into view; no mouse event was sent.");

            // The page takes the events as if it had the focus, also in a window behind others; ends with the connection
            await SendQuietlyAsync(connection, "Emulation.setFocusEmulationEnabled", new { enabled = true },
                Min(InputAckTime, Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);

            // A dialog the click opens is answered on this connection, as the agent asked before it knew the dialog
            if (dialogAnswer is { } reply)
                answered = connection.AnswerNextDialog(reply.Accept, reply.PromptText);

            var events = new List<Dictionary<string, object>> { MouseEvent("mouseMoved", point, "none", 0, 0) };
            for (var click = 1; click <= clickCount; click++)
            {
                events.Add(MouseEvent("mousePressed", point, name, buttons, click));
                events.Add(MouseEvent("mouseReleased", point, name, 0, click));
            }

            for (var index = 0; index < events.Count; index++)
            {
                var mouse = events[index];
                var type = (string)mouse["type"];

                // A pause stops the click between events; a press already sent is always released
                if (type != "mouseReleased" && Paused(hold))
                {
                    return PausedFail(index == 0
                        ? "No mouse event was sent."
                        : $"{index / 2} of {clickCount} click(s) were sent; the rest were not.");
                }

                var sent = await SendUntilDialogAsync(connection, "Input.dispatchMouseEvent", mouse,
                    Min(InputAckTime, Left(timeout, stopwatch)), dialogOpened, cancellationToken).ConfigureAwait(false);
                if (sent is { Success: true, Value.Error: { } refused })
                    return CdpPageResult<CdpInputOutcome>.From(CdpReplies.ToFailure<bool>("Input.dispatchMouseEvent", refused));

                if (sent is { Success: false } failed && failed.ErrorCode != BrowserErrorCode.CdpTimeout)
                    return CdpPageResult<CdpInputOutcome>.From(failed);

                unconfirmed |= sent is { Success: false };
                if (DialogOf(dialogOpened) != null)
                {
                    // A dialog opened on the press (a mousedown handler): the button is released all the same, or the page
                    // would keep it pressed. The page cannot answer while the dialog is open, so the release is not waited for
                    if (type == "mousePressed")
                    {
                        _ = await connection.SendAsync("Input.dispatchMouseEvent", events[index + 1], ReleaseAfterDialogTime, cancellationToken,
                            CdpSendOptions.AllowWhileDialogOpen).ConfigureAwait(false);
                    }

                    break;
                }
            }
        }

        var dialog = DialogOf(dialogOpened);
        var (answerGiven, answerFailure) = await AnswerOfAsync(dialog, answered, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        return CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(info, found.MatchCount, point,
            Dialog: dialog,
            Warning: unconfirmed && dialog == null
                ? "The page did not confirm the click within a second (it may be busy); it was sent. Check the page before you click again."
                : null,
            Answer: answerGiven,
            AnswerFailure: answerFailure));
    }

    /// <inheritdoc />
    public async Task<CdpPageResult<CdpInputOutcome>> TypeAsync(
        CdpTarget target, BrowserTypeOptions options, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default,
        BrowserDialogAnswer? dialogAnswer = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();
        var connected = await ConnectAsync(target, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return CdpPageResult<CdpInputOutcome>.From(connected);

        await using var connection = connected.Value!;
        EnablePage(connection, timeout, cancellationToken);

        var page = await ReadPageAsync(connection, target, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!page.Success)
            return page.FailAs<CdpInputOutcome>();

        BrowserElementInfo? info = null;
        int? matches = null;
        string? objectId = null;
        if (options.Selector != null)
        {
            var element = await FindAsync(connection, target, new BrowserElementLocator(options.Selector, null, null, null), timeout, stopwatch,
                cancellationToken, takesIndex: false).ConfigureAwait(false);
            if (!element.Success)
                return element.FailAs<CdpInputOutcome>();

            var state = await StateAsync(connection, target, element.Value!.ObjectId, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
            if (!state.Success)
                return state.FailAs<CdpInputOutcome>();

            info = state.Value!.Info;
            matches = element.Value.MatchCount;
            objectId = element.Value.ObjectId;
        }

        using var opening = connection.ExpectEvent("Page.javascriptDialogOpening");
        var dialogOpened = opening.WaitAsync(Left(timeout, stopwatch), cancellationToken);
        Task<CdpResult<CdpAnsweredDialog>>? answered = null;
        var unconfirmed = false;
        FocusedField field;

        // The focus, the selection and the text are sent inside the gate; the read-only lookup comes before it
        using (var hold = await gate(cancellationToken).ConfigureAwait(false))
        {
            if (objectId != null)
            {
                var focused = await connection.SendAsync("DOM.focus", new { objectId }, Gated(timeout, stopwatch), cancellationToken)
                    .ConfigureAwait(false);
                if (!focused.Success)
                    return Unanswered<CdpInputOutcome>("DOM.focus", focused, target);

                if (focused.Value!.Error is { } unfocusable)
                {
                    return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.FocusNotOnElement),
                        $"The element ({Describe(info!)}) that selector '{options.Selector}' names cannot take the focus ({unfocusable.Message}). " +
                        "Nothing was typed.");
                }

                // The page's focus handlers may have moved the focus on (an auto-advancing field, an overlay): the text goes
                // only to the field the selector names
                var holder = await CallOnAsync(connection, objectId, BrowserPageScripts.FocusHolderFunction, null, byValue: true,
                    Gated(timeout, stopwatch), cancellationToken).ConfigureAwait(false);
                if (!holder.Success)
                    return Unanswered<CdpInputOutcome>("Runtime.callFunctionOn", holder, target);

                var holds = ValueOf(holder.Value!.Result);
                if (holds is not { } answer || !IsTrue(answer, "holds"))
                {
                    var elsewhere = holds is { } moved && CdpReplies.GetString(moved, "tag") is { Length: > 0 } tag ? $"to a {tag}" : "elsewhere";
                    return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.FocusNotOnElement),
                        $"The page moved the focus {elsewhere} once the field ({Describe(info!)}) that selector '{options.Selector}' names " +
                        "was focused, so the text could land in another field. Nothing was typed. Read the page (get_browser_content " +
                        "or browser_screenshot) to see what took the focus.");
                }
            }

            var read = await FieldAsync(connection, target, options.Replace, Gated(timeout, stopwatch), cancellationToken).ConfigureAwait(false);
            if (!read.Success)
                return read.FailAs<CdpInputOutcome>();

            field = read.Value!;
            if (!field.Editable)
            {
                var what = field.Tag is { } tag ? $"the focused element is a {tag} that does not take text" : "no element of the page has the focus";
                return CdpPageResult<CdpInputOutcome>.Fail(nameof(UIElementErrorCode.FocusNotOnElement),
                    $"Nothing was typed: {what}. Pass the field's selector, or click it first with browser_click.");
            }

            var prepared = objectId != null
                ? "The field was focused" + (options.Replace ? " and its content selected" : string.Empty) + ", but nothing was typed."
                : options.Replace ? "The focused field's content was selected, but nothing was typed." : "Nothing was typed.";

            // A pause stops the typing before the text, and before Enter; the Enter key's release always follows its press
            var commands = new List<(string Method, object Parameters, string? PausedBefore)>
            {
                ("Input.insertText", new { text = options.Text }, prepared)
            };
            if (options.PressEnter)
            {
                commands.Add(("Input.dispatchKeyEvent", new
                {
                    type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = "\r",
                    unmodifiedText = "\r"
                }, "The text was typed, but Enter was not pressed."));
                commands.Add(("Input.dispatchKeyEvent", new
                {
                    type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13
                }, null));
            }

            if (Paused(hold))
                return PausedFail(prepared);

            await SendQuietlyAsync(connection, "Emulation.setFocusEmulationEnabled", new { enabled = true },
                Min(InputAckTime, Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);

            if (dialogAnswer is { } reply)
                answered = connection.AnswerNextDialog(reply.Accept, reply.PromptText);

            foreach (var (method, parameters, pausedBefore) in commands)
            {
                if (pausedBefore != null && Paused(hold))
                    return PausedFail(pausedBefore);

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
        if (warning == null && dialog == null && !options.PressEnter && !field.Frame && options.Text.Length > 0 &&
            field.Length is int before)
        {
            var after = await FieldAsync(connection, target, replace: false, Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
            if (after is { Success: true, Value.Length: int now } && now == before && !(options.Replace && before == options.Text.Length))
            {
                warning = "The focused field's length did not change, so the page may not have taken the text (the field may refuse " +
                          "it, or the focus moved). Read the field before you type again.";
            }
        }

        var (answerGiven, answerFailure) = await AnswerOfAsync(dialog, answered, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        return CdpPageResult<CdpInputOutcome>.Ok(new CdpInputOutcome(info ?? (field.Tag is { } t ? new BrowserElementInfo(t) : null),
            matches, TypedLength: options.Text.Length, PressedEnter: options.PressEnter ? true : null, Dialog: dialog, Warning: warning,
            Answer: answerGiven, AnswerFailure: answerFailure));
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

        var read = await ReadPageAsync(connection, target, timeout, stopwatch, cancellationToken).ConfigureAwait(false);
        if (!read.Success)
            return read.FailAs<CdpScreenshot>();

        var page = read.Value!;
        var url = page.Url;
        var title = page.Title;

        // A page that is not shown (a tab behind another, a covered or minimized window) produces no frames to capture
        var activated = false;
        if (page.Visibility != "visible")
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
    /// <param name="takesIndex">Whether the tool takes an index (browser_type does not), for the advice of AmbiguousElement.</param>
    private static async Task<CdpPageResult<FoundElement>> FindAsync(
        ICdpConnection connection, CdpTarget target, BrowserElementLocator locator, TimeSpan timeout, Stopwatch stopwatch,
        CancellationToken cancellationToken, bool takesIndex = true)
    {
        return locator.ByAccessibility
            ? await FindByAccessibilityAsync(connection, target, locator, timeout, stopwatch, cancellationToken).ConfigureAwait(false)
            : await FindByScriptAsync(connection, target, locator, timeout, stopwatch, cancellationToken, takesIndex).ConfigureAwait(false);
    }

    private static async Task<CdpPageResult<FoundElement>> FindByScriptAsync(
        ICdpConnection connection, CdpTarget target, BrowserElementLocator locator, TimeSpan timeout, Stopwatch stopwatch,
        CancellationToken cancellationToken, bool takesIndex)
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
            : NoSingleMatch(locator, count, first, takesIndex);
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
            return NoSingleMatch(locator, matches.Count, string.Join("; ", matches.Take(5).Select(m => m.Description)), takesIndex: true);

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
    private static CdpPageResult<FoundElement> NoSingleMatch(BrowserElementLocator locator, int count, string? first, bool takesIndex)
    {
        if (count > 1 && locator.Index == null)
        {
            return CdpPageResult<FoundElement>.Fail(nameof(UIElementErrorCode.AmbiguousElement),
                $"{count} elements match {locator.Describe()} (the first: {first}; text from the page). Nothing was done. " + (takesIndex
                    ? "Pass index (from 0, in document order) once you know which you mean, or narrow it."
                    : "Narrow the selector to one field (an id, an attribute, :nth-of-type), or click the field with browser_click " +
                      "(which takes index) and type without a selector."));
        }

        return CdpPageResult<FoundElement>.Fail(nameof(UIElementErrorCode.ElementNotFound),
            count == 0
                ? $"No element of the page matches {locator.Describe()}" +
                  (locator.Text != null ? " among its shown elements" : string.Empty) + ". Nothing was done. Wait for it with " +
                  "wait_for_browser, or read the page (get_browser_content) to see what it has."
                : $"Only {count} element(s) match {locator.Describe()}, so index {locator.Index} names none. Nothing was done.");
    }

    /// <summary>The element's description and whether it is disabled (<see cref="BrowserPageScripts.ElementStateFunction"/>).</summary>
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
            new BrowserElementInfo(CdpReplies.GetString(value, "tag"), text.Length > 0 ? text : null), IsTrue(value, "disabled")));
    }

    /// <summary>
    /// The page the connection shows (<see cref="BrowserPageScripts.ViewportExpression"/>, in an isolated world of the tab's
    /// main frame, <see cref="CdpIsolatedWorld"/>): its viewport, devicePixelRatio, pinch-zoom scale and visibilityState,
    /// which an element inside a frame shares (its quads are in the main frame's viewport), and its URL and title. The
    /// isolated world keeps the page's scripts from redefining what is read, so a background page cannot pass
    /// browser_element_rect's check that it is shown, nor move where its element is placed. BrowserInternalPage when the
    /// page is not a web page (<see cref="BrowserUrls.IsBrowserOwnPage"/>): the tab may have navigated there after it was
    /// listed.
    /// </summary>
    private static async Task<CdpPageResult<PageView>> ReadPageAsync(
        ICdpConnection connection, CdpTarget target, TimeSpan timeout, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var (method, view) = await CdpIsolatedWorld.EvaluateAsync(connection, BrowserPageScripts.ViewportExpression,
            Bounded(Left(timeout, stopwatch)), cancellationToken).ConfigureAwait(false);
        if (!view.Success)
            return Unanswered<PageView>(method, view, target);

        if (view.Value.Error is { } fault)
            return CdpPageResult<PageView>.From(CdpReplies.ToFailure<bool>(method, fault));

        if (ValueOf(view.Value.Result) is not { } page)
        {
            return CdpPageResult<PageView>.Fail(BrowserErrorCode.CdpEvaluationFailed,
                $"The page in tab {target.Id} could not be read (it may be loading a new document). Nothing was done. Try again.");
        }

        var url = CdpReplies.GetString(page, "url");
        if (BrowserUrls.IsBrowserOwnPage(url))
        {
            var colon = url.IndexOf(':');
            return CdpPageResult<PageView>.Fail(BrowserErrorCode.BrowserInternalPage,
                $"Tab {target.Id} now shows a page that is not a web page ({(colon > 0 ? url[..colon].ToLowerInvariant() : "no address")}: " +
                "one of the browser's own pages, an extension's or an app's), which these tools never read or act on. Nothing was done.");
        }

        return CdpPageResult<PageView>.Ok(new PageView(
            CdpReplies.GetString(page, "visibility"),
            Number(page, "vw") ?? 0,
            Number(page, "vh") ?? 0,
            Number(page, "dpr") is > 0 and var dpr ? dpr : 1,
            Number(page, "scale") is > 0 and var scale ? scale : 1,
            url,
            CdpReplies.GetString(page, "title")));
    }

    /// <summary>The element's boxes (DOM.getContentQuads) on the page: ElementOffscreen when it has none (not rendered).</summary>
    private static async Task<CdpPageResult<CdpElementBox>> BoxAsync(
        ICdpConnection connection, CdpTarget target, FoundElement found, BrowserElementInfo info, PageView page, TimeSpan limit,
        CancellationToken cancellationToken)
    {
        var quads = await connection.SendAsync("DOM.getContentQuads", new { objectId = found.ObjectId }, limit, cancellationToken)
            .ConfigureAwait(false);
        if (!quads.Success)
            return Unanswered<CdpElementBox>("DOM.getContentQuads", quads, target);

        var rects = quads.Value!.Error == null ? QuadsOf(quads.Value.Result) : [];
        if (rects.Count == 0)
        {
            return CdpPageResult<CdpElementBox>.Fail(nameof(UIElementErrorCode.ElementOffscreen),
                $"The element ({Describe(info)}) has no box on the page: it is hidden (display: none) or not rendered" +
                (quads.Value.Error is { } fault ? $" ({fault.Message})" : string.Empty) + ".");
        }

        var left = rects.Min(r => r.X);
        var top = rects.Min(r => r.Y);
        var union = new CssRect(left, top, rects.Max(r => r.X + r.Width) - left, rects.Max(r => r.Y + r.Height) - top);
        return CdpPageResult<CdpElementBox>.Ok(new CdpElementBox(info, found.MatchCount, union, page.DevicePixelRatio, page.ViewportWidth,
            page.ViewportHeight, page.Scale, page.Visibility, rects));
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
        ICdpConnection connection, CdpTarget target, bool replace, TimeSpan limit, CancellationToken cancellationToken)
    {
        var field = await connection.SendAsync("Runtime.evaluate", new
        {
            expression = BrowserPageScripts.BuildFocusedFieldExpression(replace),
            returnByValue = true
        }, limit, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// How the dialog an action opened was answered (<see cref="ICdpConnection.AnswerNextDialog"/>): the answer, or why it
    /// failed (the dialog is then still open); both null when no dialog opened or none was to be answered.
    /// </summary>
    private static async Task<(CdpAnsweredDialog? Answer, string? Failure)> AnswerOfAsync(
        CdpDialog? dialog, Task<CdpResult<CdpAnsweredDialog>>? answered, TimeSpan timeout, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (dialog == null || answered == null)
            return (null, null);

        var wait = Bounded(Left(timeout, stopwatch)) + CdpConnection.DialogAnswerTimeout;
        if (await Task.WhenAny(answered, Task.Delay(wait, cancellationToken)).ConfigureAwait(false) != answered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (null, $"the browser did not confirm the answer within {FormatMs(wait)}");
        }

        var result = await answered.ConfigureAwait(false);
        return result.Success ? (result.Value, null) : (null, result.Error);
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

    /// <summary>The time a command sent inside the gate may take: at most <see cref="GatedCommandTime"/>.</summary>
    private static TimeSpan Gated(TimeSpan timeout, Stopwatch stopwatch) => Min(GatedCommandTime, Left(timeout, stopwatch));

    /// <summary>Whether the user paused input since the gate was taken (<see cref="ICdpGateHold"/>).</summary>
    private static bool Paused(IDisposable hold) => hold is ICdpGateHold { IsPaused: true };

    /// <summary>PausedByUser for an action the pause stopped part way, saying what had been sent.</summary>
    private static CdpPageResult<CdpInputOutcome> PausedFail(string done) =>
        CdpPageResult<CdpInputOutcome>.Fail(nameof(WindowErrorCode.PausedByUser),
            $"Input was paused: the user pressed the pause hotkey while this call ran, so it stopped. {done} Stop and ask the user " +
            "how to continue; do not try to resume input yourself.");

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
    private sealed record ElementState(BrowserElementInfo Info, bool Disabled);

    /// <summary>What <see cref="BrowserPageScripts.ViewportExpression"/> read of the page, in an isolated world of its main frame.</summary>
    private sealed record PageView(
        string Visibility, double ViewportWidth, double ViewportHeight, double DevicePixelRatio, double Scale, string Url, string Title);

    /// <summary>The page's focused element: whether it takes text, its tag, whether it is a frame, and its content's length.</summary>
    private sealed record FocusedField(bool Editable, string? Tag, bool Frame, int? Length);
}
