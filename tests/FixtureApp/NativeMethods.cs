using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// The few user32 calls the fixture needs: its DPI awareness, its own geometry for the ready event, and the Win32 menu bar
/// of its menu window.
/// </summary>
internal static class NativeMethods
{
    public static readonly nint DpiAwarenessContextUnaware = -1;
    public static readonly nint DpiAwarenessContextSystemAware = -2;
    public static readonly nint DpiAwarenessContextPerMonitorAware = -3;
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    public const int WM_COMMAND = 0x0111;
    public const uint MF_STRING = 0x0000;
    public const uint MF_POPUP = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    public static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hwnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreateMenu();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(nint menu, uint flags, nuint idOrSubmenu, string text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenu(nint hwnd, nint menu);
}
