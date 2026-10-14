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
        return count > 0 ? new string(buffer, 0, count) : null;
    }
}
