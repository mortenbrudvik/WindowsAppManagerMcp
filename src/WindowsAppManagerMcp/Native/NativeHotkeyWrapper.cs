using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeHotkeyWrapper"/>: RegisterHotKey for the calling thread
/// and that thread's GetMessage loop, plus the named events every server on the desktop shares (the pause
/// and the mark of the server that holds the chord).
/// </summary>
public sealed class NativeHotkeyWrapper : INativeHotkeyWrapper
{
    private const uint WmUser = 0x0400;
    private const uint WmHotkey = 0x0312;
    private const uint PmNoRemove = 0x0000;

    /// <summary>WM_APP + 0x57: the message that ends the hotkey thread's loop.</summary>
    internal const uint StopMessage = 0x8000 + 0x57;

    /// <summary>
    /// The shared pause. Local\ is the user's logon session, the scope of the desktop the hotkey belongs to:
    /// servers of another user, or in another remote session, have their own.
    /// </summary>
    internal const string DesktopPauseName = @"Local\WindowsAppManagerMcp.InputPaused";

    /// <summary>The prefix of the mark of the server that registered a chord.</summary>
    internal const string ChordHolderPrefix = @"Local\WindowsAppManagerMcp.PauseHotkeyHolder.";

    private readonly string _desktopPauseName;

    /// <summary>The server's wrapper: its shared pause is <see cref="DesktopPauseName"/>, the one every server on the desktop opens.</summary>
    public NativeHotkeyWrapper()
        : this(DesktopPauseName)
    {
    }

    /// <summary>
    /// A wrapper whose shared pause is the named event <paramref name="desktopPauseName"/>. Only tests pass a name: one of
    /// their own, so that two wrappers on it stand for two servers on one desktop without touching the pause of the
    /// servers running on the machine (the default name is theirs). Nothing is opened until <see cref="OpenDesktopPause"/>.
    /// </summary>
    public NativeHotkeyWrapper(string desktopPauseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopPauseName);
        _desktopPauseName = desktopPauseName;
    }

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

    /// <inheritdoc />
    public IDesktopPauseFlag OpenDesktopPause() =>
        new NamedPauseFlag(new EventWaitHandle(initialState: false, EventResetMode.ManualReset, _desktopPauseName));

    /// <inheritdoc />
    public IDisposable MarkChordHolder(string chordId) =>
        new EventWaitHandle(initialState: false, EventResetMode.ManualReset, ChordHolderName(chordId));

    /// <inheritdoc />
    public bool IsChordHeldByAServer(string chordId)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ChordHolderName(chordId), out var mark))
            {
                return false;
            }

            mark.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists; a server running as administrator made it
            return true;
        }
    }

    private static string ChordHolderName(string chordId)
    {
        ArgumentException.ThrowIfNullOrEmpty(chordId);
        if (!chordId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException("A chord ID has only letters, digits and '-'.", nameof(chordId));
        }

        return ChordHolderPrefix + chordId;
    }

    /// <summary>The shared pause: a named manual-reset event, set while input is paused.</summary>
    private sealed class NamedPauseFlag(EventWaitHandle handle) : IDesktopPauseFlag, IDisposable
    {
        public bool IsSet => handle.WaitOne(0);

        public WaitHandle WaitHandle => handle;

        public void Set() => handle.Set();

        public void Reset() => handle.Reset();

        public void Dispose() => handle.Dispose();
    }
}
