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

    /// <summary>The application the agent's copy keys or click went to put it there, right after that input.</summary>
    AgentInput
}

/// <summary>The kind of input that can make an application copy.</summary>
public enum ClipboardCopyTrigger
{
    /// <summary>Copy keys: Ctrl+C, Ctrl+X, Ctrl+Insert or Shift+Delete.</summary>
    CopyKeys,

    /// <summary>A press of the left mouse button, as on a Copy menu item or button.</summary>
    Click
}

/// <summary>A batch of input that can copy, with the clipboard's sequence number right before it.</summary>
/// <param name="SequenceBefore">GetClipboardSequenceNumber right before the batch was sent.</param>
/// <param name="Trigger">What in the batch can copy.</param>
/// <param name="TargetProcessId">
/// The process of the window the batch went to (the foreground window for keys, the window at the point for a click);
/// only a clipboard owned by a window of this process counts as the copy. 0 when it is not known: then nothing counts.
/// </param>
public sealed record ClipboardCopyMark(uint SequenceBefore, ClipboardCopyTrigger Trigger, int TargetProcessId);

/// <summary>
/// Keeps track of which clipboard content this server's agent put there (roadmap M2.16), by the clipboard's sequence
/// number, which Windows changes with every change of the content: the number after set_clipboard, and the number of the
/// copy the application made right after the agent's copy keys (send_keys with Ctrl+C, Ctrl+X, Ctrl+Insert or
/// Shift+Delete) or left click (as on a Copy menu item). get_clipboard returns only such content unless the user allowed
/// more. One instance serves the whole server.
/// </summary>
public interface IClipboardOrigin
{
    /// <summary>
    /// Before an input batch is sent: a mark with the current sequence number when the batch can copy, otherwise null
    /// (and the sequence number is not read).
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="targetProcessId">The process of the window the batch goes to; 0 when unknown.</param>
    ClipboardCopyMark? BeforeInput(IReadOnlyList<InputEvent> batch, int targetProcessId);

    /// <summary>After the batch of <paramref name="mark"/> was sent (in part or whole): starts its copy window.</summary>
    void InputSent(ClipboardCopyMark mark);

    /// <summary>set_clipboard put <paramref name="text"/> on the clipboard, which then had this sequence number.</summary>
    void ServerWrote(uint sequenceNumber, string text);

    /// <summary>Who put the content with <paramref name="sequenceNumber"/> on the clipboard, as far as the server can tell.</summary>
    ClipboardOriginKind Classify(uint sequenceNumber);

    /// <summary>
    /// True when the agent's copy keys were sent moments ago and the clipboard still has <paramref name="sequenceNumber"/>,
    /// the number it had before them: the application may still be copying. Read again shortly. Never true after a click.
    /// </summary>
    bool IsCopyPending(uint sequenceNumber);

    /// <summary>
    /// True when <paramref name="text"/> (at most the asked number of characters of a text <paramref name="textLength"/>
    /// characters long) is the text set_clipboard put on the clipboard last: the check that the content numbered as
    /// set_clipboard's really is.
    /// </summary>
    bool IsServerText(string? text, int textLength);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// A copy counts only when all of these hold: the clipboard changed after the agent's input that can copy, within
/// <see cref="CopyKeysWindow"/> (keys) or <see cref="ClickWindow"/> (a click); the window that owns the clipboard after
/// the change belongs to the process the input went to, so a copy the user makes in another application never counts;
/// and the change is the first one by that process, or follows it within <see cref="SettleTime"/> (an application can
/// change the clipboard a few times in one copy). Any later change ends the window, so the agent cannot keep it open
/// with harmless clicks or copy keys and collect what the user copies next.
/// </para>
/// <para>
/// The window bounds when the clipboard changed, not when get_clipboard reads it, because an agent often reads many
/// seconds after its copy. So after input that can copy, a timer reads the sequence number and the owner (which do not
/// open the clipboard) every <see cref="PollInterval"/> until the window has passed.
/// </para>
/// </remarks>
public sealed class ClipboardOrigin : IClipboardOrigin, IDisposable
{
    /// <summary>
    /// How long after the agent's copy keys a change of the clipboard can count as the agent's copy. An app copies within
    /// milliseconds; the window only has to cover a slow app (a large spreadsheet range, a remote session).
    /// </summary>
    public static readonly TimeSpan CopyKeysWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after the agent's left click a change of the clipboard can count as the agent's copy. Shorter than for
    /// keys: a click on a Copy command copies at once, and clicks are the agent's most frequent input.
    /// </summary>
    public static readonly TimeSpan ClickWindow = TimeSpan.FromSeconds(2);

    /// <summary>How long after the first change of a copy further changes by the same application still belong to it.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the sequence number is read during a copy window.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private const ushort VkC = 0x43, VkX = 0x58;

    private readonly INativeClipboardWrapper _clipboard;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly object _lock = new();
    private uint? _serverWrite;
    private string? _serverText;
    private uint? _agentCopy;
    private Pending? _pending;
    private ITimer? _timer;

    /// <param name="clipboard">The clipboard, for its sequence number and owner.</param>
    /// <param name="time">The clock; default the system's.</param>
    /// <param name="pollInterval">
    /// How often the copy window reads the sequence number; default <see cref="PollInterval"/>. Tests pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> and call <see cref="Poll"/> themselves (or let a read poll).
    /// </param>
    public ClipboardOrigin(INativeClipboardWrapper clipboard, TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _time = time ?? TimeProvider.System;
        _pollInterval = pollInterval ?? PollInterval;
    }

    /// <summary>
    /// What in <paramref name="batch"/> can make an application copy: copy keys when it presses Ctrl+C, Ctrl+X or
    /// Ctrl+Insert (with any other modifiers, so Ctrl+Shift+C in a terminal counts) or Shift+Delete; otherwise a click when
    /// it presses the left mouse button; otherwise null.
    /// </summary>
    public static ClipboardCopyTrigger? CopyTrigger(IReadOnlyList<InputEvent> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (SendKeysParser.Chords(batch).Any(chord => chord.Kind == InputEventKind.VirtualKey &&
                (((chord.Modifiers & KeyModifiers.Control) != 0 && chord.Code is VkC or VkX or NativeEnums.VK_INSERT) ||
                 ((chord.Modifiers & KeyModifiers.Shift) != 0 && chord.Code == NativeEnums.VK_DELETE))))
        {
            return ClipboardCopyTrigger.CopyKeys;
        }

        return batch.Any(e => e.Kind == InputEventKind.Mouse && (e.Flags & NativeEnums.MOUSEEVENTF_LEFTDOWN) != 0)
            ? ClipboardCopyTrigger.Click
            : null;
    }

    /// <summary>True when <paramref name="batch"/> can make an application copy (see <see cref="CopyTrigger"/>).</summary>
    public static bool CanCopy(IReadOnlyList<InputEvent> batch) => CopyTrigger(batch) != null;

    public ClipboardCopyMark? BeforeInput(IReadOnlyList<InputEvent> batch, int targetProcessId)
    {
        if (CopyTrigger(batch) is not { } trigger)
        {
            return null;
        }

        // A copy of the window before this one that has not been seen yet must be seen before this mark replaces it
        Poll();
        return new ClipboardCopyMark(_clipboard.GetSequenceNumber(), trigger, targetProcessId);
    }

    public void InputSent(ClipboardCopyMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        lock (_lock)
        {
            _pending = new Pending(mark, _time.GetTimestamp());
            if (_timer == null && _pollInterval != Timeout.InfiniteTimeSpan)
            {
                _timer = _time.CreateTimer(_ => Poll(), null, _pollInterval, _pollInterval);
            }
        }
    }

    /// <summary>
    /// During a copy window: takes a change of the clipboard by the process the input went to as the agent's copy. Ends
    /// the window (and stops the timer) once it has passed, or once a copy was seen and <see cref="SettleTime"/> has passed.
    /// </summary>
    public void Poll()
    {
        lock (_lock)
        {
            if (_pending == null)
            {
                StopTimer();
                return;
            }
        }

        // Read without the lock (native calls); the owner counts only when the number did not change around its read
        var sequenceNumber = _clipboard.GetSequenceNumber();
        var ownerProcessId = _clipboard.GetOwnerProcessId();
        var consistent = _clipboard.GetSequenceNumber() == sequenceNumber;

        lock (_lock)
        {
            if (_pending is not { } pending)
            {
                StopTimer();
                return;
            }

            if (pending.FirstCopyAt is { } first
                    ? _time.GetElapsedTime(first) > SettleTime
                    : _time.GetElapsedTime(pending.SentAt) > Window(pending.Mark.Trigger))
            {
                EndWindow();
                return;
            }

            if (!consistent || sequenceNumber == 0 || sequenceNumber == pending.Mark.SequenceBefore || sequenceNumber == _agentCopy)
            {
                return;
            }

            if (pending.Mark.TargetProcessId != 0 && ownerProcessId == pending.Mark.TargetProcessId)
            {
                _agentCopy = sequenceNumber;
                pending.FirstCopyAt ??= _time.GetTimestamp();
            }
            else if (pending.FirstCopyAt != null)
            {
                // Another application changed the clipboard after the copy: nothing after this is the agent's
                EndWindow();
            }

            // A change by another application before the copy does not end the window: the target may still copy, and
            // only its own change counts
        }
    }

    public void ServerWrote(uint sequenceNumber, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_lock)
        {
            _serverWrite = sequenceNumber == 0 ? null : sequenceNumber;
            _serverText = sequenceNumber == 0 ? null : text;
        }
    }

    public ClipboardOriginKind Classify(uint sequenceNumber)
    {
        // 0: the number cannot be read, so nothing can be told apart
        if (sequenceNumber == 0)
        {
            return ClipboardOriginKind.NotFromAgent;
        }

        Poll();
        lock (_lock)
        {
            if (_serverWrite == sequenceNumber)
            {
                return ClipboardOriginKind.SetClipboard;
            }

            return _agentCopy == sequenceNumber ? ClipboardOriginKind.AgentInput : ClipboardOriginKind.NotFromAgent;
        }
    }

    public bool IsCopyPending(uint sequenceNumber)
    {
        if (sequenceNumber == 0)
        {
            return false;
        }

        Poll();
        lock (_lock)
        {
            return _pending is { Mark.Trigger: ClipboardCopyTrigger.CopyKeys, FirstCopyAt: null } pending &&
                   pending.Mark.SequenceBefore == sequenceNumber &&
                   _time.GetElapsedTime(pending.SentAt) <= CopyKeysWindow;
        }
    }

    public bool IsServerText(string? text, int textLength)
    {
        lock (_lock)
        {
            return text != null && _serverText != null && textLength == _serverText.Length &&
                   _serverText.AsSpan().StartsWith(text, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopTimer();
        }
    }

    private static TimeSpan Window(ClipboardCopyTrigger trigger) =>
        trigger == ClipboardCopyTrigger.CopyKeys ? CopyKeysWindow : ClickWindow;

    private void EndWindow()
    {
        _pending = null;
        StopTimer();
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private sealed class Pending(ClipboardCopyMark mark, long sentAt)
    {
        public ClipboardCopyMark Mark { get; } = mark;

        public long SentAt { get; } = sentAt;

        /// <summary>When the target's first change of the clipboard was seen; null until then.</summary>
        public long? FirstCopyAt { get; set; }
    }
}
