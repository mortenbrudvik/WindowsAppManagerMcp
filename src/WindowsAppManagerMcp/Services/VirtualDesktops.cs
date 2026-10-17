using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Where cloaked windows are, for the listings and the refusals (roadmap M7.3). Detection only: the server never switches
/// the user's virtual desktop or moves a window to another one.
/// </summary>
internal static class VirtualDesktops
{
    /// <summary>
    /// Where the shell's virtual desktop manager puts <paramref name="topLevelWindow"/>; Unknown without a manager, or
    /// when it cannot say.
    /// </summary>
    public static VirtualDesktopPlacement PlacementOf(INativeVirtualDesktopWrapper? desktops, nint topLevelWindow) =>
        desktops?.GetPlacements([topLevelWindow]) is [var placement] ? placement : VirtualDesktopPlacement.Unknown;

    /// <summary>
    /// Those of <paramref name="windows"/> the manager puts on another virtual desktop, asked in one call. Empty without
    /// a manager or windows; a window it cannot place is not in the set.
    /// </summary>
    public static IReadOnlySet<nint> OnOtherDesktops(INativeVirtualDesktopWrapper? desktops, IReadOnlyList<nint> windows)
    {
        if (desktops == null || windows.Count == 0)
        {
            return new HashSet<nint>();
        }

        var placements = desktops.GetPlacements(windows);
        return windows.Where((_, i) => i < placements.Count && placements[i] == VirtualDesktopPlacement.OtherDesktop).ToHashSet();
    }
}
