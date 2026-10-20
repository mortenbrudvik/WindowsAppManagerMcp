using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>Production implementation of <see cref="INativeKeyboardLayoutWrapper"/>. Every call only reads.</summary>
public sealed class NativeKeyboardLayoutWrapper : INativeKeyboardLayoutWrapper
{
    private const byte KeyDown = 0x80, Toggled = 0x01;

    public nint GetKeyboardLayout(nint window)
    {
        var owner = window == 0 ? NativeMethods.Keyboard.GetForegroundWindow() : window;
        if (owner == 0)
        {
            return 0;
        }

        var thread = NativeMethods.Keyboard.GetWindowThreadProcessId(owner, 0);
        return thread == 0 ? 0 : NativeMethods.Keyboard.GetKeyboardLayout(thread);
    }

    public short VkKeyScan(char character, nint layout) => NativeMethods.Keyboard.VkKeyScanExW(character, layout);

    public uint ScanCode(ushort virtualKey, nint layout) =>
        NativeMethods.Keyboard.MapVirtualKeyExW(virtualKey, NativeMethods.Keyboard.MAPVK_VK_TO_VSC_EX, layout);

    public string? KeyText(ushort virtualKey, uint scanCode, byte shiftState, bool capsLock, nint layout)
    {
        var (count, buffer) = ToUnicode(virtualKey, scanCode, shiftState, capsLock, layout);
        return count > 0 ? new string(buffer, 0, count) : null;
    }

    public string? DeadKeyText(ushort virtualKey, uint scanCode, byte shiftState, bool capsLock, nint layout)
    {
        // -1 is a dead key, with its spacing accent written to the buffer where Windows has one
        var (count, buffer) = ToUnicode(virtualKey, scanCode, shiftState, capsLock, layout);
        return count >= 0 ? null : buffer[0] == '\0' ? string.Empty : buffer[0].ToString();
    }

    /// <summary>ToUnicodeEx for the key with <paramref name="shiftState"/> and Caps Lock, changing no keyboard state.</summary>
    private static (int Count, char[] Buffer) ToUnicode(ushort virtualKey, uint scanCode, byte shiftState, bool capsLock, nint layout)
    {
        var state = new byte[256];
        if (capsLock)
        {
            state[NativeEnums.VK_CAPITAL] = Toggled;
        }

        if ((shiftState & 1) != 0)
        {
            state[NativeEnums.VK_SHIFT] = state[NativeEnums.VK_LSHIFT] = KeyDown;
        }

        if ((shiftState & 2) != 0)
        {
            state[NativeEnums.VK_CONTROL] = state[NativeEnums.VK_LCONTROL] = KeyDown;
        }

        if ((shiftState & 4) != 0)
        {
            state[NativeEnums.VK_MENU] = state[NativeEnums.VK_RMENU] = KeyDown;
        }

        var buffer = new char[8];
        var count = NativeMethods.Keyboard.ToUnicodeEx(virtualKey, scanCode & 0xFF, state, buffer, buffer.Length,
            NativeMethods.Keyboard.ToUnicodeNoStateChange, layout);
        return (count, buffer);
    }

    public SystemKeyboardHotkeys? ReadSystemHotkeys()
    {
        try
        {
            var hotkeys = new List<InputMethodHotkey>();
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SystemKeyboardHotkeys.InputMethodHotkeysKey, writable: false))
            {
                foreach (var name in key?.GetSubKeyNames() ?? [])
                {
                    using var entry = key!.OpenSubKey(name, writable: false);
                    if (entry != null && InputMethodHotkey.FromRegistry(name, entry.GetValue("Key Modifiers"), entry.GetValue("Virtual Key")) is { } hotkey)
                    {
                        hotkeys.Add(hotkey);
                    }
                }
            }

            using var toggle = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SystemKeyboardHotkeys.ToggleKey, writable: false);
            return new SystemKeyboardHotkeys(hotkeys, SystemKeyboardHotkeys.TogglesFrom(toggle != null, toggle?.GetValue("Hotkey"),
                toggle?.GetValue("Language Hotkey"), toggle?.GetValue("Layout Hotkey")));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
