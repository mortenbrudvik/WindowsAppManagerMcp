using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class Ntdll
    {
        /// <summary>
        /// PROCESSINFOCLASS ProcessCommandLineInformation (Windows 8.1 and later): a UNICODE_STRING with the process's
        /// command line, readable with PROCESS_QUERY_LIMITED_INFORMATION.
        /// </summary>
        public const int ProcessCommandLineInformation = 60;

        /// <summary>Returns an NTSTATUS: 0 on success, STATUS_INFO_LENGTH_MISMATCH with the needed size in returnLength.</summary>
        [LibraryImport("ntdll.dll")]
        public static partial int NtQueryInformationProcess(
            nint processHandle,
            int processInformationClass,
            nint processInformation,
            int processInformationLength,
            out int returnLength);
    }
}
