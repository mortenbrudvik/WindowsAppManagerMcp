namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Read-only window hit testing: which window is at a point, and which top-level window a window
/// belongs to. Coordinates are physical virtual-screen pixels, the same space as click coordinates.
/// </summary>
public interface INativeHitTestWrapper
{
    /// <summary>
    /// The deepest visible, enabled window at the point (WindowFromPoint), or 0 when there is none.
    /// Hidden and disabled windows are skipped.
    /// </summary>
    nint WindowFromPoint(int x, int y);

    /// <summary>
    /// The top-level window that contains <paramref name="windowHandle"/> (GetAncestor with GA_ROOT),
    /// or 0 when the handle is not a window. Owned windows such as dialogs and menus are their own
    /// root: GA_ROOT follows the parent chain, not the owner.
    /// </summary>
    nint GetRootWindow(nint windowHandle);
}
