using System.Runtime.InteropServices;
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

    /// <summary>
    /// Centres <paramref name="window"/> in the work area of the first monitor whose scale differs from the primary's (roadmap
    /// M3.F30), and returns true; false, leaving the window where it is, when every monitor has the primary's scale. Call it
    /// once its Width and Height are set. WPF still creates the window at the primary's DPI and moves it there inside Show,
    /// with WM_DPICHANGED, whose handler activates it: show it inside a <see cref="NoActivateWhileShown"/> so that a
    /// --no-activate fixture does not take the foreground.
    /// </summary>
    public static bool CenterOnMonitorAtAnotherScale(Window window)
    {
        var primaryDpi = NativeMethods.GetDpiForSystem();
        NativeMethods.RECT? workArea = null;
        uint dpi = 0;
        NativeMethods.EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info) &&
                NativeMethods.GetDpiForMonitor(monitor, NativeMethods.EffectiveDpi, out var monitorDpi, out _) == 0 &&
                monitorDpi != primaryDpi)
            {
                workArea = info.WorkArea;
                dpi = monitorDpi;
                return false;
            }

            return true;
        }, 0);
        if (workArea is not { } area)
        {
            return false;
        }

        // The window's size there in physical pixels, and its corner in device-independent units at the system DPI, which
        // is what WPF converts Left and Top with when it creates the window
        var width = window.Width * dpi / 96.0;
        var height = window.Height * dpi / 96.0;
        var scale = primaryDpi / 96.0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = (area.Left + (area.Right - area.Left - width) / 2) / scale;
        window.Top = (area.Top + (area.Bottom - area.Top - height) / 2) / scale;
        return true;
    }
}
