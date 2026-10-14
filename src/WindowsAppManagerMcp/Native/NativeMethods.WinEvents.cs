using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // The window events behind wait_for_window's wake-ups (NativeWinEventWrapper, roadmap M3.F22): out-of-context WinEvent
    // hooks installed by the server's window event thread, and that thread's message loop, in which Windows calls them. In
    // their own class so the guard tests can pin these calls to their wrapper.
    internal static partial class WinEvents
    {
        /// <summary>The callback of a WinEvent hook (WINEVENTPROC).</summary>
        public delegate void WinEventProc(
            nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

        /// <summary>
        /// Installs a WinEvent hook. With WINEVENT_OUTOFCONTEXT nothing is loaded into another process: Windows queues each
        /// event to the calling thread and calls the callback there while it waits for messages. Zero on failure.
        /// </summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint SetWinEventHook(
            uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        /// <summary>Removes a WinEvent hook; the thread that installed it must call it.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnhookWinEvent(nint hWinEventHook);

        /// <summary>Waits for the next message of the calling thread: greater than 0 a message, 0 WM_QUIT, -1 an error.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        /// <summary>Looks at the calling thread's queue; the first call creates the queue.</summary>
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        /// <summary>Posts a message to a thread's queue (the event thread's update and stop messages).</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostThreadMessageW(uint idThread, uint Msg, nuint wParam, nint lParam);
    }
}
