using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// Remote Desktop Services session queries: whether the server's session is locked or
    /// disconnected (NativeSecurityWrapper). Read-only.
    /// </summary>
    internal static partial class Wtsapi32
    {
        public static readonly nint WTS_CURRENT_SERVER_HANDLE = nint.Zero;

        /// <summary>WTS_INFO_CLASS.WTSSessionInfoEx: returns a WTSINFOEXW.</summary>
        public const int WTSSessionInfoEx = 25;

        [LibraryImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool WTSQuerySessionInformationW(
            nint hServer,
            uint SessionId,
            int WTSInfoClass,
            out nint ppBuffer,
            out uint pBytesReturned);

        [LibraryImport("wtsapi32.dll")]
        public static partial void WTSFreeMemory(nint pMemory);
    }
}
