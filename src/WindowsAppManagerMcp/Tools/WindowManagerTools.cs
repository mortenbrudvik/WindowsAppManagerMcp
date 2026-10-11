using System.ComponentModel;
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
        "rect as bounds), state (normal, minimized or maximized; a minimized window has no bounds) and monitorIndex.";

    /// <summary>How the placements read their coordinates and treat the window's state (roadmap M2.3).</summary>
    private const string PlacementRules =
        " Coordinates are physical virtual-screen pixels (negative left of or above the primary monitor) of the visible frame. " +
        "A minimized or maximized window is restored to normal first, without activating it.";

    private const string SettleDescription =
        "Wait up to this many milliseconds (0-5000, default 0) for the window to stop moving before it is read: until two reads " +
        "20 ms apart agree. Use it for an app that animates or re-applies its own size after the change.";

    private const string BringToFrontDescription =
        "Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). Default: true";

    /// <summary>The per-window errors of the window batches.</summary>
    private const string BatchErrors =
        " Each window is handled on its own and has its own success, error and errorCode: InvalidArguments for an item that is " +
        "null or misses a field, InvalidHandle, TargetDeniedByPolicy (the user's policy denies the app; it is not changed), " +
        "AccessDenied, NotResponding (the window's app does not answer within 5 s). Each item reports its window afterwards, " +
        "failed or not: bounds (visible frame), outerBounds, state and monitorIndex.";

    private readonly IWindowService _windowService;
    private readonly IForegroundService _foregroundService;
    private readonly IInputSession? _session;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan? _gateWaitTimeout;

    /// <param name="windowService">Reads and changes windows.</param>
    /// <param name="foregroundService">Brings a window to the front (focus_window).</param>
    /// <param name="session">The input session, whose pause close_windows_batch respects (it takes the input gate itself).</param>
    /// <param name="answerTimeout">How long close_windows_batch waits for the user's answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    /// <param name="gateWaitTimeout">How long close_windows_batch waits for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    public WindowManagerTools(
        IWindowService windowService,
        IForegroundService foregroundService,
        IInputSession? session = null,
        TimeSpan? answerTimeout = null,
        TimeSpan? gateWaitTimeout = null)
    {
        _windowService = windowService;
        _foregroundService = foregroundService;
        _session = session;
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
        _gateWaitTimeout = gateWaitTimeout;
    }

    [McpServerTool(Name = "move_window", Title = "Move Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move a window so that its visible frame's top-left corner is at x, y; it keeps its size." + PlacementRules +
                 GeometryResult + WindowChangeErrors +
                 " MoveFailed: Windows did not move it, or it did not return to normal from minimized or maximized (error has the reason).")]
    public WindowOperationResult MoveWindow(
        [Description("Window handle (as integer) from find_windows")]
        long handle,
        [Description("X coordinate of the visible frame's left edge")]
        int x,
        [Description("Y coordinate of the visible frame's top edge")]
        int y,
        [Description(BringToFrontDescription)]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.MoveWindow(hwnd, x, y, bringToFront), settleMs);
    }

    [McpServerTool(Name = "resize_window", Title = "Resize Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Resize a window's visible frame; its top-left corner stays where it is and it is not raised." + PlacementRules +
                 GeometryResult + WindowChangeErrors +
                 " ResizeFailed: Windows did not resize it, or it did not return to normal from minimized or maximized (error has the reason). " +
                 "InvalidArguments: width or height is not positive. An app can keep a minimum or maximum size: compare bounds with what you asked for.")]
    public WindowOperationResult ResizeWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("New width of the visible frame in pixels (at least 1)")]
        int width,
        [Description("New height of the visible frame in pixels (at least 1)")]
        int height,
        [Description(SettleDescription)]
        int settleMs = 0)
    {
        var hwnd = (nint)handle;
        return SizeRefusal(width, height) ?? Precheck(hwnd) ??
               Change(hwnd, () => _windowService.ResizeWindow(hwnd, width, height), settleMs);
    }

    [McpServerTool(Name = "set_window_bounds", Title = "Set Window Bounds", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move and resize a window in a single operation: x, y, width and height are its visible frame." + PlacementRules +
                 " A window moved to a monitor with another scale is placed again once the app has rescaled itself." +
                 GeometryResult + WindowChangeErrors +
                 " MoveFailed: Windows did not place it, or it did not return to normal from minimized or maximized (error has the reason). " +
                 "InvalidArguments: width or height is not positive.")]
    public WindowOperationResult SetWindowBounds(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("X coordinate of the visible frame's left edge")]
        int x,
        [Description("Y coordinate of the visible frame's top edge")]
        int y,
        [Description("New width of the visible frame in pixels (at least 1)")]
        int width,
        [Description("New height of the visible frame in pixels (at least 1)")]
        int height,
        [Description(BringToFrontDescription)]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0)
    {
        var hwnd = (nint)handle;
        return SizeRefusal(width, height) ?? Precheck(hwnd) ??
               Change(hwnd, () => _windowService.SetWindowFrame(hwnd, x, y, width, height, bringToFront), settleMs);
    }

    [McpServerTool(Name = "set_window_state", Title = "Minimize, Maximize or Restore Window", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the window state (minimize, maximize, restore). success is true only when the window was seen in that state " +
                 "afterwards (within 300 ms); state reports the state it is in (minimized, maximized or normal)." + GeometryResult +
                 WindowChangeErrors +
                 " StateChangeFailed: the window did not reach the state (the app refused or delayed it, or it runs as administrator). " +
                 "An unknown state fails with InvalidState and the allowed values.")]
    public WindowOperationResult SetWindowState(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Desired state: 'minimize', 'maximize' or 'restore' ('minimized', 'maximized' and 'normal' are accepted too). " +
                     "Restore returns a minimized window to its previous state (normal, or maximized if it was maximized before) and makes a maximized window normal.")]
        WindowStateRequest state,
        [Description(SettleDescription)]
        int settleMs = 0)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SetWindowState(hwnd, state), settleMs);
    }

    [McpServerTool(Name = "focus_window", Title = "Focus Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Bring a window to the foreground and verify it. Works around the Windows foreground lock without sending keys. " +
        "Returns isForeground (also as success), method (already, setForeground, attachThreadInput or inputUnlock), " +
        "the window actually in front (foregroundHandle, foregroundTitle, foregroundProcess) and blockingPopup, a dialog of the " +
        "window that takes keyboard input instead of it. inMenuMode: a menu is still open, so typed letters would pick menu items. " +
        "errorCode: FocusFailed, NotResponding, Cloaked, WindowHidden (the window is hidden, so keys would go to a window the user cannot see), " +
        "TargetDeniedByPolicy (the user's policy denies the app), PausedByUser or InvalidHandle. " +
        "The foreground can change again before your next call, so confirm with get_foreground_window immediately before keyboard or mouse input.")]
    public FocusResult FocusWindow(
        [Description("Window handle (as integer)")]
        long handle)
    {
        return FocusResult.From(_foregroundService.EnsureForeground((nint)handle));
    }

    /// <summary>close_windows_batch closes at most this many windows without confirm=true.</summary>
    public const int MaxCloseBatchWithoutConfirm = 3;

    private const string ForceDescription =
        "Close even a protected window: the desktop shell (Program Manager, the taskbars) or the agent's own host " +
        "(the terminal, editor or chat app this server was started from). Default false: such windows are refused " +
        "with errorCode ProtectedWindow. Set it only when the user explicitly asked to close that window.";

    [McpServerTool(Name = "close_window", Title = "Close Window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close a window gracefully by sending WM_CLOSE. The application may prompt to save unsaved work. success means the close message was sent, not that the window has closed; check with find_windows. " +
                 "Refuses the desktop shell and the agent's own terminal or editor window (errorCode ProtectedWindow) unless force is true, " +
                 "refuses an app the user's policy denies (TargetDeniedByPolicy; force does not override it), " +
                 "and reports AccessDenied when Windows blocks the message (the window belongs to an app running as administrator).")]
    public WindowOperationResult CloseWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description(ForceDescription)]
        bool force = false)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle));
        }

        var result = _windowService.CloseWindow(hwnd, force);
        return new WindowOperationResult(
            Success: result.Success,
            Error: result.Error,
            ErrorCode: result.Success ? null : result.ErrorCode.ToString());
    }

    [McpServerTool(Name = "close_windows_batch", Title = "Close Windows (Batch)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close multiple windows gracefully in a single batch operation. Applications may prompt to save unsaved work. " +
                 "More than 3 windows need a confirmation: when the MCP client can ask the user (elicitation), the user is asked, with each " +
                 "window's title, process and PID, and nothing is closed unless they confirm (errorCode ConfirmationDeclined); otherwise " +
                 "confirm=true is required (ConfirmationRequired). A window that changed hands after the user confirmed is not closed " +
                 "(HandleMismatch). Protected windows (the desktop shell, the agent's own terminal or editor) " +
                 "are refused per window with errorCode ProtectedWindow unless force is true, and apps the user's policy denies with TargetDeniedByPolicy; " +
                 "the other windows are still closed. An item that is null fails alone with InvalidArguments.")]
    public async Task<BatchWindowResult> CloseWindowsBatch(
        [Description("Array of window handles to close")]
        WindowCloseRequest[] windows,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        [Description("Must be true to close more than 3 windows in one batch when the client cannot ask the user. Check the list (find_windows) first: closing many windows at once can lose unsaved work.")]
        bool confirm = false,
        [Description(ForceDescription + " Applies to every window in the batch.")]
        bool force = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        RequireItems(windows, nameof(windows));

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        Dictionary<nint, int?>? confirmedOwners = null;
        if (windows.Length > MaxCloseBatchWithoutConfirm)
        {
            if (UserConfirmation.CanAsk(server))
            {
                // Ask the user, not the model, and before the input gate: a waiting user must not hold up every input call
                var targets = windows.Where(w => w != null).Select(w => (Handle: (nint)w.Handle, Window: _windowService.GetWindowInfo((nint)w.Handle)))
                    .ToList();
                var answer = await UserConfirmation.AskAsync(server!, CloseQuestion(targets, force), $"Close these {targets.Count} windows",
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
                throw new ToolErrorException(
                    nameof(ConfirmationRequired),
                    $"close_windows_batch was asked to close {windows.Length} windows; more than {MaxCloseBatchWithoutConfirm} " +
                    "require confirm=true. Nothing was closed. Check the handles (find_windows) and call again with confirm=true.");
            }
        }

        using var gate = await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false);

        // A window the user was asked about must still belong to the same process; a reused handle is not closed
        var refusals = windows.Select((w, i) => w == null
            ? NullItem(i, nameof(windows), "a handle")
            : confirmedOwners == null ? null : HandleMismatchRefusal(w.Handle, confirmedOwners)).ToList();
        var handles = windows.Where((_, i) => refusals[i] == null).Select(w => (nint)w.Handle).ToList();

        var results = await _windowService.CloseWindowsBatchAsync(handles, force, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(windows.Length, InOrder(refusals, results.Select(r => new WindowResultItem(
            Handle: r.Handle.ToInt64(),
            Success: r.Success,
            Error: r.Error,
            ErrorCode: r.Success ? null : r.ErrorCode.ToString()))));
    }

    [McpServerTool(Name = "snap_window", Title = "Snap Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap a window to predefined screen positions (like Windows Snap Assist). Supported positions: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen; any other position fails with errorCode InvalidSnapPosition. " +
                 "The visible frames of snapped windows tile the work area exactly: no gap from the invisible borders, no pixel lost on odd sizes." +
                 PlacementRules + GeometryResult + WindowChangeErrors +
                 " InvalidMonitorIndex: monitorIndex is not a monitor (error gives the valid range). MoveFailed: Windows did not place it, " +
                 "or it did not return to normal from minimized or maximized.")]
    public WindowOperationResult SnapWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Snap position: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen")]
        SnapPosition position,
        [Description("Target monitor index (0-based, see get_monitors). Defaults to the window's current monitor. An index that is not a monitor fails with InvalidMonitorIndex.")]
        int? monitorIndex = null,
        [Description(BringToFrontDescription)]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SnapWindow(hwnd, position, monitorIndex, bringToFront), settleMs);
    }

    [McpServerTool(Name = "move_window_to_monitor", Title = "Move Window to Monitor", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move a window to a specific monitor, optionally positioning it. The window keeps its normal size, made smaller when " +
                 "needed so that it fits the monitor's work area. To a monitor with other scaling it is moved first, and sized after " +
                 "the app has rescaled itself. success requires the window to be on that monitor afterwards." + PlacementRules +
                 GeometryResult + WindowChangeErrors +
                 " InvalidMonitorIndex: monitorIndex is not a monitor (error gives the valid range). MoveFailed: Windows did not place it, " +
                 "it did not return to normal from minimized or maximized, or it is not on that monitor afterwards (with 'maximize': not " +
                 "maximized there). An unknown positioning fails with InvalidPositioning.")]
    public WindowOperationResult MoveWindowToMonitor(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Target monitor index (0-based, see get_monitors)")]
        int monitorIndex,
        [Description("Position on the monitor's work area: 'center' (default), 'topleft', 'topright', 'bottomleft', 'bottomright' (each at the window's normal size, fitted to the work area), 'maximize' (moved there, then maximized) or 'restore' (normal size, at the work area's top-left corner)")]
        MonitorPositioning positioning = MonitorPositioning.Center,
        [Description(BringToFrontDescription + ". Positioning 'maximize' ignores it and activates the window: Windows has no maximize that does not activate.")]
        bool bringToFront = true,
        [Description(SettleDescription)]
        int settleMs = 0)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ??
               Change(hwnd, () => _windowService.MoveWindowToMonitor(hwnd, monitorIndex, positioning, bringToFront), settleMs);
    }

    [McpServerTool(Name = "set_windows_bounds_batch", Title = "Set Window Bounds (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move and resize multiple windows in a single batch operation, as set_window_bounds does: x, y, width and height are " +
                 "each window's visible frame. More efficient than multiple individual calls." + PlacementRules + BatchErrors +
                 " MoveFailed: Windows did not place the window, or it did not return to normal from minimized or maximized.")]
    public async Task<BatchWindowResult> SetWindowsBoundsBatch(
        [Description("Array of window placements with handle, x, y, width, height (the visible frame), and optional bringToFront (raises the window without activating it)")]
        WindowBoundsPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));

        // Items that are invalid, or for windows the policy denies, get their result here; the others go to the service
        var refusals = placements.Select((p, i) => p == null
            ? NullItem(i, nameof(placements), "handle, x, y, width and height")
            : ItemSizeRefusal(p.Handle, p.Width, p.Height) ?? BatchPolicyRefusal(p.Handle)).ToList();
        var servicePlacements = placements.Where((_, i) => refusals[i] == null).Select(p =>
            ((nint)p.Handle, p.X, p.Y, p.Width, p.Height, p.BringToFront)).ToList();

        var results = await _windowService.SetWindowBoundsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
    }

    [McpServerTool(Name = "snap_windows_batch", Title = "Snap Windows (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap multiple windows to predefined positions in a single batch operation. More efficient than multiple individual calls." + BatchErrors +
                 " InvalidSnapPosition: the item's position is missing or unknown (error lists the positions). InvalidMonitorIndex: its monitorIndex is not a monitor. " +
                 "MoveFailed: Windows did not place the window, or it did not return to normal from minimized or maximized.")]
    public async Task<BatchWindowResult> SnapWindowsBatch(
        [Description("Array of snap placements with handle, position, optional monitorIndex, and optional bringToFront (raises the window without activating it; a minimized or maximized window is restored first, without activating it)")]
        WindowSnapPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));

        // An item's position is checked here, so one bad item fails alone instead of failing the call
        var positions = new SnapPosition[placements.Length];
        var refusals = placements.Select((p, i) =>
        {
            if (p == null)
                return NullItem(i, nameof(placements), "handle and position");

            return ToolEnum<SnapPosition>.TryParse(p.Position, out positions[i], out var error)
                ? BatchPolicyRefusal(p.Handle)
                : new WindowResultItem(p.Handle, false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidSnapPosition));
        }).ToList();
        var servicePlacements = placements.Select((p, i) => (Placement: p, Index: i)).Where(x => refusals[x.Index] == null)
            .Select(x => ((nint)x.Placement.Handle, positions[x.Index], x.Placement.MonitorIndex, x.Placement.BringToFront))
            .ToList();

        var results = await _windowService.SnapWindowsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
    }

    [McpServerTool(Name = "set_windows_state_batch", Title = "Set Window States (Batch)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the state (minimize, maximize, restore) of multiple windows in a single batch operation. Each item's success is true only " +
                 "when its window was seen in that state afterwards, and state reports the state it is in." + BatchErrors +
                 " InvalidState: the item's state is missing or unknown (error lists the states). StateChangeFailed: the window did not reach the state.")]
    public async Task<BatchWindowResult> SetWindowsStateBatch(
        [Description("Array of state changes with handle and state ('minimize', 'maximize' or 'restore'; 'minimized', 'maximized' and 'normal' are accepted too)")]
        WindowStateChange[] changes,
        [Description("Delay between operations in milliseconds (0-5000, default: 30)")]
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default)
    {
        RequireItems(changes, nameof(changes));

        var states = new WindowStateRequest[changes.Length];
        var refusals = changes.Select((c, i) =>
        {
            if (c == null)
                return NullItem(i, nameof(changes), "handle and state");

            return ToolEnum<WindowStateRequest>.TryParse(c.State, out states[i], out var error)
                ? BatchPolicyRefusal(c.Handle)
                : new WindowResultItem(c.Handle, false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidState));
        }).ToList();
        var serviceChanges = changes.Select((c, i) => (Change: c, Index: i)).Where(x => refusals[x.Index] == null)
            .Select(x => ((nint)x.Change.Handle, states[x.Index]))
            .ToList();

        var results = await _windowService.SetWindowStateBatchAsync(serviceChanges, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(changes.Length, InOrder(refusals, results.Select(r => Item(r))));
    }

    /// <summary>The question close_windows_batch asks the user: every window, with its title, process and PID.</summary>
    private static string CloseQuestion(IReadOnlyList<(nint Handle, WindowInfo? Window)> targets, bool force)
    {
        var lines = targets.Take(20).Select((t, i) => t.Window is { } w
            ? $"{i + 1}. {UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId}){(w.IsAgentHost ? ", the agent's own window" : string.Empty)}"
            : $"{i + 1}. window {t.Handle} (no longer exists)");
        var more = targets.Count > 20 ? $"\n... and {targets.Count - 20} more" : string.Empty;
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
            ? $"not to the process (PID {pid}) the user confirmed: the window closed and its handle was reused"
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
            return new WindowOperationResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle));
        }

        return _windowService.EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? new WindowOperationResult(false, Error: WindowService.WindowChangeRefusal(handle, verdict), ErrorCode: nameof(TargetDeniedByPolicy))
            : null;
    }

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
            ErrorCode: result.ErrorCodeName);
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
/// The result of a single-window tool. The geometry tools (roadmap M2.3) report the window as it is afterwards, failed or
/// not (left out for an invalid handle): Bounds is its visible frame, OuterBounds its window rect with the invisible
/// resize borders (the bounds find_windows reports), State normal, minimized or maximized, MonitorIndex its monitor. A
/// minimized window has no bounds. close_window reports only success and the error.
/// </summary>
public record WindowOperationResult(
    bool Success,
    BoundsDto? Bounds = null,
    BoundsDto? OuterBounds = null,
    string? State = null,
    int? MonitorIndex = null,
    string? Error = null,
    string? ErrorCode = null
);
