using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The user's pause and the server's held input (see <see cref="IInputSession"/>). One lock covers the pause
/// state, every SendInput and SetCursorPos call and the held-input bookkeeping, so a pause takes effect
/// between two batches and never inside one.
/// </summary>
/// <remarks>
/// Until the server starts, the pause is this session's own. Then it is shared through a flag every server on the
/// desktop opens (<see cref="IDesktopPauseFlag"/>), and this session's state follows the flag's changes: every
/// operation under the lock looks at the flag first, so a pause another server made is in force before this
/// server's next batch, and a watcher thread waits on the flag, so running calls stop at once (their pause
/// token is cancelled) and held input is released. A change this session makes itself is written to the flag
/// and takes effect here even when the flag cannot be written.
/// </remarks>
public sealed class InputSession : IInputSession, IDisposable
{
    /// <summary>
    /// A hotkey press within this time of the pause is ignored: the user gets no feedback from the hotkey, and
    /// pressing it twice to be sure must not resume. Measured on the monotonic clock, so a change of the wall
    /// clock (a time sync, or the user setting it) neither stretches nor skips it.
    /// </summary>
    public static readonly TimeSpan HotkeyResumeDelay = TimeSpan.FromSeconds(2);

    /// <summary>While the shared pause is on, how often the watcher looks whether another server resumed.</summary>
    public static readonly TimeSpan SharedResumePoll = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan WatcherStopTimeout = TimeSpan.FromSeconds(2);

    private readonly INativeInputWrapper _input;
    private readonly PauseHotkeyOptions _hotkey;
    private readonly TimeProvider _time;
    private readonly ILogger<InputSession>? _logger;
    private readonly object _lock = new();
    private readonly HeldInputTracker _held = new();
    private readonly CancellationTokenSource _watcherStop = new();
    private IDesktopPauseFlag? _flag;
    private bool _flagSeen;

    // True from a read of the flag that failed until the next one that works (see SyncLocked)
    private bool _flagUnreadable;
    private bool _shared;
    private Thread? _watcher;
    private long _pauses;
    private long _pausedTimestamp;
    private CancellationTokenSource _pauseSource = new();
    private InputPauseState _state = InputPauseState.Running;
    private InputReleaseRecord? _lastRelease;
    private int _resumeQuestionOpen;

    public InputSession(
        INativeInputWrapper input,
        PauseHotkeyOptions? hotkey = null,
        TimeProvider? time = null,
        ILogger<InputSession>? logger = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _hotkey = hotkey ?? PauseHotkeyOptions.Default;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                SyncLocked();
                return _state.Paused;
            }
        }
    }

    /// <inheritdoc />
    public InputPauseState PauseState
    {
        get
        {
            lock (_lock)
            {
                SyncLocked();
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public CancellationToken PauseToken
    {
        get
        {
            lock (_lock)
            {
                SyncLocked();
                return _pauseSource.Token;
            }
        }
    }

    /// <inheritdoc />
    public string? PauseChord => _hotkey.Chord?.Text;

    /// <inheritdoc />
    public bool SharesDesktopPause
    {
        get
        {
            lock (_lock)
            {
                return _shared;
            }
        }
    }

    /// <inheritdoc />
    public bool IsSharedPauseUnreadable
    {
        get
        {
            lock (_lock)
            {
                SyncLocked();
                return _flagUnreadable;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HeldInput> ServerHeld
    {
        get
        {
            lock (_lock)
            {
                return _held.Held;
            }
        }
    }

    /// <inheritdoc />
    public InputReleaseRecord? LastRelease
    {
        get
        {
            lock (_lock)
            {
                return _lastRelease;
            }
        }
    }

    /// <inheritdoc />
    public bool Pause(string source)
    {
        lock (_lock)
        {
            SyncLocked();
            if (_state.Paused)
            {
                return false;
            }

            WriteFlagLocked(paused: true);
            ApplyPauseLocked(source);
            return true;
        }
    }

    /// <inheritdoc />
    public bool Resume(string source, long? pauseNumber = null)
    {
        lock (_lock)
        {
            SyncLocked();
            if (!_state.Paused || (pauseNumber is { } number && number != _state.PauseNumber))
            {
                return false;
            }

            WriteFlagLocked(paused: false);
            ApplyResumeLocked(source);
            return true;
        }
    }

    /// <inheritdoc />
    public void TogglePauseByHotkey()
    {
        lock (_lock)
        {
            SyncLocked();
            if (!_state.Paused)
            {
                WriteFlagLocked(paused: true);
                ApplyPauseLocked(InputControlSources.Hotkey);
                return;
            }

            if (_state.PausedBy == InputControlSources.SharedPauseUnreadable)
            {
                // The server paused itself and nobody told the user, so this press asks for a pause: resuming would make the
                // user's stop control a go. The pause stays and is the user's own from now on, so their next press resumes
                _logger?.LogWarning("The pause hotkey was pressed while this server had paused itself (the shared pause could not be read); " +
                                    "input stays paused, now by the user");
                WriteFlagLocked(paused: true);
                ApplyPauseLocked(InputControlSources.Hotkey);
                return;
            }

            if (_time.GetElapsedTime(_pausedTimestamp) < HotkeyResumeDelay)
            {
                _logger?.LogWarning("The pause hotkey was pressed again within {Delay} s of the pause; input stays paused",
                    HotkeyResumeDelay.TotalSeconds);
                return;
            }

            WriteFlagLocked(paused: false);
            ApplyResumeLocked(InputControlSources.Hotkey);
        }
    }

    /// <inheritdoc />
    public void ShareDesktopPause(IDesktopPauseFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        lock (_lock)
        {
            if (_shared)
            {
                throw new InvalidOperationException("The pause is already shared.");
            }

            _flag = flag;
            _shared = true;
            _flagSeen = false;

            // A pause made before carries over to the other servers; a pause they have already made applies here
            if (_state.Paused)
            {
                WriteFlagLocked(paused: true);
            }

            SyncLocked();
            _watcher = new Thread(() => Watch(flag, _watcherStop.Token)) { IsBackground = true, Name = "WAM shared pause" };
            _watcher.Start();
        }
    }

    /// <inheritdoc />
    public bool IsPauseChordHeld(IReadOnlyList<HeldInput> modifiersDown)
    {
        ArgumentNullException.ThrowIfNull(modifiersDown);
        if (_hotkey.Chord is not { Modifiers: not KeyModifiers.None } chord)
        {
            return false;
        }

        var held = modifiersDown
            .Where(k => k.Kind == InputEventKind.VirtualKey)
            .Aggregate(KeyModifiers.None, (all, key) => all | ModifierOf(key.Code));
        // Any of them, not all: the user presses a chord's modifiers one after another
        return (held & chord.Modifiers) != KeyModifiers.None;
    }

    /// <inheritdoc />
    public string? FindPauseChord(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return _hotkey.Chord is { } chord && chord.IsPressedBy(events) ? chord.Text : null;
    }

    /// <inheritdoc />
    public InputSendOutcome Send(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_lock)
        {
            SyncLocked();
            if (_state.Paused)
            {
                return new InputSendOutcome(PausedByUser: true, Inserted: 0, Requested: events.Count);
            }

            uint inserted;
            try
            {
                inserted = _input.SendInput(events);
            }
            catch (Exception ex)
            {
                // How many events went in is unknown: what this batch pressed and Windows reports down counts as
                // held (and is retried later if this release fails too)
                _held.MarkHeld(HeldInputTracker.Pressed(events)
                    .Where(input => HeldInputTracker.StateKey(input) is { } key && _input.IsKeyDown(key)));
                if (!_held.IsEmpty)
                {
                    ReleaseLocked(_held.Held, $"SendInput failed ({ex.GetType().Name})");
                }

                throw;
            }

            var count = (int)Math.Min(inserted, (uint)events.Count);
            _held.Apply(events, count);
            if (count < events.Count && !_held.IsEmpty)
            {
                ReleaseLocked(_held.Held, $"SendInput inserted only {count} of {events.Count} events");
            }

            return new InputSendOutcome(PausedByUser: false, Inserted: count, Requested: events.Count);
        }
    }

    /// <inheritdoc />
    public bool? SetCursorPos(int x, int y)
    {
        lock (_lock)
        {
            SyncLocked();
            return _state.Paused ? null : _input.SetCursorPos(x, y);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HeldInput> ReadModifiersDown() =>
        KeyNames.SidedModifiers
            .Where(_input.IsKeyDown)
            .Select(HeldInput.Modifier)
            .ToList();

    /// <inheritdoc />
    public InputReleaseOutcome ReleaseHeldInput(string reason)
    {
        lock (_lock)
        {
            return _held.IsEmpty ? InputReleaseOutcome.Nothing : ReleaseLocked(_held.Held, reason);
        }
    }

    /// <inheritdoc />
    public InputReleaseOutcome ReleaseModifiers(IReadOnlyList<HeldInput> keys, string reason)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_lock)
        {
            SyncLocked();
            if (_state.Paused)
            {
                return InputReleaseOutcome.Paused;
            }

            return keys.Count == 0 ? InputReleaseOutcome.Nothing : ReleaseLocked(keys, reason);
        }
    }

    /// <inheritdoc />
    public IDisposable? TryOpenResumeQuestion() =>
        Interlocked.CompareExchange(ref _resumeQuestionOpen, 1, 0) == 0 ? new ResumeQuestion(this) : null;

    /// <summary>Stops the watcher thread and closes the shared pause.</summary>
    public void Dispose()
    {
        Thread? watcher;
        lock (_lock)
        {
            watcher = _watcher;
        }

        _watcherStop.Cancel();
        if (watcher != null && !watcher.Join(WatcherStopTimeout))
        {
            _logger?.LogWarning("The shared pause's watcher thread did not stop within {Timeout} s", WatcherStopTimeout.TotalSeconds);
            return;
        }

        (_flag as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Takes over a change of the flag made by another server: a pause pauses this server too, a resume resumes
    /// it. Only changes count, so a flag that could not be written does not undo this server's own change.
    /// </summary>
    /// <remarks>
    /// A flag that cannot be read fails closed: a pause the user made in another server may be in it, so the first failed
    /// read pauses this server (<see cref="InputControlSources.SharedPauseUnreadable"/>) and is logged. Later failed reads
    /// change nothing, so the user can still resume here (a confirmed resume_input; in this server, if it registered the
    /// chord, a hotkey press first makes the pause the user's own, see <see cref="TogglePauseByHotkey"/>, and the next one
    /// resumes) while it cannot be read, and a read that works again does not resume by itself: what happened meanwhile is
    /// not known, so only the user resumes. Another server's presses reach this one only through the flag, so while it cannot
    /// be read they neither pause nor resume this server (<see cref="IsSharedPauseUnreadable"/>). Once it can be read, a set
    /// flag makes the server's own pause the user's (pausedBy hotkey, a new pause, as a press here makes it): the user paused
    /// in another server while this one could not read the flag, or another server's first press since did, so the press
    /// that resumes the other servers resumes this one too. The named
    /// event throws ObjectDisposedException only after <see cref="Dispose"/>, where pausing is right too: a closed session
    /// sends nothing.
    /// </remarks>
    private void SyncLocked()
    {
        if (_flag == null)
        {
            return;
        }

        bool paused;
        try
        {
            paused = _flag.IsSet;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            if (!_flagUnreadable)
            {
                _flagUnreadable = true;
                _logger?.LogError(ex, "The shared pause could not be read, so this server cannot tell whether the user paused input in " +
                                      "another server; input is paused until the user resumes it");
                if (!_state.Paused)
                {
                    ApplyPauseLocked(InputControlSources.SharedPauseUnreadable);
                }
            }

            return;
        }

        _flagUnreadable = false;
        if (paused && _state is { Paused: true, PausedBy: InputControlSources.SharedPauseUnreadable })
        {
            // The pause in force is the user's: they paused with the hotkey, in whichever server registered it, while this
            // server could not read the flag or since. The pause stays and is the user's own from now on, as after a press
            // here (TogglePauseByHotkey), so the press that resumes the other servers resumes this one too, and a resume_input
            // answer about the server's own pause does not resume it
            _logger?.LogWarning("The shared pause is set while this server had paused itself (it could not read the shared pause); " +
                                "input stays paused, now by the user");
            _flagSeen = true;
            ApplyPauseLocked(InputControlSources.Hotkey);
            return;
        }

        if (paused == _flagSeen)
        {
            return;
        }

        _flagSeen = paused;
        if (paused && !_state.Paused)
        {
            // Only the pause hotkey pauses, in whichever server registered it
            ApplyPauseLocked(InputControlSources.Hotkey);
        }
        else if (!paused && _state.Paused)
        {
            ApplyResumeLocked(InputControlSources.OtherServer);
        }
    }

    private void WriteFlagLocked(bool paused)
    {
        if (_flag == null)
        {
            return;
        }

        try
        {
            if (paused)
            {
                _flag.Set();
            }
            else
            {
                _flag.Reset();
            }

            _flagSeen = paused;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "The shared pause could not be {Change}; only this server is {State}",
                paused ? "set" : "reset", paused ? "paused" : "resumed");
        }
    }

    private void ApplyPauseLocked(string source)
    {
        // PausedAt is for reports; the hotkey's resume delay is measured from the monotonic timestamp
        _state = _state with { Paused = true, PausedBy = source, PausedAt = _time.GetUtcNow(), PauseNumber = ++_pauses };
        _pausedTimestamp = _time.GetTimestamp();

        // Running calls stop at their next check. CancelAsync runs their callbacks on the thread pool, not on this
        // thread (the hotkey's or the watcher's); no batch can start meanwhile, because Send checks the state first.
        _ = _pauseSource.CancelAsync();
        _logger?.LogWarning("Input paused by {Who} ({Source}); input tools return PausedByUser until the user resumes",
            source == InputControlSources.SharedPauseUnreadable ? "this server" : "the user", source);

        if (!_held.IsEmpty)
        {
            ReleaseLocked(_held.Held, "input was paused");
        }
    }

    private void ApplyResumeLocked(string source)
    {
        _state = _state with { Paused = false, ResumedBy = source, ResumedAt = _time.GetUtcNow() };

        // The cancelled source stays cancelled for the calls that linked it; new calls get a new one. It is not
        // disposed, because a call may still be reading its token.
        _pauseSource = new CancellationTokenSource();
        _logger?.LogWarning("Input resumed ({Source})", source);
    }

    /// <summary>
    /// The watcher thread: waits until the shared pause is set, takes it over (which cancels running calls),
    /// then looks every <see cref="SharedResumePoll"/> whether it was reset.
    /// </summary>
    private void Watch(IDesktopPauseFlag flag, CancellationToken stop)
    {
        try
        {
            var handles = new[] { flag.WaitHandle, stop.WaitHandle };
            while (!stop.IsCancellationRequested)
            {
                if (!flag.IsSet)
                {
                    if (WaitHandle.WaitAny(handles) == 1)
                    {
                        return;
                    }
                }
                else if (stop.WaitHandle.WaitOne(SharedResumePoll))
                {
                    return;
                }

                lock (_lock)
                {
                    SyncLocked();
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception end the process from this thread: the pause still takes effect before every batch
            if (!stop.IsCancellationRequested)
            {
                _logger?.LogError(ex, "The shared pause's watcher failed; a pause made by another server now takes effect at this server's next input");
            }
        }
    }

    /// <summary>
    /// Sends the up events of <paramref name="keys"/> in one batch and records what went in. Never throws:
    /// a release runs on failure paths, where the original outcome must still be reported.
    /// </summary>
    private InputReleaseOutcome ReleaseLocked(IReadOnlyList<HeldInput> keys, string reason)
    {
        var events = HeldInputTracker.ReleaseEvents(keys);
        string? error = null;
        int inserted;
        try
        {
            inserted = (int)Math.Min(_input.SendInput(events), (uint)events.Count);
        }
        catch (Exception ex)
        {
            inserted = 0;
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        // The up events that went in release their keys; a masking key that went down but not up is held now
        _held.Apply(events, inserted);
        // The up events that went in, after the masking key's down and up (sent first when Alt or Windows is released)
        var maskPrefix = keys.Any(k => k.OpensAMenuWhenReleasedAlone) ? 2 : 0;
        var releasedEvents = events.Take(inserted).Skip(maskPrefix).ToList();
        var released = releasedEvents.SelectMany(e => keys.Where(k => Releases(e, k))).Distinct().ToList();
        // What was asked for and not released, and the masking key if it went down without its up
        var stillHeld = keys.Except(released)
            .Concat(_held.Held.Where(h => h.Kind == InputEventKind.VirtualKey && h.Code == HeldInputTracker.MaskKey))
            .Select(k => k.Name)
            .Distinct()
            .ToList();

        var outcome = new InputReleaseOutcome(released.Select(k => k.Name).ToList(), stillHeld, Error: error);
        _lastRelease = new InputReleaseRecord(_time.GetUtcNow(), reason, outcome.Released, outcome.StillHeld, error);
        if (stillHeld.Count > 0)
        {
            _logger?.LogWarning("Releasing held input ({Reason}) left {StillHeld} down{Error}",
                reason, string.Join(", ", stillHeld), error == null ? string.Empty : $": {error}");
        }

        return outcome;
    }

    /// <summary>An open resume question; disposing it (once) lets the next one open.</summary>
    private sealed class ResumeQuestion(InputSession session) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Volatile.Write(ref session._resumeQuestionOpen, 0);
            }
        }
    }

    private static bool Releases(InputEvent e, HeldInput key) => key.Kind switch
    {
        InputEventKind.Mouse => e.Kind == InputEventKind.Mouse && (e.Flags & ((uint)key.Code << 1)) != 0 &&
                                (key.Code != NativeEnums.MOUSEEVENTF_XDOWN || (e.MouseData & key.Flags) != 0),
        _ => e.Kind == key.Kind && e.Code == key.Code && (e.Flags & NativeEnums.KEYEVENTF_KEYUP) != 0
    };

    private static KeyModifiers ModifierOf(ushort virtualKey) => virtualKey switch
    {
        NativeEnums.VK_CONTROL or NativeEnums.VK_LCONTROL or NativeEnums.VK_RCONTROL => KeyModifiers.Control,
        NativeEnums.VK_MENU or NativeEnums.VK_LMENU or NativeEnums.VK_RMENU => KeyModifiers.Alt,
        NativeEnums.VK_SHIFT or NativeEnums.VK_LSHIFT or NativeEnums.VK_RSHIFT => KeyModifiers.Shift,
        NativeEnums.VK_LWIN or NativeEnums.VK_RWIN => KeyModifiers.Windows,
        _ => KeyModifiers.None
    };
}
