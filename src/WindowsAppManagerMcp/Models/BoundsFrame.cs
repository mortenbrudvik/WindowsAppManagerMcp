namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Which rectangle of a window a placement's coordinates describe (roadmap M2.3).
/// </summary>
public enum BoundsFrame
{
    /// <summary>
    /// The window rect (GetWindowRect), with the invisible resize borders: the rect find_windows reports as bounds and
    /// layouts save and apply (IWindowService.SetWindowBounds).
    /// </summary>
    Outer,

    /// <summary>
    /// The visible frame (DWMWA_EXTENDED_FRAME_BOUNDS), as the user sees the window: the geometry tools place and report
    /// it (IWindowService.SetWindowFrame, MoveWindow, ResizeWindow, SnapWindow, MoveWindowToMonitor), so windows meet the
    /// work area's edges and each other without the gap of the invisible borders.
    /// </summary>
    Visible
}
