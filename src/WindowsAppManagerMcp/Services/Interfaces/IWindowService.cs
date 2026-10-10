using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IWindowService
{
    /// <summary>Lists the visible user-facing app windows (see <see cref="IsUserFacingAppWindow"/>), in z-order.</summary>
    IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true);

    /// <summary>
    /// Shorthand for <see cref="FindWindows(WindowQuery)"/> with these filters. A handle skips the
    /// user-facing filter.
    /// </summary>
    IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true);

    /// <summary>
    /// Finds the windows that match every filter of <paramref name="query"/>, in z-order. Lists only
    /// user-facing app windows unless the query names a handle or class name or sets IncludeShell.
    /// </summary>
    IReadOnlyList<WindowInfo> FindWindows(WindowQuery query);

    /// <summary>
    /// The foreground window, read without any listing filter: it can be untitled (a menu, a popup,
    /// the taskbar). Null only when no window is in the foreground.
    /// </summary>
    WindowInfo? GetForegroundWindow();

    /// <summary>
    /// Reads any window by handle, without the listing filter: untitled, cloaked, tool and shell
    /// windows are described too, with their ClassName. Null when the handle is not a window.
    /// </summary>
    WindowInfo? GetWindowInfo(nint handle);

    /// <summary>
    /// The listing predicate: the window is visible, not cloaked, not a tool window (unless it also has
    /// WS_EX_APPWINDOW), not the desktop or a taskbar, and has a title. Owned windows such as dialogs
    /// count as user-facing.
    /// </summary>
    bool IsUserFacingAppWindow(nint handle);

    bool MoveWindow(nint handle, int x, int y, bool bringToFront = true);

    bool ResizeWindow(nint handle, int width, int height);

    bool SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true);

    bool SetWindowState(nint handle, WindowState state);

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

    IReadOnlyList<(nint Handle, bool Success, string? Error)> CloseWindowsBatch(
        IReadOnlyList<nint> handles,
        int delayBetweenMs = 50);
}
