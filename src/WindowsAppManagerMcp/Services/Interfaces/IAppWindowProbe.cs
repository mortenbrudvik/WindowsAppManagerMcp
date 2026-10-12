using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Tells which top-level windows of an application appeared during an action (roadmap M3.6: element_action's openedPopup).
/// A plain before/after comparison of the application's visible top-level windows through Win32 reads.
/// </summary>
/// <remarks>
/// <para><b>Why not <see cref="IPopupService"/> (decided when M3.5 and M3.6 were integrated).</b> The popup service answers
/// "which popups belong to this one window", while openedPopup asks "which window of the application did this action open",
/// and the two differ where it matters most. (1) An element of an open menu has a ref registered under the menu's own
/// window, so element_action's window is the menu; the dialog its item opens is owned by the app's main window, which the
/// popup service attributes to that main window, never to the menu, so GetPopups(menu) and WaitForNewPopupAsync(menu) would
/// miss it, and InvokedPendingModal for a menu item that opens a modal dialog would turn into ActionTimedOut. (2) A new
/// main window (New Window, Open in new window) is by design no popup of another main window. (3) An element of another
/// process (a child or owned window of another program) has its own process watched here. Swapping in the popup service
/// would need an "app window of this popup" mapping that the service does not have; this probe stays until it does.</para>
/// </remarks>
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
