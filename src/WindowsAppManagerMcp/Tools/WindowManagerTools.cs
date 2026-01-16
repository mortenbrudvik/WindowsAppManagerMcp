using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;
using static WindowsAppManagerMcp.Models.WindowErrorCode;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class WindowManagerTools
{
    private readonly IWindowService _windowService;

    public WindowManagerTools(IWindowService windowService)
    {
        _windowService = windowService;
    }

    [McpServerTool(Name = "move_window")]
    [Description("Move a window to a specific position. Returns true if successful.")]
    public WindowOperationResult MoveWindow(
        [Description("Window handle (as integer) from find_windows")]
        long handle,
        [Description("X coordinate (left edge)")]
        int x,
        [Description("Y coordinate (top edge)")]
        int y,
        [Description("Bring window to front of z-order (default: true)")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        var success = _windowService.MoveWindow(hwnd, x, y, bringToFront);
        var newInfo = _windowService.GetWindowInfo(hwnd);

        return new WindowOperationResult(
            Success: success,
            NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
        );
    }

    [McpServerTool(Name = "resize_window")]
    [Description("Resize a window to specific dimensions. Returns true if successful.")]
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

        var success = _windowService.ResizeWindow(hwnd, width, height);
        var newInfo = _windowService.GetWindowInfo(hwnd);

        return new WindowOperationResult(
            Success: success,
            NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
        );
    }

    [McpServerTool(Name = "set_window_bounds")]
    [Description("Move and resize a window in a single operation. Returns true if successful.")]
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
        [Description("Bring window to front of z-order (default: true)")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        var success = _windowService.SetWindowBounds(hwnd, x, y, width, height, bringToFront);
        var newInfo = _windowService.GetWindowInfo(hwnd);

        return new WindowOperationResult(
            Success: success,
            NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
        );
    }

    [McpServerTool(Name = "set_window_state")]
    [Description("Set the window state (minimize, maximize, restore). Returns true if successful.")]
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

        var windowState = state.ToLowerInvariant() switch
        {
            "minimize" or "minimized" => WindowState.Minimized,
            "maximize" or "maximized" => WindowState.Maximized,
            "restore" or "normal" => WindowState.Normal,
            _ => WindowState.Normal
        };

        var success = _windowService.SetWindowState(hwnd, windowState);
        var newInfo = _windowService.GetWindowInfo(hwnd);

        return new WindowOperationResult(
            Success: success,
            NewState: newInfo?.State.ToString().ToLowerInvariant()
        );
    }

    [McpServerTool(Name = "focus_window")]
    [Description("Bring a window to the foreground and give it focus. Returns true if successful.")]
    public WindowOperationResult FocusWindow(
        [Description("Window handle (as integer)")]
        long handle)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        var success = _windowService.FocusWindow(hwnd);
        return new WindowOperationResult(Success: success);
    }

    [McpServerTool(Name = "close_window")]
    [Description("Close a window gracefully by sending WM_CLOSE. The application may prompt to save unsaved work. Returns true if the close message was sent successfully.")]
    public WindowOperationResult CloseWindow(
        [Description("Window handle (as integer)")]
        long handle)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        var success = _windowService.CloseWindow(hwnd);
        return new WindowOperationResult(Success: success);
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
        [Description("Bring window to front of z-order (default: true)")]
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

        var success = _windowService.SnapWindow(hwnd, snapPosition.Value, monitorIndex, bringToFront);
        var newInfo = _windowService.GetWindowInfo(hwnd);

        return new WindowOperationResult(
            Success: success,
            NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
        );
    }

    [McpServerTool(Name = "move_window_to_monitor")]
    [Description("Move a window to a specific monitor, optionally positioning it. Returns true if successful.")]
    public WindowOperationResult MoveWindowToMonitor(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Target monitor index (0-based)")]
        int monitorIndex,
        [Description("Position on monitor: 'center', 'topleft', 'topright', 'bottomleft', 'bottomright', 'maximize', or 'restore' (default: center)")]
        string positioning = "center",
        [Description("Bring window to front of z-order (default: true)")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle", ErrorCode: nameof(InvalidHandle));
        }

        var success = _windowService.MoveWindowToMonitor(hwnd, monitorIndex, positioning, bringToFront);
        var newInfo = _windowService.GetWindowInfo(hwnd);

        return new WindowOperationResult(
            Success: success,
            NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
        );
    }

    [McpServerTool(Name = "set_windows_bounds_batch")]
    [Description("Move and resize multiple windows in a single batch operation. More efficient than multiple individual calls.")]
    public BatchWindowResult SetWindowsBoundsBatch(
        [Description("Array of window placements with handle, x, y, width, height, and optional bringToFront")]
        WindowBoundsPlacement[] placements,
        [Description("Delay between operations in milliseconds (default: 50)")]
        int delayBetweenMs = 50)
    {
        var servicePlacements = placements.Select(p =>
            ((nint)p.Handle, p.X, p.Y, p.Width, p.Height, p.BringToFront)).ToList();

        var results = _windowService.SetWindowBoundsBatch(servicePlacements, delayBetweenMs);

        var items = results.Select(r =>
        {
            var newInfo = r.Success ? _windowService.GetWindowInfo(r.Handle) : null;
            return new WindowResultItem(
                Handle: r.Handle.ToInt64(),
                Success: r.Success,
                NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null,
                Error: r.Error
            );
        }).ToList();

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
        [Description("Array of snap placements with handle, position, optional monitorIndex, and optional bringToFront")]
        WindowSnapPlacement[] placements,
        [Description("Delay between operations in milliseconds (default: 50)")]
        int delayBetweenMs = 50)
    {
        var servicePlacements = new List<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)>();

        foreach (var p in placements)
        {
            var snapPos = ParseSnapPosition(p.Position);
            if (snapPos == null)
            {
                // We'll handle invalid positions in the results
                continue;
            }
            servicePlacements.Add(((nint)p.Handle, snapPos.Value, p.MonitorIndex, p.BringToFront));
        }

        var results = _windowService.SnapWindowsBatch(servicePlacements, delayBetweenMs);

        var items = new List<WindowResultItem>();
        var resultIndex = 0;

        foreach (var p in placements)
        {
            var snapPos = ParseSnapPosition(p.Position);
            if (snapPos == null)
            {
                items.Add(new WindowResultItem(
                    Handle: p.Handle,
                    Success: false,
                    Error: $"Invalid snap position: {p.Position}"
                ));
                continue;
            }

            if (resultIndex < results.Count)
            {
                var r = results[resultIndex++];
                var newInfo = r.Success ? _windowService.GetWindowInfo(r.Handle) : null;
                items.Add(new WindowResultItem(
                    Handle: r.Handle.ToInt64(),
                    Success: r.Success,
                    NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null,
                    Error: r.Error
                ));
            }
        }

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
        [Description("Delay between operations in milliseconds (default: 30)")]
        int delayBetweenMs = 30)
    {
        var serviceChanges = changes.Select(c =>
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

        var items = results.Select(r =>
        {
            var newInfo = r.Success ? _windowService.GetWindowInfo(r.Handle) : null;
            return new WindowResultItem(
                Handle: r.Handle.ToInt64(),
                Success: r.Success,
                NewState: newInfo?.State.ToString().ToLowerInvariant(),
                Error: r.Error
            );
        }).ToList();

        return new BatchWindowResult(
            TotalRequested: changes.Length,
            Succeeded: items.Count(i => i.Success),
            Failed: items.Count(i => !i.Success),
            Results: items
        );
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
