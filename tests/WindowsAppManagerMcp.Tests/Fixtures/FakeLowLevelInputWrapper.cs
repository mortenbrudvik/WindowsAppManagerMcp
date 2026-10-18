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
    private readonly ConcurrentQueue<int> _loopThreads = new();
    private int _nextHook = 0x100;
    private int _stopsPosted;
    private int _callbackExceptions;

    /// <summary>Whether SetKeyboardHook and SetMouseHook install (true) or fail (zero). Default: both install.</summary>
    public Func<bool, bool> OnInstall { get; set; } = _ => true;

    /// <summary>The Win32 error a hook that <see cref="OnInstall"/> fails reports (ERROR_ACCESS_DENIED by default).</summary>
    public int InstallError { get; set; } = 5;

    /// <summary>The managed thread that ran the message loop last.</summary>
    public int? LoopThread { get; private set; }

    /// <summary>Every managed thread that created a message queue (one per monitor thread started), in order.</summary>
    public IReadOnlyList<int> LoopThreads => _loopThreads.ToArray();

    /// <summary>True once the loop's thread created its message queue.</summary>
    public bool QueueCreated { get; private set; }

    /// <summary>The hooks installed and not removed.</summary>
    public int ActiveHooks => _hooks.Count;

    /// <summary>Every hook installed (keyboard or mouse), with the managed thread that installed it, in order.</summary>
    public IReadOnlyList<(bool Keyboard, int Thread)> Installed => _installed.ToArray();

    /// <summary>How often the stop message was posted.</summary>
    public int StopsPosted => Volatile.Read(ref _stopsPosted);

    /// <summary>
    /// How many exceptions a hook's callback threw into the "wrapper": the real one swallows them, because an exception that
    /// crossed back into Windows would end the process, so this one does too, and counts them.
    /// </summary>
    public int CallbackExceptions => Volatile.Read(ref _callbackExceptions);

    /// <summary>
    /// An input event, as Windows hands it to the low-level hooks: a key event to the keyboard hooks, any other to the mouse
    /// hooks.
    /// </summary>
    public void Raise(LowLevelInputKind kind, bool injected = false) => _queue.Add(new LowLevelInput(kind, injected));

    /// <summary>The loop's next wait fails (GetMessage returns -1).</summary>
    public void FailLoop() => _queue.Add(WinEventLoopMessage.Failed);

    /// <summary>The loop's next wait ends it as WM_QUIT does, without the monitor's stop message.</summary>
    public void QuitLoop() => _queue.Add(WinEventLoopMessage.Stop);

    /// <summary>The loop's next wait throws <paramref name="exception"/>.</summary>
    public void ThrowInLoop(Exception exception) => _queue.Add(exception);

    public uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;

    public void CreateMessageQueue()
    {
        LoopThread = Environment.CurrentManagedThreadId;
        _loopThreads.Enqueue(Environment.CurrentManagedThreadId);
        QueueCreated = true;
    }

    public LowLevelHookResult SetKeyboardHook(LowLevelInputCallback callback) => Install(keyboard: true, callback);

    public LowLevelHookResult SetMouseHook(LowLevelInputCallback callback) => Install(keyboard: false, callback);

    public bool Unhook(nint hook) => _hooks.TryRemove(hook, out _);

    public WinEventLoopMessage WaitForMessage()
    {
        while (_queue.TryTake(out var item, TimeSpan.FromSeconds(30)))
        {
            switch (item)
            {
                case WinEventLoopMessage message:
                    return message;
                case Exception exception:
                    throw exception;
            }

            // An input event: the hooks of its device are called here, on the loop's thread, while it waits
            var input = (LowLevelInput)item;
            var keyboard = input.Kind is LowLevelInputKind.KeyDown or LowLevelInputKind.KeyUp;
            foreach (var hook in _hooks.Values.Where(h => h.Keyboard == keyboard))
            {
                try
                {
                    hook.Callback(input);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _callbackExceptions);
                }
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

    private LowLevelHookResult Install(bool keyboard, LowLevelInputCallback callback)
    {
        if (!OnInstall(keyboard))
        {
            return LowLevelHookResult.Failure(InstallError);
        }

        var hook = new nint(Interlocked.Increment(ref _nextHook));
        _hooks[hook] = (keyboard, callback);
        _installed.Enqueue((keyboard, Environment.CurrentManagedThreadId));
        return LowLevelHookResult.Installed(hook);
    }
}
