using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>How <see cref="IClipboardOrigin.Classify"/> sees the clipboard's current content.</summary>
public enum ClipboardOriginKind
{
    /// <summary>The user or another app put it there, or the server cannot tell.</summary>
    NotFromAgent,

    /// <summary>set_clipboard put it there, and nothing changed the clipboard since.</summary>
    SetClipboard,

    /// <summary>It changed within the copy window after the agent's input that can copy.</summary>
    AgentInput,

    /// <summary>
    /// The agent's input that can copy was sent moments ago and the clipboard has not changed yet: the application may
    /// still be copying. Read again shortly.
    /// </summary>
    CopyPending
}

/// <summary>A batch of input that can copy, with the clipboard's sequence number right before it.</summary>
/// <param name="SequenceBefore">GetClipboardSequenceNumber right before the batch was sent.</param>
public sealed record ClipboardCopyMark(uint SequenceBefore);

/// <summary>
/// Keeps track of which clipboard content this server's agent put there (roadmap M2.16), by the clipboard's sequence
/// number, which Windows changes with every change of the content: the number after set_clipboard, and the numbers the
/// clipboard reaches within <see cref="ClipboardOrigin.CopyWindow"/> after the agent's input that can copy (send_keys
/// with Ctrl+C, Ctrl+X, Ctrl+Insert or Shift+Delete, or a click with the left button, as on a Copy menu item).
/// get_clipboard returns only such content unless the user allowed more. One instance serves the whole server.
/// </summary>
public interface IClipboardOrigin
{
    /// <summary>
    /// Before an input batch is sent: a mark with the current sequence number when the batch can copy, otherwise null
    /// (and the sequence number is not read).
    /// </summary>
    ClipboardCopyMark? BeforeInput(IReadOnlyList<InputEvent> batch);

    /// <summary>After the batch of <paramref name="mark"/> was sent (in part or whole): starts its copy window.</summary>
    void InputSent(ClipboardCopyMark mark);

    /// <summary>set_clipboard put content on the clipboard, which then had this sequence number.</summary>
    void ServerWrote(uint sequenceNumber);

    /// <summary>Who put the content with <paramref name="sequenceNumber"/> on the clipboard, as far as the server can tell.</summary>
    ClipboardOriginKind Classify(uint sequenceNumber);
}

/// <inheritdoc />
/// <remarks>
/// The copy window bounds when the clipboard changed, not when get_clipboard reads it, because an agent often reads many
/// seconds after its copy. So after input that can copy, a timer reads the sequence number (which does not open the
/// clipboard) every <see cref="PollInterval"/> until the window has passed, and remembers the number the clipboard
/// reached in it.
/// </remarks>
public sealed class ClipboardOrigin : IClipboardOrigin, IDisposable
{
    /// <summary>
    /// How long after the agent's input that can copy a change of the clipboard counts as the agent's copy. An app
    /// copies within milliseconds; the window only has to cover a slow app, and the shorter it is, the less a copy the
    /// user makes meanwhile can be mistaken for the agent's.
    /// </summary>
    public static readonly TimeSpan CopyWindow = TimeSpan.FromSeconds(5);

    /// <summary>How often the sequence number is read during a copy window.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private const ushort VkC = 0x43, VkX = 0x58;

    private readonly INativeClipboardWrapper _clipboard;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly object _lock = new();
    private uint? _serverWrite;
    private uint? _agentCopy;
    private (uint SequenceBefore, long SentAt)? _pending;
    private ITimer? _timer;

    /// <param name="clipboard">The clipboard, for its sequence number.</param>
    /// <param name="time">The clock; default the system's.</param>
    /// <param name="pollInterval">
    /// How often the copy window reads the sequence number; default <see cref="PollInterval"/>. Tests pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> and call <see cref="Poll"/> themselves.
    /// </param>
    public ClipboardOrigin(INativeClipboardWrapper clipboard, TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _time = time ?? TimeProvider.System;
        _pollInterval = pollInterval ?? PollInterval;
    }

    /// <summary>
    /// True when <paramref name="batch"/> can make an application copy: it presses Ctrl+C, Ctrl+X or Ctrl+Insert (with
    /// any other modifiers, so Ctrl+Shift+C in a terminal counts) or Shift+Delete, or presses the left mouse button.
    /// </summary>
    public static bool CanCopy(IReadOnlyList<InputEvent> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Any(e => e.Kind == InputEventKind.Mouse && (e.Flags & NativeEnums.MOUSEEVENTF_LEFTDOWN) != 0))
        {
            return true;
        }

        return SendKeysParser.Chords(batch).Any(chord => chord.Kind == InputEventKind.VirtualKey &&
            (((chord.Modifiers & KeyModifiers.Control) != 0 && chord.Code is VkC or VkX or NativeEnums.VK_INSERT) ||
             ((chord.Modifiers & KeyModifiers.Shift) != 0 && chord.Code == NativeEnums.VK_DELETE)));
    }

    public ClipboardCopyMark? BeforeInput(IReadOnlyList<InputEvent> batch) =>
        CanCopy(batch) ? new ClipboardCopyMark(_clipboard.GetSequenceNumber()) : null;

    public void InputSent(ClipboardCopyMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        lock (_lock)
        {
            _pending = (mark.SequenceBefore, _time.GetTimestamp());
            if (_timer == null && _pollInterval != Timeout.InfiniteTimeSpan)
            {
                _timer = _time.CreateTimer(_ => Poll(), null, _pollInterval, _pollInterval);
            }
        }
    }

    /// <summary>
    /// During a copy window: remembers the sequence number when the clipboard has changed since the input. Once the
    /// window has passed, ends it (and stops the timer).
    /// </summary>
    public void Poll()
    {
        var sequenceNumber = _clipboard.GetSequenceNumber();
        lock (_lock)
        {
            if (_pending is not { } pending)
            {
                StopTimer();
                return;
            }

            if (_time.GetElapsedTime(pending.SentAt) > CopyWindow)
            {
                _pending = null;
                StopTimer();
                return;
            }

            if (sequenceNumber != 0 && sequenceNumber != pending.SequenceBefore)
            {
                _agentCopy = sequenceNumber;
            }
        }
    }

    public void ServerWrote(uint sequenceNumber)
    {
        lock (_lock)
        {
            _serverWrite = sequenceNumber == 0 ? null : sequenceNumber;
        }
    }

    public ClipboardOriginKind Classify(uint sequenceNumber)
    {
        // 0: the number cannot be read, so nothing can be told apart
        if (sequenceNumber == 0)
        {
            return ClipboardOriginKind.NotFromAgent;
        }

        lock (_lock)
        {
            if (_serverWrite == sequenceNumber)
            {
                return ClipboardOriginKind.SetClipboard;
            }

            if (_pending is { } pending && _time.GetElapsedTime(pending.SentAt) <= CopyWindow)
            {
                if (sequenceNumber == pending.SequenceBefore)
                {
                    return ClipboardOriginKind.CopyPending;
                }

                _agentCopy = sequenceNumber;
            }

            return _agentCopy == sequenceNumber ? ClipboardOriginKind.AgentInput : ClipboardOriginKind.NotFromAgent;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopTimer();
        }
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
