using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The window event journal (roadmap M7.2; see <see cref="IWindowEventJournal"/>). It listens to the server's window events
/// (<see cref="IWindowEventSource"/>, the hooks behind wait_for_window's wake-ups), so it adds no hook of its own.
/// </summary>
/// <remarks>
/// <para>Recording starts with the first read and stops when no read came for <see cref="IdleStop"/>, so a session that
/// never reads the journal keeps no hook installed for it. A read after that starts it again (StartedNow), and what came in
/// between counts as missed.</para>
/// <para>The event thread only queues each event; a worker thread of the journal reads the window (title, class, process,
/// owner) at once, so a popup that is gone a moment later is still recorded with what it was. These reads send the window
/// no message and cannot hang. Only top-level windows are recorded. A destroyed window is recorded only when the journal
/// knew it (it read it before), since it can no longer be read. Tooltips, their shadows and input method windows are left
/// out, and so are the title changes of the agent's own host (a terminal retitles itself all the time).</para>
/// <para>It keeps the last <see cref="Capacity"/> events, numbered from 1 upwards across recordings.</para>
/// </remarks>
public sealed class WindowEventJournal : IWindowEventJournal, IDisposable
{
    /// <summary>How many events the journal keeps.</summary>
    public const int Capacity = 1024;

    /// <summary>
    /// How many raw events may wait for the worker. More are dropped (the event thread never waits), so a burst beyond it
    /// leaves no trace; a worker that only reads windows keeps far ahead of it.
    /// </summary>
    public const int QueueCapacity = 4096;

    /// <summary>How long the journal records without a read before it stops.</summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(10);

    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;

    /// <summary>Window classes whose events are noise: tooltips, their drop shadows, and input method windows.</summary>
    private static readonly HashSet<string> NoiseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "tooltips_class32", "SysShadow", "IME", "MSCTFIME UI", "Xaml_WindowedPopupShadow"
    };

    private static readonly TimeSpan WorkerPoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly IWindowEventSource _source;
    private readonly INativeWindowWrapper _windows;
    private readonly IHostGuard? _hostGuard;
    private readonly TimeProvider _time;
    private readonly ILogger<WindowEventJournal>? _logger;
    private readonly object _lock = new();
    private readonly WindowEventEntry?[] _ring = new WindowEventEntry?[Capacity];

    // Under the lock
    private long _lastSeq;
    private long _missedFrom;
    private bool _recording;
    private bool _disposed;
    private DateTimeOffset _recordingSince;
    private long _lastRead;
    private IDisposable? _subscription;
    private BlockingCollection<RawEvent>? _queue;
    private Thread? _worker;

    /// <param name="source">The server's window events.</param>
    /// <param name="windows">The window reads.</param>
    /// <param name="hostGuard">The agent's host, whose title changes are left out; null: none are.</param>
    /// <param name="time">The clock of the events and of the idle stop.</param>
    /// <param name="logger">The logger.</param>
    public WindowEventJournal(
        IWindowEventSource source,
        INativeWindowWrapper windows,
        IHostGuard? hostGuard = null,
        TimeProvider? time = null,
        ILogger<WindowEventJournal>? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _hostGuard = hostGuard;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>True while the journal records (between the first read and the idle stop).</summary>
    public bool IsRecording
    {
        get
        {
            lock (_lock)
            {
                return _recording;
            }
        }
    }

    /// <inheritdoc />
    public WindowEventRead Read(long? sinceSeq, int limit, Func<WindowEventEntry, bool>? include = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var startedNow = false;
            if (!_recording)
            {
                StartLocked();
                startedNow = true;
            }

            _lastRead = _time.GetTimestamp();

            // The events held: the last Capacity, after any that came while the journal was not recording
            var oldest = Math.Max(Math.Max(1, _lastSeq - Capacity + 1), _missedFrom);

            // A cursor past the last event (from an earlier server, whose numbers started again at 1) would hide every new
            // event until the numbers caught up: read it as one from before the events held
            if (sinceSeq > _lastSeq)
            {
                sinceSeq = Math.Max(0, oldest - 1);
            }

            var after = sinceSeq is { } since ? Math.Max(since, 0) : 0;
            var missed = sinceSeq != null && !startedNow ? Math.Max(0, oldest - after - 1) : 0;
            var from = Math.Max(after + 1, oldest);

            var matches = new List<WindowEventEntry>();
            var next = Math.Max(_lastSeq, sinceSeq ?? 0);
            var more = false;
            if (sinceSeq == null)
            {
                // The latest events: scan back from the newest
                for (var seq = _lastSeq; seq >= from && matches.Count < limit; seq--)
                {
                    if (_ring[(seq - 1) % Capacity] is { } entry && (include == null || include(entry)))
                    {
                        matches.Add(entry);
                    }
                }

                matches.Reverse();
            }
            else
            {
                for (var seq = from; seq <= _lastSeq; seq++)
                {
                    if (_ring[(seq - 1) % Capacity] is not { } entry || (include != null && !include(entry)))
                    {
                        continue;
                    }

                    if (matches.Count == limit)
                    {
                        more = true;
                        next = matches[^1].Seq;
                        break;
                    }

                    matches.Add(entry);
                }
            }

            return new WindowEventRead(matches, next, _recordingSince, startedNow, missed, more);
        }
    }

    /// <summary>Stops recording and the worker thread; waits for it at most <see cref="StopTimeout"/>.</summary>
    public void Dispose()
    {
        Thread? worker;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            worker = StopLocked();
        }

        if (worker != null && worker != Thread.CurrentThread && !worker.Join(StopTimeout))
        {
            _logger?.LogWarning("The window event journal's thread did not stop within {Timeout} s", StopTimeout.TotalSeconds);
        }
    }

    private void StartLocked()
    {
        // Events numbered from here on came while the journal records; earlier ones were missed
        _missedFrom = _lastSeq + 1;
        _recordingSince = _time.GetUtcNow();
        _recording = true;
        var queue = new BlockingCollection<RawEvent>(QueueCapacity);
        _queue = queue;
        _worker = new Thread(() => Work(queue, new Recording())) { IsBackground = true, Name = "WAM window event journal" };
        _worker.Start();
        try
        {
            _subscription = _source.Subscribe((eventType, window) => Enqueue(queue, eventType, window));
        }
        catch (Exception ex)
        {
            // The source is stopping, or cannot listen: the journal records nothing until the next read tries again
            _logger?.LogWarning(ex, "The window event journal could not subscribe to the window events");
            StopLocked();
        }
    }

    /// <summary>Ends the subscription and tells the worker to stop; returns the worker to wait for.</summary>
    private Thread? StopLocked()
    {
        _recording = false;
        _subscription?.Dispose();
        _subscription = null;
        _queue?.CompleteAdding();
        _queue = null;
        var worker = _worker;
        _worker = null;
        return worker;
    }

    /// <summary>On the event thread: only queues the event, with its time.</summary>
    private void Enqueue(BlockingCollection<RawEvent> queue, uint eventType, nint window)
    {
        if (eventType is not (EventSystemForeground or EventSystemMinimizeStart or EventSystemMinimizeEnd or EventObjectDestroy
            or EventObjectShow or EventObjectHide or EventObjectNameChange))
        {
            return;
        }

        try
        {
            // A worker that cannot keep up loses events rather than the event thread waiting for it
            queue.TryAdd(new RawEvent(eventType, window, _time.GetUtcNow()));
        }
        catch (InvalidOperationException)
        {
            // The recording stopped meanwhile
        }
    }

    /// <summary>The worker: reads each queued event's window and records it, and stops the recording when nobody reads.</summary>
    private void Work(BlockingCollection<RawEvent> queue, Recording recording)
    {
        try
        {
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var raw, WorkerPoll))
                {
                    try
                    {
                        Record(raw, recording);
                    }
                    catch (Exception ex)
                    {
                        // A window that went away mid-read, or a read that failed: that one event is not recorded
                        _logger?.LogDebug(ex, "A window event could not be recorded");
                    }
                }

                StopWhenIdle(queue);
            }
        }
        catch (ObjectDisposedException)
        {
            // Stopped
        }
        catch (InvalidOperationException)
        {
            // Stopped
        }
    }

    private void StopWhenIdle(BlockingCollection<RawEvent> queue)
    {
        lock (_lock)
        {
            if (_recording && ReferenceEquals(_queue, queue) && _time.GetElapsedTime(_lastRead) >= IdleStop)
            {
                _logger?.LogInformation("The window event journal stopped recording: no read for {Minutes} minutes", IdleStop.TotalMinutes);
                StopLocked();
            }
        }
    }

    /// <summary>
    /// Whether the window is the agent's host as the guard knows it, whose title changes are noise. A window that counts as the
    /// host's only because the server cannot read its ancestors (IHostGuard.IsHostWindowOnlyWhileAncestorsUnknown) is every
    /// window of a program that started before the server: leaving out all their title changes would hide what the journal is
    /// read for, and this is no refusal, so they are kept.
    /// </summary>
    private bool IsKnownHostWindow(nint window) =>
        _hostGuard != null && _hostGuard.IsHostWindow(window) && !_hostGuard.IsHostWindowOnlyWhileAncestorsUnknown(window);

    /// <summary>On the worker: turns a raw event into an entry, when it is about a top-level window worth recording.</summary>
    private void Record(RawEvent raw, Recording recording)
    {
        var known = recording.Known;
        var window = raw.Window;
        switch (raw.EventType)
        {
            case EventObjectDestroy:
                // Gone: only what the journal knew of it
                if (known.Remove(window, out var gone))
                {
                    Append(WindowEventType.Destroyed, window, gone, raw.At);
                }

                return;

            case EventObjectHide:
                if (Read(window, recording) is { } hidden)
                {
                    Append(WindowEventType.Hidden, window, hidden, raw.At);
                }
                else if (known.TryGetValue(window, out var last))
                {
                    Append(WindowEventType.Hidden, window, last, raw.At);
                }

                return;

            case EventObjectNameChange:
                // A title change of a window the journal has seen shown, when the title really changed, unless it is the host's
                if (!known.TryGetValue(window, out var before) || !_windows.IsWindowVisible(window) || IsKnownHostWindow(window))
                {
                    return;
                }

                var title = _windows.GetWindowText(window) ?? string.Empty;
                if (title == before.Title)
                {
                    return;
                }

                var renamed = before with { Title = title };
                known[window] = renamed;
                Append(WindowEventType.TitleChanged, window, renamed, raw.At);
                return;

            default:
                if (Read(window, recording) is not { } seen)
                {
                    return;
                }

                var type = raw.EventType switch
                {
                    EventObjectShow => WindowEventType.Shown,
                    EventSystemForeground => WindowEventType.Foreground,
                    EventSystemMinimizeStart => WindowEventType.Minimized,
                    _ => WindowEventType.Restored
                };
                Append(type, window, seen, raw.At);
                return;
        }
    }

    /// <summary>Reads a top-level window and remembers it; null for a child window, one gone, or one whose class is noise.</summary>
    private Seen? Read(nint window, Recording recording)
    {
        if (window == nint.Zero || !_windows.IsWindow(window))
        {
            // Gone: forget it, so a handle Windows gives to a later window never carries this one's class and process
            recording.Known.Remove(window);
            return null;
        }

        if (_windows.GetAncestor(window, WindowAncestor.Root) != window)
        {
            // A control inside a window, or one that went away between the two reads
            return null;
        }

        var className = _windows.GetClassName(window) ?? string.Empty;
        if (NoiseClasses.Contains(className))
        {
            return null;
        }

        var processId = _windows.GetWindowThreadProcessId(window);
        if (!recording.ProcessNames.TryGetValue(processId, out var processName))
        {
            // A process ID can be reused by a later process: the cache is kept small, and starts again with each recording
            if (recording.ProcessNames.Count >= Recording.MaxProcessNames)
            {
                recording.ProcessNames.Clear();
            }

            processName = processId == 0 ? null : _windows.GetProcessName(processId);
            recording.ProcessNames[processId] = processName;
        }

        var owner = _windows.GetWindow(window, NativeEnums.GW_OWNER);
        var seen = new Seen(_windows.GetWindowText(window) ?? string.Empty, className, processName, (int)processId,
            owner == nint.Zero ? null : owner);
        recording.Known[window] = seen;
        return seen;
    }

    private void Append(WindowEventType type, nint window, Seen seen, DateTimeOffset at)
    {
        lock (_lock)
        {
            var seq = ++_lastSeq;
            _ring[(seq - 1) % Capacity] = new WindowEventEntry(seq, at, type, window.ToInt64(), AppText.Cut(seen.Title),
                seen.ClassName, seen.ProcessName, seen.ProcessId, seen.Owner?.ToInt64());
        }
    }

    private sealed record RawEvent(uint EventType, nint Window, DateTimeOffset At);

    /// <summary>
    /// What one recording's worker knows: the windows it read, and process names. Each recording has its own, so a worker
    /// still finishing after a stop never shares them with the next one.
    /// </summary>
    private sealed class Recording
    {
        public const int MaxProcessNames = 256;

        public Dictionary<nint, Seen> Known { get; } = [];

        public Dictionary<uint, string?> ProcessNames { get; } = [];
    }

    /// <summary>A window as the journal last read it.</summary>
    private sealed record Seen(string Title, string ClassName, string? ProcessName, int ProcessId, nint? Owner);
}
