using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;
using static WindowsAppManagerMcp.Models.WindowErrorCode;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class WindowManagerTools
{
    private readonly IWindowService _windowService;
    private readonly IForegroundService _foregroundService;

    public WindowManagerTools(IWindowService windowService, IForegroundService foregroundService)
    {
        _windowService = windowService;
        _foregroundService = foregroundService;
    }

    [McpServerTool(Name = "move_window")]
    [Description("Move a window to a specific position. Returns success and the window's newBounds.")]
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
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        if (PolicyRefusal(hwnd) is { } denied)
        {
            return denied;
        }

        return ReportingNotResponding(() =>
        {
            var success = _windowService.MoveWindow(hwnd, x, y, bringToFront);
            var newInfo = _windowService.GetWindowInfo(hwnd);

            return new WindowOperationResult(
                Success: success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
            );
        });
    }

    [McpServerTool(Name = "resize_window")]
    [Description("Resize a window to specific dimensions. Returns success and the window's newBounds.")]
    public WindowOperationResult ResizeWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("New width in pixels")]
        int width,
        [Description("New height in pixels")]
        int height)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        if (PolicyRefusal(hwnd) is { } denied)
        {
            return denied;
        }

        return ReportingNotResponding(() =>
        {
            var success = _windowService.ResizeWindow(hwnd, width, height);
            var newInfo = _windowService.GetWindowInfo(hwnd);

            return new WindowOperationResult(
                Success: success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
            );
        });
    }

    [McpServerTool(Name = "set_window_bounds")]
    [Description("Move and resize a window in a single operation. Returns success and the window's newBounds.")]
    public WindowOperationResult SetWindowBounds(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("X coordinate (left edge)")]
        int x,
        [Description("Y coordinate (top edge)")]
        int y,
        [Description("New width in pixels")]
        int width,
        [Description("New height in pixels")]
        int height,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        if (PolicyRefusal(hwnd) is { } denied)
        {
            return denied;
        }

        return ReportingNotResponding(() =>
        {
            var success = _windowService.SetWindowBounds(hwnd, x, y, width, height, bringToFront);
            var newInfo = _windowService.GetWindowInfo(hwnd);

            return new WindowOperationResult(
                Success: success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
            );
        });
    }

    [McpServerTool(Name = "set_window_state")]
    [Description("Set the window state (minimize, maximize, restore). Returns success and the window's newState.")]
    public WindowOperationResult SetWindowState(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Desired state: 'minimize', 'maximize', or 'restore'")]
        string state)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        if (PolicyRefusal(hwnd) is { } denied)
        {
            return denied;
        }

        var windowState = state.ToLowerInvariant() switch
        {
            "minimize" or "minimized" => WindowState.Minimized,
            "maximize" or "maximized" => WindowState.Maximized,
            "restore" or "normal" => WindowState.Normal,
            _ => WindowState.Normal
        };

        return ReportingNotResponding(() =>
        {
            var success = _windowService.SetWindowState(hwnd, windowState);
            var newInfo = _windowService.GetWindowInfo(hwnd);

            return new WindowOperationResult(
                Success: success,
                NewState: newInfo?.State.ToString().ToLowerInvariant()
            );
        });
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
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
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
                 "are refused per window with errorCode ProtectedWindow unless force is true; the other windows are still closed.")]
    public BatchWindowResult CloseWindowsBatch(
        [Description("Array of window handles to close")]
        WindowCloseRequest[] windows,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        [Description("Must be true to close more than 3 windows in one batch. Check the list (find_windows) first: closing many windows at once can lose unsaved work.")]
        bool confirm = false,
        [Description(ForceDescription + " Applies to every window in the batch.")]
        bool force = false)
    {
        if (windows.Length > MaxCloseBatchWithoutConfirm && !confirm)
        {
            throw new Infrastructure.ToolErrorException(
                nameof(ConfirmationRequired),
                $"close_windows_batch was asked to close {windows.Length} windows; more than {MaxCloseBatchWithoutConfirm} " +
                "require confirm=true. Nothing was closed. Check the handles (find_windows) and call again with confirm=true.");
        }

        var handles = windows.Select(w => (nint)w.Handle).ToList();

        var results = _windowService.CloseWindowsBatch(handles, force, delayBetweenMs);

        var items = results.Select(r => new WindowResultItem(
            Handle: r.Handle.ToInt64(),
            Success: r.Success,
            Error: r.Error,
            ErrorCode: r.Success ? null : r.ErrorCode.ToString()
        )).ToList();

        return new BatchWindowResult(
            TotalRequested: windows.Length,
            Succeeded: items.Count(i => i.Success),
            Failed: items.Count(i => !i.Success),
            Results: items
        );
    }

    [McpServerTool(Name = "snap_window")]
    [Description("Snap a window to predefined screen positions (like Windows Snap Assist). Supported positions: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen.")]
    public WindowOperationResult SnapWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Snap position: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen")]
        string position,
        [Description("Target monitor index (0-based). Defaults to current monitor if not specified.")]
        int? monitorIndex = null,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        var snapPosition = ParseSnapPosition(position);
        if (snapPosition == null)
        {
            return new WindowOperationResult(false, Error: $"Invalid snap position: {position}", ErrorCode: nameof(InvalidSnapPosition));
        }

        if (PolicyRefusal(hwnd) is { } denied)
        {
            return denied;
        }

        return ReportingNotResponding(() =>
        {
            var success = _windowService.SnapWindow(hwnd, snapPosition.Value, monitorIndex, bringToFront);
            var newInfo = _windowService.GetWindowInfo(hwnd);

            return new WindowOperationResult(
                Success: success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
            );
        });
    }

    [McpServerTool(Name = "move_window_to_monitor")]
    [Description("Move a window to a specific monitor, optionally positioning it. Returns success and the window's newBounds.")]
    public WindowOperationResult MoveWindowToMonitor(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Target monitor index (0-based)")]
        int monitorIndex,
        [Description("Position on monitor: 'center', 'topleft', 'topright', 'bottomleft', 'bottomright', 'maximize', or 'restore' (default: center)")]
        string positioning = "center",
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it; positioning 'maximize' ignores bringToFront and can activate it too. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        if (PolicyRefusal(hwnd) is { } denied)
        {
            return denied;
        }

        return ReportingNotResponding(() =>
        {
            var success = _windowService.MoveWindowToMonitor(hwnd, monitorIndex, positioning, bringToFront);
            var newInfo = _windowService.GetWindowInfo(hwnd);

            return new WindowOperationResult(
                Success: success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
            );
        });
    }

    [McpServerTool(Name = "set_windows_bounds_batch")]
    [Description("Move and resize multiple windows in a single batch operation. More efficient than multiple individual calls.")]
    public BatchWindowResult SetWindowsBoundsBatch(
        [Description("Array of window placements with handle, x, y, width, height, and optional bringToFront (raises the window without activating it; a minimized window is restored first, and restoring can activate it)")]
        WindowBoundsPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50)
    {
        var refusals = placements.Select(p => BatchPolicyRefusal(p.Handle)).ToList();
        var servicePlacements = placements.Where((_, i) => refusals[i] == null).Select(p =>
            ((nint)p.Handle, p.X, p.Y, p.Width, p.Height, p.BringToFront)).ToList();

        var results = _windowService.SetWindowBoundsBatch(servicePlacements, delayBetweenMs);

        var items = InOrder(refusals, results.Select(r =>
        {
            var newInfo = r.Success ? _windowService.GetWindowInfo(r.Handle) : null;
            return new WindowResultItem(
                Handle: r.Handle.ToInt64(),
                Success: r.Success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null,
                Error: r.Error
            );
        }));

        return new BatchWindowResult(
            TotalRequested: placements.Length,
            Succeeded: items.Count(i => i.Success),
            Failed: items.Count(i => !i.Success),
            Results: items
        );
    }

    [McpServerTool(Name = "snap_windows_batch")]
    [Description("Snap multiple windows to predefined positions in a single batch operation. More efficient than multiple individual calls.")]
    public BatchWindowResult SnapWindowsBatch(
        [Description("Array of snap placements with handle, position, optional monitorIndex, and optional bringToFront (raises the window without activating it; a minimized window is restored first, and restoring can activate it)")]
        WindowSnapPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50)
    {
        // Invalid positions and windows the policy denies get their item here; the others go to the service
        var refusals = placements.Select(p => ParseSnapPosition(p.Position) == null
            ? new WindowResultItem(Handle: p.Handle, Success: false, Error: $"Invalid snap position: {p.Position}")
            : BatchPolicyRefusal(p.Handle)).ToList();
        var servicePlacements = placements.Where((_, i) => refusals[i] == null)
            .Select(p => ((nint)p.Handle, ParseSnapPosition(p.Position)!.Value, p.MonitorIndex, p.BringToFront))
            .ToList();

        var results = _windowService.SnapWindowsBatch(servicePlacements, delayBetweenMs);

        var items = InOrder(refusals, results.Select(r =>
        {
            var newInfo = r.Success ? _windowService.GetWindowInfo(r.Handle) : null;
            return new WindowResultItem(
                Handle: r.Handle.ToInt64(),
                Success: r.Success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null,
                Error: r.Error
            );
        }));

        return new BatchWindowResult(
            TotalRequested: placements.Length,
            Succeeded: items.Count(i => i.Success),
            Failed: items.Count(i => !i.Success),
            Results: items
        );
    }

    [McpServerTool(Name = "set_windows_state_batch")]
    [Description("Set the state (minimize, maximize, restore) of multiple windows in a single batch operation.")]
    public BatchWindowResult SetWindowsStateBatch(
        [Description("Array of state changes with handle and state")]
        WindowStateChange[] changes,
        [Description("Delay between operations in milliseconds (0-5000, default: 30)")]
        int delayBetweenMs = 30)
    {
        var refusals = changes.Select(c => BatchPolicyRefusal(c.Handle)).ToList();
        var serviceChanges = changes.Where((_, i) => refusals[i] == null).Select(c =>
        {
            var state = c.State.ToLowerInvariant() switch
            {
                "minimize" or "minimized" => WindowState.Minimized,
                "maximize" or "maximized" => WindowState.Maximized,
                "restore" or "normal" => WindowState.Normal,
                _ => WindowState.Normal
            };
            return ((nint)c.Handle, state);
        }).ToList();

        var results = _windowService.SetWindowStateBatch(serviceChanges, delayBetweenMs);

        var items = InOrder(refusals, results.Select(r =>
        {
            var newInfo = r.Success ? _windowService.GetWindowInfo(r.Handle) : null;
            return new WindowResultItem(
                Handle: r.Handle.ToInt64(),
                Success: r.Success,
                NewState: newInfo?.State.ToString().ToLowerInvariant(),
                Error: r.Error
            );
        }));

        return new BatchWindowResult(
            TotalRequested: changes.Length,
            Succeeded: items.Count(i => i.Success),
            Failed: items.Count(i => !i.Success),
            Results: items
        );
    }

    /// <summary>
    /// Runs a window change and reports a window that did not answer in time (<see cref="WindowNotRespondingException"/>,
    /// from the service's <see cref="IWindowCallRunner"/>) as NotResponding: the application is hung or busy, and the
    /// tool returns instead of holding the input gate until it answers.
    /// </summary>
    private static WindowOperationResult ReportingNotResponding(Func<WindowOperationResult> change)
    {
        try
        {
            return change();
        }
        catch (WindowNotRespondingException ex)
        {
            return new WindowOperationResult(false, Error: ex.Message, ErrorCode: nameof(NotResponding));
        }
    }

    /// <summary>
    /// The refusal of a change to a window the user's target policy denies (TargetDeniedByPolicy), or null when
    /// it allows the window. A denied window is never restored, raised or moved, so a screenshot cannot show it.
    /// </summary>
    private WindowOperationResult? PolicyRefusal(nint handle) =>
        _windowService.EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? new WindowOperationResult(false, Error: WindowService.WindowChangeRefusal(handle, verdict), ErrorCode: nameof(TargetDeniedByPolicy))
            : null;

    /// <summary>A batch item for a window the target policy denies, or null when the window is allowed or not a window.</summary>
    private WindowResultItem? BatchPolicyRefusal(long handle) =>
        _windowService.IsValidWindow((nint)handle) && PolicyRefusal((nint)handle) is { } denied
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

    private static SnapPosition? ParseSnapPosition(string position)
    {
        return position.ToLowerInvariant().Replace("_", "").Replace("-", "") switch
        {
            "lefthalf" => SnapPosition.LeftHalf,
            "righthalf" => SnapPosition.RightHalf,
            "tophalf" => SnapPosition.TopHalf,
            "bottomhalf" => SnapPosition.BottomHalf,
            "topleftquarter" => SnapPosition.TopLeftQuarter,
            "toprightquarter" => SnapPosition.TopRightQuarter,
            "bottomleftquarter" => SnapPosition.BottomLeftQuarter,
            "bottomrightquarter" => SnapPosition.BottomRightQuarter,
            "leftthird" => SnapPosition.LeftThird,
            "centerthird" => SnapPosition.CenterThird,
            "rightthird" => SnapPosition.RightThird,
            "lefttwothirds" => SnapPosition.LeftTwoThirds,
            "righttwothirds" => SnapPosition.RightTwoThirds,
            "fullscreen" or "full" => SnapPosition.Fullscreen,
            _ => null
        };
    }
}

public record WindowOperationResult(
    bool Success,
    BoundsDto? NewBounds = null,
    string? NewState = null,
    string? Error = null,
    string? ErrorCode = null
);
