using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The open popups of a window (roadmap M3.5): its menus (#32768), combo box dropdowns (ComboLBox), WPF popups, WinUI
/// flyouts, dialogs and the popups a WebView2 child process draws. They are top-level windows of their own, outside the
/// UI Automation subtree of the window that opened them, so a search of that window alone cannot find "Delete" in its
/// context menu. find_ui_elements' windowAndPopups scope searches them (and so do the criteria of read_element_text,
/// wait_for_element and element_action, which share its search), and get_active_popups lists them. element_action's
/// openedPopup does not use this service: see <see cref="IAppWindowProbe"/> for why.
/// </summary>
/// <remarks>
/// <para><b>Which windows</b> (<see cref="GetPopups"/>): the visible, uncloaked top-level windows with a size, other than the
/// target, that (1) the target owns, directly or through other owned windows (GW_OWNER, which includes its enabled popup,
/// GW_ENABLEDPOPUP, and an owned window of another process); or (2) belong to the target's process (for a UWP frame, the
/// app's process, not the ApplicationFrameHost every UWP app shares; one process has one or more threads, so this covers
/// the target's thread) and are not tied to another of its main windows: unowned windows with no title bar or tool
/// windows (a context menu, a dropdown, a palette), unowned dialog boxes (#32770) while the process shows no main window
/// other than the target, and windows owned by a hidden or popup-styled window (a WinForms parking window, a Delphi or Qt
/// application window); or (3) are popups (no title bar, or a tool window) of a direct child process of the target's
/// (msedgewebview2.exe for a WebView2 pane), checked against one process snapshot with the start times, so a reused process
/// id does not count, and never for the desktop shell (explorer.exe starts almost every program the user opens). A window
/// owned by another main window of the process (a dialog or popup of a second Word, Explorer or Notepad window) is that
/// window's, never the target's, and another main window itself is not a popup. The desktop, the taskbars and the hidden
/// input helper windows (IME, MSCTFIME UI, GDI+ hook) are never popups. Ordered front to back (z-order), at most
/// <see cref="PopupService.MaxPopups"/>.</para>
/// <para><b>Against the input guard.</b> The guard's test for a window that belongs to the target
/// (InputTargetGuard.BelongsToTarget) answers where a click may land, so it is wider: it accepts any popup-styled window of
/// the target's process, also one that another main window owns, and it does not accept a captioned dialog box that the
/// target does not own. Every popup this service names that has no title bar, or that the target owns, the guard accepts
/// too; an unowned captioned dialog box or a captioned window owned by a hidden window it may refuse (TargetOccluded), so
/// act on such a dialog by naming it as the target.</para>
/// <para><b>Cost.</b> <see cref="GetPopups"/>, <see cref="IsInForeground"/> and <see cref="GetDesktopWindows"/> only read
/// Win32 state that Windows answers without the windows' threads (no UI Automation call, no message to a window): call them
/// on any thread. The child-process rule takes one process snapshot, and only when a window of another process could be
/// such a popup. <see cref="GetActivePopupsAsync"/> reads UI Automation on the worker.</para>
/// <para><b>Refs.</b> An element of a popup is registered under the popup's own top-level window
/// (<see cref="PopupWindow.Handle"/>), the window a resolution searches: a ref of a menu item becomes ElementStale when
/// the menu closes.</para>
/// <para><b>Waiting for a popup an action opens.</b> Take <see cref="GetPopups"/> before acting, act, then
/// <see cref="WaitForNewPopupAsync"/> with the handles seen before: it returns the first popup that was not there, or null
/// when none opened in the time given. A popup does not need the input gate or the worker: none of these members sends
/// input or changes a window.</para>
/// </remarks>
public interface IPopupService
{
    /// <summary>The open popups of <paramref name="window"/>, front first (see the remarks); empty when it is no window.</summary>
    IReadOnlyList<PopupWindow> GetPopups(nint window);

    /// <summary>
    /// <see cref="GetPopups"/>, and whether more popups were open than it lists (find_ui_elements reports that), with each
    /// popup's <see cref="PopupWindow.IsAgentHost"/> and <see cref="PopupWindow.IsDeniedByPolicy"/> set: the reads of a window
    /// and its popups (find_ui_elements' windowAndPopups scope, get_desktop_state's walk) leave those popups out and count
    /// them (<see cref="PopupList.ForReading"/>). The dialog tools use <see cref="GetPopups"/>, which keeps them, so a denied
    /// dialog is found and then refused as a target rather than missed.
    /// </summary>
    PopupList GetPopupList(nint window);

    /// <summary>
    /// True when the window in front is <paramref name="window"/>, a window it owns (a dialog it opened), or another window
    /// of its process (for a UWP frame, of its app's process): the default scope of find_ui_elements is then
    /// windowAndPopups, since a menu or dropdown of an app that is not in front has closed.
    /// </summary>
    bool IsInForeground(nint window);

    /// <summary>
    /// The windows a desktop-scope search covers: the visible, uncloaked top-level windows with a size, front first, at most
    /// <paramref name="limit"/>. <paramref name="first"/> (the window the caller named, or zero) comes first and is never
    /// excluded: the search refuses a named window the target policy denies before it asks for the windows, and a named host
    /// window is read as find_ui_elements on a handle reads it. The others are left out when the target policy denies their
    /// process (its deny list, or an allow list without it) or when they are the agent's own host windows (its terminal or
    /// editor, and the popups they own), the rule the read tools keep for a window nobody named. The server's own windows and
    /// the input helper windows are never searched.
    /// </summary>
    DesktopWindowSet GetDesktopWindows(nint first, int limit);

    /// <summary>
    /// get_active_popups: the popups of <paramref name="window"/> (<see cref="GetPopups"/>), each with the ref of its own
    /// element (registered under the popup's window) and its UI Automation control type, which also sharpens its kind (a
    /// root Menu is a menu, a List a dropdown). Runs the UI Automation reads on the worker under
    /// <paramref name="timeout"/>; a popup that does not respond, or that closed meanwhile, is returned without a ref.
    /// A popup of the agent's host or of an app the target policy denies is neither listed nor read, and is counted
    /// (PopupsExcluded). Fails with InvalidHandle for a handle that is no window, with WindowNotFound when
    /// <paramref name="window"/> is zero and no window is in front, and with TargetDeniedByPolicy for a window named whose
    /// app the policy denies.
    /// </summary>
    /// <param name="window">The app window, or zero for the app in front: the window that owns the foreground window
    /// (WindowOwnerChain.TopOwner), so a dialog in front is listed as a popup of its app.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ActivePopupsResult> GetActivePopupsAsync(nint window, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Waits up to <paramref name="wait"/> for a popup of <paramref name="window"/> whose handle is not in
    /// <paramref name="before"/> (the handles of <see cref="GetPopups"/> taken before acting), checking every 50 ms. Returns
    /// the front one, or null when none opened in time. Tooltips are never returned: one that shows because the pointer
    /// rests on the control is not what the action opened. The target's child processes (one process snapshot) are read
    /// again at most once a second (<see cref="PopupService.ChildProcessRefresh"/>), not on every check. Only reads Win32
    /// state.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<PopupWindow?> WaitForNewPopupAsync(
        nint window, IReadOnlySet<nint> before, TimeSpan wait, CancellationToken cancellationToken);
}
