using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static class Shcore
    {
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(
            nint hmonitor,
            MONITOR_DPI_TYPE dpiType,
            out uint dpiX,
            out uint dpiY);
    }
}

// Monitor DPI types for GetDpiForMonitor
public enum MONITOR_DPI_TYPE
{
    MDT_EFFECTIVE_DPI = 0,
    MDT_ANGULAR_DPI = 1,
    MDT_RAW_DPI = 2
}
