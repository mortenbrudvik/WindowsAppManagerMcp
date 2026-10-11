using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Read-only window hit testing: which window is at a point, and which top-level window a window
/// belongs to. Coordinates are physical virtual-screen pixels, the same space as click coordinates.
/// </summary>
public interface INativeHitTestWrapper
{
    /// <summary>
    /// The deepest window at the point (WindowFromPoint), or 0 when there is none. Hidden windows and
    /// disabled child windows are skipped; a disabled top-level window (the owner of a modal dialog)
    /// is still returned. On a monitor some window is practically always hit, since the desktop is
    /// under everything. It does not wait for the window's application.
    /// </summary>
    nint WindowFromPoint(int x, int y);

    /// <summary>
    /// The top-level window that contains <paramref name="windowHandle"/> (GetAncestor with GA_ROOT),
    /// or 0 when the handle is not a window. Owned windows such as dialogs and menus are their own
    /// root: GA_ROOT follows the parent chain, not the owner.
    /// </summary>
    nint GetRootWindow(nint windowHandle);

    /// <summary>
    /// The window's client area on screen (GetClientRect, then MapWindowPoints to the screen), in physical
    /// virtual-screen pixels: where scroll points by default. Null when the handle is not a window or the area cannot
    /// be read; a minimized window has an empty area. It does not wait for the window's application.
    /// </summary>
    WindowRect? GetClientAreaOnScreen(nint windowHandle);
}
