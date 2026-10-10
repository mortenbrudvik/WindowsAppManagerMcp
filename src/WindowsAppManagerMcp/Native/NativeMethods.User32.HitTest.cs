using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // Hit testing for get_element_at_point (NativeHitTestWrapper). In its own file so the window
    // wrappers' declarations in NativeMethods.User32.cs stay untouched.
    internal static partial class User32
    {
        /// <summary>
        /// The deepest visible, enabled window at a point. The server is per-monitor DPI aware, so the
        /// point is in physical screen pixels.
        /// </summary>
        [LibraryImport("user32.dll")]
        public static partial nint WindowFromPoint(POINT point);
    }
}
