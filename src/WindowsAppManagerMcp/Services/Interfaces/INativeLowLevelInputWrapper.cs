namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The native side of takeover detection (roadmap M7.4): low-level keyboard and mouse hooks (WH_KEYBOARD_LL, WH_MOUSE_LL)
/// installed by the calling thread, and that thread's message loop, in which Windows calls them. Only the takeover monitor's
/// thread calls it (<c>UserTakeoverMonitor</c>); tests replace it with a fake, so no test installs a real hook.
/// </summary>
/// <remarks>
/// A low-level hook loads nothing into another process: Windows calls it on the thread that installed it, before the input
/// reaches any window, and waits for it, so the callback must return at once. The wrapper hands the callback only the kind of
/// input and whether it was injected (SendInput, by any process); never which key or button or where, and it always passes
/// the input on unchanged (CallNextHookEx). The thread that installed a hook must remove it.
/// </remarks>
public interface INativeLowLevelInputWrapper
{
    /// <summary>The ID of the calling thread (GetCurrentThreadId).</summary>
    uint GetCurrentThreadId();

    /// <summary>
    /// Gives the calling thread a message queue (PeekMessage), so another thread can post it a message before it first waits
    /// for one.
    /// </summary>
    void CreateMessageQueue();

    /// <summary>
    /// Installs a low-level keyboard hook (WH_KEYBOARD_LL) for the calling thread. <paramref name="callback"/> is called on
    /// that thread while it waits for messages; the wrapper keeps it alive until <see cref="Unhook"/>. A callback should not
    /// throw: the wrapper swallows what it throws, because an exception that crossed back into Windows would end the
    /// process, so a callback that must report its failures catches them itself. The result has the hook, or zero and the
    /// Win32 error when the hook could not be installed.
    /// </summary>
    LowLevelHookResult SetKeyboardHook(LowLevelInputCallback callback);

    /// <summary>Installs a low-level mouse hook (WH_MOUSE_LL), as <see cref="SetKeyboardHook"/> does.</summary>
    LowLevelHookResult SetMouseHook(LowLevelInputCallback callback);

    /// <summary>Removes a hook the calling thread installed (UnhookWindowsHookEx).</summary>
    bool Unhook(nint hook);

    /// <summary>Waits for the calling thread's next message (GetMessage), calling the hooks' callbacks meanwhile.</summary>
    WinEventLoopMessage WaitForMessage();

    /// <summary>Posts the update message to thread <paramref name="threadId"/> (PostThreadMessage): its watches changed.</summary>
    bool PostUpdateMessage(uint threadId);

    /// <summary>Posts the stop message to thread <paramref name="threadId"/> (PostThreadMessage).</summary>
    bool PostStopMessage(uint threadId);
}

/// <summary>
/// What installing a low-level hook did: the hook (<see cref="Handle"/>), or zero and the Win32 error SetWindowsHookEx
/// failed with. The wrapper reads the error right after the call, because any native call in between can overwrite it.
/// </summary>
/// <param name="Handle">The hook; zero when it was not installed.</param>
/// <param name="Win32Error">Why it was not installed (GetLastError); 0 when it was.</param>
public readonly record struct LowLevelHookResult(nint Handle, int Win32Error)
{
    /// <summary>True when the hook is installed.</summary>
    public bool Succeeded => Handle != nint.Zero;

    /// <summary>The hook <paramref name="handle"/> was installed.</summary>
    public static LowLevelHookResult Installed(nint handle) => new(handle, 0);

    /// <summary>No hook was installed: SetWindowsHookEx failed with <paramref name="win32Error"/>.</summary>
    public static LowLevelHookResult Failure(int win32Error) => new(nint.Zero, win32Error);
}

/// <summary>One keyboard or mouse event as a low-level hook saw it; never which key or button.</summary>
public delegate void LowLevelInputCallback(LowLevelInput input);

/// <summary>What kind of input a low-level hook saw.</summary>
public enum LowLevelInputKind
{
    /// <summary>A key went down (WM_KEYDOWN, WM_SYSKEYDOWN; auto-repeat too).</summary>
    KeyDown,

    /// <summary>A key went up.</summary>
    KeyUp,

    /// <summary>A mouse button went down.</summary>
    ButtonDown,

    /// <summary>A mouse button went up.</summary>
    ButtonUp,

    /// <summary>The wheel turned (vertical or horizontal).</summary>
    Wheel,

    /// <summary>The pointer moved.</summary>
    Move,

    /// <summary>Anything else.</summary>
    Other
}

/// <summary>
/// One event a low-level hook saw: its kind, and whether it was injected (LLKHF_INJECTED, LLMHF_INJECTED: SendInput or
/// keybd_event by any process, this server's own input among it). Nothing else is read: not the key, button or position.
/// </summary>
public readonly record struct LowLevelInput(LowLevelInputKind Kind, bool Injected);
