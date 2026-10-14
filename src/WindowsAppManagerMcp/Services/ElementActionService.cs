using System.Diagnostics;
using System.Globalization;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>element_action (roadmap M3.6); see <see cref="IElementActionService"/> for the order of the checks.</summary>
/// <remarks>
/// <para>A call has three phases. (1) Inside the input gate, after the window's target checks, one read on the UI Automation
/// worker resolves the ref, reads the element's identity and state and chooses how to act (<see cref="Prepare"/>), refusing a
/// disabled, offscreen or read-only element or one without the pattern the action needs. (2) The action: a pattern call on a
/// thread of the action runner (every pattern action, not only the ones known to block: none may block the read worker);
/// or, for the mouse and keyboard paths, verified activation (EnsureForeground) inside the gate, then the guarded input
/// service after the gate is released. (3) After a short settle, the state is read again, up to
/// <see cref="VerifyAttempts"/> times while it does not show the action's effect yet, and the application's new top-level
/// windows are compared with those before the action (openedPopup).</para>
/// <para>Values: a password field's value is never read (not before, not after, not to check it), and an element whose
/// identity cannot be read is not acted on, so it cannot pass for a field that is not a password field. Other fields' values are
/// read back into the result's state (at most 200 characters); the audit log keeps only the value's length (AuditRecord).</para>
/// <para>Target checks: the ref's window is checked first (<see cref="IInputTargetGuard.CheckTarget"/>); an element of
/// another process (a child or owned window of another program) has its own window checked too
/// (<see cref="IInputTargetGuard.CheckElementWindow"/>), before its state is read, so a refusal carries no name or value of
/// it; and a pause the user started while the element was read stops the call before it acts.</para>
/// </remarks>
public sealed partial class ElementActionService : IElementActionService
{
    /// <summary>The default timeout of an action, and the bound of each of its reads.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many times the state is read after the action while it does not show the action's effect yet.</summary>
    public const int VerifyAttempts = 3;

    /// <summary>
    /// How long the keyboard path's focus recheck waits for UI Automation (roadmap M3.F17). It runs inside the input gate
    /// before every batch of keys, so an application that stops answering holds the gate at most this long.
    /// </summary>
    public static readonly TimeSpan FocusRecheckTimeout = TimeSpan.FromSeconds(1);

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly IElementRegistry _registry;
    private readonly IInputTargetGuard _guard;
    private readonly IInputSession _session;
    private readonly IUiaActionRunner _runner;
    private readonly IAppWindowProbe _probe;
    private readonly IInputSimulationService _input;
    private readonly IForegroundService _foreground;
    private readonly INativeWindowWrapper _windows;
    private readonly IClipboardService? _clipboard;
    private readonly bool _clipboardWritesServed;

    /// <summary>The tool whose absence (a toolset or exclusion the user chose) turns mode paste off.</summary>
    private const string SetClipboardTool = "set_clipboard";

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the reads under a deadline.</param>
    /// <param name="registry">Resolves the ref.</param>
    /// <param name="guard">The target checks of the element's window.</param>
    /// <param name="session">The user's pause.</param>
    /// <param name="runner">Runs the pattern calls.</param>
    /// <param name="probe">The application's new windows (openedPopup).</param>
    /// <param name="input">The mouse and keyboard paths, with every input check.</param>
    /// <param name="foreground">Verified activation before the mouse, keyboard and focus paths.</param>
    /// <param name="windows">The hung-window check, the processes and whether a window is top-level.</param>
    /// <param name="clipboard">set_value mode paste; without it that mode is refused.</param>
    /// <param name="toolsets">The tools the user lets the server serve: mode paste writes the clipboard, so it is refused
    /// when set_clipboard is not served. Null: every tool is served.</param>
    public ElementActionService(
        IUiaTree tree,
        IUiaWorker worker,
        IElementRegistry registry,
        IInputTargetGuard guard,
        IInputSession session,
        IUiaActionRunner runner,
        IAppWindowProbe probe,
        IInputSimulationService input,
        IForegroundService foreground,
        INativeWindowWrapper windows,
        IClipboardService? clipboard = null,
        ToolsetSelection? toolsets = null)
    {
        _clipboardWritesServed = toolsets?.Serves(SetClipboardTool, readOnlyHint: false) ?? true;
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _clipboard = clipboard;
    }

    /// <summary>How long to wait after the action before its state is read (and between the reads that verify it).</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the read of an opened popup's root element, for its ref, may take (roadmap M3.F19).</summary>
    public TimeSpan PopupRefTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</summary>
    public TimeSpan? GateTimeout { get; init; }

    /// <summary>
    /// How long a toggle toward a state waits for the state to change before it toggles again (at most a quarter of the call's
    /// timeout); when it does not change, there is no next toggle.
    /// </summary>
    public TimeSpan ToggleStateWait { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the toggle state is read while <see cref="ToggleStateWait"/> runs.</summary>
    public TimeSpan ToggleStatePoll { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The clock <see cref="ToggleStateWait"/> is measured on; the system clock by default.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// How the toggle wait pauses between its reads, on the action's thread (<see cref="Thread.Sleep(TimeSpan)"/> by default);
    /// a test moves its <see cref="Clock"/> here instead.
    /// </summary>
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>
    /// Why the request's arguments do not fit its action (a missing or invalid value, a direction for another action), or
    /// null. The tool checks this before it searches for the element, so a wrong call costs no search.
    /// </summary>
    public static string? ArgumentError(ElementActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = ToolEnum<ElementActionKind>.Name(request.Action);
        switch (request.Action)
        {
            case ElementActionKind.SetValue when request.Value == null:
                return "set_value needs value: the text to put in the element (\"\" empties it).";
            case ElementActionKind.AppendText when string.IsNullOrEmpty(request.Value):
                return "append_text needs value: the text to add at the end.";
            case ElementActionKind.SetRangeValue when !TryParseNumber(request.Value, out _):
                return $"set_range_value needs value: a number such as \"42\" or \"0.5\" (got {Describe(request.Value)}).";
            case ElementActionKind.Toggle when request.Value != null && ParseToggleState(request.Value) == null:
                return $"toggle's value is the state to reach: on, off or indeterminate (got {Describe(request.Value)}); leave it out to toggle once.";
            case ElementActionKind.Scroll when request.Direction == null:
                return "scroll needs direction: up, down, left or right (amount: page or line).";
            case ElementActionKind.MenuPath when MenuPaths.Parse(request.Value, out var pathError) == null:
                return pathError;
        }

        if (request.Value != null && request.Action is not (ElementActionKind.SetValue or ElementActionKind.AppendText
                or ElementActionKind.SetRangeValue or ElementActionKind.Toggle or ElementActionKind.MenuPath))
        {
            return $"value is for set_value, append_text, set_range_value, toggle and menu_path, not {action}.";
        }

        if (request.Mode != ElementValueMode.Value && request.Action is not (ElementActionKind.SetValue or ElementActionKind.AppendText))
        {
            return $"mode is for set_value and append_text, not {action}.";
        }

        if (request.Direction != null && request.Action != ElementActionKind.Scroll)
        {
            return $"direction is for scroll, not {action}.";
        }

        if (request.ScrollPercent is { } percent)
        {
            if (request.Action != ElementActionKind.Scroll)
            {
                return $"scrollPercent is for scroll, not {action}.";
            }

            if (!double.IsFinite(percent) || percent is < 0 or > 100)
            {
                return $"scrollPercent is a position from 0 to 100 (got {percent.ToString(CultureInfo.InvariantCulture)}).";
            }
        }

        if (request.Fallback == ElementActionFallback.Mouse && request.Action is not (ElementActionKind.Invoke or ElementActionKind.Click
                or ElementActionKind.Select))
        {
            return $"fallback mouse is for invoke, click and select (a click does what they do), not {action}. " +
                   "double_click and right_click are always mouse input.";
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<ElementActionResult> ActAsync(ElementActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var call = new ActionCall(request, Stopwatch.StartNew());
        if (ArgumentError(request) is { } argumentError)
        {
            return call.Fail(ToolErrorReporting.InvalidArgumentsErrorCode, argumentError + " Nothing was done.");
        }

        if (PasteRefusal(request) is { } pasteError)
        {
            return call.Fail(ToolErrorReporting.InvalidArgumentsErrorCode, pasteError + " Nothing was done.");
        }

        if (request.Action == ElementActionKind.MenuPath)
        {
            // Several elements: the tool carries the path out item by item through IMenuPathService, each item through here
            return call.Fail(ToolErrorReporting.InvalidArgumentsErrorCode,
                "menu_path names several menu items, not one element: it is carried out item by item (IMenuPathService). Nothing was done.");
        }

        var lookup = _registry.Lookup(request.Ref);
        if (!lookup.Success)
        {
            return call.Fail(lookup.ErrorCode.ToString(), lookup.Error!);
        }

        call.Ref = lookup.Ref;
        call.Window = lookup.WindowHandle;

        IDisposable gate;
        try
        {
            gate = await InputGateToolFilter.EnterAsync(_session, cancellationToken, GateTimeout).ConfigureAwait(false);
        }
        catch (ToolErrorException ex)
        {
            return call.Fail(ex.ErrorCode, ex.Message);
        }

        ElementActionResult result;
        try
        {
            result = await ActInsideGateAsync(call, gate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Dispose();
        }

        return await WithPopupRefAsync(call, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The result with its openedPopup's ref (roadmap M3.F19): the popup window's root element, read on the worker and
    /// registered under that window, as get_active_popups registers a popup's, once the window has passed the target checks
    /// of an element's own window (policy, elevation, host: Win32 reads) and answers messages. A hint, as openedPopup is: when
    /// the window is refused or its root cannot be read within <see cref="PopupRefTimeout"/>, the popup is named without a ref.
    /// </summary>
    private async Task<ElementActionResult> WithPopupRefAsync(ActionCall call, ElementActionResult result, CancellationToken cancellationToken)
    {
        if (result.OpenedPopup is not { Ref: null } popup)
        {
            return result;
        }

        var handle = (nint)popup.Handle;
        if (UiaTargets.NotRespondingReason(_windows, handle) != null ||
            !_guard.CheckElementWindow(InputTarget.Window(call.Window, call.Request.AllowHostTarget), handle).Allowed)
        {
            return result;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PopupRefTimeout);
        try
        {
            var popupRef = await _worker.RunAsync(popup.ProcessId, _ => _tree.FromHandle(handle) is { } root
                    ? _registry.Register(handle, _tree.ReadElement(root), new ElementOrigin(_registry.NextGeneration()))
                    : null,
                deadline.Token, TimeSpan.Zero).ConfigureAwait(false);
            return popupRef == null ? result : result with { OpenedPopup = popup with { Ref = popupRef } };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // It closed meanwhile, its provider failed or did not answer: named without a ref
            return result;
        }
    }

    /// <summary>
    /// Why mode paste cannot be used (it replaces the user's clipboard, so it follows set_clipboard: served, and the same
    /// length limit), or null.
    /// </summary>
    private string? PasteRefusal(ElementActionRequest request)
    {
        if (request.Mode != ElementValueMode.Paste)
        {
            return null;
        }

        if (_clipboard == null || !_clipboardWritesServed)
        {
            return "mode paste replaces the clipboard, and this server does not write the clipboard (set_clipboard is not " +
                   "served: the user turned clipboard writes off). Use mode type.";
        }

        return request.Value is { Length: > ClipboardService.MaxTextLength } text
            ? $"mode paste takes at most {ClipboardService.MaxTextLength} characters, as set_clipboard (got {text.Length})."
            : null;
    }

    /// <summary>Phases 1 to 3 (see the remarks); <paramref name="gate"/> is released as soon as nothing more needs it.</summary>
    private async Task<ElementActionResult> ActInsideGateAsync(ActionCall call, IDisposable gate, CancellationToken cancellationToken)
    {
        var request = call.Request;
        var target = InputTarget.Window(call.Window, request.AllowHostTarget);
        var checkedTarget = _guard.CheckTarget(target);
        if (!checkedTarget.Allowed)
        {
            return call.Fail(checkedTarget.ErrorCode.ToString(), checkedTarget.Error!);
        }

        var targetProcess = UiaTargets.TargetProcessId(_windows, call.Window);
        // An element of another process has its own window checked in there too, before its state is read
        var prepared = await ReadAsync(call, targetProcess, token => Prepare(call, target, token), cancellationToken).ConfigureAwait(false);
        if (prepared.Failure is { } refused)
        {
            return refused;
        }

        var element = prepared.Element!;
        call.Ref = element.Ref;
        call.Before = element.Before;

        // The read can take up to the timeout: a pause the user started meanwhile stops the call before it acts
        try
        {
            InputGateToolFilter.ThrowIfPaused(_session);
        }
        catch (ToolErrorException ex)
        {
            return call.Fail(ex.ErrorCode, ex.Message);
        }

        var windowsBefore = _probe.Snapshot(ProcessesOf(call.Window, element));
        var plan = element.Plan;

        switch (plan.Kind)
        {
            case PlanKind.Nothing:
                gate.Dispose();
                return Finish(call, element, ElementActionMethod.None, windowsBefore, input: null);

            case PlanKind.Pattern:
            {
                var outcome = await _runner.RunAsync(targetProcess, plan.Act!, Timeout(request), cancellationToken).ConfigureAwait(false);
                gate.Dispose();
                if (Unfinished(call, outcome, windowsBefore, plan.Method) is { } unfinished)
                {
                    return unfinished;
                }

                break;
            }

            case PlanKind.Focus:
            {
                if (Activate(call) is { } notInFront)
                {
                    return notInFront;
                }

                var outcome = await _runner.RunAsync(targetProcess, () => _tree.SetFocus(element.Node), Timeout(request), cancellationToken)
                    .ConfigureAwait(false);
                gate.Dispose();
                if (Unfinished(call, outcome, windowsBefore, plan.Method) is { } unfinished)
                {
                    return unfinished;
                }

                break;
            }

            case PlanKind.Mouse:
            {
                var clicked = await ClickAsync(call, element, plan, gate, targetProcess, cancellationToken).ConfigureAwait(false);
                if (clicked.Failure != null)
                {
                    return clicked.Failure;
                }

                return await AfterAsync(call, element, plan.Method, windowsBefore, targetProcess, clicked.Receiving, cancellationToken)
                    .ConfigureAwait(false);
            }

            case PlanKind.Keyboard:
            {
                var typed = await TypeAsync(call, element, gate, targetProcess, cancellationToken).ConfigureAwait(false);
                if (typed.Failure != null)
                {
                    return typed.Failure;
                }

                return await AfterAsync(call, element, plan.Method, windowsBefore, targetProcess, typed.Receiving, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return await AfterAsync(call, element, plan.Method, windowsBefore, targetProcess, receiving: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The result when a pattern call did not simply return: refused (ActionsPending), still running at the timeout or
    /// timed out in the provider (InvokedPendingModal when a window of the application appeared, else ActionTimedOut), or
    /// failed (the exception's code). Null when it returned.
    /// </summary>
    private ElementActionResult? Unfinished(ActionCall call, UiaActionOutcome outcome, AppWindowSnapshot windowsBefore, ElementActionMethod method)
    {
        switch (outcome.Status)
        {
            case UiaActionStatus.Refused:
                return call.Fail(nameof(UIElementErrorCode.ActionsPending), outcome.Message!);
            case UiaActionStatus.Pending:
                return Pending(call, windowsBefore, method, "had not returned");
        }

        return outcome.Error switch
        {
            null => null,
            // The UIA3 engine's transaction timeout: the provider did not answer, often because the call waits for a dialog
            TimeoutException => Pending(call, windowsBefore, method, "timed out in the application"),
            UiaElementUnavailableException => call.Fail(nameof(UIElementErrorCode.ElementStale),
                $"The element disappeared while it was acted on ({outcome.Error.Message}). Whether the action took effect is " +
                "unknown: read the window again before you repeat it."),
            NotSupportedException => call.Fail(nameof(UIElementErrorCode.PatternNotSupported),
                $"The application does not support {method} on this element ({outcome.Error.Message})."),
            _ => call.Fail(nameof(UIElementErrorCode.ActionFailed),
                $"The application refused {method}: {outcome.Error.Message}. The element may not allow it in its current state; " +
                "read it again (find_ui_elements with detail standard) before you retry.")
        };
    }

    /// <summary>
    /// The action is still running: a new window of the application that can be a dialog (not a tooltip, a menu or a hover
    /// card that showed meanwhile) is the dialog it waits for.
    /// </summary>
    private ElementActionResult Pending(ActionCall call, AppWindowSnapshot windowsBefore, ElementActionMethod method, string what)
    {
        var opened = _probe.OpenedSince(windowsBefore);
        if (opened.Dialogs is not [var dialog, ..])
        {
            var others = opened.Others.Count == 0
                ? string.Empty
                : $" (only {(opened.Others.Count == 1 ? "a new window that is" : $"{opened.Others.Count} new windows that are")} no " +
                  "dialog: a menu, dropdown or popup, which the action does not wait for)";
            return call.Fail(nameof(UIElementErrorCode.ActionTimedOut),
                $"{method} {what} after {Timeout(call.Request).TotalMilliseconds:0} ms and no new dialog of the application " +
                $"appeared{others}: it may be busy. The action was sent and may still take effect, so do not repeat it blindly: read " +
                "the element again (find_ui_elements with detail standard), or wait_for_window for a dialog.") with
            {
                Method = method,
                OpenedPopup = opened.Others.FirstOrDefault()
            };
        }

        return call.Succeed(method) with
        {
            Status = ElementActionStatus.InvokedPendingModal,
            OpenedPopup = dialog,
            Warnings = Warnings(
                $"{method} {what} after {Timeout(call.Request).TotalMilliseconds:0} ms: it waits for the window it opened " +
                $"('{dialog.Title}', handle {dialog.Handle}). Read and answer it (find_ui_elements with that handle), then " +
                "read the element again.",
                SeveralOpened(opened))
        };
    }

    /// <summary>The warning when several windows appeared, or null.</summary>
    private static string? SeveralOpened(AppWindowsOpened opened) => opened.All.Count > 1
        ? $"{opened.All.Count} new windows of the application appeared; openedPopup is the topmost that can be a dialog " +
          "(else the topmost)."
        : null;

    /// <summary>
    /// Verified activation of the element's window, inside the gate; the failure, or null when it is in front. The codes are
    /// the input path's (as the input guard maps its own activation): a window that is gone, does not respond, is denied or
    /// paused keeps its code, and any other window that did not come to the front (hidden, cloaked, refused by the
    /// foreground lock) is TargetNotForeground. A menu that activation could not close is TargetInMenuMode.
    /// </summary>
    /// <param name="click">The mouse path: a click, not keys, would go to the open menu.</param>
    private ElementActionResult? Activate(ActionCall call, bool click = false)
    {
        var outcome = _foreground.EnsureForeground(call.Window);
        if (outcome.IsForeground)
        {
            return !outcome.InMenuMode
                ? null
                : call.Fail(nameof(InputErrorCode.TargetInMenuMode), click
                    ? "A menu is open in the element's window and could not be closed, so a click would only close it or choose " +
                      "one of its items. Nothing was clicked. Close the menu first (send_keys {ESC} with windowHandle), then try again."
                    : "A menu is active in the element's window, so keys would select menu items. Nothing was done. Close the menu " +
                      "first (send_keys {ESC} with windowHandle), then try again.");
        }

        var code = outcome.ErrorCode is WindowErrorCode.InvalidHandle or WindowErrorCode.NotResponding or WindowErrorCode.PausedByUser
            or WindowErrorCode.TargetDeniedByPolicy
            ? outcome.ErrorCode.ToString()
            : nameof(InputErrorCode.TargetNotForeground);
        return call.Fail(code,
            $"The element's window could not be brought to the front ({outcome.ErrorCode}), so nothing was done. {outcome.Error} " +
            "Call focus_window for details, or ask the user to bring the window to the front.");
    }

    /// <summary>
    /// The mouse path: activation and a fresh click point inside the gate, hit-tested so that the click lands on the element
    /// and not on another element over it (TargetOccluded), then the guarded click outside the gate.
    /// </summary>
    private async Task<InputStep> ClickAsync(
        ActionCall call, PreparedElement element, Plan plan, IDisposable gate, int targetProcess, CancellationToken cancellationToken)
    {
        if (Activate(call, click: true) is { } notInFront)
        {
            return InputStep.Failed(notInFront);
        }

        // Activation can restore or move the window: the point is read again now
        var point = await ReadAsync(call, targetProcess, _ => ClickPointOf(call, element), cancellationToken).ConfigureAwait(false);
        if (point.Failure != null)
        {
            return InputStep.Failed(point.Failure);
        }

        if (point.Element is { Covered: true } covered)
        {
            return InputStep.Failed(Covered(call, element, covered));
        }

        if (point.Element is not { Point: { } at } clickPoint)
        {
            return InputStep.Failed(call.Fail(nameof(UIElementErrorCode.ElementOffscreen),
                "The element has no point on screen to click (it is offscreen or has no bounds). Nothing was clicked. Use " +
                "scroll_into_view first."));
        }

        if (clickPoint.Warning != null)
        {
            element.Warnings.Add(clickPoint.Warning);
        }

        gate.Dispose();
        var target = InputTarget.Window(call.Window, call.Request.AllowHostTarget);
        var clicked = plan.Mouse switch
        {
            MouseKind.Right => _input.RightClick(at.X, at.Y, target, releaseHeldModifiers: false, cancellationToken),
            MouseKind.Double => _input.DoubleClick(at.X, at.Y, 50, target, releaseHeldModifiers: false, cancellationToken),
            _ => _input.Click(at.X, at.Y, target, releaseHeldModifiers: false, cancellationToken)
        };

        return clicked.Success
            ? new InputStep(null, clicked.ReceivingWindow)
            : InputStep.Failed(call.Fail(clicked.ErrorCode.ToString(), InputError(clicked.ErrorCode, clicked.Error, "The click was not sent.")) with
            {
                Method = ElementActionMethod.Mouse,
                ReceivingWindow = clicked.ReceivingWindow
            });
    }

    /// <summary>
    /// The keyboard path (set_value and append_text, mode type or paste): inside the gate, activation, SetFocus and a check
    /// that keyboard focus is on the element (FocusNotOnElement otherwise: select-all would reach another control), and for
    /// paste the text on the clipboard; then outside it, through the guarded input service, select-all (set_value) or
    /// Ctrl+End (append_text), a second focus check, and the text typed or pasted.
    /// </summary>
    private async Task<InputStep> TypeAsync(
        ActionCall call, PreparedElement element, IDisposable gate, int targetProcess, CancellationToken cancellationToken)
    {
        var request = call.Request;
        if (Activate(call) is { } notInFront)
        {
            return InputStep.Failed(notInFront);
        }

        var focused = await _runner.RunAsync(targetProcess, () => _tree.SetFocus(element.Node), Timeout(request), cancellationToken)
            .ConfigureAwait(false);
        if (focused.Status != UiaActionStatus.Completed || focused.Error is not null and not InvalidOperationException)
        {
            var failure = Unfinished(call, focused, _probe.Snapshot([]), ElementActionMethod.Focus);
            if (failure != null)
            {
                return InputStep.Failed(failure);
            }
        }

        if (await FocusRefusalAsync(call, element, targetProcess, "Nothing was typed.", cancellationToken).ConfigureAwait(false) is { } off)
        {
            return InputStep.Failed(off);
        }

        var text = request.Value ?? string.Empty;
        if (request.Mode == ElementValueMode.Paste && text.Length > 0)
        {
            var clipboard = _clipboard;
            if (clipboard == null)
            {
                return InputStep.Failed(call.Fail(ToolErrorReporting.InvalidArgumentsErrorCode,
                    "mode paste needs the clipboard, which this server has not set up. Use mode type."));
            }

            // As set_clipboard does, inside the gate; the paste below passes the paste guard because the agent put it there
            var set = clipboard.SetText(text);
            if (!set.Success)
            {
                return InputStep.Failed(call.Fail(set.ErrorCode.ToString(), $"The text could not be put on the clipboard: {set.Error} Nothing was typed."));
            }
        }

        gate.Dispose();

        // Each call below takes the input gate again, so focus could move between them: the guard runs this recheck inside
        // the gate before every batch, and keys never reach another control the focus moved to (roadmap M3.F17)
        var focus = new FocusWatch();
        var target = InputTarget.Window(call.Window, request.AllowHostTarget) with
        {
            FocusRecheck = () => RecheckFocus(element, targetProcess, focus, cancellationToken)
        };
        var selectKey = request.Action == ElementActionKind.AppendText ? "Ctrl+End" : "Ctrl+A";

        InputStep Failed(InputErrorCode code, WindowSummary? receivingWindow, string message, string ifFocusMoved) =>
            InputStep.Failed(call.Fail(focus.ErrorCode ?? code.ToString(), focus.Message is { } why
                ? $"{why} {ifFocusMoved} Read the element again before you retry, or use mode value."
                : message) with
            {
                Method = ElementActionMethod.Keyboard,
                ReceivingWindow = receivingWindow
            });

        var first = await _input.SendKeysAsync(request.Action == ElementActionKind.AppendText ? "^{END}" : "^a", target,
            releaseHeldModifiers: false, cancellationToken).ConfigureAwait(false);
        if (!first.Success)
        {
            return Failed(first.ErrorCode, first.ReceivingWindow,
                InputError(first.ErrorCode, first.Error, "The keys were not sent.") + " Nothing was typed.", "Nothing was typed.");
        }

        var receiving = first.ReceivingWindow;
        if (text.Length == 0)
        {
            // set_value "": the selected text is deleted
            var deleted = await _input.SendKeysAsync("{DELETE}", target, releaseHeldModifiers: false, cancellationToken).ConfigureAwait(false);
            return deleted.Success
                ? new InputStep(null, deleted.ReceivingWindow ?? receiving)
                : Failed(deleted.ErrorCode, deleted.ReceivingWindow, InputError(deleted.ErrorCode, deleted.Error, "Delete was not sent."),
                    $"Only {selectKey} was sent; the text was not deleted.");
        }

        if (request.Mode == ElementValueMode.Paste)
        {
            var pasted = await _input.SendKeysAsync("^v", target, releaseHeldModifiers: false, cancellationToken).ConfigureAwait(false);
            return pasted.Success
                ? new InputStep(null, pasted.ReceivingWindow ?? receiving)
                : Failed(pasted.ErrorCode, pasted.ReceivingWindow, InputError(pasted.ErrorCode, pasted.Error, "The paste was not sent."),
                    $"Only {selectKey} was sent; nothing was pasted.");
        }

        var typed = await _input.TypeTextAsync(text, 0, target, releaseHeldModifiers: false, cancellationToken).ConfigureAwait(false);
        return typed.Success
            ? new InputStep(null, typed.ReceivingWindow ?? receiving)
            : Failed(typed.ErrorCode, typed.ReceivingWindow,
                $"{InputError(typed.ErrorCode, typed.Error, "The text was not typed.")} " +
                $"({typed.CharactersTyped} of {text.Length} characters were typed.)",
                typed.CharactersTyped == 0
                    ? $"Only {selectKey} was sent; nothing was typed."
                    : $"{typed.CharactersTyped} of {text.Length} characters were typed before it moved.");
    }

    /// <summary>Why the keyboard path's focus recheck refused keys (roadmap M3.F17); both null while it allowed them.</summary>
    private sealed class FocusWatch
    {
        /// <summary>element_action's errorCode for it: FocusNotOnElement, or OperationTimedOut when focus could not be read.</summary>
        public string? ErrorCode { get; set; }

        public string? Message { get; set; }
    }

    /// <summary>
    /// <see cref="InputTarget.FocusRecheck"/> for the keyboard path, run by the input guard inside the gate: keyboard focus must
    /// still be on the element. The read runs on the worker for at most <see cref="FocusRecheckTimeout"/>, since the gate is held
    /// meanwhile; when it fails or times out the keys are refused, because they could reach any control of the window.
    /// </summary>
    private SelectorRefusal? RecheckFocus(PreparedElement element, int targetProcess, FocusWatch watch, CancellationToken cancellationToken)
    {
        bool onElement;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(FocusRecheckTimeout);
            onElement = _worker.RunAsync(targetProcess, _ => HasFocus(element), deadline.Token, TimeSpan.Zero)
                .GetAwaiter().GetResult().Element == true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            watch.ErrorCode = nameof(UIElementErrorCode.OperationTimedOut);
            watch.Message = $"UI Automation did not say in time where keyboard focus is ({ex.Message}), so the keys could reach " +
                            "another control of the window.";
            return new SelectorRefusal(nameof(InputErrorCode.TargetLostFocus), watch.Message);
        }

        if (onElement)
        {
            return null;
        }

        watch.ErrorCode = nameof(UIElementErrorCode.FocusNotOnElement);
        watch.Message = "Keyboard focus moved off the element to another control after the element was focused, so the keys " +
                        "would have reached that control.";
        return new SelectorRefusal(nameof(InputErrorCode.TargetLostFocus), watch.Message);
    }

    /// <summary>
    /// An input path's error as element_action reports it (<paramref name="ifNone"/> when it has none). The input service's
    /// ModifiersHeld advice for a stuck key names its own releaseHeldModifiers argument, which element_action does not take
    /// (an agent passing it would get the same refusal again): release_all_input is named instead.
    /// </summary>
    private static string InputError(InputErrorCode code, string? error, string ifNone) =>
        code == InputErrorCode.ModifiersHeld && error != null
            ? error.Replace(InputSimulationService.ReleaseHeldModifiersAdvice, "call release_all_input with releaseHeldModifiers: true",
                StringComparison.Ordinal)
            : error ?? ifNone;

    /// <summary>FocusNotOnElement when keyboard focus is not on the element or inside it; null when it is.</summary>
    private async Task<ElementActionResult?> FocusRefusalAsync(
        ActionCall call, PreparedElement element, int targetProcess, string done, CancellationToken cancellationToken)
    {
        var focus = await ReadAsync(call, targetProcess, _ => HasFocus(element), cancellationToken).ConfigureAwait(false);
        if (focus.Failure != null)
        {
            return focus.Failure;
        }

        return focus.Element == true
            ? null
            : call.Fail(nameof(UIElementErrorCode.FocusNotOnElement),
                $"Keyboard focus is not on the element after focusing it, so the keys would reach another control. {done} " +
                "The element may not take focus this way: click it first (action click with fallback mouse), or use mode value.");
    }

    /// <summary>Phase 3: settle, read the state again (up to <see cref="VerifyAttempts"/> times), and the new windows.</summary>
    private async Task<ElementActionResult> AfterAsync(
        ActionCall call, PreparedElement element, ElementActionMethod method, AppWindowSnapshot windowsBefore, int targetProcess,
        WindowSummary? receiving, CancellationToken cancellationToken)
    {
        AfterRead? after = null;
        for (var attempt = 0; attempt < VerifyAttempts; attempt++)
        {
            if (SettleDelay > TimeSpan.Zero)
            {
                await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);
            }

            var read = await ReadAsync(call, targetProcess, token => ReadAfter(call, element, token), cancellationToken).ConfigureAwait(false);
            if (read.Element is not { } state)
            {
                // The action was done; only its check failed
                after = new AfterRead(null, null, null, Gone: false, Warning: read.Failure?.Error);
                break;
            }

            after = state;
            if (Verify(call.Request, element, state) != false)
            {
                break;
            }
        }

        return Finish(call, element, method, windowsBefore, receiving, after);
    }

    private ElementActionResult Finish(
        ActionCall call, PreparedElement element, ElementActionMethod method, AppWindowSnapshot windowsBefore, WindowSummary? input,
        AfterRead? after = null)
    {
        var request = call.Request;
        after ??= new AfterRead(element.Before, element.FullValue, element.ScrollBefore, Gone: false, Warning: null);

        // Nothing done means the state asked for was there already; except for realize, which can find nothing to do for an
        // item that is still offscreen (no VirtualizedItem or ScrollItem pattern): its outcome, on screen, is checked
        var verified = method == ElementActionMethod.None && request.Action != ElementActionKind.Realize
            ? true
            : Verify(request, element, after);
        var opened = _probe.OpenedSince(windowsBefore);
        var warnings = new List<string?> { after.Warning, SeveralOpened(opened) };
        if (verified == false && method != ElementActionMethod.None)
        {
            // Without an action there is nothing that could still take effect: the plan's own warning says why
            warnings.Add(UnverifiedWarning(request, element));
        }

        if (after.Gone && request.Action != ElementActionKind.WindowClose)
        {
            warnings.Add("The element is gone after the action (it closed, or its list or dialog was rebuilt): its ref is stale now.");
        }

        // A toggle asked for a state it did not reach did not do what was asked
        if (request.Action == ElementActionKind.Toggle && request.Value != null && verified == false)
        {
            var goal = ParseToggleState(request.Value);
            return call.Fail(nameof(UIElementErrorCode.ActionFailed), element.Plan.CycledBack
                ? $"The element came back to {element.Before.ToggleState} after {element.Plan.Toggles} toggles without reaching " +
                  $"toggle state {goal}: it does not have that state (a two-state check box has no Indeterminate) or skips it. " +
                  "It was not toggled again, so it is left as it was."
                : $"The element did not reach toggle state {goal} after {element.Plan.Toggles} toggles " +
                  $"(it is {after.State?.ToggleState?.ToString() ?? "unknown"}). It may cycle through other states or refuse this one.") with
            {
                Method = method,
                After = after.State,
                OpenedPopup = opened.All.FirstOrDefault()
            };
        }

        return call.Succeed(method) with
        {
            After = after.State,
            OutcomeVerified = verified,
            Scroll = request.Action == ElementActionKind.Scroll ? ScrollPosition(request, element, after) : null,
            OpenedPopup = opened.All.FirstOrDefault(),
            ReceivingWindow = input,
            Warnings = Warnings([.. warnings, .. element.Warnings])
        };
    }

    /// <summary>A read on the worker under the call's timeout; a timeout or refusal is OperationTimedOut.</summary>
    private async Task<ReadOutcome<T>> ReadAsync<T>(ActionCall call, int targetProcess, Func<CancellationToken, ReadOutcome<T>> read,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout(call.Request));
        try
        {
            return await _worker.RunAsync(targetProcess, read, deadline.Token, TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return ReadOutcome<T>.Failed(call.Fail(nameof(UIElementErrorCode.OperationTimedOut),
                $"UI Automation did not answer in time ({ex.Message}). Nothing more was done; read the element again before you retry."));
        }
    }

    private static TimeSpan Timeout(ElementActionRequest request) => request.Timeout ?? DefaultTimeout;

    /// <summary>The processes whose new windows count as opened by the action: the element's, and the window's (and its app's).</summary>
    private int[] ProcessesOf(nint window, PreparedElement element)
    {
        var processes = new HashSet<int> { element.ProcessId };
        try
        {
            processes.Add((int)_windows.GetWindowThreadProcessId(window));
            processes.Add(UiaTargets.TargetProcessId(_windows, window));
        }
        catch (Exception)
        {
            // The element's process is enough
        }

        processes.Remove(0);
        return [.. processes];
    }

    private static IReadOnlyList<string>? Warnings(params string?[] warnings)
    {
        var kept = warnings.Where(w => !string.IsNullOrEmpty(w)).Select(w => w!).Distinct().ToList();
        return kept.Count == 0 ? null : kept;
    }

    private static bool TryParseNumber(string? value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    /// <summary>The toggle state a toggle value names (on, off, indeterminate, and checked, unchecked, true, false, mixed), or null.</summary>
    public static UiaToggleState? ParseToggleState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "checked" or "true" => UiaToggleState.On,
        "off" or "unchecked" or "false" => UiaToggleState.Off,
        "indeterminate" or "mixed" => UiaToggleState.Indeterminate,
        _ => null
    };

    private static string Describe(string? value) => value == null ? "nothing" : $"a value of {value.Length} characters";

    /// <summary>One call's request, what it found out so far, and its results.</summary>
    private sealed class ActionCall(ElementActionRequest request, Stopwatch stopwatch)
    {
        public ElementActionRequest Request { get; } = request;

        public string Action { get; } = ToolEnum<ElementActionKind>.Name(request.Action);

        public string? Ref { get; set; } = ElementRefs.Normalize(request.Ref) ?? request.Ref;

        public nint Window { get; set; }

        public ElementState? Before { get; set; }

        public ElementActionResult Fail(string code, string error) => new(
            false, Action, Ref, (long)Window, Before: Before, Error: error, ErrorCode: code, ElapsedMilliseconds: Elapsed);

        public ElementActionResult Succeed(ElementActionMethod method) => new(
            true, Action, Ref, (long)Window, ElementActionStatus.Done, method, Before, ElapsedMilliseconds: Elapsed);

        private double Elapsed => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
    }

    /// <summary>A step of the mouse or keyboard path: its failure, or the window its input went to.</summary>
    private sealed record InputStep(ElementActionResult? Failure, WindowSummary? Receiving)
    {
        public static InputStep Failed(ElementActionResult failure) => new(failure, null);
    }

    /// <summary>What a read on the worker produced: a value, or the call's failure.</summary>
    private readonly record struct ReadOutcome<T>(T? Element, ElementActionResult? Failure)
    {
        public static ReadOutcome<T> Of(T value) => new(value, null);

        public static ReadOutcome<T> Failed(ElementActionResult failure) => new(default, failure);
    }
}
