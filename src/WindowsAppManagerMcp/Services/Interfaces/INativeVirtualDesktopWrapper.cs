namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>Where the shell's virtual desktop manager puts a top-level window (<see cref="INativeVirtualDesktopWrapper"/>).</summary>
public enum VirtualDesktopPlacement
{
    /// <summary>The manager could not be asked, failed, or did not answer in time.</summary>
    Unknown,

    /// <summary>On the virtual desktop the user sees now.</summary>
    CurrentDesktop,

    /// <summary>On another virtual desktop.</summary>
    OtherDesktop,

    /// <summary>
    /// On no virtual desktop: a window the manager does not track, such as the frame of a suspended or prelaunched Store
    /// app, which the shell cloaks as it cloaks a window on another desktop.
    /// </summary>
    NoDesktop
}

/// <summary>
/// The shell's virtual desktop manager (IVirtualDesktopManager), behind a seam the tests replace with a fake. It only
/// reads: the interface the server declares has no way to move a window to another desktop.
/// </summary>
public interface INativeVirtualDesktopWrapper
{
    /// <summary>
    /// Where each of <paramref name="windows"/> is, in the same order (IsWindowOnCurrentVirtualDesktop, then
    /// GetWindowDesktopId for a window that is not on the current desktop). Never throws: a window whose placement cannot
    /// be read is <see cref="VirtualDesktopPlacement.Unknown"/>, and all of them are when the manager is not available or
    /// does not answer within a deadline (it answers from the shell's process, which can hang).
    /// </summary>
    IReadOnlyList<VirtualDesktopPlacement> GetPlacements(IReadOnlyList<nint> windows);
}
