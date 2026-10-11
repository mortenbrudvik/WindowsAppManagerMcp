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

/// <summary>Input that makes an application paste (see <see cref="ClipboardOrigin.PasteTrigger"/>).</summary>
public enum ClipboardPasteKind
{
    /// <summary>
    /// Pastes what is on the clipboard now: Ctrl+V (with any other modifiers, so Ctrl+Shift+V counts), Shift+Insert, the
    /// SYN control character (U+0016, which an edit control takes as Ctrl+V), or a right click in a terminal window.
    /// </summary>
    Paste,

    /// <summary>Win+V: opens clipboard history, which holds what the user copied before, not only what is there now.</summary>
    History
}

/// <summary>A batch of input that can copy, with the clipboard's sequence number right before it.</summary>
/// <param name="SequenceBefore">GetClipboardSequenceNumber right before the batch was sent.</param>
/// <param name="Trigger">What in the batch can copy.</param>
/// <param name="TargetProcessId">
/// The process of the window the batch went to (the foreground window for keys, the window at the point for a click);
/// only a clipboard owned by a window of this process counts as the copy. 0 when it is not known: then nothing counts.
/// </param>
/// <param name="ReceivingWindow">
/// The top-level window the batch went to; 0 when not known. When the origin can read windows, a copy counts only while
/// this window (or <paramref name="AnchorWindow"/>, or a window either owns) is in front.
/// </param>
/// <param name="AnchorWindow">The window the caller targeted (windowHandle), when not the receiving window; 0 when none.</param>
public sealed record ClipboardCopyMark(
    uint SequenceBefore,
    ClipboardCopyTrigger Trigger,
    int TargetProcessId,
    nint ReceivingWindow = 0,
    nint AnchorWindow = 0);

/// <summary>
/// Keeps track of which clipboard content this server's agent put there (roadmap M2.16), by the clipboard's sequence
/// number, which Windows changes with every change of the content: the number after set_clipboard, and the number of the
/// copy the application made right after the agent's copy keys (send_keys with Ctrl+C, Ctrl+X, Ctrl+Insert or
/// Shift+Delete) or left click (as on a Copy menu item). get_clipboard returns only such content unless the user allowed
/// more, and the input tools paste only such content (or content the user let the agent read). One instance serves the
/// whole server.
/// </summary>
public interface IClipboardOrigin
{
    /// <summary>
    /// Before an input batch is sent: a mark with the current sequence number when the batch can copy, otherwise null
    /// (and the sequence number is not read).
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <param name="targetProcessId">The process of the window the batch goes to; 0 when unknown.</param>
    /// <param name="receivingWindow">The top-level window the batch goes to; 0 when unknown.</param>
    /// <param name="anchorWindow">The window the caller targeted, when another than the receiving window; 0 when none.</param>
    ClipboardCopyMark? BeforeInput(IReadOnlyList<InputEvent> batch, int targetProcessId, nint receivingWindow = 0, nint anchorWindow = 0);

    /// <summary>After the batch of <paramref name="mark"/> was sent (in part or whole): starts or joins its copy window.</summary>
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

    /// <summary>
    /// The user let the agent read the content with <paramref name="sequenceNumber"/> (get_clipboard, after a yes): the
    /// agent may paste that content too, since it has seen it. Only the last such content is remembered.
    /// </summary>
    void UserAllowed(uint sequenceNumber);

    /// <summary>True when the user let the agent read the content with <paramref name="sequenceNumber"/>.</summary>
    bool IsUserAllowed(uint sequenceNumber);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// A copy counts only when all of these hold: the clipboard changed within the copy window of the agent's input that can
/// copy; the window that owns the clipboard after the change belongs to the process the input went to, so a copy the user
/// makes in another application never counts; when the server can read windows, the window in front at the change is the
/// window the input went to (or the window the caller targeted, or one either owns; or, when the input went to a menu or
/// popup that has closed since, that popup's owner), so a copy the user makes in another window of the same application
/// (another browser window) does not count either; and the change is the first one by that process, or follows it within
/// <see cref="SettleTime"/> (an application can change the clipboard a few times in one copy).
/// </para>
/// <para>
/// The copy window runs from the first input of a run, so harmless clicks or copy keys cannot keep it open: input to the
/// same application before it copies joins the run and never moves its start. The run lasts <see cref="ClickWindow"/>
/// when it has only clicks and <see cref="CopyKeysWindow"/> from its start once it has copy keys. After a run that ends
/// without a copy, input to that application opens no new window until the agent has sent it no copy keys or clicks for a
/// quiet time: the run's window length, doubled for each further empty run in a row (at most <see cref="MaxQuiet"/>);
/// empty runs of clicks hold back only clicks. A copy the application makes for the agent clears this. Any later change
/// ends the window. Within these bounds a copy the user makes in the very window the agent is working in still counts as
/// the agent's: the server cannot tell who pressed the keys.
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

    /// <summary>
    /// The longest quiet time after copy windows that ended without a copy (see the remarks), and how long an app's empty
    /// windows are remembered after the agent's last copy keys or click to it.
    /// </summary>
    public static readonly TimeSpan MaxQuiet = TimeSpan.FromSeconds(60);

    /// <summary>The SYN control character: an edit control takes it, typed as a character, as Ctrl+V.</summary>
    public const char PasteCharacter = '\u0016';

    /// <summary>The ETX control character: an edit control takes it, typed as a character, as Ctrl+C.</summary>
    public const char CopyCharacter = '\u0003';

    /// <summary>The CAN control character: an edit control takes it, typed as a character, as Ctrl+X.</summary>
    public const char CutCharacter = '\u0018';

    /// <summary>
    /// The window classes of terminals in which a right click pastes the clipboard: the console host (with QuickEdit),
    /// Windows Terminal, PuTTY and KiTTY, mintty and ConEmu.
    /// </summary>
    public static IReadOnlySet<string> RightClickPasteClasses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PuTTY", "KiTTY", "mintty", "VirtualConsoleClass"
    };

    private const ushort VkC = 0x43, VkV = 0x56, VkX = 0x58;

    private readonly INativeClipboardWrapper _clipboard;
    private readonly INativeWindowWrapper? _windows;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pollInterval;
    private readonly object _lock = new();
    private readonly Dictionary<int, EmptyRuns> _emptyRuns = [];
    private uint? _serverWrite;
    private string? _serverText;
    private uint? _agentCopy;
    private uint? _userAllowed;
    private Pending? _pending;
    private ITimer? _timer;

    /// <param name="clipboard">The clipboard, for its sequence number and owner.</param>
    /// <param name="time">The clock; default the system's.</param>
    /// <param name="pollInterval">
    /// How often the copy window reads the sequence number; default <see cref="PollInterval"/>. Tests pass
    /// <see cref="Timeout.InfiniteTimeSpan"/> and call <see cref="Poll"/> themselves (or let a read poll).
    /// </param>
    /// <param name="windows">
    /// The windows, to check that the window the agent's input went to is in front when the copy happens. The server
    /// always passes it; without it (tests of the other rules) the check is skipped.
    /// </param>
    public ClipboardOrigin(INativeClipboardWrapper clipboard, TimeProvider? time = null, TimeSpan? pollInterval = null,
        INativeWindowWrapper? windows = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _time = time ?? TimeProvider.System;
        _pollInterval = pollInterval ?? PollInterval;
        _windows = windows;
    }

    /// <summary>
    /// What in <paramref name="batch"/> can make an application copy: copy keys when it presses Ctrl+C, Ctrl+X or
    /// Ctrl+Insert (with any other modifiers, so Ctrl+Shift+C in a terminal counts) or Shift+Delete; otherwise a click when
    /// it presses the left mouse button; otherwise null.
    /// </summary>
    public static ClipboardCopyTrigger? CopyTrigger(IReadOnlyList<InputEvent> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (SendKeysParser.Chords(batch).Any(IsCopyKeys))
        {
            return ClipboardCopyTrigger.CopyKeys;
        }

        return batch.Any(e => e.Kind == InputEventKind.Mouse && (e.Flags & NativeEnums.MOUSEEVENTF_LEFTDOWN) != 0)
            ? ClipboardCopyTrigger.Click
            : null;
    }

    /// <summary>True when <paramref name="batch"/> can make an application copy (see <see cref="CopyTrigger"/>).</summary>
    public static bool CanCopy(IReadOnlyList<InputEvent> batch) => CopyTrigger(batch) != null;

    /// <summary>
    /// What in <paramref name="batch"/> makes an application paste: <see cref="ClipboardPasteKind.History"/> when it
    /// presses Win+V; <see cref="ClipboardPasteKind.Paste"/> when it presses Ctrl+V or Shift+Insert (with any other
    /// modifiers), sends the SYN character (<see cref="PasteCharacter"/>), or presses the right mouse button and
    /// <paramref name="receivingClass"/> is a terminal's (<see cref="RightClickPasteClasses"/>); otherwise null.
    /// </summary>
    /// <remarks>
    /// Paste through an application's own menu (Edit, Paste, or a context menu's Paste chosen by a click or its access
    /// key), drag and drop, and shortcuts an application or the user defined are not recognised.
    /// </remarks>
    /// <param name="batch">The input.</param>
    /// <param name="receivingClass">The window class of the top-level window the input goes to; null when not known.</param>
    public static ClipboardPasteKind? PasteTrigger(IReadOnlyList<InputEvent> batch, string? receivingClass = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var chords = SendKeysParser.Chords(batch);
        if (chords.Any(chord => chord.Kind == InputEventKind.VirtualKey && chord.Code == VkV && (chord.Modifiers & KeyModifiers.Windows) != 0))
        {
            return ClipboardPasteKind.History;
        }

        if (chords.Any(IsPasteKeys))
        {
            return ClipboardPasteKind.Paste;
        }

        return receivingClass != null && RightClickPasteClasses.Contains(receivingClass) &&
               batch.Any(e => e.Kind == InputEventKind.Mouse && (e.Flags & NativeEnums.MOUSEEVENTF_RIGHTDOWN) != 0)
            ? ClipboardPasteKind.Paste
            : null;
    }

    /// <summary>
    /// True when <paramref name="batch"/> copies and then pastes: copy keys (Ctrl+C, Ctrl+X, Ctrl+Insert, Shift+Delete),
    /// or the ETX or CAN character (<see cref="CopyCharacter"/>, <see cref="CutCharacter"/>), followed later in the batch
    /// by input that pastes what is on the clipboard (Ctrl+V, Shift+Insert, the SYN character, or a right click when
    /// <paramref name="receivingClass"/> is a terminal's). Such a paste pastes what the copy takes, which no check made
    /// before the batch is sent can see.
    /// </summary>
    /// <param name="batch">The input of one call.</param>
    /// <param name="receivingClass">The window class of the top-level window the input goes to; null when not known.</param>
    public static bool CopiesBeforePaste(IReadOnlyList<InputEvent> batch, string? receivingClass = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var chords = SendKeysParser.IndexedChords(batch);
        var firstCopy = -1;
        foreach (var (chord, index) in chords)
        {
            if (IsCopyKeys(chord) || (chord.Kind == InputEventKind.Unicode && chord.Code is CopyCharacter or CutCharacter))
            {
                firstCopy = index;
                break;
            }
        }

        if (firstCopy < 0)
        {
            return false;
        }

        if (chords.Any(c => c.Index > firstCopy && IsPasteKeys(c.Chord)))
        {
            return true;
        }

        if (receivingClass == null || !RightClickPasteClasses.Contains(receivingClass))
        {
            return false;
        }

        for (var i = firstCopy + 1; i < batch.Count; i++)
        {
            if (batch[i].Kind == InputEventKind.Mouse && (batch[i].Flags & NativeEnums.MOUSEEVENTF_RIGHTDOWN) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ctrl+C, Ctrl+X or Ctrl+Insert (with any other modifiers), or Shift+Delete.</summary>
    private static bool IsCopyKeys(KeyChord chord) =>
        chord.Kind == InputEventKind.VirtualKey &&
        (((chord.Modifiers & KeyModifiers.Control) != 0 && chord.Code is VkC or VkX or NativeEnums.VK_INSERT) ||
         ((chord.Modifiers & KeyModifiers.Shift) != 0 && chord.Code == NativeEnums.VK_DELETE));

    /// <summary>Ctrl+V or Shift+Insert (with any other modifiers), or the SYN character.</summary>
    private static bool IsPasteKeys(KeyChord chord) =>
        (chord.Kind == InputEventKind.VirtualKey &&
         (((chord.Modifiers & KeyModifiers.Control) != 0 && chord.Code == VkV) ||
          ((chord.Modifiers & KeyModifiers.Shift) != 0 && chord.Code == NativeEnums.VK_INSERT))) ||
        (chord.Kind == InputEventKind.Unicode && chord.Code == PasteCharacter);

    public ClipboardCopyMark? BeforeInput(IReadOnlyList<InputEvent> batch, int targetProcessId, nint receivingWindow = 0,
        nint anchorWindow = 0)
    {
        if (CopyTrigger(batch) is not { } trigger)
        {
            return null;
        }

        // A copy of the window before this one that has not been seen yet must be seen before this mark joins or ends it
        Poll();
        return new ClipboardCopyMark(_clipboard.GetSequenceNumber(), trigger, targetProcessId, receivingWindow,
            anchorWindow == receivingWindow ? 0 : anchorWindow);
    }

    public void InputSent(ClipboardCopyMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);

        // Read without the lock (native calls): the windows that own the receiving window, for a menu that closes
        var owners = _windows == null || mark.ReceivingWindow == 0
            ? []
            : WindowOwnerChain.SelfAndOwners(_windows, mark.ReceivingWindow).Skip(1).ToList();

        lock (_lock)
        {
            var now = _time.GetTimestamp();
            if (_pending is { FirstCopyAt: null } run && run.Mark.TargetProcessId == mark.TargetProcessId &&
                _time.GetElapsedTime(run.Start) <= run.Length)
            {
                // Joins the run: its start stays, so input cannot keep the window open; copy keys after clicks only give the
                // run the copy keys' window, from its start
                run.Join(mark, now, owners);
                return;
            }

            if (_pending is { FirstCopyAt: null } ended)
            {
                // A run for another app, or one that has passed, ended without a copy
                RecordEmptyRun(ended);
                _pending = null;
                StopTimer();
            }

            if (_emptyRuns.TryGetValue(mark.TargetProcessId, out var empty) && empty.Blocks(mark.Trigger, _time.GetElapsedTime(empty.LastInput)))
            {
                // Copy-capable input again and again without a copy: it opens no window until the agent has stopped
                empty.LastInput = now;
                return;
            }

            _pending = new Pending(mark, now, owners);
            if (_timer == null && _pollInterval != Timeout.InfiniteTimeSpan)
            {
                _timer = _time.CreateTimer(_ => Poll(), null, _pollInterval, _pollInterval);
            }
        }
    }

    /// <summary>
    /// During a copy window: takes a change of the clipboard by the process the input went to, made while the window the
    /// input went to was in front, as the agent's copy. Ends the window (and stops the timer) once it has passed, once a
    /// copy was seen and <see cref="SettleTime"/> has passed, or once the target changed the clipboard while another
    /// window was in front.
    /// </summary>
    public void Poll()
    {
        Pending candidateRun;
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
                    : _time.GetElapsedTime(pending.Start) > pending.Length)
            {
                EndWindow();
                return;
            }

            if (!consistent || sequenceNumber == 0 || sequenceNumber == pending.Mark.SequenceBefore || sequenceNumber == _agentCopy)
            {
                return;
            }

            if (pending.Mark.TargetProcessId == 0 || ownerProcessId != pending.Mark.TargetProcessId)
            {
                if (pending.FirstCopyAt != null)
                {
                    // Another application changed the clipboard after the copy: nothing after this is the agent's
                    EndWindow();
                }

                // A change by another application before the copy does not end the window: the target may still copy, and
                // only its own change counts
                return;
            }

            candidateRun = pending;
        }

        // The target changed the clipboard: it counts only when the window the input went to is in front (read without the
        // lock, then decided on the same run)
        var inFront = InFront(candidateRun);

        lock (_lock)
        {
            if (!ReferenceEquals(_pending, candidateRun))
            {
                return;
            }

            switch (inFront)
            {
                case null:
                    // No window is in front (activation in progress): decide on a later read
                    return;
                case false:
                    // A copy in another window of the target's application: the user's, and nothing after it is the agent's
                    EndWindow();
                    return;
            }

            _agentCopy = sequenceNumber;
            candidateRun.FirstCopyAt ??= _time.GetTimestamp();
            _emptyRuns.Remove(candidateRun.Mark.TargetProcessId);
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
                   _time.GetElapsedTime(pending.Start) <= pending.Length;
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

    public void UserAllowed(uint sequenceNumber)
    {
        lock (_lock)
        {
            _userAllowed = sequenceNumber == 0 ? null : sequenceNumber;
        }
    }

    public bool IsUserAllowed(uint sequenceNumber)
    {
        lock (_lock)
        {
            return sequenceNumber != 0 && _userAllowed == sequenceNumber;
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

    /// <summary>
    /// True when a window the run's input went to (or that the caller targeted) is in front or owns the window in front,
    /// or the input went to popups that have closed since and their owner is in front; null when no window is in front;
    /// otherwise false. Always true when the server cannot read windows (tests of the other rules).
    /// </summary>
    private bool? InFront(Pending run)
    {
        if (_windows == null)
        {
            return true;
        }

        IReadOnlyList<nint> targets;
        IReadOnlyList<nint> receivers;
        IReadOnlyList<nint> popupOwners;
        lock (_lock)
        {
            targets = [.. run.Targets];
            receivers = [.. run.Receivers];
            popupOwners = [.. run.PopupOwners];
        }

        var foreground = _windows.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return null;
        }

        if (targets.Count == 0)
        {
            return false;
        }

        if (WindowOwnerChain.SelfAndOwners(_windows, foreground).Any(targets.Contains))
        {
            return true;
        }

        return popupOwners.Contains(foreground) && receivers.All(r => !_windows.IsWindow(r) || !_windows.IsWindowVisible(r));
    }

    /// <summary>A run ended without a copy: the app's next window waits for a longer quiet time (see <see cref="EmptyRuns"/>).</summary>
    private void RecordEmptyRun(Pending run)
    {
        // Entries of apps the agent has left alone for MaxQuiet are forgotten
        foreach (var key in _emptyRuns.Where(e => _time.GetElapsedTime(e.Value.LastInput) > MaxQuiet).Select(e => e.Key).ToList())
        {
            _emptyRuns.Remove(key);
        }

        var pid = run.Mark.TargetProcessId;
        if (!_emptyRuns.TryGetValue(pid, out var empty))
        {
            empty = new EmptyRuns { LastInput = run.LastInput };
            _emptyRuns[pid] = empty;
        }

        empty.Add(run.HasCopyKeys, run.LastInput);
    }

    private void EndWindow()
    {
        if (_pending is { FirstCopyAt: null } run)
        {
            RecordEmptyRun(run);
        }

        _pending = null;
        StopTimer();
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>A copy window: the input of one run to one application, until it copies or the window passes.</summary>
    private sealed class Pending
    {
        public Pending(ClipboardCopyMark mark, long start, IEnumerable<nint> owners)
        {
            Mark = mark;
            Start = start;
            LastInput = start;
            Length = Window(mark.Trigger);
            HasCopyKeys = mark.Trigger == ClipboardCopyTrigger.CopyKeys;
            Add(mark, owners);
        }

        /// <summary>The latest input of the run.</summary>
        public ClipboardCopyMark Mark { get; private set; }

        /// <summary>When the run's first input was sent: the window runs from here.</summary>
        public long Start { get; }

        /// <summary>When the run's latest input was sent.</summary>
        public long LastInput { get; private set; }

        /// <summary>How long the window lasts from <see cref="Start"/>.</summary>
        public TimeSpan Length { get; private set; }

        public bool HasCopyKeys { get; private set; }

        /// <summary>When the target's first change of the clipboard was seen; null until then.</summary>
        public long? FirstCopyAt { get; set; }

        /// <summary>The receiving and targeted windows of the run's input.</summary>
        public HashSet<nint> Targets { get; } = [];

        /// <summary>The receiving windows of the run's input.</summary>
        public HashSet<nint> Receivers { get; } = [];

        /// <summary>The windows that own a receiving window.</summary>
        public HashSet<nint> PopupOwners { get; } = [];

        public void Join(ClipboardCopyMark mark, long at, IEnumerable<nint> owners)
        {
            Mark = mark;
            LastInput = at;
            if (mark.Trigger == ClipboardCopyTrigger.CopyKeys)
            {
                HasCopyKeys = true;
                Length = CopyKeysWindow;
            }

            Add(mark, owners);
        }

        private void Add(ClipboardCopyMark mark, IEnumerable<nint> owners)
        {
            if (mark.ReceivingWindow != 0)
            {
                Targets.Add(mark.ReceivingWindow);
                Receivers.Add(mark.ReceivingWindow);
            }

            if (mark.AnchorWindow != 0)
            {
                Targets.Add(mark.AnchorWindow);
            }

            PopupOwners.UnionWith(owners);
        }
    }

    /// <summary>
    /// The runs to one app in a row that ended without a copy. After them, input to the app opens no window until the
    /// agent has sent it no copy keys or clicks for a quiet time: the window's length, doubled for each further empty run
    /// of that kind (at most <see cref="MaxQuiet"/>). Empty runs of clicks hold back only clicks; empty runs with copy keys
    /// hold back both. A copy the app makes for the agent clears it.
    /// </summary>
    private sealed class EmptyRuns
    {
        private int _clickRuns;
        private int _keyRuns;

        /// <summary>When the agent's latest copy keys or click went to the app.</summary>
        public long LastInput { get; set; }

        public void Add(bool hadCopyKeys, long lastInput)
        {
            if (hadCopyKeys)
            {
                _keyRuns++;
            }
            else
            {
                _clickRuns++;
            }

            LastInput = Math.Max(LastInput, lastInput);
        }

        /// <summary>True when input of <paramref name="trigger"/>, <paramref name="sinceLastInput"/> after the last, opens no window.</summary>
        public bool Blocks(ClipboardCopyTrigger trigger, TimeSpan sinceLastInput)
        {
            var keysQuiet = Quiet(CopyKeysWindow, _keyRuns);
            var quiet = trigger == ClipboardCopyTrigger.CopyKeys ? keysQuiet : Max(keysQuiet, Quiet(ClickWindow, _clickRuns));
            return sinceLastInput <= quiet;
        }

        private static TimeSpan Quiet(TimeSpan window, int runs) =>
            runs == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(window.Ticks << Math.Min(runs - 1, 10), MaxQuiet.Ticks));

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }
}
