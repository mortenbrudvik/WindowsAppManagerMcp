using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// The DPI awareness of the calling thread and the foreground lock timeout (NativeSystemInfoWrapper). Read-only:
    /// the awareness comes from the app manifest, and the server never sets it or any system parameter.
    /// </summary>
    internal static partial class User32
    {
        /// <summary>DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED.</summary>
        public static readonly nint DPI_AWARENESS_CONTEXT_UNAWARE_GDISCALED = -5;

        /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
        public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        /// <summary>SystemParametersInfo action that reads the foreground lock timeout into a DWORD.</summary>
        public const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;

        /// <summary>The calling thread's DPI awareness context (Windows 10 1607+). Not a handle; never freed.</summary>
        [LibraryImport("user32.dll")]
        public static partial nint GetThreadDpiAwarenessContext();

        /// <summary>The DPI_AWARENESS of a context: 0 unaware, 1 system aware, 2 per-monitor aware, -1 invalid.</summary>
        [LibraryImport("user32.dll")]
        public static partial int GetAwarenessFromDpiAwarenessContext(nint value);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AreDpiAwarenessContextsEqual(nint first, nint second);

        /// <summary>
        /// SystemParametersInfoW for a GET action that writes a DWORD. Only NativeSystemInfoWrapper calls it, with
        /// <see cref="SPI_GETFOREGROUNDLOCKTIMEOUT"/> (NativeCallGuardTests pins the caller): a SET action would change a
        /// system setting.
        /// </summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);
    }
}
