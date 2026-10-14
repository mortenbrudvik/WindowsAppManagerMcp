using System.Text;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// Keeps the windows that WPF creates on the calling thread from being activated until <see cref="Release"/> (roadmap M3.F30):
/// a WH_CBT hook gives each new WPF window (class HwndWrapper[...]) the extended style WS_EX_NOACTIVATE, and Release puts
/// every such window's style back. A window placed on a monitor at another scale gets WM_DPICHANGED inside Show, whose
/// handler resizes it with a SetWindowPos that activates it, ShowActivated = false notwithstanding; marking that call or its
/// WM_WINDOWPOSCHANGING SWP_NOACTIVATE does not stop it, and refusing HCBT_ACTIVATE leaves no window in front, because the
/// foreground has moved by then. A window with WS_EX_NOACTIVATE is not made the foreground window at all, so the terminal
/// that runs the tests keeps it (checked on a 150% primary with a 100% second monitor).
/// </summary>
internal sealed class NoActivateWhileShown
{
    // Held for as long as the hook is installed, so the delegate the hook calls is not collected
    private readonly NativeMethods.HookProc _callback;
    private readonly Dictionary<nint, nint> _styles = [];
    private nint _hook;

    public NoActivateWhileShown()
    {
        _callback = OnCbt;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_CBT, _callback, 0, NativeMethods.GetCurrentThreadId());
    }

    /// <summary>Removes the hook and gives the windows created meanwhile their extended styles back, so they can be activated.</summary>
    public void Release()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        foreach (var (window, style) in _styles)
        {
            NativeMethods.SetWindowLongPtr(window, NativeMethods.GWL_EXSTYLE, style);
        }

        _styles.Clear();
    }

    private nint OnCbt(int code, nint wParam, nint lParam)
    {
        if (code == NativeMethods.HCBT_CREATEWND)
        {
            var name = new StringBuilder(64);
            NativeMethods.GetClassName(wParam, name, name.Capacity);
            if (name.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal))
            {
                var style = NativeMethods.GetWindowLongPtr(wParam, NativeMethods.GWL_EXSTYLE);
                _styles[wParam] = style;
                NativeMethods.SetWindowLongPtr(wParam, NativeMethods.GWL_EXSTYLE, (nint)((long)style | NativeMethods.WS_EX_NOACTIVATE));
            }
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }
}
