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
    }
}
