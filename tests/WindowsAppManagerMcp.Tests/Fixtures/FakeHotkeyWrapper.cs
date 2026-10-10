using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of the pause hotkey's native side: registering records the chord instead of taking it from the
/// desktop, and the message loop reads from an in-memory queue, so a test "presses" the hotkey by posting
/// WM_HOTKEY to it. The shared pause is a <see cref="FakeDesktopPauseFlag"/> and the chord marks live in a set
/// the test can share between two fakes, as two servers on one desktop share the named objects. No real hotkey
/// is ever registered and no named object is opened.
/// </summary>
public sealed class FakeHotkeyWrapper : INativeHotkeyWrapper
{
    private readonly BlockingCollection<HotkeyMessage> _queue = new();
    private readonly ConcurrentDictionary<int, (uint Modifiers, uint VirtualKey)> _registered = new();
    private readonly ConcurrentQueue<int> _unregistered = new();

    /// <summary>The fake of the desktop: the shared pause and the chord marks, the same object for every server on it.</summary>
    public FakeHotkeyWrapper(FakeNamedObjects? desktop = null)
    {
        Desktop = desktop ?? new FakeNamedObjects();
    }

    /// <summary>The desktop this fake server runs on.</summary>
    public FakeNamedObjects Desktop { get; }

    /// <summary>Makes <see cref="OpenDesktopPause"/> throw, as when the shared pause cannot be opened.</summary>
    public Exception? OpenFails { get; set; }

    /// <summary>How many times RegisterHotKey was called.</summary>
    public int RegisterCalls => Volatile.Read(ref _registerCalls);

    private int _registerCalls;

    /// <summary>What RegisterHotKey returns for a key; null succeeds.</summary>
    public Func<int, uint, uint, Win32CallResult>? OnRegister { get; set; }

    /// <summary>Runs when the listener marks itself as a chord's holder, before the mark exists (to widen a race in a test).</summary>
    public Action? OnMark { get; set; }

    /// <summary>The hotkeys registered and not freed, by ID.</summary>
    public IReadOnlyDictionary<int, (uint Modifiers, uint VirtualKey)> Registered => _registered;

    /// <summary>The IDs freed, in order.</summary>
    public IReadOnlyCollection<int> Unregistered => _unregistered.ToArray();

    /// <summary>The managed thread that registered the hotkeys (the listener's own thread).</summary>
    public int? RegisteringThread { get; private set; }

    /// <summary>True once the listener created its message queue.</summary>
    public bool QueueCreated { get; private set; }

    /// <summary>The thread ID the stop message was posted to.</summary>
    public uint? StopPostedTo { get; private set; }

    /// <summary>Posts WM_HOTKEY for <paramref name="id"/>, as Windows does when the user presses the chord.</summary>
    public void Press(int id = PauseHotkeyListener.FirstHotkeyId) => _queue.Add(new HotkeyMessage(HotkeyMessageKind.Hotkey, id));

    /// <summary>Posts any other message.</summary>
    public void Post(HotkeyMessage message) => _queue.Add(message);

    public uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;

    public void CreateMessageQueue() => QueueCreated = true;

    public Win32CallResult RegisterHotKey(int id, uint modifiers, uint virtualKey)
    {
        Interlocked.Increment(ref _registerCalls);
        RegisteringThread = Environment.CurrentManagedThreadId;
        var result = OnRegister?.Invoke(id, modifiers, virtualKey) ?? Win32CallResult.Ok;
        if (result.Succeeded)
        {
            _registered[id] = (modifiers, virtualKey);
        }

        return result;
    }

    public bool UnregisterHotKey(int id)
    {
        _unregistered.Enqueue(id);
        return _registered.TryRemove(id, out _);
    }

    public HotkeyMessage WaitForMessage() =>
        _queue.TryTake(out var message, TimeSpan.FromSeconds(30))
            ? message
            : new HotkeyMessage(HotkeyMessageKind.Failed, Win32Error: -1); // never block a test run forever

    public bool PostStopMessage(uint threadId)
    {
        StopPostedTo = threadId;
        _queue.Add(new HotkeyMessage(HotkeyMessageKind.Stop));
        return true;
    }

    public IDesktopPauseFlag OpenDesktopPause() => OpenFails is { } failure ? throw failure : Desktop.Pause;

    public IDisposable MarkChordHolder(string chordId)
    {
        OnMark?.Invoke();
        return Desktop.Mark(chordId);
    }

    public bool IsChordHeldByAServer(string chordId) => Desktop.IsMarked(chordId);
}

/// <summary>The named objects every fake server on one desktop shares: the pause and the chord marks.</summary>
public sealed class FakeNamedObjects
{
    private readonly ConcurrentDictionary<string, int> _marks = new();

    /// <summary>The shared pause.</summary>
    public FakeDesktopPauseFlag Pause { get; } = new();

    /// <summary>True when a server marked itself as the holder of <paramref name="chordId"/>.</summary>
    public bool IsMarked(string chordId) => _marks.TryGetValue(chordId, out var count) && count > 0;

    public IDisposable Mark(string chordId)
    {
        _marks.AddOrUpdate(chordId, 1, (_, count) => count + 1);
        return new Unmark(() => _marks.AddOrUpdate(chordId, 0, (_, count) => count - 1));
    }

    private sealed class Unmark(Action undo) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                undo();
            }
        }
    }
}
