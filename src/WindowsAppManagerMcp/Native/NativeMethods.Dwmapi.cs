using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class Dwmapi
    {
        /// <summary>Reads a DWORD window attribute such as DWMWA_CLOAKED. Returns an HRESULT (0 = S_OK).</summary>
        [LibraryImport("dwmapi.dll")]
        public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

        /// <summary>Reads a RECT window attribute such as DWMWA_EXTENDED_FRAME_BOUNDS. Returns an HRESULT (0 = S_OK).</summary>
        [LibraryImport("dwmapi.dll")]
        public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out RECT pvAttribute, int cbAttribute);
    }
}
