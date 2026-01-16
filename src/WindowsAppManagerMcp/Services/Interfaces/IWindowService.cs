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

    bool MoveWindow(nint handle, int x, int y, bool bringToFront = true);

    bool ResizeWindow(nint handle, int width, int height);

    bool SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true);

    bool SetWindowState(nint handle, WindowState state);

    bool FocusWindow(nint handle);

    bool SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true);

    bool MoveWindowToMonitor(nint handle, int monitorIndex, string positioning = "center", bool bringToFront = true);

    bool IsValidWindow(nint handle);

    bool CloseWindow(nint handle);

    // Batch operations
    IReadOnlyList<(nint Handle, bool Success, string? Error)> SetWindowBoundsBatch(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50);

    IReadOnlyList<(nint Handle, bool Success, string? Error)> SnapWindowsBatch(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50);

    IReadOnlyList<(nint Handle, bool Success, string? Error)> SetWindowStateBatch(
        IReadOnlyList<(nint Handle, WindowState State)> changes,
        int delayBetweenMs = 30);
}
