using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;
using static WindowsAppManagerMcp.Models.WindowErrorCode;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class WindowManagerTools
{
    /// <summary>The errors of the single-window change tools (move, resize, bounds, state, snap, move to a monitor).</summary>
    private const string WindowChangeErrors =
        " errorCode: InvalidHandle; TargetDeniedByPolicy (the user's policy denies the app: nothing is changed, ask the user); " +
        "AccessDenied (Windows blocked the change: the window belongs to an app running as administrator; ask the user); " +
        "NotResponding (the window's app is hung or did not answer within 5 s, or an earlier call to it, or 8 calls to apps that " +
        "do not answer, are still blocked; a minimized window may already have been restored, and a call that timed out can still " +
        "take effect once the app answers, so check the window before acting on it).";

    /// <summary>What the single-window geometry tools return (roadmap M2.3).</summary>
    private const string GeometryResult =
        " Returns success and the window as it is afterwards, also when the change failed: bounds (its visible frame), outerBounds " +
        "(its window rect with the invisible resize borders, about 7 px left, right and bottom at 100%; find_windows reports this " +
        "rect as bounds, and the visible frame as visibleBounds), state (normal, minimized or maximized; a minimized window has no " +
        "bounds) and monitorIndex.";

    /// <summary>How the placements read their coordinates and treat the window's state (roadmap M2.3).</summary>
    private const string PlacementRules =
        " Coordinates are physical virtual-screen pixels (negative left of or above the primary monitor) of the visible frame " +
        "(find_windows' visibleBounds, not its bounds). A minimized or maximized window is restored to normal first, without " +
        "activating it; one its app hid (in the notification area) is not shown, and the change fails.";

    /// <summary>The monitor errors of snap_window and move_window_to_monitor (roadmap M6.7).</summary>
    private const string MonitorErrors =
        " InvalidMonitorIndex, MonitorNotFound, AmbiguousMonitor (two stacked monitors): monitor names no single monitor (nothing changed).";

    /// <summary>How the window tools' argument errors end.</summary>
    private const string NothingChanged = "Nothing was changed.";

    private const string SettleDescription =
        "Wait up to this many milliseconds (0-5000, default 0) for the window to stop moving before it is read: until two reads " +
        "20 ms apart agree. Use it for an app that animates or re-applies its own size after the change.";

    private const string BringToFrontDescription =
        "Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). Default: true";

    /// <summary>The per-window errors of the window batches.</summary>
    private const string BatchErrors =
        " Each window is handled on its own and has its own success, error and errorCode: InvalidArguments for an item that is " +
        "null or misses a field, WindowNotFound, AmbiguousWindow (with candidates) or HandleMismatch for an item whose window " +
        "selector names no single window, InvalidHandle, TargetDeniedByPolicy (the user's policy denies the app; it is not changed), " +
        "AccessDenied, NotResponding (the window's app does not answer within 5 s). Each item reports its window afterwards, " +
        "failed or not: bounds (visible frame), outerBounds, state and monitorIndex.";

    private readonly IWindowService _windowService;
    private readonly IForegroundService _foregroundService;
    private readonly IInputSession? _session;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan? _gateWaitTimeout;
    private readonly IWindowCloseVerifier _closeVerifier;
    private readonly IMonitorService? _monitorService;

    /// <param name="windowService">Reads and changes windows.</param>
    /// <param name="foregroundService">Brings a window to the front (focus_window).</param>
    /// <param name="session">The input session, whose pause the close tools respect (they take the input gate themselves).</param>
    /// <param name="answerTimeout">How long close_windows_batch and close_application wait for the user's answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    /// <param name="gateWaitTimeout">How long the close tools wait for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    /// <param name="closeVerifier">
    /// Waits for closed windows and finds their prompts (roadmap M2.5); default a <see cref="WindowCloseVerifier"/> over
    /// <paramref name="windowService"/> alone, which finds Win32 prompts only.
    /// </param>
    /// <param name="monitorService">
    /// Resolves a `monitor` name (roadmap M6.7) and the monitor set_window_bounds' percentages are of. The server always
    /// passes it; without it a monitor name or unit 'percent' fails.
    /// </param>
    public WindowManagerTools(
        IWindowService windowService,
        IForegroundService foregroundService,
        IInputSession? session = null,
        TimeSpan? answerTimeout = null,
        TimeSpan? gateWaitTimeout = null,
        IWindowCloseVerifier? closeVerifier = null,
        IMonitorService? monitorService = null)
    {
        _windowService = windowService;
        _foregroundService = foregroundService;
        _session = session;
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
        _gateWaitTimeout = gateWaitTimeout;
        _closeVerifier = closeVerifier ?? new WindowCloseVerifier(windowService);
        _monitorService = monitorService;
    }

    [McpServerTool(Name = "set_window_bounds", Title = "Set Window Bounds", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move, resize, or move and resize a window in a single operation: x, y, width and height are its visible frame. " +
                 "Pass x and y to move it (it keeps its size), width and height to resize it (its top-left corner stays), or all four." +
                 PlacementRules +
                 " unit 'percent': percent (decimals allowed) of monitor's work area (relativeTo 'monitor': its bounds; default the " +
                 "window's monitor); adjoining values tile exactly." +
                 " A window moved to a monitor with another scale is placed again once the app has rescaled itself." +
                 GeometryResult + WindowChangeErrors +
                 " MoveFailed (ResizeFailed for a size only): Windows did not place it, or it did not return to normal from minimized or maximized (error has the reason). " +
                 "InvalidArguments (nothing changed): half a pair, no pair, a width or height below 1, pixels that are not whole, " +
                 "percentages outside 0-100, monitor or relativeTo without percent." + MonitorErrors)]
    public async Task<WindowOperationResult> SetWindowBounds(
        [Description("X of the visible frame's left edge (with y)")]
        double? x = null,
        [Description("Y of the visible frame's top edge (with x)")]
        double? y = null,
        [Description("Width of the visible frame, at least 1 pixel (with height)")]
        double? width = null,
        [Description("Height of the visible frame, at least 1 pixel (with width)")]
        double? height = null,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use " +
                     "focus_window for that). Default: true with x and y, false for a size only")]
        bool? bringToFront = null,
        [Description(SettleDescription)]
        int settleMs = 0,
        [Description("pixels (default) or percent")]
        BoundsUnit? unit = null,
        [Description("With unit 'percent': workArea (default) or monitor (its full bounds)")]
        RelativeArea? relativeTo = null,
        [Description(MonitorSelectorDescriptions.Monitor + " With unit 'percent'; default: the window's own.")]
        MonitorSelector? monitor = null,
        CancellationToken cancellationToken = default)
    {
        var percent = unit == BoundsUnit.Percent;
        if (BoundsRefusal(x, y, width, height, percent, relativeTo, monitor) is { } invalid)
        {
            return invalid;
        }

        var hwnd = Target(handle, window);
        var choice = percent ? MonitorTargeting.Choose(_monitorService, monitor, allowAll: false, NothingChanged) : default;
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return refused;

            if (!percent)
                return Change(hwnd, BoundsChange(hwnd, Pixels(x), Pixels(y), Pixels(width), Pixels(height), bringToFront), settleMs);

            // A named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var monitorIndex = MonitorTargeting.Current(_monitorService, choice, NothingChanged);

            // Percentages become pixels once, against the target monitor, before the window moves: the second placement after
            // a DPI change places the same pixels again (RelativeRectMath)
            if (PercentBounds(hwnd, monitorIndex, relativeTo ?? RelativeArea.WorkArea, x, y, width, height, out var pixels) is { } notPlaced)
                return notPlaced;

            return Change(hwnd, BoundsChange(hwnd, pixels.X, pixels.Y, pixels.Width, pixels.Height, bringToFront), settleMs);
        }
    }

    private static int? Pixels(double? value) => value is { } v ? (int)v : null;

    /// <summary>
    /// set_window_bounds' percentages in pixels (roadmap M6.7): of the work area or bounds of monitor
    /// <paramref name="monitorIndex"/>, or of the window's own monitor; each value given is converted, as a pair. A monitor
    /// index that is no monitor is InvalidMonitorIndex, a rectangle under one pixel InvalidArguments. Null when converted.
    /// </summary>
    private WindowOperationResult? PercentBounds(nint hwnd, int? monitorIndex, RelativeArea relativeTo, double? x, double? y,
        double? width, double? height, out (int? X, int? Y, int? Width, int? Height) pixels)
    {
        pixels = default;
        var monitors = MonitorService().GetAllMonitors();
        var target = monitorIndex is { } index
            ? index >= 0 && index < monitors.Count ? monitors[index] : null
            : MonitorService().GetMonitorForWindow(hwnd);
        if (target == null)
        {
            return new WindowOperationResult(false, Error: monitorIndex is { } missing
                    ? WindowService.MonitorIndexMessage(missing, monitors.Count)
                    : $"The window's monitor could not be read. {NothingChanged} Pass monitor.",
                ErrorCode: nameof(InvalidMonitorIndex), Handle: hwnd.ToInt64());
        }

        var area = RelativeRectMath.AreaOf(target, relativeTo);
        try
        {
            if (x is { } left && y is { } top && width is { } w && height is { } h)
            {
                var rect = RelativeRectMath.FromPercent(area, left, top, w, h);
                pixels = (rect.X, rect.Y, rect.Width, rect.Height);
            }
            else if (x is { } px && y is { } py)
            {
                var (pointX, pointY) = RelativeRectMath.PointFromPercent(area, px, py);
                pixels = (pointX, pointY, null, null);
            }
            else
            {
                var (sizeWidth, sizeHeight) = RelativeRectMath.SizeFromPercent(area, width!.Value, height!.Value);
                pixels = (null, null, sizeWidth, sizeHeight);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Checked before, so only a size that floors to 0 px on this monitor is left
            return new WindowOperationResult(false,
                Error: $"width and height in percent give less than one pixel on monitor {target.Index}. {NothingChanged}",
                ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode, Handle: hwnd.ToInt64());
        }

        return null;
    }

    private IMonitorService MonitorService() =>
        _monitorService ?? throw new InvalidOperationException("No monitor service is registered.");

    /// <summary>
    /// set_window_bounds' change (roadmap M6.1): both pairs place the visible frame in one SetWindowPos; a position alone is
    /// move_window's move (it keeps the size) and a size alone resize_window's resize (it keeps the corner and, unless asked,
    /// the z-order), so each keeps its own error code and the second placement after a DPI change.
    /// </summary>
    private Func<WindowChangeResult> BoundsChange(nint hwnd, int? x, int? y, int? width, int? height, bool? bringToFront) =>
        (x, y, width, height) switch
        {
            ({ } left, { } top, { } w, { } h) => () => _windowService.SetWindowFrame(hwnd, left, top, w, h, bringToFront ?? true),
            ({ } left, { } top, _, _) => () => _windowService.MoveWindow(hwnd, left, top, bringToFront ?? true),
            (_, _, { } w, { } h) when bringToFront == true => () => _windowService.ResizeWindow(hwnd, w, h, bringToFront: true),
            (_, _, { } w, { } h) => () => _windowService.ResizeWindow(hwnd, w, h),
            _ => throw new InvalidOperationException("BoundsRefusal lets only a whole pair through.")
        };

    [McpServerTool(Name = "set_window_state", Title = "Minimize, Maximize or Restore Window", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the window state (minimize, maximize, restore). success is true only when the window was seen in that state " +
                 "afterwards (within 300 ms); state reports the state it is in (minimized, maximized or normal)." + GeometryResult +
                 WindowChangeErrors +
                 " StateChangeFailed: the window did not reach the state (the app refused or delayed it, or it runs as administrator). " +
                 "An unknown state fails with InvalidState and the allowed values.")]
    public async Task<WindowOperationResult> SetWindowState(
        [Description("Desired state: 'minimize', 'maximize' or 'restore' ('minimized', 'maximized' and 'normal' are accepted too). " +
                     "Restore returns a minimized window to its previous state (normal, or maximized if it was maximized before) and makes a maximized window normal.")]
        WindowStateRequest state,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description(SettleDescription)]
        int settleMs = 0,
        CancellationToken cancellationToken = default)
    {
        var hwnd = Target(handle, window);
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SetWindowState(hwnd, state), settleMs);
        }
    }

    [McpServerTool(Name = "focus_window", Title = "Focus Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Bring a window to the foreground and verify it. Works around the Windows foreground lock without sending keys. " +
        "Returns isForeground (also as success), method (already, setForeground, attachThreadInput or inputUnlock), " +
        "the window actually in front (foregroundHandle, foregroundTitle, foregroundProcess) and blockingPopup, a dialog of the " +
        "window that takes keyboard input instead of it. inMenuMode: a menu is still open, so typed letters would pick menu items. " +
        "errorCode: FocusFailed, NotResponding, Cloaked, WindowHidden (the window is hidden, so keys would go to a window the user cannot see), " +
        "TargetDeniedByPolicy (the user's policy denies the app), PausedByUser or InvalidHandle. " +
        "The foreground can change again before your next call, so confirm with get_foreground_window immediately before keyboard or mouse input.")]
    public async Task<FocusResult> FocusWindow(
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        CancellationToken cancellationToken = default)
    {
        var hwnd = Target(handle, window);
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            return FocusResult.From(_foregroundService.EnsureForeground(hwnd));
        }
    }

    /// <summary>close_windows_batch and close_application close at most this many windows without confirm=true.</summary>
    public const int MaxCloseBatchWithoutConfirm = 3;

    /// <summary>How long close_window and close_windows_batch wait for the windows to close by default (roadmap M2.5).</summary>
    public const int DefaultCloseWaitMs = 2000;

    /// <summary>The longest wait close_window, close_windows_batch and close_application accept.</summary>
    public const int MaxCloseWaitMs = 30000;

    /// <summary>How long close_application waits for its windows to close and its processes to exit by default.</summary>
    public const int DefaultApplicationCloseTimeoutMs = 5000;

    /// <summary>How long close_application waits at most for its processes to exit once their windows are gone.</summary>
    private static readonly TimeSpan MaxExitWait = TimeSpan.FromSeconds(2);

    private const string ForceDescription =
        "Close even a protected window: the desktop shell (Program Manager, the taskbars) or the agent's own host " +
        "(the terminal, editor or chat app this server was started from). Default false: such windows are refused " +
        "with errorCode ProtectedWindow. Set it only when the user explicitly asked to close that window. The user is asked " +
        "first when the client can ask (elicitation; ConfirmationDeclined), else confirm=true is needed too (ConfirmationRequired).";

    private const string ForceConfirmDescription =
        "With force=true, must also be true to close a protected window when the MCP client cannot ask the user " +
        "(elicitation); without it such a window is not closed (errorCode ConfirmationRequired). When the client can ask, " +
        "the user's answer decides and confirm does not count. Set it only when the user explicitly asked to close that window.";

    private const string WaitDescription =
        "How long to wait for the window to close, in milliseconds (0-30000, default 2000). 0 only sends the close message " +
        "(status requested). The wait ends early when the window closes or shows a prompt.";

    /// <summary>
    /// What the agent must do about a prompt a close left open. Every PromptShown text and description carries it: not
    /// every client loads the skill, and answering 'Save changes?' with No or Don't Save destroys the user's work. Once the
    /// user chose, the prompt is answered by its own handle, since respond_to_dialog refuses a press asked of an owned window
    /// (which it takes for a dialog) that a modal dialog of its own disables (BlockedByModal); an in-window dialog has no
    /// window of its own, so its button is pressed with element_action.
    /// </summary>
    internal const string PromptAdvice =
        "Nothing was answered. Do not answer it yourself: show the user the prompt's text and buttons and ask which to " +
        "choose (No or Don't Save discards their unsaved work), then answer it as they say with respond_to_dialog, handle: " +
        "prompt.handle (inWindowDialog, which has no window of its own: element_action on its button).";

    private const string StatusDescription =
        "status: closed; hidden (the app hid the window it showed instead of closing it, for example into the notification " +
        "area; it still runs: use close_application or kill_process to end it); stillOpen (errorCode StillOpen: the app is " +
        "busy or ignores the request, or the window was already hidden or on another virtual desktop); promptShown (errorCode " +
        "PromptShown: a prompt such as 'Save changes?' waits; prompt gives its handle, title, className, text, buttons and " +
        "kind (inWindowDialog: drawn inside the window). " + PromptAdvice + "); requested " +
        "(waitMs 0: sent, not checked). success is true for closed, hidden and requested.";

    [McpServerTool(Name = "close_window", Title = "Close Window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close a window gracefully by sending WM_CLOSE, then wait up to waitMs and report what happened. " + StatusDescription + " " +
                 "Refuses the desktop shell and the agent's own host window (terminal, editor or chat app; errorCode ProtectedWindow) unless force is true; " +
                 "then, when the client can ask the user, the user is asked first (ConfirmationDeclined, or HandleMismatch when the " +
                 "window changed hands meanwhile), and when it cannot, confirm must be true too (ConfirmationRequired). It " +
                 "refuses an app the user's policy denies (TargetDeniedByPolicy; force does not override it), " +
                 "and reports AccessDenied when Windows blocks the message (the window belongs to an app running as administrator) " +
                 "and NotResponding when the app's message queue is full (it is hung). " +
                 "InvalidArguments: waitMs is outside 0-30000. To close every window of an app, use close_application.")]
    public async Task<CloseWindowResult> CloseWindow(
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description(ForceDescription)]
        bool force = false,
        [Description(WaitDescription)]
        int waitMs = DefaultCloseWaitMs,
        [Description(ForceConfirmDescription)]
        bool confirm = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (WaitRefusal(waitMs, nameof(waitMs)) is { } invalid)
        {
            return new CloseWindowResult(false, Error: invalid, ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode, Handle: handle);
        }

        // Nothing while the user has paused input, not even resolving: a pause is PausedByUser, never a selector error
        InputGateToolFilter.ThrowIfPaused(_session);

        // The selector is resolved before the gate; the close guards check the window it names as they check any handle
        var hwnd = WindowTargeting.Require(_windowService, handle, window, requireNamedWindow: true);
        var reported = hwnd.ToInt64();
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new CloseWindowResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle), Handle: reported);
        }

        // Only the close message is sent inside the input gate; the wait for the window runs outside it, so a slow app or a
        // prompt does not hold up every input and window call for up to waitMs
        var shownBefore = waitMs == 0 ? EmptyHandles : _closeVerifier.ShownWindows([hwnd]);

        // force overrides the protection of the desktop shell and the agent's own host only with the user's yes when the
        // client can ask, and only with confirm=true when it cannot (roadmap M2.F1: two explicit decisions either way): the
        // window is first closed without force, and only a ProtectedWindow refusal asks the user or needs confirm
        var canAsk = UserConfirmation.CanAsk(server);
        var forceNow = force && !canAsk && confirm;
        var checkBeforeForce = force && !forceNow;
        WindowCloseResult sent;
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // The window the selector named must still be that window: it may have changed while the call waited
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            sent = _windowService.CloseWindow(hwnd, forceNow);
        }

        if (checkBeforeForce && !sent.Success && sent.ErrorCode == ProtectedWindow)
        {
            var why = ProtectionReason(sent.Error);
            if (!canAsk)
            {
                return new CloseWindowResult(false,
                    Error: $"{why} force closes it only with confirm=true too when the client cannot ask the user. Nothing was " +
                           "closed. Call again with force=true and confirm=true only if the user explicitly asked to close this window.",
                    ErrorCode: nameof(ConfirmationRequired), Handle: reported);
            }

            // Asked outside the gate: a waiting user must not hold up every input and window call
            var asked = _windowService.GetWindowInfo(hwnd);
            var answer = await UserConfirmation.AskAsync(server!, ForceQuestion(hwnd, asked, why),
                "Close this protected window", _answerTimeout, cancellationToken).ConfigureAwait(false);
            if (!answer.Confirmed)
            {
                return new CloseWindowResult(false,
                    Error: $"{answer.Reason} {why} force closes it only with the user's yes. Nothing was closed. Ask the user how " +
                           "to continue.",
                    ErrorCode: nameof(ConfirmationDeclined), Handle: reported);
            }

            InputGateToolFilter.ThrowIfPaused(_session);
            using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
            {
                WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);

                // The user agreed to close that window of that process; a handle reused meanwhile is not closed
                if (HandleMismatchRefusal(reported, new Dictionary<nint, int?> { [hwnd] = asked?.ProcessId }) is { } mismatch)
                {
                    return new CloseWindowResult(false, Error: mismatch.Error, ErrorCode: mismatch.ErrorCode, Handle: reported);
                }

                sent = _windowService.CloseWindow(hwnd, force: true);
            }
        }

        if (!sent.Success)
        {
            return new CloseWindowResult(false, Error: sent.Error, ErrorCode: sent.ErrorCode.ToString(), Handle: reported);
        }

        if (waitMs == 0)
        {
            return new CloseWindowResult(true, CloseNames.Of(CloseStatus.Requested), Handle: reported);
        }

        var observed = await _closeVerifier.WaitForCloseAsync([hwnd], shownBefore, TimeSpan.FromMilliseconds(waitMs), cancellationToken)
            .ConfigureAwait(false);
        var outcome = Outcome(observed[0], waitMs);
        return new CloseWindowResult(outcome.Success, outcome.Status, outcome.Prompt, outcome.Error, outcome.ErrorCode, reported);
    }

    [McpServerTool(Name = "close_windows_batch", Title = "Close Windows (Batch)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close multiple windows gracefully in a single batch operation, then wait up to waitMs for all of them and report each " +
                 "window's status as close_window does (closed, hidden, stillOpen, promptShown with its prompt, or requested), or notSent " +
                 "for a window refused or failed before its close message (see its errorCode), as close_application reports it. A prompt " +
                 "is never answered: ask the user which button to choose, and do not answer it yourself (then answer it as close_window says). " +
                 "More than 3 windows need a confirmation, and so does force=true on a protected window when the MCP client can ask " +
                 "the user (elicitation): the user is asked, with each window's title, process and PID, and nothing is closed unless " +
                 "they confirm (errorCode ConfirmationDeclined). When the client cannot ask, more than 3 windows, and force=true on a " +
                 "protected window, need confirm=true (ConfirmationRequired; nothing is closed). A window that changed hands after the user confirmed is not closed " +
                 "(HandleMismatch). Protected windows (the desktop shell, the agent's own host) " +
                 "are refused per window with errorCode ProtectedWindow unless force is true, and apps the user's policy denies with TargetDeniedByPolicy; " +
                 "the other windows are still closed. An item that is null, or names its window by both handle and window, fails alone " +
                 "with InvalidArguments; an item whose selector names no single window fails alone with WindowNotFound, " +
                 "AmbiguousWindow (with candidates) or HandleMismatch. InvalidArguments (the whole call): waitMs is outside 0-30000.")]
    public async Task<BatchWindowResult> CloseWindowsBatch(
        [Description("Array of windows to close, each with handle or window (a selector, as close_window takes it)")]
        WindowCloseRequest[] windows,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        [Description("Must be true to close more than 3 windows in one batch, or (with force=true) a protected window, when the client cannot ask the user. Check the list (find_windows) first: closing many windows at once can lose unsaved work.")]
        bool confirm = false,
        [Description(ForceDescription + " Applies to every window in the batch.")]
        bool force = false,
        [Description("How long to wait for the windows to close after the last close message, in milliseconds (0-30000, default 2000); " +
                     "every window is waited for at the same time. 0 only sends the close messages (status requested).")]
        int waitMs = DefaultCloseWaitMs,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        RequireItems(windows, nameof(windows));
        if (WaitRefusal(waitMs, nameof(waitMs)) is { } invalid)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, invalid);
        }

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        // Each item's window, resolved before the question and the gate (roadmap M2.4); an item that names no single window
        // fails alone
        var items = ResolveItems(windows, nameof(windows), "a handle or a window", w => w.Handle, w => w.Window, requireNamedWindow: true);

        // force overrides the protection of the desktop shell and the agent's own host only with the user's yes when the
        // client can ask, and only with confirm=true when it cannot (roadmap M2.F1), whatever the number of windows: a
        // window protected now needs that confirmation, and force is passed on only when the user was asked (or, when they
        // cannot be, confirm is true)
        var canAsk = UserConfirmation.CanAsk(server);
        var protectedNow = force && (canAsk || !confirm)
            ? items.Where(t => t.Refusal == null && _windowService.CheckClose(t.Handle, force: false) is { ErrorCode: ProtectedWindow })
                .Select(t => t.Handle).ToHashSet()
            : [];
        var forceOverrides = protectedNow.Count > 0;
        Dictionary<nint, int?>? confirmedOwners = null;
        if (windows.Length > MaxCloseBatchWithoutConfirm || forceOverrides)
        {
            if (canAsk)
            {
                // Ask the user, not the model, and before the input gate: a waiting user must not hold up every input call
                var targets = items.Where(t => t.Refusal == null).Select(t => (t.Handle, Window: _windowService.GetWindowInfo(t.Handle)))
                    .ToList();
                var answer = await UserConfirmation.AskAsync(server!, CloseQuestion(targets, force, protectedNow), $"Close these {targets.Count} windows",
                    _answerTimeout, cancellationToken).ConfigureAwait(false);
                if (!answer.Confirmed)
                {
                    throw new ToolErrorException(nameof(ConfirmationDeclined),
                        $"{answer.Reason} Nothing was closed. Ask the user how to continue.");
                }

                // Every handle the user was asked about, with its owner then (null: it was no window)
                confirmedOwners = targets.GroupBy(t => t.Handle).ToDictionary(g => g.Key, g => g.First().Window?.ProcessId);
            }
            else if (!confirm)
            {
                throw new ToolErrorException(nameof(ConfirmationRequired),
                    BatchConfirmationRequired(windows.Length, items.Select(t => t.Handle).Where(protectedNow.Contains).Distinct().ToList()));
            }
        }

        // Which windows the user could see before the close: only those can be reported hidden
        var shownBefore = waitMs == 0
            ? EmptyHandles
            : _closeVerifier.ShownWindows(items.Where(t => t.Refusal == null).Select(t => t.Handle).ToList());

        List<WindowResultItem?> refusals;
        IReadOnlyList<WindowCloseResult> results;
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // A window the user was asked about must still belong to the same process; a reused handle is not closed
            refusals = items.Select((t, i) => t.Refusal ??
                (confirmedOwners == null ? null : HandleMismatchRefusal(t.Handle.ToInt64(), confirmedOwners)) ??
                ItemRecheck(i, t.Handle, windows[i].Window)).ToList();
            var handles = items.Where((_, i) => refusals[i] == null).Select(t => t.Handle).ToList();

            var effectiveForce = force && (canAsk ? confirmedOwners != null : confirm);
            results = await _windowService.CloseWindowsBatchAsync(handles, effectiveForce, delayBetweenMs, cancellationToken).ConfigureAwait(false);
        }

        // The wait runs outside the gate, for every window at once
        var sent = results.Where(r => r.Success).Select(r => r.Handle).ToList();
        var observed = waitMs == 0 || sent.Count == 0
            ? new Dictionary<nint, CloseObservation>()
            : (await _closeVerifier.WaitForCloseAsync(sent, shownBefore, TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false))
                .GroupBy(o => o.Handle).ToDictionary(g => g.Key, g => g.First());

        // A window that got no close message (refused, or the send failed) is notSent, as close_application reports it
        return Summary(windows.Length, InOrder(refusals.Select(r => r == null ? null : r with { Status = NotSent }).ToList(), results.Select(r =>
        {
            if (!r.Success)
            {
                return new WindowResultItem(r.Handle.ToInt64(), false, Error: r.Error, ErrorCode: r.ErrorCode.ToString(), Status: NotSent);
            }

            if (!observed.TryGetValue(r.Handle, out var observation))
            {
                return new WindowResultItem(r.Handle.ToInt64(), true, Status: CloseNames.Of(CloseStatus.Requested));
            }

            var outcome = Outcome(observation, waitMs);
            return new WindowResultItem(r.Handle.ToInt64(), outcome.Success, Error: outcome.Error, ErrorCode: outcome.ErrorCode,
                Status: outcome.Status, Prompt: outcome.Prompt);
        })));
    }

    [McpServerTool(Name = "close_application", Title = "Close Application", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close an application gracefully: send WM_CLOSE to each of its windows the user can switch to (the Alt+Tab set: visible, " +
                 "not owned, not tool windows; never hidden helper or tray windows), then wait up to timeoutMs. It never forces: apps may ask " +
                 "to save, and kill_process is the way to end an app that will not close. Name the app by exactly one of handle (one of its " +
                 "windows), window (a selector for one of its windows), processId or processName (every process with that name that has such a window). A packaged (UWP) app is closed " +
                 "through its frame window, and its processId is the app's own (as find_windows reports it), not ApplicationFrameHost's; " +
                 "a frame whose app cannot be told is closed only by its own handle, alone. Of the shell's process (explorer) only File " +
                 "Explorer windows are closed. Returns status closed (every window on the current virtual desktop gone; exited says whether " +
                 "the processes ended, " +
                 "runningProcessIds lists those that keep running, for example in the notification area or with otherDesktopWindows: its " +
                 "windows on other virtual desktops, which are not closed), promptShown (errorCode " +
                 "PromptShown, promptDetected, and prompt with its handle, title, text and buttons. " + PromptAdvice + ") or stillOpen " +
                 "(errorCode StillOpen), with windows (each window's status) and remainingWindows; timeoutMs 0 only sends (status " +
                 "requested). Refused before anything is sent, with nothing closed: NoClosableWindow (no such window), " +
                 "ProtectedWindow (the agent's own host or the desktop shell; there is no force), TargetDeniedByPolicy, InvalidHandle, " +
                 "InvalidArguments. More than 3 windows need a confirmation as close_windows_batch does (ConfirmationDeclined, " +
                 "ConfirmationRequired), and a window that changed hands meanwhile is not closed (HandleMismatch).")]
    public async Task<CloseApplicationResult> CloseApplication(
        [Description("A window of the application (as integer); its process is closed")]
        long? handle = null,
        [Description("Alternative to handle: a window of the application as a window selector (find_windows' filters), resolved to " +
                     "exactly one window; its process is closed.")]
        WindowSelector? window = null,
        [Description("The application's process ID")]
        int? processId = null,
        [Description("The application's process name, with or without .exe (e.g. 'notepad'); matched exactly, ignoring case")]
        string? processName = null,
        [Description("How long to wait for the windows to close and the processes to exit, in milliseconds (0-30000, default 5000). " +
                     "0 only sends the close messages (status requested).")]
        int timeoutMs = DefaultApplicationCloseTimeoutMs,
        [Description("Must be true to close more than 3 windows when the client cannot ask the user. Check the windows (find_windows) first.")]
        bool confirm = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (WaitRefusal(timeoutMs, nameof(timeoutMs)) is { } invalidWait)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, invalidWait);
        }

        var name = ApplicationName(handle, window != null, processId, processName);

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        // A window selector names the application by one of its windows, as handle does (roadmap M2.4); it is resolved
        // before anything is asked or sent, and the window it names goes through the same checks as a handle
        if (window != null)
        {
            handle = WindowTargeting.Require(_windowService, null, window, requireNamedWindow: true).ToInt64();
        }

        var appWindows = _windowService.GetApplicationWindows();
        var application = TargetApplication(handle, processId, name, appWindows);
        var processIds = application.ProcessIds;
        var targets = appWindows.Where(application.Includes).ToList();

        // The audit log records each window with its process, as it is before the close
        AuditScope.NoteWindows(targets);
        if (targets.Count == 0)
        {
            return ApplicationRefusal(processIds, nameof(ProcessErrorCode.NoClosableWindow), application.NamesFrameHost
                ? $"{Describe(handle, processId, name)} is ApplicationFrameHost, which hosts the frame windows of every packaged " +
                  "(UWP) app, so closing it would close apps nobody named. Nothing was sent. Pass the handle of the one frame " +
                  "window to close instead (find_windows reports it)."
                : $"{Describe(handle, processId, name)} has no window the user can switch to (no visible, unowned window in Alt+Tab), " +
                  "so there is nothing to close gracefully: it may run without windows, only in the notification area, or only on another " +
                  "virtual desktop (windows there are closed only from that desktop). Nothing was " +
                  "sent. Check with find_windows or list_processes; kill_process ends a process without a window.");
        }

        // A close that would be refused is refused for the whole app before anything is asked or sent
        foreach (var target in targets)
        {
            if (_windowService.CheckClose(target.Handle, force: false) is { ErrorCode: not InvalidHandle } refused)
            {
                return ApplicationRefusal(processIds, refused.ErrorCode.ToString(),
                    $"{refused.Error} Nothing was closed: close_application closes all of an application's windows or none.");
            }
        }

        // The process each window belonged to when it was listed: a window that closed while the call waited (for the user or
        // the gate) and whose handle another process's window got is not closed (HandleMismatch), whether or not the user was asked
        var owners = targets.ToDictionary(t => t.Handle, t => (int?)t.ProcessId);
        if (targets.Count > MaxCloseBatchWithoutConfirm)
        {
            if (UserConfirmation.CanAsk(server))
            {
                var answer = await UserConfirmation.AskAsync(server!, ApplicationQuestion(targets), $"Close these {targets.Count} windows",
                    _answerTimeout, cancellationToken).ConfigureAwait(false);
                if (!answer.Confirmed)
                {
                    throw new ToolErrorException(nameof(ConfirmationDeclined),
                        $"{answer.Reason} Nothing was closed. Ask the user how to continue.");
                }

            }
            else if (!confirm)
            {
                throw new ToolErrorException(
                    nameof(ConfirmationRequired),
                    $"close_application would close {targets.Count} windows; more than {MaxCloseBatchWithoutConfirm} require confirm=true. " +
                    "Nothing was closed. Check the windows (find_windows) and call again with confirm=true.");
            }
        }

        var items = new Dictionary<nint, ClosedWindowItem>();
        var sent = new List<nint>();
        using (await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false))
        {
            // The window the selector named the application by must still be that window, or nothing is closed
            if (window != null && handle is { } named)
            {
                WindowTargeting.ThrowIfChanged(_windowService, window, (nint)named);
            }

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HandleMismatchRefusal(target.Handle, owners) is { } mismatch)
                {
                    items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, NotSent, mismatch.Error, mismatch.ErrorCode);
                    continue;
                }

                // One window that fails does not stop the others: some may already have their close message, and the result
                // must still say what became of each
                WindowCloseResult result;
                try
                {
                    result = await SendApplicationCloseAsync(target, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = WindowCloseResult.Failed(target.Handle, CloseFailed, $"The close could not be sent: {ex.Message}");
                }

                if (result.Success)
                {
                    sent.Add(target.Handle);
                }
                else
                {
                    items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, NotSent, result.Error, result.ErrorCode.ToString());
                }
            }
        }

        // The waits run outside the gate. Every target was shown (Alt+Tab has only visible, uncloaked windows), so one the
        // app hid can be reported hidden
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var observed = sent.Count == 0 || timeoutMs == 0
            ? []
            : await _closeVerifier.WaitForCloseAsync(sent, targets.Select(t => t.Handle).ToHashSet(), TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken).ConfigureAwait(false);
        foreach (var observation in observed)
        {
            var target = targets.First(t => t.Handle == observation.Handle);
            var outcome = Outcome(observation, timeoutMs);
            items[observation.Handle] = new ClosedWindowItem(target.Handle, target.Title, outcome.Status, outcome.Error, outcome.ErrorCode);
        }

        if (timeoutMs == 0)
        {
            foreach (var target in targets.Where(t => sent.Contains(t.Handle)))
            {
                items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, CloseNames.Of(CloseStatus.Requested));
            }
        }

        var prompt = observed.Select(o => o.Prompt).FirstOrDefault(p => p != null);
        var remaining = _windowService.GetApplicationWindows().Where(application.Includes)
            .Select(w => new RemainingWindowDto(w.Handle.ToInt64(), w.Title, w.ProcessId, w.ProcessName)).ToList();
        var windowItems = targets.Select(t => items[t.Handle]).ToList();
        var pids = processIds.Order().ToList();

        // Windows of the application on other virtual desktops are not closed (Alt+Tab shows only this desktop's): they are
        // reported, so a status closed is never read as 'nothing of the application is left open'
        var otherDesktops = (_windowService.GetApplicationWindowsOnOtherDesktops() ?? []).Where(application.Includes)
            .Select(w => new RemainingWindowDto(w.Handle.ToInt64(), w.Title, w.ProcessId, w.ProcessName)).ToList();
        CloseApplicationResult Result(CloseApplicationResult result) =>
            otherDesktops.Count == 0 ? result : result with { OtherDesktopWindows = otherDesktops };

        if (prompt != null)
        {
            return Result(new CloseApplicationResult(false, CloseNames.Of(CloseStatus.PromptShown), pids, windowItems, remaining, true,
                ClosePromptDto.From(prompt),
                Error: $"The application did not close: it shows a prompt (see prompt) that waits for an answer. {PromptAdvice} " +
                       "Then call close_application again if it is still open.",
                ErrorCode: nameof(PromptShown)));
        }

        if (timeoutMs == 0 && sent.Count > 0)
        {
            // Only sent, as asked: nothing was checked
            return Result(new CloseApplicationResult(true, CloseNames.Of(CloseStatus.Requested), pids, windowItems, remaining, false));
        }

        if (remaining.Count > 0)
        {
            return Result(new CloseApplicationResult(false, CloseNames.Of(CloseStatus.StillOpen), pids, windowItems, remaining, false,
                Error: $"{remaining.Count} window{(remaining.Count == 1 ? " is" : "s are")} still open after {timeoutMs} ms, without a " +
                       "prompt (see windows for why: a close Windows refused, or an app that is busy or ignores it). Check them with " +
                       "find_windows; kill_process ends an app that will not close.",
                ErrorCode: nameof(StillOpen)));
        }

        if (pids.Count == 0)
        {
            // A frame closed by its own handle: its app cannot be told, so there is no process to wait for
            return Result(new CloseApplicationResult(true, CloseNames.Of(CloseStatus.Closed), pids, windowItems, remaining, false));
        }

        // A process with a window on another desktop keeps running: it is not waited for
        var staying = otherDesktops.Select(w => w.ProcessId).Where(pids.Contains).ToHashSet();
        var exitWait = TimeSpan.FromMilliseconds(timeoutMs) - waited.Elapsed;
        IReadOnlyList<int> running = staying.Count == pids.Count
            ? []
            : await _closeVerifier.WaitForProcessesToExitAsync(pids.Where(p => !staying.Contains(p)).ToList(),
                exitWait > MaxExitWait ? MaxExitWait : exitWait > TimeSpan.Zero ? exitWait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        var stillRunning = running.Concat(staying).Distinct().Order().ToList();
        return Result(new CloseApplicationResult(true, CloseNames.Of(CloseStatus.Closed), pids, windowItems, remaining, false,
            Exited: stillRunning.Count == 0, RunningProcessIds: stillRunning.Count == 0 ? null : stillRunning));
    }

    /// <summary>
    /// Sends close_application's close to one window: WM_CLOSE, or for a packaged app's frame that does not take it,
    /// WindowPattern.Close. A WindowPattern.Close that did not return in time may still close the window, so it counts as
    /// sent and the wait reports what became of it.
    /// </summary>
    private async Task<WindowCloseResult> SendApplicationCloseAsync(WindowInfo target, CancellationToken cancellationToken)
    {
        var result = _windowService.CloseWindow(target.Handle, force: false);
        if (result.Success || result.ErrorCode != CloseFailed || !WindowClassification.IsApplicationFrameWindow(target.ClassName))
        {
            return result;
        }

        // A packaged app's frame that did not take the message (a full queue is NotResponding, not CloseFailed: a hung app
        // would not answer UI Automation either), asked through its Window pattern instead
        return await _closeVerifier.CloseWithUiAutomationAsync(target.Handle, cancellationToken).ConfigureAwait(false) is false
            ? result
            : WindowCloseResult.Sent(target.Handle);
    }

    /// <summary>The status of a window close_application did not send a close message to.</summary>
    private const string NotSent = "notSent";

    /// <summary>The success, status, prompt and error of a window after its close, as the close tools report it.</summary>
    private static (bool Success, string Status, ClosePromptDto? Prompt, string? Error, string? ErrorCode) Outcome(CloseObservation observation, int waitMs) =>
        observation.Status switch
        {
            CloseStatus.Closed or CloseStatus.Hidden or CloseStatus.Requested => (true, CloseNames.Of(observation.Status), null, null, null),
            CloseStatus.PromptShown => (false, CloseNames.Of(CloseStatus.PromptShown), ClosePromptDto.From(observation.Prompt),
                $"The window did not close: it shows a prompt (see prompt) that waits for an answer. {PromptAdvice}", nameof(PromptShown)),
            _ => (false, CloseNames.Of(CloseStatus.StillOpen), null,
                $"The close message was sent, but the window was still open after {waitMs} ms and shows no prompt: the app may be busy " +
                "or slow, or ignore the request (a window that was already hidden or on another virtual desktop counts as open too). " +
                "Check it with find_windows, or wait longer.", nameof(StillOpen))
        };

    /// <summary>No windows: the shown-before set of a close that is not waited for.</summary>
    private static readonly IReadOnlySet<nint> EmptyHandles = new HashSet<nint>();

    /// <summary>InvalidArguments text for a wait outside 0 to <see cref="MaxCloseWaitMs"/>, or null.</summary>
    private static string? WaitRefusal(int waitMs, string name) =>
        waitMs is >= 0 and <= MaxCloseWaitMs
            ? null
            : $"{name} must be between 0 and {MaxCloseWaitMs} milliseconds, but it is {waitMs}. Nothing was closed.";

    /// <summary>
    /// close_application's one target, checked: exactly one of handle, window, processId and processName, and the name
    /// without ".exe". Returns the name (null unless processName was given).
    /// </summary>
    private static string? ApplicationName(long? handle, bool window, int? processId, string? processName)
    {
        var given = (handle.HasValue ? 1 : 0) + (window ? 1 : 0) + (processId.HasValue ? 1 : 0) + (processName != null ? 1 : 0);
        if (given != 1)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"Pass exactly one of handle, window, processId and processName ({given} given). Nothing was closed.");
        }

        if (processId is <= 0)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"processId must be a positive process ID, but it is {processId}. Nothing was closed.");
        }

        if (processName == null)
        {
            return null;
        }

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length > 0
            ? name
            : throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, "processName is empty. Nothing was closed.");
    }

    /// <summary>
    /// The application close_application closes: its processes, and which application windows belong to it. A packaged
    /// app's frame whose app cannot be told (it is minimized or suspended, and its title is not unique) is reported under
    /// ApplicationFrameHost, which hosts the frames of every packaged app: such a frame is closed only when its own handle
    /// names it, and then alone, so one call never closes packaged apps nobody named.
    /// </summary>
    private TargetedApplication TargetApplication(long? handle, int? processId, string? name, IReadOnlyList<WindowInfo> appWindows)
    {
        if (handle is { } window)
        {
            // The handle's own process (for a UWP frame, the app's), whether or not the window itself is in Alt+Tab
            var info = _windowService.GetWindowInfo((nint)window)
                       ?? throw new ToolErrorException(nameof(InvalidHandle),
                           $"{WindowService.InvalidHandleMessage}: {window} is no window. Nothing was closed.");
            return IsUnattributedFrame(info)
                ? new TargetedApplication([], w => w.Handle == info.Handle, NamesFrameHost: false)
                : ForProcesses([info.ProcessId], namesFrameHost: false);
        }

        Func<WindowInfo, bool> named = processId is { } id
            ? w => w.ProcessId == id
            : w => string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        var ids = processId is { } pid
            ? new HashSet<int> { pid }
            : appWindows.Where(w => named(w) && !IsUnattributedFrame(w)).Select(w => w.ProcessId).ToHashSet();
        return ForProcesses(ids, appWindows.Any(w => named(w) && IsUnattributedFrame(w)));

        static TargetedApplication ForProcesses(HashSet<int> ids, bool namesFrameHost) =>
            new(ids, w => ids.Contains(w.ProcessId) && !IsUnattributedFrame(w), namesFrameHost);
    }

    /// <summary>A packaged app's frame whose app could not be told: it is reported under ApplicationFrameHost.</summary>
    private static bool IsUnattributedFrame(WindowInfo window) =>
        WindowClassification.IsApplicationFrameWindow(window.ClassName) && window.HostProcessId == null;

    /// <param name="ProcessIds">The processes closed; empty for one frame closed by its own handle.</param>
    /// <param name="Includes">Whether an application window belongs to the application.</param>
    /// <param name="NamesFrameHost">The process named is ApplicationFrameHost: it has frames of packaged apps that cannot be told apart.</param>
    private sealed record TargetedApplication(HashSet<int> ProcessIds, Func<WindowInfo, bool> Includes, bool NamesFrameHost);

    private static string Describe(long? handle, int? processId, string? name) =>
        handle is { } h ? $"The application of window {h}" : processId is { } id ? $"Process {id}" : $"No process named '{name}'";

    private static CloseApplicationResult ApplicationRefusal(IEnumerable<int> processIds, string errorCode, string error) =>
        new(false, null, processIds.Order().ToList(), [], [], false, Error: error, ErrorCode: errorCode);

    /// <summary>The question close_application asks the user: the application's windows, with their titles, processes and PIDs.</summary>
    private static string ApplicationQuestion(IReadOnlyList<WindowInfo> targets)
    {
        var apps = string.Join(", ", targets.Select(t => $"{t.ProcessName} (PID {t.ProcessId})").Distinct());
        var lines = targets.Take(20).Select((w, i) =>
            $"{i + 1}. {UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId})");
        var more = targets.Count > 20 ? $"\n... and {targets.Count - 20} more" : string.Empty;
        return $"The AI agent asks to close {apps}: {targets.Count} windows. Each may ask to save your work, or close without asking.\n" +
               string.Join("\n", lines) + more + "\nAllow it?";
    }

    [McpServerTool(Name = "snap_window", Title = "Snap Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap a window to predefined screen positions (like Windows Snap Assist). Supported positions: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen; any other position fails with errorCode InvalidSnapPosition. " +
                 "The visible frames of snapped windows tile the work area exactly: no gap from the invisible borders, no pixel lost on odd sizes." +
                 PlacementRules + GeometryResult + WindowChangeErrors +
                 MonitorErrors + " MoveFailed: Windows did not place it, " +
                 "or it did not return to normal from minimized or maximized.")]
    public async Task<WindowOperationResult> SnapWindow(
        [Description("Snap position: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen")]
        SnapPosition position,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description(MonitorSelectorDescriptions.Monitor + " Default: the window's own.")]
        MonitorSelector? monitor = null,
        [Description(BringToFrontDescription)]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0,
        CancellationToken cancellationToken = default)
    {
        var hwnd = Target(handle, window);
        var choice = MonitorTargeting.Choose(_monitorService, monitor, allowAll: false, NothingChanged);
        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return refused;

            // A named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var index = MonitorTargeting.Current(_monitorService, choice, NothingChanged);
            return Change(hwnd, () => _windowService.SnapWindow(hwnd, position, index, bringToFront), settleMs);
        }
    }

    [McpServerTool(Name = "move_window_to_monitor", Title = "Move Window to Monitor", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move a window to a specific monitor, optionally positioning it. The window keeps its normal size, made smaller when " +
                 "needed so that it fits the monitor's work area. To a monitor with other scaling it is moved first, and sized after " +
                 "the app has rescaled itself. success requires the window to be on that monitor afterwards." + PlacementRules +
                 GeometryResult + WindowChangeErrors + MonitorErrors +
                 " MoveFailed: Windows did not place it, " +
                 "it did not return to normal from minimized or maximized, or it is not on that monitor afterwards (with 'maximize': not " +
                 "maximized there). An unknown positioning fails with InvalidPositioning.")]
    public async Task<WindowOperationResult> MoveWindowToMonitor(
        [Description(MonitorSelectorDescriptions.Monitor)]
        MonitorSelector monitor,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("Position on the monitor's work area: 'center' (default), 'topleft', 'topright', 'bottomleft', 'bottomright' (each at the window's normal size, fitted to the work area), 'maximize' (moved there, then maximized) or 'restore' (normal size, at the work area's top-left corner)")]
        MonitorPositioning positioning = MonitorPositioning.Center,
        [Description(BringToFrontDescription + ". Positioning 'maximize' ignores it and activates the window: Windows has no maximize that does not activate.")]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var hwnd = Target(handle, window);
        var choice = MonitorTargeting.Choose(_monitorService, monitor, allowAll: false, NothingChanged);

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return refused;

            // A named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var index = MonitorTargeting.Current(_monitorService, choice, NothingChanged)!.Value;
            return Change(hwnd, () => _windowService.MoveWindowToMonitor(hwnd, index, positioning, bringToFront), settleMs);
        }
    }

    [McpServerTool(Name = "set_window_zorder", Title = "Set Window Z-Order", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Change a window's place in the z-order without moving or activating it (focus stays). mode: 'top' (above the " +
                 "other normal windows), 'bottom' (behind all), 'topmost' (pinned above all normal windows, also while another is " +
                 "active), 'notopmost' (unpinned), 'above' (directly above aboveHandle; topmost if that window is). get_server_info " +
                 "lists the windows this server pinned (pinnedWindows); unpin them when done. Returns isTopmost and zOrder (0 is the " +
                 "front) read back. errorCode: InvalidHandle, TargetDeniedByPolicy, AccessDenied (an app running as administrator), " +
                 "NotResponding, ZOrderNotApplied (the app changed it back).")]
    public async Task<ZOrderResult> SetWindowZOrder(
        [Description("Where to put the window")]
        ZOrderMode mode,
        [Description(HandleDescription)]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("With mode 'above': the window to place it above (a handle from find_windows)")]
        long? aboveHandle = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == ZOrderMode.Above && aboveHandle == null || mode != ZOrderMode.Above && aboveHandle != null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, mode == ZOrderMode.Above
                ? $"mode 'above' needs aboveHandle, the window to place it above. {NothingChanged}"
                : $"aboveHandle goes only with mode 'above'. {NothingChanged}");
        }

        var hwnd = Target(handle, window);
        if (aboveHandle == hwnd.ToInt64())
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"aboveHandle is the window itself. {NothingChanged} Pass another window to place it above.");
        }

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            WindowTargeting.ThrowIfChanged(_windowService, window, hwnd);
            if (Precheck(hwnd) is { } refused)
                return new ZOrderResult(false, hwnd.ToInt64(), Error: refused.Error, ErrorCode: refused.ErrorCode);

            WindowChangeResult result;
            try
            {
                result = _windowService.SetWindowZOrder(hwnd, mode, aboveHandle is { } above ? (nint)above : null);
            }
            catch (WindowNotRespondingException ex)
            {
                result = WindowChangeResult.Failed(hwnd, NotResponding, ex.Message);
            }

            var details = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowDetails(hwnd);
            return new ZOrderResult(result.Success, hwnd.ToInt64(), details?.IsTopmost, details?.ZOrder, result.Error, result.ErrorCodeName);
        }
    }

    [McpServerTool(Name = "set_windows_bounds_batch", Title = "Set Window Bounds (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move and resize multiple windows in a single batch operation, as set_window_bounds does: x, y, width and height are " +
                 "each window's visible frame. More efficient than multiple individual calls." + PlacementRules + BatchErrors +
                 " MoveFailed: Windows did not place the window, or it did not return to normal from minimized or maximized.")]
    public async Task<BatchWindowResult> SetWindowsBoundsBatch(
        [Description("Array of window placements, each with x, y, width, height (the visible frame), the window as handle or window " +
                     "(a selector, as the single-window tools take it), and optional bringToFront (raises the window without activating it)")]
        WindowBoundsPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));
        InputGateToolFilter.ThrowIfPaused(_session);

        // Each item's window, resolved before the gate (roadmap M2.4); an item that names no single window fails alone
        var items = ResolveItems(placements, nameof(placements), "x, y, width, height and a handle or window", p => p.Handle, p => p.Window);
        var invalid = items.Select((t, i) => t.Refusal ?? ItemSizeRefusal(t.Handle.ToInt64(), placements[i].Width, placements[i].Height))
            .ToList();

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // Items for windows the policy denies get their result here; the others go to the service
            var refusals = invalid.Select((r, i) => r ?? ItemRecheck(i, items[i].Handle, placements[i].Window) ??
                BatchPolicyRefusal(items[i].Handle.ToInt64())).ToList();
            var servicePlacements = new List<(nint, int, int, int, int, bool)>();
            for (var i = 0; i < placements.Length; i++)
            {
                if (refusals[i] == null)
                {
                    var p = placements[i];
                    servicePlacements.Add((items[i].Handle, p.X, p.Y, p.Width, p.Height, p.BringToFront));
                }
            }

            var results = await _windowService.SetWindowBoundsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

            return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
        }
    }

    [McpServerTool(Name = "snap_windows_batch", Title = "Snap Windows (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap multiple windows to predefined positions in a single batch operation. More efficient than multiple individual calls." + BatchErrors +
                 " InvalidSnapPosition: the item's position is missing or unknown (error lists the positions). InvalidMonitorIndex, " +
                 "MonitorNotFound, AmbiguousMonitor: its monitor names no single monitor. " +
                 "MoveFailed: Windows did not place the window, or it did not return to normal from minimized or maximized.")]
    public async Task<BatchWindowResult> SnapWindowsBatch(
        [Description("Array of snap placements, each with position, the window as handle or window (a selector, as the single-window " +
                     "tools take it), optional monitor, and optional bringToFront (raises the window without activating it; a " +
                     "minimized or maximized window is restored first, without activating it)")]
        WindowSnapPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));
        InputGateToolFilter.ThrowIfPaused(_session);

        // Each item's window, resolved before the gate, and its position, checked here, so one bad item fails alone
        var items = ResolveItems(placements, nameof(placements), "a position and a handle or window", p => p.Handle, p => p.Window);
        var positions = new SnapPosition[placements.Length];
        var monitors = new MonitorChoice[placements.Length];
        var invalid = items.Select((t, i) =>
        {
            if (t.Refusal != null)
                return t.Refusal;

            if (!ToolEnum<SnapPosition>.TryParse(placements[i].Position, out positions[i], out var error))
                return new WindowResultItem(t.Handle.ToInt64(), false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidSnapPosition));

            // The item's monitor, resolved before the gate (roadmap M6.7); one that names no single monitor fails the item alone
            return MonitorTargeting.TryChoose(_monitorService, placements[i].Monitor, allowAll: false,
                $"{nameof(placements)}[{i}].", out monitors[i], out var monitorCode, out var monitorError)
                ? null
                : new WindowResultItem(t.Handle.ToInt64(), false, Error: $"Item {i}: {monitorError}", ErrorCode: monitorCode);
        }).ToList();

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            // Each item's named monitor is found again now, inside the gate: the displays may have changed while the call waited
            var monitorIndexes = new int?[placements.Length];
            var refusals = invalid.Select((r, i) => r ?? ItemRecheck(i, items[i].Handle, placements[i].Window) ??
                BatchPolicyRefusal(items[i].Handle.ToInt64()) ??
                (MonitorTargeting.TryCurrent(_monitorService, monitors[i], $"{nameof(placements)}[{i}].", out monitorIndexes[i], out var gone)
                    ? null
                    : new WindowResultItem(items[i].Handle.ToInt64(), false, Error: $"Item {i}: {gone}", ErrorCode: nameof(MonitorNotFound))))
                .ToList();
            var servicePlacements = new List<(nint, SnapPosition, int?, bool)>();
            for (var i = 0; i < placements.Length; i++)
            {
                if (refusals[i] == null)
                {
                    servicePlacements.Add((items[i].Handle, positions[i], monitorIndexes[i], placements[i].BringToFront));
                }
            }

            var results = await _windowService.SnapWindowsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

            return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
        }
    }

    [McpServerTool(Name = "set_windows_state_batch", Title = "Set Window States (Batch)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the state (minimize, maximize, restore) of multiple windows in a single batch operation. Each item's success is true only " +
                 "when its window was seen in that state afterwards, and state reports the state it is in." + BatchErrors +
                 " InvalidState: the item's state is missing or unknown (error lists the states). StateChangeFailed: the window did not reach the state.")]
    public async Task<BatchWindowResult> SetWindowsStateBatch(
        [Description("Array of state changes, each with state ('minimize', 'maximize' or 'restore'; 'minimized', 'maximized' and 'normal' " +
                     "are accepted too) and the window as handle or window (a selector, as the single-window tools take it)")]
        WindowStateChange[] changes,
        [Description("Delay between operations in milliseconds (0-5000, default: 30)")]
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default)
    {
        RequireItems(changes, nameof(changes));
        InputGateToolFilter.ThrowIfPaused(_session);

        var items = ResolveItems(changes, nameof(changes), "a state and a handle or window", c => c.Handle, c => c.Window);
        var states = new WindowStateRequest[changes.Length];
        var invalid = items.Select((t, i) =>
        {
            if (t.Refusal != null)
                return t.Refusal;

            return ToolEnum<WindowStateRequest>.TryParse(changes[i].State, out states[i], out var error)
                ? null
                : new WindowResultItem(t.Handle.ToInt64(), false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidState));
        }).ToList();

        using (await EnterGateAsync(cancellationToken).ConfigureAwait(false))
        {
            var refusals = invalid.Select((r, i) => r ?? ItemRecheck(i, items[i].Handle, changes[i].Window) ??
                BatchPolicyRefusal(items[i].Handle.ToInt64())).ToList();
            var serviceChanges = new List<(nint, WindowStateRequest)>();
            for (var i = 0; i < changes.Length; i++)
            {
                if (refusals[i] == null)
                {
                    serviceChanges.Add((items[i].Handle, states[i]));
                }
            }

            var results = await _windowService.SetWindowStateBatchAsync(serviceChanges, delayBetweenMs, cancellationToken).ConfigureAwait(false);

            return Summary(changes.Length, InOrder(refusals, results.Select(r => Item(r))));
        }
    }

    /// <summary>
    /// The question close_window asks before force overrides a window's protection: the window, why it is protected, and
    /// what closing it does.
    /// </summary>
    private static string ForceQuestion(nint handle, WindowInfo? window, string protection)
    {
        var line = window is { } w
            ? $"{UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId}){(w.IsAgentHost ? ", the agent's own window" : string.Empty)}"
            : $"window {handle} (no longer exists)";
        return "The AI agent asks to close a protected window with force: the desktop shell (closing it can open the Shut Down " +
               "dialog) or the window of the terminal, editor or chat app the agent runs in (closing it can end the agent's " +
               $"session and lose unsaved work).\n{line}\n{protection}\nAllow it?";
    }

    /// <summary>Why the close guard protects a window: its ProtectedWindow error without the advice to the agent.</summary>
    private static string ProtectionReason(string? error)
    {
        var text = error ?? "The window is protected.";
        var advice = text.IndexOf(" Nothing was sent.", StringComparison.Ordinal);
        return advice < 0 ? text : text[..advice];
    }

    /// <summary>
    /// Why close_windows_batch needs confirm=true on a client that cannot ask the user: more than
    /// <see cref="MaxCloseBatchWithoutConfirm"/> windows, force on the <paramref name="protectedWindows"/> (roadmap M2.F1), or both.
    /// </summary>
    private static string BatchConfirmationRequired(int count, IReadOnlyList<nint> protectedWindows)
    {
        if (protectedWindows.Count == 0)
        {
            return $"close_windows_batch was asked to close {count} windows; more than {MaxCloseBatchWithoutConfirm} " +
                   "require confirm=true. Nothing was closed. Check the handles (find_windows) and call again with confirm=true.";
        }

        var handles = string.Join(", ", protectedWindows.Select(h => h.ToInt64().ToString(CultureInfo.InvariantCulture)));
        var one = protectedWindows.Count == 1;
        var rule = count > MaxCloseBatchWithoutConfirm
            ? $"More than {MaxCloseBatchWithoutConfirm} windows, and force on a protected window, require"
            : "force on a protected window requires";
        return $"close_windows_batch was asked to close {count} windows with force=true, and {(one ? "window" : "windows")} " +
               $"{handles} {(one ? "is" : "are")} protected (the desktop shell or the agent's own terminal or editor). {rule} " +
               "confirm=true when the client cannot ask the user. Nothing was closed. Call again with confirm=true only if the " +
               $"user explicitly asked to close {(one ? "that window" : "those windows")}, or without force to close the others.";
    }

    /// <summary>How many windows close_windows_batch's question lists one by one, besides the protected ones.</summary>
    private const int MaxListedInCloseQuestion = 20;

    /// <summary>
    /// The question close_windows_batch asks the user: the windows, with their title, process and PID; past the first
    /// <see cref="MaxListedInCloseQuestion"/> only a count, except for the protected windows force would close, which are
    /// always named, so a yes never closes a protected window the user was not shown.
    /// </summary>
    private static string CloseQuestion(IReadOnlyList<(nint Handle, WindowInfo? Window)> targets, bool force,
        IReadOnlySet<nint> protectedWindows)
    {
        var lines = targets
            .Select((t, i) => (Target: t, Number: i + 1, Protected: force && protectedWindows.Contains(t.Handle)))
            .Where(t => t.Number <= MaxListedInCloseQuestion || t.Protected)
            .Select(t => t.Target.Window is { } w
                ? $"{t.Number}. {UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId}){(w.IsAgentHost ? ", the agent's own window" : string.Empty)}{(t.Protected ? ", protected" : string.Empty)}"
                : $"{t.Number}. window {t.Target.Handle} (no longer exists)")
            .ToList();
        var more = targets.Count > lines.Count ? $"\n... and {targets.Count - lines.Count} more{(force ? " (none of them protected)" : string.Empty)}" : string.Empty;
        var protection = force
            ? " force is set: protected windows (the desktop shell, the agent's own terminal or editor) are closed too."
            : string.Empty;
        return $"The AI agent asks to close {targets.Count} windows. Each app may ask to save your work, or close without asking.{protection}\n" +
               string.Join("\n", lines) + more + "\nAllow it?";
    }

    /// <summary>
    /// HandleMismatch for a window the user did not see: it belongs to another process than when the user confirmed, or
    /// it was no window then and is one now (its handle was reused), or null. A window that is gone is left to the
    /// close, which reports InvalidHandle.
    /// </summary>
    private WindowResultItem? HandleMismatchRefusal(long handle, IReadOnlyDictionary<nint, int?> confirmedOwners)
    {
        if (!confirmedOwners.TryGetValue((nint)handle, out var asked) || _windowService.GetWindowInfo((nint)handle) is not { } now ||
            now.ProcessId == asked)
        {
            return null;
        }

        var then = asked is { } pid
            ? $"not to the process (PID {pid}) it belonged to when it was chosen: the window closed and its handle was reused"
            : "but it was no window when the user was asked (the user saw it as 'no longer exists'): a new window got its handle";
        return new WindowResultItem(handle, false,
            Error: $"Window {handle} now belongs to {now.ProcessName} (PID {now.ProcessId}), {then}. It was not closed.",
            ErrorCode: nameof(HandleMismatch));
    }

    /// <summary>
    /// The refusal of a change before anything is sent: InvalidHandle, or TargetDeniedByPolicy for a window the user's
    /// target policy denies (these tools never restore, raise or move such a window). Null when the change can go ahead.
    /// </summary>
    private WindowOperationResult? Precheck(nint handle)
    {
        if (!_windowService.IsValidWindow(handle))
        {
            return new WindowOperationResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle),
                Handle: handle.ToInt64());
        }

        return _windowService.EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? new WindowOperationResult(false, Error: WindowService.WindowChangeRefusal(handle, verdict), ErrorCode: nameof(TargetDeniedByPolicy),
                Handle: handle.ToInt64())
            : null;
    }

    /// <summary>The handle parameter of the single-window tools: kept for compatibility beside the window selector (roadmap M2.4).</summary>
    private const string HandleDescription = "Window handle (as integer) from find_windows. Pass handle or window, not both.";

    /// <summary>
    /// The window a window-changing tool acts on (roadmap M2.4). Nothing while the user has paused input; then the handle,
    /// or the selector resolved, before the tool takes the input gate: resolution only reads windows, and the window then
    /// goes through the same checks (<see cref="Precheck"/>, the service's own) as a handle the caller passed.
    /// </summary>
    /// <exception cref="ToolErrorException">Input is paused, or the arguments name no single window.</exception>
    private nint Target(long? handle, WindowSelector? window)
    {
        InputGateToolFilter.ThrowIfPaused(_session);
        return WindowTargeting.Require(_windowService, handle, window);
    }

    /// <summary>
    /// The input gate for a window-changing tool, after its window was resolved: waits, refuses (InputBusy) and reports a
    /// pause (PausedByUser) exactly as the gate filter does for the tools it gates.
    /// </summary>
    private Task<IDisposable> EnterGateAsync(CancellationToken cancellationToken) =>
        InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout);

    /// <summary>
    /// The window of each item of a batch, resolved before the gate (roadmap M2.4): its handle, or its selector resolved.
    /// An item that is null, that names no window, or whose selector names no single window gets its refusal (InvalidArguments,
    /// WindowNotFound, AmbiguousWindow with candidates, HandleMismatch, InvalidHandle) and fails alone.
    /// </summary>
    private List<(nint Handle, WindowResultItem? Refusal)> ResolveItems<T>(
        T[] items, string array, string fields, Func<T, long?> handleOf, Func<T, WindowSelector?> windowOf,
        bool requireNamedWindow = false) where T : class
    {
        var resolved = new List<(nint, WindowResultItem?)>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item == null)
            {
                resolved.Add((nint.Zero, NullItem(i, array, fields)));
                continue;
            }

            var target = WindowTargeting.Resolve(_windowService, handleOf(item), windowOf(item), requireNamedWindow: requireNamedWindow);
            if (target == null)
            {
                resolved.Add((nint.Zero, new WindowResultItem(0, false,
                    Error: $"Item {i} of {array} names no window; each item needs {fields}. Nothing was done for it.",
                    ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode)));
            }
            else if (target.Refusal is { } refusal)
            {
                resolved.Add((nint.Zero, new WindowResultItem(0, false, Error: $"Item {i}: {refusal.Error}", ErrorCode: refusal.ErrorCode,
                    Candidates: refusal.Candidates)));
            }
            else
            {
                resolved.Add((target.Handle, null));
            }
        }

        return resolved;
    }

    /// <summary>
    /// An item's resolved window checked again inside the input gate (<see cref="WindowTargeting.Recheck"/>): HandleMismatch
    /// when it no longer matches the item's selector, so that item fails alone; null otherwise.
    /// </summary>
    private WindowResultItem? ItemRecheck(int index, nint handle, WindowSelector? window) =>
        WindowTargeting.Recheck(_windowService, window, handle) is { } refusal
            ? new WindowResultItem(handle.ToInt64(), false, Error: $"Item {index}: {refusal.Error}", ErrorCode: refusal.ErrorCode)
            : null;

    /// <summary>
    /// Runs a window change and reports its outcome with the window as it is afterwards (roadmap M2.3): its visible frame,
    /// window rect, state and monitor, read after waiting up to <paramref name="settleMs"/> for it to stop moving. A window
    /// that did not answer in time (<see cref="WindowNotRespondingException"/>, from the service's <see cref="IWindowCallRunner"/>)
    /// is NotResponding: the application is hung or busy, and the tool returns instead of holding the input gate until it
    /// answers. The reads afterwards never wait for the window's thread.
    /// </summary>
    private WindowOperationResult Change(nint handle, Func<WindowChangeResult> change, int settleMs)
    {
        WindowChangeResult result;
        try
        {
            result = change();
        }
        catch (WindowNotRespondingException ex)
        {
            result = WindowChangeResult.Failed(handle, NotResponding, ex.Message);
        }

        if (result.Success)
        {
            _windowService.WaitForSettledFrame(handle, WindowService.SettleDelay(settleMs));
        }

        var window = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowInfo(handle);
        return new WindowOperationResult(
            Success: result.Success,
            Bounds: Bounds(window),
            OuterBounds: OuterBounds(window),
            State: State(window),
            MonitorIndex: window?.MonitorIndex,
            Error: result.Error,
            ErrorCode: result.ErrorCodeName,
            Handle: handle.ToInt64());
    }

    /// <summary>A batch item for the service's result, with the window as it is afterwards.</summary>
    private WindowResultItem Item(WindowChangeResult result)
    {
        // An invalid handle has nothing to read; any other window is read, failed or not, so the agent sees where it is
        var window = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowInfo(result.Handle);
        return new WindowResultItem(
            Handle: result.Handle.ToInt64(),
            Success: result.Success,
            Bounds: Bounds(window),
            OuterBounds: OuterBounds(window),
            State: State(window),
            MonitorIndex: window?.MonitorIndex,
            Error: result.Error,
            ErrorCode: result.ErrorCodeName);
    }

    /// <summary>The visible frame (the window rect when DWM cannot report it); none for a minimized window, which is off screen.</summary>
    private static BoundsDto? Bounds(WindowInfo? window) =>
        window is null || window.State == WindowState.Minimized ? null : Dto(window.VisibleBounds ?? window.Bounds);

    /// <summary>The window rect (GetWindowRect, as find_windows reports it); none for a minimized window.</summary>
    private static BoundsDto? OuterBounds(WindowInfo? window) =>
        window is null || window.State == WindowState.Minimized ? null : Dto(window.Bounds);

    private static BoundsDto Dto(WindowRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static string? State(WindowInfo? window) => window?.State.ToString().ToLowerInvariant();

    /// <summary>InvalidArguments when a size is not positive (roadmap M2.2): nothing is sent.</summary>
    private static WindowOperationResult? SizeRefusal(int width, int height) =>
        width >= 1 && height >= 1
            ? null
            : new WindowOperationResult(false, Error: SizeMessage(width, height), ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    private static WindowResultItem? ItemSizeRefusal(long handle, int width, int height) =>
        width >= 1 && height >= 1
            ? null
            : new WindowResultItem(handle, false, Error: SizeMessage(width, height), ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    /// <summary>
    /// InvalidArguments for set_window_bounds' optional pairs (roadmap M6.1): x goes with y and width with height, at least one
    /// pair is given, and a size is positive; and for its unit (roadmap M6.7): pixels are whole numbers in the int range, with
    /// no relativeTo or monitor, and percentages are 0-100 (sizes above 0) and end inside the area. Nothing is sent.
    /// </summary>
    private static WindowOperationResult? BoundsRefusal(double? x, double? y, double? width, double? height, bool percent,
        RelativeArea? relativeTo, MonitorSelector? monitor)
    {
        string? message = null;
        if (x.HasValue != y.HasValue)
        {
            message = $"x and y go together, but only {(x.HasValue ? "x" : "y")} was given. Pass both to move the window, or neither to keep its position. Nothing was changed.";
        }
        else if (width.HasValue != height.HasValue)
        {
            message = $"width and height go together, but only {(width.HasValue ? "width" : "height")} was given. Pass both to resize the window, or neither to keep its size. Nothing was changed.";
        }
        else if (!x.HasValue && !width.HasValue)
        {
            message = "Pass x and y to move the window, width and height to resize it, or all four. Nothing was changed.";
        }
        else if (!percent && (relativeTo != null || monitor != null))
        {
            message = $"{(monitor != null ? "monitor" : "relativeTo")} goes with unit 'percent'; pixels are virtual-screen coordinates, " +
                      $"which already name the monitor. {NothingChanged}";
        }
        else if (percent)
        {
            message = (x, y, width, height) switch
            {
                ({ } left, { } top, { } w, { } h) => RelativeRectMath.CheckPercent(left, top, w, h),
                ({ } left, { } top, _, _) => RelativeRectMath.CheckPercentPosition(left, top),
                (_, _, { } w, { } h) => RelativeRectMath.CheckPercentSize(w, h),
                _ => null
            };
            message = message == null ? null : $"{message} {NothingChanged}";
        }
        else if (new[] { (x, "x"), (y, "y"), (width, "width"), (height, "height") }
                     .FirstOrDefault(v => v.Item1 is { } value && (value != Math.Floor(value) || value is < int.MinValue or > int.MaxValue))
                     is ({ } fraction, { } name))
        {
            message = string.Create(CultureInfo.InvariantCulture,
                $"{name} must be a whole number of pixels, not {fraction}; pass unit 'percent' for a share of a monitor. {NothingChanged}");
        }

        if (message != null)
        {
            return new WindowOperationResult(false, Error: message, ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        return !percent && width is { } pw && height is { } ph ? SizeRefusal((int)pw, (int)ph) : null;
    }

    private static string SizeMessage(int width, int height) =>
        $"width and height must be at least 1 pixel, but they are {width} x {height}. Nothing was changed.";

    /// <summary>
    /// The batch's array argument itself: an explicit null fails the call with InvalidArguments, as a missing one does.
    /// </summary>
    private static void RequireItems<T>(T[]? items, string name)
    {
        if (items == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"{name} must be an array of items, not null. Nothing was done.");
        }
    }

    /// <summary>The result of a batch item that is null: it fails alone with InvalidArguments (roadmap M2.1).</summary>
    private static WindowResultItem NullItem(int index, string array, string fields) =>
        new(0, false, Error: $"Item {index} of {array} is null; each item needs {fields}. Nothing was done for it.",
            ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    /// <summary>A batch item for a window the target policy denies, or null when the window is allowed or not a window.</summary>
    private WindowResultItem? BatchPolicyRefusal(long handle) =>
        _windowService.IsValidWindow((nint)handle) && Precheck((nint)handle) is { } denied
            ? new WindowResultItem(Handle: handle, Success: false, Error: denied.Error, ErrorCode: denied.ErrorCode)
            : null;

    /// <summary>
    /// The batch's items in request order: the refusal where there is one, and the service's results, in order,
    /// for the other requests.
    /// </summary>
    private static List<WindowResultItem> InOrder(IReadOnlyList<WindowResultItem?> refusals, IEnumerable<WindowResultItem> results)
    {
        using var done = results.GetEnumerator();
        var items = new List<WindowResultItem>(refusals.Count);
        foreach (var refusal in refusals)
        {
            if (refusal != null)
            {
                items.Add(refusal);
            }
            else if (done.MoveNext())
            {
                items.Add(done.Current);
            }
        }

        return items;
    }

    private static BatchWindowResult Summary(int requested, IReadOnlyList<WindowResultItem> items) => new(
        TotalRequested: requested,
        Succeeded: items.Count(i => i.Success),
        Failed: items.Count(i => !i.Success),
        Results: items);
}

/// <summary>
/// set_window_zorder's result (roadmap M6.7): whether the change was made, and the window's topmost state and place in the
/// z-order (0 is the front; get_window_info's zOrder) read back afterwards, failed or not (left out when the window is gone).
/// </summary>
public sealed record ZOrderResult(
    bool Success,
    long? Handle = null,
    bool? IsTopmost = null,
    int? ZOrder = null,
    string? Error = null,
    string? ErrorCode = null);

/// <summary>
/// The result of a single-window tool. The geometry tools (roadmap M2.3) report the window as it is afterwards, failed or
/// not (left out for an invalid handle): Bounds is its visible frame, OuterBounds its window rect with the invisible
/// resize borders (the bounds find_windows reports), State normal, minimized or maximized, MonitorIndex its monitor. A
/// minimized window has no bounds. Handle is the window the tool acted on (roadmap M2.4: the one a window selector
/// resolved to); it is left out when the arguments named no window.
/// </summary>
public record WindowOperationResult(
    bool Success,
    BoundsDto? Bounds = null,
    BoundsDto? OuterBounds = null,
    string? State = null,
    int? MonitorIndex = null,
    string? Error = null,
    string? ErrorCode = null,
    long? Handle = null
);
