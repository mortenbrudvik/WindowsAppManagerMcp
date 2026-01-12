using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IWindowService
{
    IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true);

    IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true);

    WindowInfo? GetForegroundWindow();

    WindowInfo? GetWindowInfo(nint handle);

    bool MoveWindow(nint handle, int x, int y);

    bool ResizeWindow(nint handle, int width, int height);

    bool SetWindowBounds(nint handle, int x, int y, int width, int height);

    bool SetWindowState(nint handle, WindowState state);

    bool FocusWindow(nint handle);

    bool SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null);

    bool MoveWindowToMonitor(nint handle, int monitorIndex, string positioning = "center");

    bool IsValidWindow(nint handle);
}
