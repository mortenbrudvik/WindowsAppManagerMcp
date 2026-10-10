using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeHotkeyWrapper"/>: RegisterHotKey for the calling thread
/// and that thread's GetMessage loop.
/// </summary>
public sealed class NativeHotkeyWrapper : INativeHotkeyWrapper
{
    private const uint WmUser = 0x0400;
    private const uint WmHotkey = 0x0312;
    private const uint PmNoRemove = 0x0000;

    /// <summary>WM_APP + 0x57: the message that ends the hotkey thread's loop.</summary>
    internal const uint StopMessage = 0x8000 + 0x57;

    /// <inheritdoc />
    public uint GetCurrentThreadId() => NativeMethods.Kernel32.GetCurrentThreadId();

    /// <inheritdoc />
    public void CreateMessageQueue() => NativeMethods.Hotkey.PeekMessageW(out _, nint.Zero, WmUser, WmUser, PmNoRemove);

    /// <inheritdoc />
    public Win32CallResult RegisterHotKey(int id, uint modifiers, uint virtualKey) =>
        NativeMethods.Hotkey.RegisterHotKey(nint.Zero, id, modifiers, virtualKey)
            ? Win32CallResult.Ok
            : Win32CallResult.Failure(Marshal.GetLastPInvokeError());

    /// <inheritdoc />
    public bool UnregisterHotKey(int id) => NativeMethods.Hotkey.UnregisterHotKey(nint.Zero, id);

    /// <inheritdoc />
    public HotkeyMessage WaitForMessage()
    {
        var result = NativeMethods.Hotkey.GetMessageW(out var message, nint.Zero, 0, 0);
        if (result == -1)
        {
            return new HotkeyMessage(HotkeyMessageKind.Failed, Win32Error: Marshal.GetLastPInvokeError());
        }

        if (result == 0)
        {
            return new HotkeyMessage(HotkeyMessageKind.Stop);
        }

        return message.message switch
        {
            WmHotkey => new HotkeyMessage(HotkeyMessageKind.Hotkey, HotkeyId: unchecked((int)message.wParam)),
            StopMessage => new HotkeyMessage(HotkeyMessageKind.Stop),
            _ => new HotkeyMessage(HotkeyMessageKind.Other)
        };
    }

    /// <inheritdoc />
    public bool PostStopMessage(uint threadId) =>
        NativeMethods.Hotkey.PostThreadMessageW(threadId, StopMessage, 0, nint.Zero);
}
