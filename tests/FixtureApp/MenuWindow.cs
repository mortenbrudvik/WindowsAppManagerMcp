using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// A separate, unowned top-level window with a Win32 menu bar (<see cref="Win32Menu"/>, roadmap M3.F4): a real HMENU set
/// with SetMenu, so UI Automation reads it through Windows' own menu proxy and every menu that opens is a #32768 popup
/// window, as in Notepad or any Win32 app, where a WPF Menu would open WPF popups. Each command it runs is written to the
/// event log.
/// </summary>
/// <remarks>
/// The bar is on a window of its own, not on the main window, so the main window's tree and client area stay what the
/// other scenarios compare.
/// </remarks>
internal sealed class MenuWindow : Window
{
    private readonly FixtureEventLog _events;

    public MenuWindow(FixtureOptions options, FixtureEventLog events)
    {
        _events = events;

        Title = MenuTitle(options.Instance);
        Width = 420;
        Height = 240;
        FixturePlacement.CenterOnPrimaryMonitor(this);
        ShowActivated = !options.NoActivate;
        Content = new TextBlock
        {
            Text = "A Win32 menu bar: each menu it opens is a #32768 popup window.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };

        SourceInitialized += (_, _) => AttachMenuBar();
        ContentRendered += (_, _) => events.Write(Events.MenuWindowOpened, ("hwnd", (long)Handle));
    }

    private nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// Builds the menu bar and sets it on the window before it is first shown. The window destroys the menus with itself.
    /// </summary>
    private void AttachMenuBar()
    {
        var export = Popup(
            (Win32Menu.ExportAsText, Win32Menu.ExportAsTextCommand),
            (Win32Menu.ExportAsCsv, Win32Menu.ExportAsCsvCommand));
        var file = Popup((Win32Menu.New, Win32Menu.NewCommand));
        Append(file, NativeMethods.MF_POPUP, (nuint)export, Win32Menu.Export);
        var edit = Popup((Win32Menu.SelectAll, Win32Menu.SelectAllCommand));

        var bar = NativeMethods.CreateMenu();
        Append(bar, NativeMethods.MF_POPUP, (nuint)file, Win32Menu.File);
        Append(bar, NativeMethods.MF_POPUP, (nuint)edit, Win32Menu.Edit);
        if (!NativeMethods.SetMenu(Handle, bar))
        {
            throw new InvalidOperationException($"SetMenu failed with error {Marshal.GetLastWin32Error()}.");
        }

        HwndSource.FromHwnd(Handle).AddHook(OnMessage);
    }

    private static nint Popup(params (string Text, int Command)[] commands)
    {
        var menu = NativeMethods.CreatePopupMenu();
        foreach (var (text, command) in commands)
        {
            Append(menu, NativeMethods.MF_STRING, (nuint)command, text);
        }

        return menu;
    }

    private static void Append(nint menu, uint flags, nuint idOrSubmenu, string text)
    {
        if (menu == nint.Zero || !NativeMethods.AppendMenu(menu, flags, idOrSubmenu, text))
        {
            throw new InvalidOperationException(
                $"Could not build the menu item '{text}': error {Marshal.GetLastWin32Error()}.");
        }
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // A menu's WM_COMMAND: the command id in the low word of wParam, zero in the high word, no control in lParam
        if (message == NativeMethods.WM_COMMAND && lParam == nint.Zero && ((long)wParam >> 16 & 0xFFFF) == 0 &&
            Win32Menu.CommandPaths.TryGetValue((int)((long)wParam & 0xFFFF), out var path))
        {
            _events.Write(Events.MenuCommand, ("command", (int)((long)wParam & 0xFFFF)), ("path", path));
            handled = true;
        }

        return nint.Zero;
    }
}
