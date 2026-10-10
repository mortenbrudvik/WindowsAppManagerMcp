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
    /// The owned root: the top-level window found by walking parents and owners, so a dialog or
    /// popup owned by an application window resolves to that application window.
    /// </summary>
    RootOwner = 3
}
