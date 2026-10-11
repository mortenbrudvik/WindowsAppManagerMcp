using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// The clipboard (NativeClipboardWrapper, roadmap M2.16), and the memory and the message-only owner window it needs.
    /// In a class of its own so NativeCallGuardTests can pin every member to NativeClipboardWrapper: the clipboard is the
    /// user's, shared with every app, and tests replace the wrapper with a fake.
    /// </summary>
    internal static partial class Clipboard
    {
        /// <summary>Unicode text, null-terminated UTF-16.</summary>
        public const uint CF_UNICODETEXT = 13;

        /// <summary>A list of copied files (an HDROP).</summary>
        public const uint CF_HDROP = 15;

        /// <summary>GlobalAlloc: movable memory, the kind SetClipboardData takes.</summary>
        public const uint GMEM_MOVEABLE = 0x0002;

        /// <summary>The parent of a message-only window.</summary>
        public static readonly nint HWND_MESSAGE = -3;

        /// <summary>DragQueryFileW's file index that asks for the number of files.</summary>
        public const uint DragQueryCount = 0xFFFFFFFF;

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool OpenClipboard(nint hWndNewOwner);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseClipboard();

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool EmptyClipboard();

        /// <summary>Puts a memory handle on the clipboard; on success the system owns it.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint SetClipboardData(uint uFormat, nint hMem);

        /// <summary>The clipboard's memory handle of a format; the clipboard keeps owning it.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint GetClipboardData(uint uFormat);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsClipboardFormatAvailable(uint format);

        /// <summary>The ID of a named clipboard format, registering the name on first use; 0 on failure.</summary>
        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint RegisterClipboardFormatW(string lpszFormat);

        [LibraryImport("user32.dll")]
        public static partial uint GetClipboardSequenceNumber();

        /// <summary>The window that owns the clipboard (the one that emptied it last); zero when there is none.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint GetClipboardOwner();

        /// <summary>The thread and process of the clipboard owner window (it sends the window no message).</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

        /// <summary>Creates the server's own message-only window that owns the clipboard while the server sets it.</summary>
        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint CreateWindowExW(
            uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight,
            nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        /// <summary>Destroys the server's own message-only window again.</summary>
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(nint hWnd);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint GlobalLock(nint hMem);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GlobalUnlock(nint hMem);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nuint GlobalSize(nint hMem);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint GlobalFree(nint hMem);

        /// <summary>
        /// The number of files in an HDROP (<see cref="DragQueryCount"/>), the length of a path without its null
        /// (a null buffer), or copies a path into the buffer.
        /// </summary>
        [LibraryImport("shell32.dll")]
        public static unsafe partial uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);
    }
}
