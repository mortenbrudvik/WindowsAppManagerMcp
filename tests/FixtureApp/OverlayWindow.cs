using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// A small WS_EX_TOPMOST tool window of the fixture itself (roadmap M6.7), always centred on the primary monitor, so z-order
/// checks have a topmost window to compare with that is not the user's. That is over the main window's middle only while the
/// main window is on the primary too: with --other-scale-monitor the main window may open on another monitor, and the
/// overlay does not follow it. It is unowned: an owned
/// window always stays above its owner, which would tie its z-order to the main window's. It stays in the taskbar's
/// default (ShowInTaskbar), because WPF gives a window that is kept out of the taskbar a hidden owner window. With
/// <see cref="FixtureOptions.ExcludeOverlayFromCapture"/> it sets WDA_EXCLUDEFROMCAPTURE on itself once it has a handle
/// (issue #17), as a toast that keeps out of screen shares does, and reports the affinity Windows reads back.
/// </summary>
internal sealed class OverlayWindow : Window
{
    public OverlayWindow(FixtureOptions options, FixtureEventLog events)
    {
        Title = OverlayTitle(options.Instance);
        Width = 260;
        Height = 120;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        FixturePlacement.CenterOnPrimaryMonitor(this);
        ShowActivated = !options.NoActivate;
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xC0));
        Content = new TextBlock { Text = "Topmost overlay", Margin = new Thickness(12) };

        // Before it is shown, so no frame of it is ever drawn into a capture
        var affinitySet = false;
        SourceInitialized += (_, _) =>
        {
            if (options.ExcludeOverlayFromCapture)
            {
                affinitySet = NativeMethods.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            }
        };

        ContentRendered += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            events.Write(
                Events.OverlayOpened,
                ("hwnd", (long)hwnd),
                ("topmost", (exStyle & NativeMethods.WS_EX_TOPMOST) != 0),
                ("toolWindow", (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0),
                ("ownerHwnd", (long)NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER)),
                ("affinitySet", affinitySet),
                ("displayAffinity", NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity) ? (long)affinity : -1L));
        };
    }
}
