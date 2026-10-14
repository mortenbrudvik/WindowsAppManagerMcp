using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // The keyboard layout send_keys maps characters with (NativeKeyboardLayoutWrapper, roadmap M4.1). Every call only
    // reads; in its own class so the guard tests can pin these calls to their wrapper.
    internal static partial class Keyboard
    {
        /// <summary>MapVirtualKeyExW: a virtual key to its scan code, with 0xE0 or 0xE1 in the high byte for an extended key.</summary>
        public const uint MAPVK_VK_TO_VSC_EX = 4;

        /// <summary>ToUnicodeEx flag (Windows 10 1607 and later): the keyboard state, a pending dead key included, is not changed.</summary>
        public const uint ToUnicodeNoStateChange = 0x4;

        /// <summary>The input locale (HKL) of a thread; 0 is the calling thread.</summary>
        [LibraryImport("user32.dll")]
        public static partial nint GetKeyboardLayout(uint idThread);

        /// <summary>
        /// The key that types a character on a layout: the low byte is the virtual key, the high byte the shift state
        /// (1 Shift, 2 Ctrl, 4 Alt); -1 in both bytes when no key types it.
        /// </summary>
        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial short VkKeyScanExW(char ch, nint dwhkl);

        [LibraryImport("user32.dll")]
        public static partial uint MapVirtualKeyExW(uint uCode, uint uMapType, nint dwhkl);

        /// <summary>
        /// The characters a key types with the given key state on a layout: the count of characters, -1 for a dead key,
        /// 0 for none. With <see cref="ToUnicodeNoStateChange"/> it only reads.
        /// </summary>
        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out] char[] pwszBuff, int cchBuff,
            uint wFlags, nint dwhkl);

        [LibraryImport("user32.dll")]
        public static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        public static partial uint GetWindowThreadProcessId(nint hWnd, nint lpdwProcessId);
    }
}
