using System.Runtime.InteropServices;
using System.Text;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// Read-only user32 calls the harness uses to check the server's results and to guard its input,
/// plus the one process-local setup call the server makes at startup (DPI awareness).
/// Nothing here sends input or changes a window.
/// </summary>
internal static class E2ENative
{
    public const uint GW_OWNER = 4;
    public const uint GW_ENABLEDPOPUP = 6;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    /// <summary>PROCESS_PER_MONITOR_DPI_AWARE, the fallback the server's app manifest declares before Windows 10 1703.</summary>
    public const int ProcessPerMonitorDpiAware = 2;

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, what the server's app manifest declares.</summary>
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(POINT point);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    public static string GetWindowText(nint hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowTextW(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static string GetClassName(nint hwnd)
    {
        var name = new StringBuilder(256);
        GetClassNameW(hwnd, name, name.Capacity);
        return name.ToString();
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN.</summary>
    public static (int Left, int Top, int Width, int Height) GetVirtualScreen() =>
        (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    public static int GetProcessId(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }
        GetWindowThreadProcessId(hwnd, out var processId);
        return (int)processId;
    }

    /// <summary>The DPI awareness of the calling thread: unaware, system, perMonitor or perMonitorV2.</summary>
    public static string CurrentDpiAwareness()
    {
        var context = GetThreadDpiAwarenessContext();
        if (AreDpiAwarenessContextsEqual(context, DpiAwarenessContextPerMonitorAwareV2))
        {
            return "perMonitorV2";
        }
        return GetAwarenessFromDpiAwarenessContext(context) switch
        {
            0 => "unaware",
            1 => "system",
            2 => "perMonitor",
            _ => "unknown"
        };
    }
}
