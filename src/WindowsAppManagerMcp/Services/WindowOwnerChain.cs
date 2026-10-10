using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The owner rule that the close guard, the host guard (and with it <c>WindowInfo.IsAgentHost</c>) and
/// the input guard share, so that they agree on which windows belong together.
/// </summary>
/// <remarks>
/// GetAncestor(GA_ROOTOWNER) walks the chain GetParent returns. GetParent returns the owner only for a
/// top-level window with WS_POPUP, so a Win32 dialog resolves to its owner, but an owned overlapped
/// window (a WPF Window or WinForms Form with an owner, owned through GWLP_HWNDPARENT) resolves to
/// itself. GetWindow(GW_OWNER) returns the owner in both cases, so the chain follows GW_OWNER from the
/// top-level window and also includes whatever GA_ROOTOWNER returns. The walk goes up only: a window's
/// owner is in its chain, never its own dialogs or its siblings.
/// </remarks>
public static class WindowOwnerChain
{
    /// <summary>Owner chains are short; this only stops a loop on a corrupt chain.</summary>
    public const int MaxOwners = 32;

    /// <summary>
    /// <paramref name="window"/>, its top-level window (GA_ROOT) and every window that owns it, directly
    /// or through other owned windows, nearest first, without repeats. Empty for a zero handle. Only
    /// reads window relations.
    /// </summary>
    public static IReadOnlyList<nint> SelfAndOwners(INativeWindowWrapper windows, nint window)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var chain = new List<nint>();
        if (window == nint.Zero)
        {
            return chain;
        }

        chain.Add(window);
        var owners = 0;

        var root = windows.GetAncestor(window, WindowAncestor.Root);
        WalkUpFrom(root != nint.Zero ? root : window);

        // GA_ROOTOWNER can name an owner the GW_OWNER walk did not reach (it also follows parents)
        var rootOwner = windows.GetAncestor(window, WindowAncestor.RootOwner);
        if (rootOwner != nint.Zero && !chain.Contains(rootOwner))
        {
            WalkUpFrom(rootOwner);
        }

        return chain;

        void WalkUpFrom(nint start)
        {
            var current = start;
            if (!chain.Contains(current))
            {
                chain.Add(current);
            }

            while (owners < MaxOwners)
            {
                var owner = windows.GetWindow(current, NativeEnums.GW_OWNER);
                if (owner == nint.Zero || chain.Contains(owner))
                {
                    return;
                }

                chain.Add(owner);
                owners++;
                current = owner;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="owner"/> is <paramref name="window"/>, its top-level window, or a window
    /// that owns it (see <see cref="SelfAndOwners"/>).
    /// </summary>
    public static bool IsOwnedBy(INativeWindowWrapper windows, nint window, nint owner) =>
        owner != nint.Zero && SelfAndOwners(windows, window).Contains(owner);

    /// <summary>
    /// The unowned window at the top of <paramref name="window"/>'s chain: the application window that a
    /// dialog, popup or child control belongs to. <paramref name="window"/> itself when nothing owns it.
    /// </summary>
    public static nint TopOwner(INativeWindowWrapper windows, nint window)
    {
        var chain = SelfAndOwners(windows, window);
        return chain.Count == 0 ? nint.Zero : chain[^1];
    }
}
