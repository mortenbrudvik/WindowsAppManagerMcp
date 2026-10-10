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

    /// <summary>PROCESS_PER_MONITOR_DPI_AWARE, what the server's Program.cs requests.</summary>
    public const int ProcessPerMonitorDpiAware = 2;

    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwareness(int value);

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
