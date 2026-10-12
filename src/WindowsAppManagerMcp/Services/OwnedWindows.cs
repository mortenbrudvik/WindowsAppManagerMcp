using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The windows a top-level window owns, as UI Automation shows them (M3 review). The HWND proxy lists a window whose visible
/// owner is the top-level window (a dialog, a menu, a tooltip) as a child of the owner's element, ahead of the owner's own
/// children, with its whole subtree. So a search of the owner would find the dialog's elements as the owner's, and a search
/// of the owner and its popups would find them twice. The searches of a window (find_ui_elements' search and its proof that
/// a match is unique, searchVirtualized's lists, and the element registry's searches when it finds an element again) leave
/// such a child of the window's root out (<see cref="ElementSearch"/>): an owned window is searched only as its own window,
/// a popup of the windowAndPopups scope or a window of the desktop scope.
/// </summary>
/// <remarks>Only Win32 reads that wait for no application: GetWindow(GW_OWNER) of the children that have a window
/// handle of their own. A child window (WS_CHILD, such as an edit control) has no owner, so it stays in the search.</remarks>
public static class OwnedWindows
{
    /// <summary>
    /// The test of a child of <paramref name="window"/>'s root element: true for a window that <paramref name="window"/>
    /// owns. Null without the Win32 reads or a window, so nothing is left out.
    /// </summary>
    public static Func<UiaElement, bool>? Below(INativeWindowWrapper? windows, nint window) =>
        windows == null || window == nint.Zero ? null : child => IsOwnedBy(windows, child.NativeWindowHandle, window);

    private static bool IsOwnedBy(INativeWindowWrapper windows, nint child, nint owner)
    {
        if (child == nint.Zero || child == owner)
        {
            return false;
        }

        try
        {
            return windows.GetWindow(child, NativeEnums.GW_OWNER) == owner;
        }
        catch (Exception)
        {
            // Not known to be owned: searched with the window, as before
            return false;
        }
    }
}
