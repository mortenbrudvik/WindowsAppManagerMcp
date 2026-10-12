using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Tells which top-level windows of an application appeared during an action (roadmap M3.6: element_action's openedPopup).
/// A plain before/after comparison of the application's visible top-level windows through Win32 reads; the popup-aware
/// search of roadmap M3.5 can replace it behind this interface.
/// </summary>
public interface IAppWindowProbe
{
    /// <summary>The visible top-level windows of <paramref name="processIds"/> now.</summary>
    AppWindowSnapshot Snapshot(IReadOnlyCollection<int> processIds);

    /// <summary>
    /// The visible top-level windows of the snapshot's processes that are not in it, in z-order (topmost first), each
    /// described. Empty when none appeared.
    /// </summary>
    IReadOnlyList<OpenedWindow> OpenedSince(AppWindowSnapshot before);
}

/// <summary>The visible top-level windows of some processes at one moment.</summary>
public sealed record AppWindowSnapshot(IReadOnlySet<int> ProcessIds, IReadOnlySet<nint> Windows);
