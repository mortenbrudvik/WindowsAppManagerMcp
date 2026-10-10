namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Which ancestor <see cref="INativeWindowWrapper.GetAncestor"/> returns. The values are the
/// GetAncestor flags (GA_PARENT, GA_ROOT, GA_ROOTOWNER).
/// </summary>
public enum WindowAncestor : uint
{
    /// <summary>The parent window (not the owner).</summary>
    Parent = 1,

    /// <summary>The top-level window, walking parents only.</summary>
    Root = 2,

    /// <summary>
    /// The owned root: the window found by walking the chain GetParent returns, which is the parent
    /// of a child window and the owner of a WS_POPUP top-level window. A Win32 dialog or popup owned by
    /// an application window resolves to that application window, but an owned window without WS_POPUP
    /// (WPF and WinForms owned windows) resolves to itself; walk GetWindow(GW_OWNER) to find its owner.
    /// </summary>
    RootOwner = 3
}
