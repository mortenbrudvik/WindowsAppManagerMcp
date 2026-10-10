using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The user's pause and the server's held input (see <see cref="IInputSession"/>). One lock covers the pause
/// state, every SendInput and SetCursorPos call and the held-input bookkeeping, so a pause takes effect
/// between two batches and never inside one.
/// </summary>
public sealed class InputSession : IInputSession
{
    private readonly INativeInputWrapper _input;
    private readonly PauseHotkeyOptions _hotkey;
    private readonly TimeProvider _time;
    private readonly ILogger<InputSession>? _logger;
    private readonly object _lock = new();
    private readonly HeldInputTracker _held = new();
    private CancellationTokenSource _pauseSource = new();
    private InputPauseState _state = InputPauseState.Running;
    private InputReleaseRecord? _lastRelease;

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
                return _pauseSource.Token;
            }
        }
    }

    /// <inheritdoc />
    public string? PauseChord => _hotkey.Chord?.Text;

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
        CancellationTokenSource running;
        lock (_lock)
        {
            if (_state.Paused)
            {
                return false;
            }

            _state = _state with { Paused = true, PausedBy = source, PausedAt = _time.GetUtcNow() };
            running = _pauseSource;
        }

        // Running calls stop at their next check. CancelAsync runs their callbacks on the thread pool, not on
        // the caller (the hotkey thread); no batch can start meanwhile, because Send checks the state above.
        _ = running.CancelAsync();
        _logger?.LogWarning("Input paused by the user ({Source}); input tools return PausedByUser until the user resumes", source);

        ReleaseHeldInput("input was paused");
        return true;
    }

    /// <inheritdoc />
    public bool Resume(string source)
    {
        lock (_lock)
        {
            if (!_state.Paused)
            {
                return false;
            }

            _state = _state with { Paused = false, ResumedBy = source, ResumedAt = _time.GetUtcNow() };

            // The cancelled source stays cancelled for the calls that linked it; new calls get a new one. It is
            // not disposed, because a call may still be reading its token.
            _pauseSource = new CancellationTokenSource();
        }

        _logger?.LogWarning("Input resumed ({Source})", source);
        return true;
    }

    /// <inheritdoc />
    public void TogglePauseByHotkey()
    {
        if (!Pause(InputControlSources.Hotkey))
        {
            Resume(InputControlSources.Hotkey);
        }
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
            return _state.Paused ? null : _input.SetCursorPos(x, y);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HeldInput> ReadModifiersDown() =>
        KeyNames.SidedModifiers
            .Where(_input.IsKeyDown)
            .Select(key => HeldInput.Key(key, KeyNames.IsExtended(key)))
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
            if (_state.Paused)
            {
                return InputReleaseOutcome.Paused;
            }

            return keys.Count == 0 ? InputReleaseOutcome.Nothing : ReleaseLocked(keys, reason);
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
        var releasedEvents = events.Take(inserted).Where(e => e.Code != HeldInputTracker.MaskKey || e.Kind != InputEventKind.VirtualKey).ToList();
        var released = releasedEvents.SelectMany(e => keys.Where(k => Releases(e, k))).Distinct().ToList();
        var stillHeld = keys.Except(released).Concat(_held.Held).Distinct().Select(k => k.Name).ToList();

        var outcome = new InputReleaseOutcome(released.Select(k => k.Name).ToList(), stillHeld, Error: error);
        _lastRelease = new InputReleaseRecord(_time.GetUtcNow(), reason, outcome.Released, outcome.StillHeld, error);
        if (stillHeld.Count > 0)
        {
            _logger?.LogWarning("Releasing held input ({Reason}) left {StillHeld} down{Error}",
                reason, string.Join(", ", stillHeld), error == null ? string.Empty : $": {error}");
        }

        return outcome;
    }

    private static bool Releases(InputEvent e, HeldInput key) => key.Kind switch
    {
        InputEventKind.Mouse => e.Kind == InputEventKind.Mouse && (e.Flags & ((uint)key.Code << 1)) != 0,
        _ => e.Kind == key.Kind && e.Code == key.Code && (e.Flags & Native.NativeEnums.KEYEVENTF_KEYUP) != 0
    };
}
