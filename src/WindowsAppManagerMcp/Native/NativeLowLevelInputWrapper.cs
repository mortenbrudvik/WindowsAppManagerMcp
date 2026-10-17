using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeLowLevelInputWrapper"/> (roadmap M7.4): low-level keyboard and mouse hooks
/// for the calling thread, and that thread's GetMessage loop, in which Windows calls them. The hooks read only the message
/// (what kind of input) and the injected flag, and always pass the input on.
/// </summary>
public sealed class NativeLowLevelInputWrapper : INativeLowLevelInputWrapper
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const uint WmUser = 0x0400;
    private const uint PmNoRemove = 0x0000;

    /// <summary>KBDLLHOOKSTRUCT.flags' LLKHF_INJECTED, at offset 8.</summary>
    private const int KeyboardInjected = 0x10;
    private const int KeyboardFlagsOffset = 8;

    /// <summary>MSLLHOOKSTRUCT.flags' LLMHF_INJECTED, at offset 12 (after pt and mouseData).</summary>
    private const int MouseInjected = 0x01;
    private const int MouseFlagsOffset = 12;

    /// <summary>WM_APP + 0x5A: the watches of the takeover monitor changed.</summary>
    internal const uint UpdateMessage = 0x8000 + 0x5A;

    /// <summary>WM_APP + 0x5B: the message that ends the takeover monitor's loop.</summary>
    internal const uint StopMessage = 0x8000 + 0x5B;

    // The callbacks of the installed hooks: Windows holds only a pointer to each, so they must stay alive until removed
    private readonly ConcurrentDictionary<nint, NativeMethods.LowLevelHooks.LowLevelHookProc> _callbacks = new();

    /// <inheritdoc />
    public uint GetCurrentThreadId() => NativeMethods.Kernel32.GetCurrentThreadId();

    /// <inheritdoc />
    public void CreateMessageQueue() => NativeMethods.LowLevelHooks.PeekMessageW(out _, nint.Zero, WmUser, WmUser, PmNoRemove);

    /// <inheritdoc />
    public nint SetKeyboardHook(LowLevelInputCallback callback) => SetHook(WhKeyboardLl, callback, Keyboard);

    /// <inheritdoc />
    public nint SetMouseHook(LowLevelInputCallback callback) => SetHook(WhMouseLl, callback, Mouse);

    /// <inheritdoc />
    public bool Unhook(nint hook)
    {
        var removed = NativeMethods.LowLevelHooks.UnhookWindowsHookEx(hook);
        _callbacks.TryRemove(hook, out _);
        return removed;
    }

    /// <inheritdoc />
    public WinEventLoopMessage WaitForMessage()
    {
        var result = NativeMethods.LowLevelHooks.GetMessageW(out var message, nint.Zero, 0, 0);
        if (result == -1)
        {
            return WinEventLoopMessage.Failed;
        }

        if (result == 0)
        {
            return WinEventLoopMessage.Stop;
        }

        return message.message switch
        {
            UpdateMessage => WinEventLoopMessage.Update,
            StopMessage => WinEventLoopMessage.Stop,
            _ => WinEventLoopMessage.Other
        };
    }

    /// <inheritdoc />
    public bool PostUpdateMessage(uint threadId) =>
        NativeMethods.LowLevelHooks.PostThreadMessageW(threadId, UpdateMessage, 0, nint.Zero);

    /// <inheritdoc />
    public bool PostStopMessage(uint threadId) =>
        NativeMethods.LowLevelHooks.PostThreadMessageW(threadId, StopMessage, 0, nint.Zero);

    private nint SetHook(int kind, LowLevelInputCallback callback, Func<nint, nint, LowLevelInput> read)
    {
        ArgumentNullException.ThrowIfNull(callback);
        nint hook = 0;
        NativeMethods.LowLevelHooks.LowLevelHookProc proc = (code, wParam, lParam) =>
        {
            if (code == HcAction)
            {
                try
                {
                    callback(read(wParam, lParam));
                }
                catch (Exception)
                {
                    // An exception must not cross back into Windows, which called the hook: it would end the process
                }
            }

            return NativeMethods.LowLevelHooks.CallNextHookEx(hook, code, wParam, lParam);
        };

        hook = NativeMethods.LowLevelHooks.SetWindowsHookExW(kind, proc, NativeMethods.LowLevelHooks.GetModuleHandleW(null), 0);
        if (hook != nint.Zero)
        {
            _callbacks[hook] = proc;
        }

        return hook;
    }

    private static LowLevelInput Keyboard(nint message, nint data)
    {
        var kind = (uint)message switch
        {
            0x0100 or 0x0104 => LowLevelInputKind.KeyDown, // WM_KEYDOWN, WM_SYSKEYDOWN
            0x0101 or 0x0105 => LowLevelInputKind.KeyUp, // WM_KEYUP, WM_SYSKEYUP
            _ => LowLevelInputKind.Other
        };
        return new LowLevelInput(kind, (Marshal.ReadInt32(data, KeyboardFlagsOffset) & KeyboardInjected) != 0);
    }

    private static LowLevelInput Mouse(nint message, nint data)
    {
        var kind = (uint)message switch
        {
            0x0200 => LowLevelInputKind.Move, // WM_MOUSEMOVE
            0x0201 or 0x0204 or 0x0207 or 0x020B => LowLevelInputKind.ButtonDown, // L, R, M, X button down
            0x0202 or 0x0205 or 0x0208 or 0x020C => LowLevelInputKind.ButtonUp,
            0x020A or 0x020E => LowLevelInputKind.Wheel, // WM_MOUSEWHEEL, WM_MOUSEHWHEEL
            _ => LowLevelInputKind.Other
        };
        return new LowLevelInput(kind, (Marshal.ReadInt32(data, MouseFlagsOffset) & MouseInjected) != 0);
    }
}
