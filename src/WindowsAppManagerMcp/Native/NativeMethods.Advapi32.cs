using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// Access-token queries: the integrity level and elevation of a process (NativeSecurityWrapper).
    /// Only TOKEN_QUERY is ever requested; nothing here changes a token.
    /// </summary>
    internal static partial class Advapi32
    {
        public const uint TOKEN_QUERY = 0x0008;

        /// <summary>TOKEN_INFORMATION_CLASS.TokenElevation: a TOKEN_ELEVATION (one DWORD, non-zero when elevated).</summary>
        public const int TokenElevation = 20;

        /// <summary>TOKEN_INFORMATION_CLASS.TokenIntegrityLevel: a TOKEN_MANDATORY_LABEL whose SID ends with the integrity RID.</summary>
        public const int TokenIntegrityLevel = 25;

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool OpenProcessToken(nint ProcessHandle, uint DesiredAccess, out nint TokenHandle);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetTokenInformation(
            nint TokenHandle,
            int TokenInformationClass,
            nint TokenInformation,
            uint TokenInformationLength,
            out uint ReturnLength);

        /// <summary>Returns a pointer to the sub-authority count (a byte) of a valid SID.</summary>
        [LibraryImport("advapi32.dll")]
        public static partial nint GetSidSubAuthorityCount(nint pSid);

        /// <summary>Returns a pointer to the sub-authority (a DWORD) at the given index of a valid SID.</summary>
        [LibraryImport("advapi32.dll")]
        public static partial nint GetSidSubAuthority(nint pSid, uint nSubAuthority);
    }
}
