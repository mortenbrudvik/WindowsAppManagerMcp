using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The top-level windows Windows draws on the screen, front to back: visible, not minimized and not cloaked. The screen reads
/// walk them for the windows that matter to each: <see cref="DeniedWindowMask"/> for the windows the policy denies and those
/// that cover them (owner decision D8), <see cref="DisplayAffinityScan"/> for the windows that keep themselves out of captures
/// (issue #17).
/// </summary>
public static class DrawnWindows
{
    /// <summary>
    /// The drawn top-level windows in z-order, front to back, from one EnumWindows pass. <paramref name="where"/>, when given,
    /// is asked of a visible window before it is read further, so a reader that wants one kind of window reads little of the
    /// others. A window that closes during the walk drops out, as one that is not drawn.
    /// </summary>
    public static IEnumerable<nint> FrontToBack(INativeWindowWrapper windows, Func<nint, bool>? where = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var topLevel = new List<nint>();
        windows.EnumWindows(handle =>
        {
            topLevel.Add(handle);
            return true;
        });

        return topLevel.Where(handle => windows.IsWindowVisible(handle) && (where == null || where(handle)) &&
                                        !windows.IsIconic(handle) && !windows.IsCloaked(handle));
    }
}
