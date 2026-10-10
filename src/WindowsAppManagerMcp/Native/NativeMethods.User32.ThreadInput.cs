using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // Thread input for verified window activation (NativeThreadInputWrapper). In its own file so the
    // window wrappers' declarations in NativeMethods.User32.cs stay untouched.
    internal static partial class User32
    {
        /// <summary>
        /// Attaches or detaches the input queue of one thread to that of another. Both threads need a
        /// message queue; a thread gets one on its first USER or GDI call.
        /// </summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AttachThreadInput(
            uint idAttach,
            uint idAttachTo,
            [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        /// <summary>Reads the GUI state of a thread. <c>cbSize</c> must be set before the call.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct GUITHREADINFO
{
    public int cbSize;
    public uint flags;
    public nint hwndActive;
    public nint hwndFocus;
    public nint hwndCapture;
    public nint hwndMenuOwner;
    public nint hwndMoveSize;
    public nint hwndCaret;
    public RECT rcCaret;

    public static GUITHREADINFO Create() => new() { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
}
