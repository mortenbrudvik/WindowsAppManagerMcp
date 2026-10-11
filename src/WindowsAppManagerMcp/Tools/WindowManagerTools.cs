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

    /// <summary>The per-window errors of the window batches.</summary>
    private const string BatchErrors =
        " Each window is handled on its own and has its own success, error and errorCode: InvalidArguments for an item that is " +
        "null or misses a field, InvalidHandle, TargetDeniedByPolicy (the user's policy denies the app; it is not changed), " +
        "AccessDenied, NotResponding (the window's app does not answer within 5 s).";

    private readonly IWindowService _windowService;
    private readonly IForegroundService _foregroundService;

    public WindowManagerTools(IWindowService windowService, IForegroundService foregroundService)
    {
        _windowService = windowService;
        _foregroundService = foregroundService;
    }

    [McpServerTool(Name = "move_window")]
    [Description("Move a window to a specific position. Returns success and the window's newBounds." + WindowChangeErrors +
                 " MoveFailed: Windows did not move it (error has the reason).")]
    public WindowOperationResult MoveWindow(
        [Description("Window handle (as integer) from find_windows")]
        long handle,
        [Description("X coordinate (left edge)")]
        int x,
        [Description("Y coordinate (top edge)")]
        int y,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.MoveWindow(hwnd, x, y, bringToFront));
    }

    [McpServerTool(Name = "resize_window")]
    [Description("Resize a window to specific dimensions. Returns success and the window's newBounds." + WindowChangeErrors +
                 " ResizeFailed: Windows did not resize it (error has the reason). InvalidArguments: width or height is not positive.")]
    public WindowOperationResult ResizeWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("New width in pixels (at least 1)")]
        int width,
        [Description("New height in pixels (at least 1)")]
        int height)
    {
        var hwnd = (nint)handle;
        return SizeRefusal(width, height) ?? Precheck(hwnd) ?? Change(hwnd, () => _windowService.ResizeWindow(hwnd, width, height));
    }

    [McpServerTool(Name = "set_window_bounds")]
    [Description("Move and resize a window in a single operation. Returns success and the window's newBounds." + WindowChangeErrors +
                 " MoveFailed: Windows did not place it (error has the reason). InvalidArguments: width or height is not positive.")]
    public WindowOperationResult SetWindowBounds(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("X coordinate (left edge)")]
        int x,
        [Description("Y coordinate (top edge)")]
        int y,
        [Description("New width in pixels (at least 1)")]
        int width,
        [Description("New height in pixels (at least 1)")]
        int height,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return SizeRefusal(width, height) ?? Precheck(hwnd) ??
               Change(hwnd, () => _windowService.SetWindowBounds(hwnd, x, y, width, height, bringToFront));
    }

    [McpServerTool(Name = "set_window_state")]
    [Description("Set the window state (minimize, maximize, restore). success is true only when the window was seen in that state " +
                 "afterwards (within 300 ms); newState reports the state it is in (minimized, maximized or normal)." + WindowChangeErrors +
                 " StateChangeFailed: the window did not reach the state (the app refused or delayed it, or it runs as administrator). " +
                 "An unknown state fails with InvalidState and the allowed values.")]
    public WindowOperationResult SetWindowState(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Desired state: 'minimize', 'maximize' or 'restore' ('minimized', 'maximized' and 'normal' are accepted too). " +
                     "Restore returns a minimized window to its previous state (normal, or maximized if it was maximized before) and makes a maximized window normal.")]
        WindowStateRequest state)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SetWindowState(hwnd, state), reportsState: true);
    }

    [McpServerTool(Name = "focus_window")]
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

    [McpServerTool(Name = "close_window")]
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

    [McpServerTool(Name = "close_windows_batch")]
    [Description("Close multiple windows gracefully in a single batch operation. Applications may prompt to save unsaved work. " +
                 "More than 3 windows require confirm=true. Protected windows (the desktop shell, the agent's own terminal or editor) " +
                 "are refused per window with errorCode ProtectedWindow unless force is true, and apps the user's policy denies with TargetDeniedByPolicy; " +
                 "the other windows are still closed. An item that is null fails alone with InvalidArguments.")]
    public async Task<BatchWindowResult> CloseWindowsBatch(
        [Description("Array of window handles to close")]
        WindowCloseRequest[] windows,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        [Description("Must be true to close more than 3 windows in one batch. Check the list (find_windows) first: closing many windows at once can lose unsaved work.")]
        bool confirm = false,
        [Description(ForceDescription + " Applies to every window in the batch.")]
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        RequireItems(windows, nameof(windows));
        if (windows.Length > MaxCloseBatchWithoutConfirm && !confirm)
        {
            throw new ToolErrorException(
                nameof(ConfirmationRequired),
                $"close_windows_batch was asked to close {windows.Length} windows; more than {MaxCloseBatchWithoutConfirm} " +
                "require confirm=true. Nothing was closed. Check the handles (find_windows) and call again with confirm=true.");
        }

        var refusals = windows.Select((w, i) => w == null ? NullItem(i, nameof(windows), "a handle") : null).ToList();
        var handles = windows.Where((_, i) => refusals[i] == null).Select(w => (nint)w.Handle).ToList();

        var results = await _windowService.CloseWindowsBatchAsync(handles, force, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(windows.Length, InOrder(refusals, results.Select(r => new WindowResultItem(
            Handle: r.Handle.ToInt64(),
            Success: r.Success,
            Error: r.Error,
            ErrorCode: r.Success ? null : r.ErrorCode.ToString()))));
    }

    [McpServerTool(Name = "snap_window")]
    [Description("Snap a window to predefined screen positions (like Windows Snap Assist). Supported positions: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen; any other position fails with errorCode InvalidSnapPosition. Returns success and the window's newBounds." + WindowChangeErrors +
                 " InvalidMonitorIndex: monitorIndex is not a monitor (error gives the valid range). MoveFailed: Windows did not place it.")]
    public WindowOperationResult SnapWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Snap position: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen")]
        SnapPosition position,
        [Description("Target monitor index (0-based, see get_monitors). Defaults to the window's current monitor. An index that is not a monitor fails with InvalidMonitorIndex.")]
        int? monitorIndex = null,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SnapWindow(hwnd, position, monitorIndex, bringToFront));
    }

    [McpServerTool(Name = "move_window_to_monitor")]
    [Description("Move a window to a specific monitor, optionally positioning it. Returns success and the window's newBounds." + WindowChangeErrors +
                 " InvalidMonitorIndex: monitorIndex is not a monitor (error gives the valid range). MoveFailed: Windows did not place it, " +
                 "or with positioning 'maximize' it was not maximized on that monitor. An unknown positioning fails with InvalidPositioning.")]
    public WindowOperationResult MoveWindowToMonitor(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Target monitor index (0-based, see get_monitors)")]
        int monitorIndex,
        [Description("Position on the monitor: 'center' (default), 'topleft', 'topright', 'bottomleft', 'bottomright' (each at the window's current size), 'maximize' (moved there, then maximized) or 'restore' (current size, at the work area's top-left corner)")]
        MonitorPositioning positioning = MonitorPositioning.Center,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it; positioning 'maximize' ignores bringToFront and can activate it too. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.MoveWindowToMonitor(hwnd, monitorIndex, positioning, bringToFront));
    }

    [McpServerTool(Name = "set_windows_bounds_batch")]
    [Description("Move and resize multiple windows in a single batch operation. More efficient than multiple individual calls." + BatchErrors +
                 " MoveFailed: Windows did not place the window.")]
    public async Task<BatchWindowResult> SetWindowsBoundsBatch(
        [Description("Array of window placements with handle, x, y, width, height, and optional bringToFront (raises the window without activating it; a minimized window is restored first, and restoring can activate it)")]
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

    [McpServerTool(Name = "snap_windows_batch")]
    [Description("Snap multiple windows to predefined positions in a single batch operation. More efficient than multiple individual calls." + BatchErrors +
                 " InvalidSnapPosition: the item's position is missing or unknown (error lists the positions). InvalidMonitorIndex: its monitorIndex is not a monitor. MoveFailed: Windows did not place the window.")]
    public async Task<BatchWindowResult> SnapWindowsBatch(
        [Description("Array of snap placements with handle, position, optional monitorIndex, and optional bringToFront (raises the window without activating it; a minimized window is restored first, and restoring can activate it)")]
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

    [McpServerTool(Name = "set_windows_state_batch")]
    [Description("Set the state (minimize, maximize, restore) of multiple windows in a single batch operation. Each item's success is true only " +
                 "when its window was seen in that state afterwards, and newState reports the state it is in." + BatchErrors +
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

        return Summary(changes.Length, InOrder(refusals, results.Select(r => Item(r, reportsState: true))));
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
    /// Runs a window change and reports its outcome with the window as it is afterwards: its bounds, or with
    /// <paramref name="reportsState"/> its state. A window that did not answer in time (<see cref="WindowNotRespondingException"/>,
    /// from the service's <see cref="IWindowCallRunner"/>) is NotResponding: the application is hung or busy, and the
    /// tool returns instead of holding the input gate until it answers.
    /// </summary>
    private WindowOperationResult Change(nint handle, Func<WindowChangeResult> change, bool reportsState = false)
    {
        try
        {
            var result = change();
            var window = _windowService.GetWindowInfo(handle);
            return new WindowOperationResult(
                Success: result.Success,
                NewBounds: reportsState ? null : Bounds(window),
                NewState: reportsState ? State(window) : null,
                Error: result.Error,
                ErrorCode: result.ErrorCodeName);
        }
        catch (WindowNotRespondingException ex)
        {
            return new WindowOperationResult(false, Error: ex.Message, ErrorCode: nameof(NotResponding));
        }
    }

    /// <summary>A batch item for the service's result, with the window's bounds (or state) as it is afterwards.</summary>
    private WindowResultItem Item(WindowChangeResult result, bool reportsState = false)
    {
        // An invalid handle has nothing to read; any other window is read, failed or not, so the agent sees where it is
        var window = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowInfo(result.Handle);
        return new WindowResultItem(
            Handle: result.Handle.ToInt64(),
            Success: result.Success,
            NewBounds: reportsState ? null : Bounds(window),
            NewState: reportsState ? State(window) : null,
            Error: result.Error,
            ErrorCode: result.ErrorCodeName);
    }

    private static BoundsDto? Bounds(WindowInfo? window) =>
        window == null ? null : new BoundsDto(window.Bounds.X, window.Bounds.Y, window.Bounds.Width, window.Bounds.Height);

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

public record WindowOperationResult(
    bool Success,
    BoundsDto? NewBounds = null,
    string? NewState = null,
    string? Error = null,
    string? ErrorCode = null
);
