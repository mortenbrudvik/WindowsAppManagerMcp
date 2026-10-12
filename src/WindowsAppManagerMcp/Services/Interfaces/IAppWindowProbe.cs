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
    /// The visible top-level windows of the snapshot's processes that are not in it, each described, in z-order (topmost
    /// first): those that can be a dialog an action waits for apart from the others. Windows no action opens for the agent
    /// are left out: tooltips (whatever shows them, also a thread of the application other than the busy one), windows
    /// without a size, and disabled or cloaked ones.
    /// </summary>
    AppWindowsOpened OpenedSince(AppWindowSnapshot before);
}

/// <summary>The visible top-level windows of some processes at one moment.</summary>
public sealed record AppWindowSnapshot(IReadOnlySet<int> ProcessIds, IReadOnlySet<nint> Windows);

/// <summary>The windows of an application that appeared during an action (<see cref="IAppWindowProbe.OpenedSince"/>).</summary>
/// <param name="Dialogs">Those that can be a dialog the action waits for, topmost first: a dialog box (#32770), a window with a
/// title bar that is no tool window, or a window whose owner is disabled (a modal dialog disables the window that owns it).</param>
/// <param name="Others">The others (a menu, a dropdown, a hover card), topmost first.</param>
public sealed record AppWindowsOpened(IReadOnlyList<OpenedWindow> Dialogs, IReadOnlyList<OpenedWindow> Others)
{
    /// <summary>No window appeared.</summary>
    public static AppWindowsOpened None { get; } = new([], []);

    /// <summary>Every window that appeared: the dialogs first, then the others.</summary>
    public IReadOnlyList<OpenedWindow> All => [.. Dialogs, .. Others];
}
