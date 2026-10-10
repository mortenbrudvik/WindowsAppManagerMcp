using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // The user's pause hotkey (NativeHotkeyWrapper, roadmap M1.10): a system-wide hotkey registered for the
    // server's own hotkey thread, and that thread's message loop. In its own class so the guard tests can pin
    // these calls to their wrapper.
    internal static partial class Hotkey
    {
        /// <summary>
        /// Registers a system-wide hotkey. With hWnd NULL, WM_HOTKEY is posted to the calling thread's
        /// message queue. Fails with ERROR_HOTKEY_ALREADY_REGISTERED (1409) when another program (or Windows)
        /// already has the chord.
        /// </summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        /// <summary>Frees a hotkey the calling thread registered.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnregisterHotKey(nint hWnd, int id);

        /// <summary>Waits for the next message of the calling thread: greater than 0 a message, 0 WM_QUIT, -1 an error.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        /// <summary>Looks at the calling thread's queue; the first call creates the queue.</summary>
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        /// <summary>Posts a message to a thread's queue (the hotkey thread's stop message).</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostThreadMessageW(uint idThread, uint Msg, nuint wParam, nint lParam);
    }
}

/// <summary>A message from a thread's queue (MSG).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nuint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}
