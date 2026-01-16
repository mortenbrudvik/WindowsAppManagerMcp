using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class WindowService : IWindowService
{
    private readonly IMonitorService _monitorService;
    private readonly INativeWindowWrapper _nativeWrapper;

    public WindowService(IMonitorService monitorService, INativeWindowWrapper nativeWrapper)
    {
        _monitorService = monitorService;
        _nativeWrapper = nativeWrapper;
    }

    public IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true)
    {
        var windows = new List<WindowInfo>();

        _nativeWrapper.EnumWindows(hWnd =>
        {
            if (!_nativeWrapper.IsWindowVisible(hWnd))
                return true;

            var info = GetWindowInfoInternal(hWnd);
            if (info != null)
            {
                if (includeMinimized || info.State != WindowState.Minimized)
                {
                    windows.Add(info);
                }
            }
            return true;
        });

        return windows;
    }

    public IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true)
    {
        var windows = new List<WindowInfo>();

        _nativeWrapper.EnumWindows(hWnd =>
        {
            if (visibleOnly && !_nativeWrapper.IsWindowVisible(hWnd))
                return true;

            // Filter by handle if specified
            if (handle.HasValue && hWnd != handle.Value)
                return true;

            var info = GetWindowInfoInternal(hWnd);
            if (info == null)
                return true;

            // Filter by process ID
            if (processId.HasValue && info.ProcessId != processId.Value)
                return true;

            // Filter by process name
            if (!string.IsNullOrEmpty(processName))
            {
                if (!info.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Filter by title
            if (!string.IsNullOrEmpty(titleContains))
            {
                if (!info.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            windows.Add(info);
            return true;
        });

        return windows;
    }

    public WindowInfo? GetForegroundWindow()
    {
        var hWnd = _nativeWrapper.GetForegroundWindow();
        if (hWnd == nint.Zero)
            return null;

        return GetWindowInfoInternal(hWnd);
    }

    public WindowInfo? GetWindowInfo(nint handle)
    {
        if (!IsValidWindow(handle))
            return null;

        return GetWindowInfoInternal(handle);
    }

    public bool MoveWindow(nint handle, int x, int y, bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        var rect = _nativeWrapper.GetWindowRect(handle);
        if (rect == null)
            return false;

        var width = rect.Value.Right - rect.Value.Left;
        var height = rect.Value.Bottom - rect.Value.Top;

        var flags = bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

        var success = _nativeWrapper.SetWindowPos(handle, x, y, width, height, flags);

        if (success && bringToFront)
        {
            _nativeWrapper.BringWindowToTop(handle);
        }

        return success;
    }

    public bool ResizeWindow(nint handle, int width, int height)
    {
        if (!IsValidWindow(handle))
            return false;

        var rect = _nativeWrapper.GetWindowRect(handle);
        if (rect == null)
            return false;

        return _nativeWrapper.SetWindowPos(
            handle,
            rect.Value.Left, rect.Value.Top, width, height,
            NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE);
    }

    public bool SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        // Restore window first if minimized
        if (_nativeWrapper.IsIconic(handle))
        {
            _nativeWrapper.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        var flags = bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

        var success = _nativeWrapper.SetWindowPos(handle, x, y, width, height, flags);

        if (success && bringToFront)
        {
            _nativeWrapper.BringWindowToTop(handle);
        }

        return success;
    }

    public bool SetWindowState(nint handle, WindowState state)
    {
        if (!IsValidWindow(handle))
            return false;

        var cmd = state switch
        {
            WindowState.Minimized => NativeEnums.SW_MINIMIZE,
            WindowState.Maximized => NativeEnums.SW_MAXIMIZE,
            WindowState.Normal => NativeEnums.SW_RESTORE,
            _ => NativeEnums.SW_RESTORE
        };

        return _nativeWrapper.ShowWindow(handle, cmd);
    }

    public bool FocusWindow(nint handle)
    {
        if (!IsValidWindow(handle))
            return false;

        // If window is minimized, restore it first
        if (_nativeWrapper.IsIconic(handle))
        {
            _nativeWrapper.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        // Try SetForegroundWindow first
        if (_nativeWrapper.SetForegroundWindow(handle))
            return true;

        // Fallback: try BringWindowToTop
        _nativeWrapper.BringWindowToTop(handle);
        return _nativeWrapper.SetForegroundWindow(handle);
    }

    public bool SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        var targetMonitor = monitorIndex ?? _monitorService.GetMonitorIndex(handle);
        var bounds = _monitorService.CalculateSnapBounds(targetMonitor, position);

        return SetWindowBounds(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height, bringToFront);
    }

    public bool MoveWindowToMonitor(nint handle, int monitorIndex, string positioning = "center", bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        var monitors = _monitorService.GetAllMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            return false;

        var targetMonitor = monitors[monitorIndex];
        var workArea = targetMonitor.WorkArea;

        // Get current window size
        var rect = _nativeWrapper.GetWindowRect(handle);
        if (rect == null)
            return false;

        var width = rect.Value.Right - rect.Value.Left;
        var height = rect.Value.Bottom - rect.Value.Top;

        int x, y;

        switch (positioning.ToLowerInvariant())
        {
            case "center":
                x = workArea.X + (workArea.Width - width) / 2;
                y = workArea.Y + (workArea.Height - height) / 2;
                break;

            case "topleft":
            case "top_left":
                x = workArea.X;
                y = workArea.Y;
                break;

            case "topright":
            case "top_right":
                x = workArea.X + workArea.Width - width;
                y = workArea.Y;
                break;

            case "bottomleft":
            case "bottom_left":
                x = workArea.X;
                y = workArea.Y + workArea.Height - height;
                break;

            case "bottomright":
            case "bottom_right":
                x = workArea.X + workArea.Width - width;
                y = workArea.Y + workArea.Height - height;
                break;

            case "maximize":
                return SetWindowState(handle, WindowState.Maximized);

            case "restore":
                return SetWindowBounds(handle, workArea.X, workArea.Y, width, height, bringToFront);

            default:
                x = workArea.X + (workArea.Width - width) / 2;
                y = workArea.Y + (workArea.Height - height) / 2;
                break;
        }

        // Restore if minimized
        if (_nativeWrapper.IsIconic(handle))
        {
            _nativeWrapper.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        var flags = bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

        var success = _nativeWrapper.SetWindowPos(handle, x, y, width, height, flags);

        if (success && bringToFront)
        {
            _nativeWrapper.BringWindowToTop(handle);
        }

        return success;
    }

    public bool IsValidWindow(nint handle)
    {
        return handle != nint.Zero && _nativeWrapper.IsWindow(handle);
    }

    public bool CloseWindow(nint handle)
    {
        if (!IsValidWindow(handle))
            return false;

        // Send WM_CLOSE for graceful close - the application can cancel this
        return _nativeWrapper.PostMessage(handle, NativeEnums.WM_CLOSE, nint.Zero, nint.Zero);
    }

    public IReadOnlyList<(nint Handle, bool Success, string? Error)> SetWindowBoundsBatch(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50)
    {
        var results = new List<(nint Handle, bool Success, string? Error)>();

        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            try
            {
                if (!IsValidWindow(placement.Handle))
                {
                    results.Add((placement.Handle, false, "Invalid window handle"));
                    continue;
                }

                var success = SetWindowBounds(
                    placement.Handle,
                    placement.X,
                    placement.Y,
                    placement.Width,
                    placement.Height,
                    placement.BringToFront);

                results.Add((placement.Handle, success, success ? null : "SetWindowBounds failed"));

                if (delayBetweenMs > 0 && i < placements.Count - 1)
                {
                    Thread.Sleep(delayBetweenMs);
                }
            }
            catch (Exception ex)
            {
                results.Add((placement.Handle, false, ex.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<(nint Handle, bool Success, string? Error)> SnapWindowsBatch(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50)
    {
        var results = new List<(nint Handle, bool Success, string? Error)>();

        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            try
            {
                if (!IsValidWindow(placement.Handle))
                {
                    results.Add((placement.Handle, false, "Invalid window handle"));
                    continue;
                }

                var success = SnapWindow(
                    placement.Handle,
                    placement.Position,
                    placement.MonitorIndex,
                    placement.BringToFront);

                results.Add((placement.Handle, success, success ? null : "SnapWindow failed"));

                if (delayBetweenMs > 0 && i < placements.Count - 1)
                {
                    Thread.Sleep(delayBetweenMs);
                }
            }
            catch (Exception ex)
            {
                results.Add((placement.Handle, false, ex.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<(nint Handle, bool Success, string? Error)> SetWindowStateBatch(
        IReadOnlyList<(nint Handle, WindowState State)> changes,
        int delayBetweenMs = 30)
    {
        var results = new List<(nint Handle, bool Success, string? Error)>();

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            try
            {
                if (!IsValidWindow(change.Handle))
                {
                    results.Add((change.Handle, false, "Invalid window handle"));
                    continue;
                }

                var success = SetWindowState(change.Handle, change.State);
                results.Add((change.Handle, success, success ? null : "SetWindowState failed"));

                if (delayBetweenMs > 0 && i < changes.Count - 1)
                {
                    Thread.Sleep(delayBetweenMs);
                }
            }
            catch (Exception ex)
            {
                results.Add((change.Handle, false, ex.Message));
            }
        }

        return results;
    }

    private WindowInfo? GetWindowInfoInternal(nint hWnd)
    {
        // Get window title
        var title = _nativeWrapper.GetWindowText(hWnd);

        // Skip windows with empty titles (usually not user-facing windows)
        if (string.IsNullOrEmpty(title))
            return null;

        // Get process ID and name
        var processId = _nativeWrapper.GetWindowThreadProcessId(hWnd);
        var processName = GetProcessNameFromId((int)processId);

        // Skip our own process
        if (processId == _nativeWrapper.GetCurrentProcessId())
            return null;

        // Get window rectangle
        var rect = _nativeWrapper.GetWindowRect(hWnd);
        if (rect == null)
            return null;

        // Get window state
        var state = WindowState.Normal;
        if (_nativeWrapper.IsIconic(hWnd))
            state = WindowState.Minimized;
        else if (_nativeWrapper.IsZoomed(hWnd))
            state = WindowState.Maximized;

        // Get monitor index
        var monitorIndex = _monitorService.GetMonitorIndex(hWnd);

        // Check if this is visible
        var isVisible = _nativeWrapper.IsWindowVisible(hWnd);

        return new WindowInfo(
            Handle: hWnd,
            Title: title,
            ProcessName: processName,
            ProcessId: (int)processId,
            Bounds: WindowRect.FromLTRB(rect.Value.Left, rect.Value.Top, rect.Value.Right, rect.Value.Bottom),
            State: state,
            IsVisible: isVisible,
            MonitorIndex: monitorIndex
        );
    }

    private static string GetProcessNameFromId(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }
}
