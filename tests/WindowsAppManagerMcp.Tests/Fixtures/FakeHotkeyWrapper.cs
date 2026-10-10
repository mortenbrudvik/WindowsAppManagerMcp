using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of the pause hotkey's native side: registering records the chord instead of taking it from the
/// desktop, and the message loop reads from an in-memory queue, so a test "presses" the hotkey by posting
/// WM_HOTKEY to it. No real hotkey is ever registered.
/// </summary>
public sealed class FakeHotkeyWrapper : INativeHotkeyWrapper
{
    private readonly BlockingCollection<HotkeyMessage> _queue = new();
    private readonly ConcurrentDictionary<int, (uint Modifiers, uint VirtualKey)> _registered = new();
    private readonly ConcurrentQueue<int> _unregistered = new();

    /// <summary>What RegisterHotKey returns for a key; null succeeds.</summary>
    public Func<int, uint, uint, Win32CallResult>? OnRegister { get; set; }

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
}
