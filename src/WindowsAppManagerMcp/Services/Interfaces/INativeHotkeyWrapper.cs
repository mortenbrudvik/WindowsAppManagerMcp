namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The native side of the user's pause hotkey (roadmap M1.10): a system-wide hotkey registered for the
/// calling thread, and that thread's message loop. Only the server's own hotkey thread calls it
/// (<c>PauseHotkeyListener</c>); tests replace it with a fake, so no test registers a real hotkey.
/// </summary>
/// <remarks>
/// A registered hotkey sends no input and changes no window, but it takes its chord from every other
/// application while it is registered. The shared pause (<see cref="OpenDesktopPause"/>) and the chord mark
/// are named kernel objects every server on the desktop sees, so tests never open the real ones either.
/// </remarks>
public interface INativeHotkeyWrapper
{
    /// <summary>The ID of the calling thread (GetCurrentThreadId).</summary>
    uint GetCurrentThreadId();

    /// <summary>
    /// Gives the calling thread a message queue (PeekMessage), so another thread can post the stop message
    /// to it before it first waits for a message.
    /// </summary>
    void CreateMessageQueue();

    /// <summary>
    /// Registers a system-wide hotkey for the calling thread (RegisterHotKey with no window): WM_HOTKEY with
    /// <paramref name="id"/> is posted to its queue. Fails, for example, with ERROR_HOTKEY_ALREADY_REGISTERED
    /// (1409) when another program already has the chord.
    /// </summary>
    /// <param name="id">The hotkey ID, unique within the thread.</param>
    /// <param name="modifiers">MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN and MOD_NOREPEAT.</param>
    /// <param name="virtualKey">The key's virtual-key code.</param>
    Win32CallResult RegisterHotKey(int id, uint modifiers, uint virtualKey);

    /// <summary>Frees a hotkey the calling thread registered (UnregisterHotKey).</summary>
    bool UnregisterHotKey(int id);

    /// <summary>Waits for the calling thread's next message (GetMessage).</summary>
    HotkeyMessage WaitForMessage();

    /// <summary>Posts the stop message to thread <paramref name="threadId"/> (PostThreadMessage).</summary>
    bool PostStopMessage(uint threadId);

    /// <summary>
    /// Opens the pause that every server on this desktop shares: a named manual-reset event in the session's
    /// Local\ namespace, created unset by the first server. Throws when it cannot be opened, for example when
    /// a server running as administrator created it and this one is not (UnauthorizedAccessException).
    /// </summary>
    IDesktopPauseFlag OpenDesktopPause();

    /// <summary>
    /// Marks this server as the one that registered the chord <paramref name="chordId"/>, so other servers can
    /// tell that a server holds it (and its presses pause them too) rather than another program. Disposing the
    /// mark removes it; so does the end of the process.
    /// </summary>
    IDisposable MarkChordHolder(string chordId);

    /// <summary>True when a server on this desktop has marked itself as the holder of <paramref name="chordId"/>.</summary>
    bool IsChordHeldByAServer(string chordId);
}

/// <summary>What <see cref="INativeHotkeyWrapper.WaitForMessage"/> got.</summary>
public enum HotkeyMessageKind
{
    /// <summary>WM_HOTKEY: a registered hotkey was pressed; <see cref="HotkeyMessage.HotkeyId"/> says which.</summary>
    Hotkey,

    /// <summary>The stop message (or WM_QUIT): the loop ends.</summary>
    Stop,

    /// <summary>Any other message; the loop waits for the next one.</summary>
    Other,

    /// <summary>GetMessage failed; <see cref="HotkeyMessage.Win32Error"/> says why.</summary>
    Failed
}

/// <summary>A message of the hotkey thread.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="HotkeyId">With <see cref="HotkeyMessageKind.Hotkey"/>: the ID of the hotkey that was pressed.</param>
/// <param name="Win32Error">With <see cref="HotkeyMessageKind.Failed"/>: the error GetMessage set.</param>
public readonly record struct HotkeyMessage(HotkeyMessageKind Kind, int HotkeyId = 0, int Win32Error = 0);
