using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static class Shcore
    {
        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(PROCESS_DPI_AWARENESS awareness);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(
            nint hmonitor,
            MONITOR_DPI_TYPE dpiType,
            out uint dpiX,
            out uint dpiY);
    }
}

// DPI Awareness levels (Windows 8.1+)
public enum PROCESS_DPI_AWARENESS
{
    PROCESS_DPI_UNAWARE = 0,
    PROCESS_SYSTEM_DPI_AWARE = 1,
    PROCESS_PER_MONITOR_DPI_AWARE = 2
}

// Monitor DPI types for GetDpiForMonitor
public enum MONITOR_DPI_TYPE
{
    MDT_EFFECTIVE_DPI = 0,
    MDT_ANGULAR_DPI = 1,
    MDT_RAW_DPI = 2
}
