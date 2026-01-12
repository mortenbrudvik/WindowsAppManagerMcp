using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class WindowService : IWindowService
{
    private readonly IMonitorService _monitorService;

    public WindowService(IMonitorService monitorService)
    {
        _monitorService = monitorService;
    }

    public IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true)
    {
        var windows = new List<WindowInfo>();

        NativeMethods.User32.EnumWindowsProc callback = (hWnd, lParam) =>
        {
            if (!NativeMethods.User32.IsWindowVisible(hWnd))
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
        };

        NativeMethods.User32.EnumWindows(callback, nint.Zero);
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

        NativeMethods.User32.EnumWindowsProc callback = (hWnd, lParam) =>
        {
            if (visibleOnly && !NativeMethods.User32.IsWindowVisible(hWnd))
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
        };

        NativeMethods.User32.EnumWindows(callback, nint.Zero);
        return windows;
    }

    public WindowInfo? GetForegroundWindow()
    {
        var hWnd = NativeMethods.User32.GetForegroundWindow();
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

    public bool MoveWindow(nint handle, int x, int y)
    {
        if (!IsValidWindow(handle))
            return false;

        if (!NativeMethods.User32.GetWindowRect(handle, out var rect))
            return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        return NativeMethods.User32.SetWindowPos(
            handle,
            nint.Zero,
            x, y, width, height,
            NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE);
    }

    public bool ResizeWindow(nint handle, int width, int height)
    {
        if (!IsValidWindow(handle))
            return false;

        if (!NativeMethods.User32.GetWindowRect(handle, out var rect))
            return false;

        return NativeMethods.User32.SetWindowPos(
            handle,
            nint.Zero,
            rect.Left, rect.Top, width, height,
            NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE);
    }

    public bool SetWindowBounds(nint handle, int x, int y, int width, int height)
    {
        if (!IsValidWindow(handle))
            return false;

        // Restore window first if minimized
        if (NativeMethods.User32.IsIconic(handle))
        {
            NativeMethods.User32.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        return NativeMethods.User32.SetWindowPos(
            handle,
            nint.Zero,
            x, y, width, height,
            NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE);
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

        return NativeMethods.User32.ShowWindow(handle, cmd);
    }

    public bool FocusWindow(nint handle)
    {
        if (!IsValidWindow(handle))
            return false;

        // If window is minimized, restore it first
        if (NativeMethods.User32.IsIconic(handle))
        {
            NativeMethods.User32.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        // Try SetForegroundWindow first
        if (NativeMethods.User32.SetForegroundWindow(handle))
            return true;

        // Fallback: try BringWindowToTop
        NativeMethods.User32.BringWindowToTop(handle);
        return NativeMethods.User32.SetForegroundWindow(handle);
    }

    public bool SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null)
    {
        if (!IsValidWindow(handle))
            return false;

        var targetMonitor = monitorIndex ?? _monitorService.GetMonitorIndex(handle);
        var bounds = _monitorService.CalculateSnapBounds(targetMonitor, position);

        return SetWindowBounds(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public bool MoveWindowToMonitor(nint handle, int monitorIndex, string positioning = "center")
    {
        if (!IsValidWindow(handle))
            return false;

        var monitors = _monitorService.GetAllMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            return false;

        var targetMonitor = monitors[monitorIndex];
        var workArea = targetMonitor.WorkArea;

        // Get current window size
        if (!NativeMethods.User32.GetWindowRect(handle, out var rect))
            return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

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
                return SetWindowBounds(handle, workArea.X, workArea.Y, width, height);

            default:
                x = workArea.X + (workArea.Width - width) / 2;
                y = workArea.Y + (workArea.Height - height) / 2;
                break;
        }

        // Restore if minimized
        if (NativeMethods.User32.IsIconic(handle))
        {
            NativeMethods.User32.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        return NativeMethods.User32.SetWindowPos(
            handle,
            nint.Zero,
            x, y, width, height,
            NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE);
    }

    public bool IsValidWindow(nint handle)
    {
        return handle != nint.Zero && NativeMethods.User32.IsWindow(handle);
    }

    private WindowInfo? GetWindowInfoInternal(nint hWnd)
    {
        // Get window title
        var titleLength = NativeMethods.User32.GetWindowTextLengthW(hWnd);
        var title = string.Empty;
        if (titleLength > 0)
        {
            var titleBuffer = new char[titleLength + 1];
            NativeMethods.User32.GetWindowTextW(hWnd, titleBuffer, titleBuffer.Length);
            title = new string(titleBuffer, 0, titleLength);
        }

        // Skip windows with empty titles (usually not user-facing windows)
        if (string.IsNullOrEmpty(title))
            return null;

        // Get process ID and name
        NativeMethods.User32.GetWindowThreadProcessId(hWnd, out var processId);
        var processName = GetProcessNameFromId((int)processId);

        // Skip our own process
        if (processId == NativeMethods.Kernel32.GetCurrentProcessId())
            return null;

        // Get window rectangle
        if (!NativeMethods.User32.GetWindowRect(hWnd, out var rect))
            return null;

        // Get window state
        var state = WindowState.Normal;
        if (NativeMethods.User32.IsIconic(hWnd))
            state = WindowState.Minimized;
        else if (NativeMethods.User32.IsZoomed(hWnd))
            state = WindowState.Maximized;

        // Get monitor index
        var monitorIndex = _monitorService.GetMonitorIndex(hWnd);

        // Check if this is the foreground window
        var isVisible = NativeMethods.User32.IsWindowVisible(hWnd);

        return new WindowInfo(
            Handle: hWnd,
            Title: title,
            ProcessName: processName,
            ProcessId: (int)processId,
            Bounds: WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
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
