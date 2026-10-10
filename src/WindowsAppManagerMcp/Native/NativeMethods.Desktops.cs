using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// Desktop and shell queries: which desktop receives input, which desktop a thread is on, and
    /// the shell's desktop window. Read-only: the server never switches or creates desktops.
    /// </summary>
    internal static partial class User32
    {
        /// <summary>Desktop access right that allows reading objects on the desktop; the least the probe needs.</summary>
        public const uint DESKTOP_READOBJECTS = 0x0001;

        /// <summary>GetUserObjectInformation index for the object's name.</summary>
        public const int UOI_NAME = 2;

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint OpenInputDesktop(
            uint dwFlags,
            [MarshalAs(UnmanagedType.Bool)] bool fInherit,
            uint dwDesiredAccess);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseDesktop(nint hDesktop);

        /// <summary>The desktop of a thread. The handle must not be closed.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint GetThreadDesktop(uint dwThreadId);

        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetUserObjectInformationW(
            nint hObj,
            int nIndex,
            [Out] char[] pvInfo,
            uint nLength,
            out uint lpnLengthNeeded);

        /// <summary>The shell's desktop window (Progman for explorer.exe), or zero when no shell is running.</summary>
        [LibraryImport("user32.dll")]
        public static partial nint GetShellWindow();
    }
}
