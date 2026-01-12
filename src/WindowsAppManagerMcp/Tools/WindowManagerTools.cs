using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

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
        int y)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle");
        }

        var success = _windowService.MoveWindow(hwnd, x, y);
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
            return new WindowOperationResult(false, Error: "Invalid window handle");
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
        int height)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle");
        }

        var success = _windowService.SetWindowBounds(hwnd, x, y, width, height);
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
            return new WindowOperationResult(false, Error: "Invalid window handle");
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
            return new WindowOperationResult(false, Error: "Invalid window handle");
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
            return new WindowOperationResult(false, Error: "Invalid window handle");
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
        int? monitorIndex = null)
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle");
        }

        var snapPosition = ParseSnapPosition(position);
        if (snapPosition == null)
        {
            return new WindowOperationResult(false, Error: $"Invalid snap position: {position}");
        }

        var success = _windowService.SnapWindow(hwnd, snapPosition.Value, monitorIndex);
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
        string positioning = "center")
    {
        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new WindowOperationResult(false, Error: "Invalid window handle");
        }

        var success = _windowService.MoveWindowToMonitor(hwnd, monitorIndex, positioning);
        var newInfo = _windowService.GetWindowInfo(hwnd);

        return new WindowOperationResult(
            Success: success,
            NewBounds: newInfo != null ? new BoundsDto(newInfo.Bounds.X, newInfo.Bounds.Y, newInfo.Bounds.Width, newInfo.Bounds.Height) : null
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
    string? Error = null
);
