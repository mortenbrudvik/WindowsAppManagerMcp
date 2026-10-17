using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of takeover detection's native side (roadmap M7.4): installing a hook records it instead of hooking the desktop,
/// and the message loop reads from an in-memory queue, where <see cref="Raise"/> puts an input event that the loop hands to
/// the installed hooks on the loop's own thread, as Windows calls a low-level hook. No real hook is ever installed.
/// </summary>
public sealed class FakeLowLevelInputWrapper : INativeLowLevelInputWrapper
{
    private readonly BlockingCollection<object> _queue = new();
    private readonly ConcurrentDictionary<nint, (bool Keyboard, LowLevelInputCallback Callback)> _hooks = new();
    private readonly ConcurrentQueue<(bool Keyboard, int Thread)> _installed = new();
    private int _nextHook = 0x100;
    private int _stopsPosted;

    /// <summary>Whether SetKeyboardHook and SetMouseHook install (true) or fail (zero). Default: both install.</summary>
    public Func<bool, bool> OnInstall { get; set; } = _ => true;

    /// <summary>The managed thread that ran the message loop.</summary>
    public int? LoopThread { get; private set; }

    /// <summary>True once the loop's thread created its message queue.</summary>
    public bool QueueCreated { get; private set; }

    /// <summary>The hooks installed and not removed.</summary>
    public int ActiveHooks => _hooks.Count;

    /// <summary>Every hook installed (keyboard or mouse), with the managed thread that installed it, in order.</summary>
    public IReadOnlyList<(bool Keyboard, int Thread)> Installed => _installed.ToArray();

    /// <summary>How often the stop message was posted.</summary>
    public int StopsPosted => Volatile.Read(ref _stopsPosted);

    /// <summary>
    /// An input event, as Windows hands it to the low-level hooks: a key event to the keyboard hooks, any other to the mouse
    /// hooks.
    /// </summary>
    public void Raise(LowLevelInputKind kind, bool injected = false) => _queue.Add(new LowLevelInput(kind, injected));

    public uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;

    public void CreateMessageQueue()
    {
        LoopThread = Environment.CurrentManagedThreadId;
        QueueCreated = true;
    }

    public nint SetKeyboardHook(LowLevelInputCallback callback) => Install(keyboard: true, callback);

    public nint SetMouseHook(LowLevelInputCallback callback) => Install(keyboard: false, callback);

    public bool Unhook(nint hook) => _hooks.TryRemove(hook, out _);

    public WinEventLoopMessage WaitForMessage()
    {
        while (_queue.TryTake(out var item, TimeSpan.FromSeconds(30)))
        {
            if (item is WinEventLoopMessage message)
            {
                return message;
            }

            // An input event: the hooks of its device are called here, on the loop's thread, while it waits
            var input = (LowLevelInput)item;
            var keyboard = input.Kind is LowLevelInputKind.KeyDown or LowLevelInputKind.KeyUp;
            foreach (var hook in _hooks.Values.Where(h => h.Keyboard == keyboard))
            {
                hook.Callback(input);
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

    private nint Install(bool keyboard, LowLevelInputCallback callback)
    {
        if (!OnInstall(keyboard))
        {
            return nint.Zero;
        }

        var hook = new nint(Interlocked.Increment(ref _nextHook));
        _hooks[hook] = (keyboard, callback);
        _installed.Enqueue((keyboard, Environment.CurrentManagedThreadId));
        return hook;
    }
}
