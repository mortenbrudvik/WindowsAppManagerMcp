using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// A borderless, right-to-left mirrored (WS_EX_LAYOUTRTL) popup window owned by the test process
/// itself, for checks the WPF fixture cannot provide: WPF mirrors its content internally and never
/// sets WS_EX_LAYOUTRTL on its window. Without a border the client area is the window rectangle, so
/// GetWindowRect gives the expected client origin independently of the server.
/// </summary>
/// <remarks>
/// It only creates, shows and destroys this one window of the test process: it is shown without
/// activation (SW_SHOWNOACTIVATE, WS_EX_NOACTIVATE) and kept off the taskbar (WS_EX_TOOLWINDOW), so
/// the foreground and the user's windows are not touched. Create and dispose it on the same thread.
/// </remarks>
public sealed class MirroredTestWindow : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayoutRtl = 0x00400000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int SwShowNoActivate = 4;
    private const int GwlExStyle = -20;

    private MirroredTestWindow(nint handle)
    {
        Handle = handle;
    }

    public nint Handle { get; private set; }

    /// <summary>Creates and shows the window at (<paramref name="x"/>, <paramref name="y"/>), in physical pixels.</summary>
    public static MirroredTestWindow Show(int x, int y, int width, int height)
    {
        var handle = CreateWindowExW(
            WsExLayoutRtl | WsExToolWindow | WsExNoActivate,
            "STATIC",
            "WAM E2E mirrored window",
            WsPopup,
            x, y, width, height,
            nint.Zero, nint.Zero, GetModuleHandleW(null), nint.Zero);
        if (handle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for the mirrored test window.");
        }

        ShowWindow(handle, SwShowNoActivate);
        return new MirroredTestWindow(handle);
    }

    /// <summary>True when the window really is mirrored (WS_EX_LAYOUTRTL is set).</summary>
    public bool IsMirrored => (GetWindowLongPtrW(Handle, GwlExStyle).ToInt64() & WsExLayoutRtl) != 0;

    /// <summary>GetWindowRect: left, top, right, bottom in screen pixels.</summary>
    public (int Left, int Top, int Right, int Bottom) WindowRect =>
        GetWindowRect(Handle, out var rect)
            ? (rect.Left, rect.Top, rect.Right, rect.Bottom)
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");

    /// <summary>ClientToScreen of client (0, 0): the client area's right edge in a mirrored window.</summary>
    public int ClientZeroOnScreenX
    {
        get
        {
            var point = new E2ENative.POINT();
            return ClientToScreen(Handle, ref point) ? point.X : throw new InvalidOperationException("ClientToScreen failed.");
        }
    }

    public void Dispose()
    {
        if (Handle != nint.Zero)
        {
            DestroyWindow(Handle);
            Handle = nint.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref E2ENative.POINT point);
}
