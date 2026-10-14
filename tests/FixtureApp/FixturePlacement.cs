using System.Windows;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>Where the fixture's top-level windows open.</summary>
internal static class FixturePlacement
{
    /// <summary>
    /// Centres <paramref name="window"/> in the primary monitor's work area; call it once its Width and Height are set.
    /// Not CenterScreen, which picks the monitor under the mouse: Windows creates the window on the primary, at the
    /// primary's DPI, and a per-monitor DPI aware window that WPF then places on a monitor with another scale gets
    /// WM_DPICHANGED inside Show. WPF's handler resizes it with a SetWindowPos that activates it, so a --no-activate
    /// fixture could take the foreground from the terminal that runs the tests (ShowActivated = false does not stop it).
    /// </summary>
    public static void CenterOnPrimaryMonitor(Window window)
    {
        // The primary monitor's work area, in device-independent units at the system DPI (the primary's)
        var workArea = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = workArea.Left + (workArea.Width - window.Width) / 2;
        window.Top = workArea.Top + (workArea.Height - window.Height) / 2;
    }
}
