using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // Takeover detection (NativeLowLevelInputWrapper, roadmap M7.4): low-level keyboard and mouse hooks installed by the
    // takeover monitor's thread, and that thread's message loop, in which Windows calls them. In their own class so the guard
    // tests can pin these calls to their wrapper.
    internal static partial class LowLevelHooks
    {
        /// <summary>The callback of a low-level hook (LowLevelKeyboardProc, LowLevelMouseProc).</summary>
        public delegate nint LowLevelHookProc(int nCode, nint wParam, nint lParam);

        /// <summary>
        /// Installs a hook. A low-level hook (WH_KEYBOARD_LL, WH_MOUSE_LL) loads nothing into another process: Windows calls it on
        /// the installing thread while that thread waits for messages. Zero on failure.
        /// </summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint SetWindowsHookExW(int idHook, LowLevelHookProc lpfn, nint hmod, uint dwThreadId);

        /// <summary>Removes a hook; the thread that installed it must call it.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnhookWindowsHookEx(nint hhk);

        /// <summary>Passes the input on to the next hook: the hook never stops or changes it.</summary>
        [LibraryImport("user32.dll")]
        public static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

        /// <summary>The server's own module (null name), which a low-level hook names as its module.</summary>
        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint GetModuleHandleW(string? lpModuleName);

        /// <summary>Waits for the next message of the calling thread: greater than 0 a message, 0 WM_QUIT, -1 an error.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        /// <summary>Looks at the calling thread's queue; the first call creates the queue.</summary>
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        /// <summary>Posts a message to a thread's queue (the monitor thread's update and stop messages).</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostThreadMessageW(uint idThread, uint Msg, nuint wParam, nint lParam);
    }
}
