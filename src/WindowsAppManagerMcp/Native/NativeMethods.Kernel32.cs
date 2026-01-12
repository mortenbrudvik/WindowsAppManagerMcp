using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class Kernel32
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint hObject);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool QueryFullProcessImageNameW(
            nint hProcess,
            uint dwFlags,
            [Out] char[] lpExeName,
            ref uint lpdwSize);

        [LibraryImport("kernel32.dll")]
        public static partial uint GetCurrentProcessId();

        [LibraryImport("kernel32.dll")]
        public static partial uint GetCurrentThreadId();
    }
}
