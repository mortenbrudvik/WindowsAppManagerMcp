using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of the window events' native side (roadmap M3.F22): installing a hook records it instead of hooking the desktop,
/// and the message loop reads from an in-memory queue, where <see cref="Raise"/> puts a window event that the loop hands to
/// the hooks whose range covers it, on the loop's own thread, as Windows calls an out-of-context hook. No real hook is ever
/// installed.
/// </summary>
public sealed class FakeWinEventWrapper : INativeWinEventWrapper
{
    private readonly BlockingCollection<object> _queue = new();
    private readonly ConcurrentDictionary<nint, (uint Min, uint Max, WinEventCallback Callback)> _hooks = new();
    private readonly ConcurrentQueue<(uint Min, uint Max, int Thread)> _installed = new();
    private readonly ConcurrentQueue<(nint Hook, int Thread)> _removed = new();
    private int _nextHook = 0x100;

    /// <summary>What SetWinEventHook returns for a range: true installs it, false fails it (zero). Default: every one installs.</summary>
    public Func<uint, uint, bool> OnInstall { get; set; } = (_, _) => true;

    /// <summary>The managed thread that ran the message loop.</summary>
    public int? LoopThread { get; private set; }

    /// <summary>True once the loop's thread created its message queue.</summary>
    public bool QueueCreated { get; private set; }

    /// <summary>The hooks installed and not removed.</summary>
    public int ActiveHooks => _hooks.Count;

    /// <summary>Every hook installed, with the managed thread that installed it, in order.</summary>
    public IReadOnlyList<(uint Min, uint Max, int Thread)> Installed => _installed.ToArray();

    /// <summary>Every hook removed, with the managed thread that removed it, in order.</summary>
    public IReadOnlyList<(nint Hook, int Thread)> Removed => _removed.ToArray();

    /// <summary>How often the stop message was posted.</summary>
    public int StopsPosted => Volatile.Read(ref _stopsPosted);

    private int _stopsPosted;

    /// <summary>A WinEvent, as Windows queues it to the thread that hooked it.</summary>
    public void Raise(uint eventType, nint window = 0x500, int objectId = 0, int childId = 0) =>
        _queue.Add(new Event(eventType, window, objectId, childId));

    /// <summary>Puts a message of the given kind on the loop's queue.</summary>
    public void Post(WinEventLoopMessage message) => _queue.Add(message);

    public uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;

    public void CreateMessageQueue()
    {
        LoopThread = Environment.CurrentManagedThreadId;
        QueueCreated = true;
    }

    public nint SetWinEventHook(uint eventMin, uint eventMax, WinEventCallback callback)
    {
        if (!OnInstall(eventMin, eventMax))
        {
            return nint.Zero;
        }

        var hook = new nint(Interlocked.Increment(ref _nextHook));
        _hooks[hook] = (eventMin, eventMax, callback);
        _installed.Enqueue((eventMin, eventMax, Environment.CurrentManagedThreadId));
        return hook;
    }

    public bool UnhookWinEvent(nint hook)
    {
        _removed.Enqueue((hook, Environment.CurrentManagedThreadId));
        return _hooks.TryRemove(hook, out _);
    }

    public WinEventLoopMessage WaitForMessage()
    {
        while (_queue.TryTake(out var item, TimeSpan.FromSeconds(30)))
        {
            if (item is WinEventLoopMessage message)
            {
                return message;
            }

            // A window event: the hooks that cover it are called here, on the loop's thread, while it waits
            var raised = (Event)item;
            foreach (var hook in _hooks.Values.Where(h => h.Min <= raised.Type && raised.Type <= h.Max))
            {
                hook.Callback(raised.Type, raised.Window, raised.ObjectId, raised.ChildId);
            }
        }

        // Never block a test run forever
        return WinEventLoopMessage.Failed;
    }

    public bool PostUpdateMessage(uint threadId)
    {
        _queue.Add(WinEventLoopMessage.Update);
        return true;
    }

    public bool PostStopMessage(uint threadId)
    {
        Interlocked.Increment(ref _stopsPosted);
        _queue.Add(WinEventLoopMessage.Stop);
        return true;
    }

    private sealed record Event(uint Type, nint Window, int ObjectId, int ChildId);
}
