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

    /// <summary>
    /// KBDLLHOOKSTRUCT.flags' offset: after vkCode and scanCode (two DWORDs). The same in a 32 and a 64 bit process: the
    /// pointer-sized dwExtraInfo comes last.
    /// </summary>
    public const int KeyboardFlagsOffset = 8;

    /// <summary>KBDLLHOOKSTRUCT.flags' LLKHF_INJECTED: SendInput or keybd_event, by any process.</summary>
    public const int KeyboardInjected = 0x10;

    /// <summary>
    /// KBDLLHOOKSTRUCT.flags' LLKHF_LOWER_IL_INJECTED: injected by a process of lower integrity. Windows sets LLKHF_INJECTED
    /// with it; it is read too, so that input it marks never counts as the user's even if Windows set it alone.
    /// </summary>
    public const int KeyboardLowerIntegrityInjected = 0x02;

    /// <summary>MSLLHOOKSTRUCT.flags' offset: after pt (two LONGs) and mouseData (a DWORD).</summary>
    public const int MouseFlagsOffset = 12;

    /// <summary>MSLLHOOKSTRUCT.flags' LLMHF_INJECTED: SendInput or mouse_event, by any process.</summary>
    public const int MouseInjected = 0x01;

    /// <summary>MSLLHOOKSTRUCT.flags' LLMHF_LOWER_IL_INJECTED, read as <see cref="KeyboardLowerIntegrityInjected"/> is.</summary>
    public const int MouseLowerIntegrityInjected = 0x02;

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
    public LowLevelHookResult SetKeyboardHook(LowLevelInputCallback callback) => SetHook(WhKeyboardLl, callback, ReadKeyboard);

    /// <inheritdoc />
    public LowLevelHookResult SetMouseHook(LowLevelInputCallback callback) => SetHook(WhMouseLl, callback, ReadMouse);

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

    /// <summary>
    /// What a low-level keyboard hook saw: the kind of key event (<paramref name="message"/>, the hook's wParam) and whether it
    /// was injected, from the KBDLLHOOKSTRUCT at <paramref name="data"/> (its lParam). Pure, so that the struct's layout is
    /// tested without a hook: a wrong offset would make the user's input look injected, and takeover would never fire. A null
    /// pointer, which Windows never passes, counts as not injected: input that cannot be told apart counts as the user's,
    /// which stops or flags the operation rather than missing the user.
    /// </summary>
    public static LowLevelInput ReadKeyboard(nint message, nint data)
    {
        var kind = (uint)message switch
        {
            0x0100 or 0x0104 => LowLevelInputKind.KeyDown, // WM_KEYDOWN, WM_SYSKEYDOWN
            0x0101 or 0x0105 => LowLevelInputKind.KeyUp, // WM_KEYUP, WM_SYSKEYUP
            _ => LowLevelInputKind.Other
        };
        return new LowLevelInput(kind, IsInjected(data, KeyboardFlagsOffset, KeyboardInjected | KeyboardLowerIntegrityInjected));
    }

    /// <summary>
    /// What a low-level mouse hook saw: the kind of mouse event (<paramref name="message"/>) and whether it was injected, from
    /// the MSLLHOOKSTRUCT at <paramref name="data"/>, read as <see cref="ReadKeyboard"/> reads its struct.
    /// </summary>
    public static LowLevelInput ReadMouse(nint message, nint data)
    {
        var kind = (uint)message switch
        {
            0x0200 => LowLevelInputKind.Move, // WM_MOUSEMOVE
            0x0201 or 0x0204 or 0x0207 or 0x020B => LowLevelInputKind.ButtonDown, // L, R, M, X button down
            0x0202 or 0x0205 or 0x0208 or 0x020C => LowLevelInputKind.ButtonUp,
            0x020A or 0x020E => LowLevelInputKind.Wheel, // WM_MOUSEWHEEL, WM_MOUSEHWHEEL
            _ => LowLevelInputKind.Other
        };
        return new LowLevelInput(kind, IsInjected(data, MouseFlagsOffset, MouseInjected | MouseLowerIntegrityInjected));
    }

    private static bool IsInjected(nint data, int flagsOffset, int injectedFlags) =>
        data != nint.Zero && (Marshal.ReadInt32(data, flagsOffset) & injectedFlags) != 0;

    private LowLevelHookResult SetHook(int kind, LowLevelInputCallback callback, Func<nint, nint, LowLevelInput> read)
    {
        ArgumentNullException.ThrowIfNull(callback);
        nint hook = 0;
        NativeMethods.LowLevelHooks.LowLevelHookProc proc = (code, wParam, lParam) =>
        {
            // No exception may cross back into Windows, which called the hook: it would end the process. The takeover
            // monitor's callback catches and reports its own failures; this only keeps any other callback's from Windows
            if (code == HcAction)
            {
                try
                {
                    callback(read(wParam, lParam));
                }
                catch (Exception)
                {
                    // Swallowed, see above
                }
            }

            try
            {
                return NativeMethods.LowLevelHooks.CallNextHookEx(hook, code, wParam, lParam);
            }
            catch (Exception)
            {
                // Zero passes the input on to the windows; only the hooks after this one would miss it
                return nint.Zero;
            }
        };

        hook = NativeMethods.LowLevelHooks.SetWindowsHookExW(kind, proc, NativeMethods.LowLevelHooks.GetModuleHandleW(null), 0);
        if (hook == nint.Zero)
        {
            // Right after the call that set it: any native call in between could overwrite it
            return LowLevelHookResult.Failure(Marshal.GetLastPInvokeError());
        }

        _callbacks[hook] = proc;
        return LowLevelHookResult.Installed(hook);
    }
}
