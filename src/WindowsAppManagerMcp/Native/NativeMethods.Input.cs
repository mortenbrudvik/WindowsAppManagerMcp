using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    internal static partial class Input
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetCursorPos(int X, int Y);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out POINT lpPoint);

        /// <summary>
        /// The asynchronous state of a key or mouse button: the high bit is set while it is down. Windows
        /// keeps one state for the session, so a key the user holds and a key injected down look the same.
        /// Returns 0 when another desktop (a UAC prompt) receives the input.
        /// </summary>
        [LibraryImport("user32.dll")]
        public static partial short GetAsyncKeyState(int vKey);
    }
}
