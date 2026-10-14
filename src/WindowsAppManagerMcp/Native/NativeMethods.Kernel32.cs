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

        /// <summary>The FILETIME values are passed as 64-bit integers (100 ns ticks since 1601, UTC).</summary>
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetProcessTimes(
            nint hProcess,
            out long lpCreationTime,
            out long lpExitTime,
            out long lpKernelTime,
            out long lpUserTime);

        /// <summary>The window of the console the calling process is attached to, or zero when it has none.</summary>
        [LibraryImport("kernel32.dll")]
        public static partial nint GetConsoleWindow();

        /// <summary>
        /// The long form of a path that may hold 8.3 short names (POWERS~1.EXE), written to <paramref name="lpszLongPath"/>;
        /// returns its length, the buffer size needed when the buffer is too small, or 0 when the path does not exist.
        /// </summary>
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);
    }
}
