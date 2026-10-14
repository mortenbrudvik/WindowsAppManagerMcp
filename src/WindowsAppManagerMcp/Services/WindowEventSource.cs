using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The server's window events for wait_for_window's wake-ups (roadmap M3.F22; see <see cref="IWindowEventSource"/>), on a
/// thread of its own with a message loop: an out-of-context WinEvent hook calls back on the thread that installed it while
/// that thread waits for messages. Everything native goes through <see cref="INativeWinEventWrapper"/>, which the tests
/// replace with a fake.
/// </summary>
/// <remarks>
/// <para>The thread (an STA thread, as a thread with a message loop is) starts with the first subscription and runs until
/// the source is disposed, when the server stops. It installs the hooks while a wait subscribes and removes them when the
/// last subscription ends, so the server listens to nothing between waits. Subscribing and ending a subscription only post
/// the thread a message: neither waits for it.</para>
/// <para>The events (<see cref="HookedEvents"/>) are those of a window itself (OBJID_WINDOW, CHILDID_SELF), not of an object
/// inside it: created, destroyed, shown or hidden, renamed, minimized or restored, cloaked or uncloaked, and the foreground
/// change. Location changes, the noisiest events, are left out: no condition reads where a window is. The callback only
/// calls the subscribers, which only signal; nothing reads a window on this thread.</para>
/// <para>Hooks that cannot be installed leave the waits as they were without events: they poll.</para>
/// </remarks>
public sealed class WindowEventSource : IWindowEventSource, IDisposable
{
    /// <summary>The ranges of WinEvent constants hooked, one hook each.</summary>
    public static readonly IReadOnlyList<(uint Min, uint Max)> HookedEvents =
    [
        // EVENT_SYSTEM_FOREGROUND
        (0x0003, 0x0003),
        // EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND
        (0x0016, 0x0017),
        // EVENT_OBJECT_CREATE, EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE
        (0x8000, 0x8003),
        // EVENT_OBJECT_NAMECHANGE
        (0x800C, 0x800C),
        // EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED
        (0x8017, 0x8018)
    ];

    private const int ObjIdWindow = 0;
    private const int ChildIdSelf = 0;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly INativeWinEventWrapper _native;
    private readonly ILogger<WindowEventSource>? _logger;
    private readonly WinEventCallback _callback;
    private readonly object _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    // The subscribers the callback calls, replaced whole under the lock so the callback reads it without one
    private Subscription[] _listeners = [];

    // Only the event thread touches the hooks
    private readonly List<nint> _hooks = [];
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;

    /// <param name="native">The native hook calls and the thread's message loop.</param>
    /// <param name="logger">The logger.</param>
    public WindowEventSource(INativeWinEventWrapper native, ILogger<WindowEventSource>? logger = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _logger = logger;
        _callback = OnEvent;
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The source was disposed (the server is stopping).</exception>
    public IDisposable Subscribe(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var subscription = new Subscription(this, changed);
        uint update;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(subscription);
            _listeners = [.. _subscriptions];
            if (_thread == null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "WAM window events" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            // The first subscriber wants the hooks. A thread whose queue does not exist yet looks for itself once it does
            update = _subscriptions.Count == 1 ? _threadId : 0;
        }

        if (update != 0)
        {
            _native.PostUpdateMessage(update);
        }

        return subscription;
    }

    /// <summary>Stops the event thread, which removes its hooks first; waits for it at most <see cref="StopTimeout"/>.</summary>
    public void Dispose()
    {
        Thread? thread;
        uint threadId;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            threadId = _threadId;
        }

        if (thread == null)
        {
            return;
        }

        // A thread whose queue does not exist yet sees the flag once it does
        if (threadId != 0)
        {
            _native.PostStopMessage(threadId);
        }

        if (!thread.Join(StopTimeout))
        {
            _logger?.LogWarning("The window event thread did not stop within {Timeout} s", StopTimeout.TotalSeconds);
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        uint update;
        lock (_lock)
        {
            if (!_subscriptions.Remove(subscription))
            {
                return;
            }

            _listeners = [.. _subscriptions];
            update = _subscriptions.Count == 0 && !_disposed ? _threadId : 0;
        }

        // The last one is gone: the thread removes the hooks
        if (update != 0)
        {
            _native.PostUpdateMessage(update);
        }
    }

    /// <summary>The event thread: its queue, the hooks while someone subscribes, and the message loop until the stop message.</summary>
    private void Run()
    {
        try
        {
            var threadId = _native.GetCurrentThreadId();
            _native.CreateMessageQueue();
            lock (_lock)
            {
                _threadId = threadId;
                if (_disposed)
                {
                    return;
                }
            }

            Update();
            while (true)
            {
                switch (_native.WaitForMessage())
                {
                    case WinEventLoopMessage.Update:
                        Update();
                        break;
                    case WinEventLoopMessage.Stop:
                        return;
                    case WinEventLoopMessage.Failed:
                        _logger?.LogWarning("The window event thread's message loop failed; wait_for_window polls without window events");
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The window event thread stopped; wait_for_window polls without window events");
        }
        finally
        {
            Unhook();
        }
    }

    /// <summary>On the event thread: installs the hooks when someone subscribes, removes them when no one does.</summary>
    private void Update()
    {
        bool wanted;
        lock (_lock)
        {
            wanted = _subscriptions.Count > 0 && !_disposed;
        }

        if (!wanted)
        {
            Unhook();
            return;
        }

        if (_hooks.Count > 0)
        {
            return;
        }

        foreach (var (min, max) in HookedEvents)
        {
            var hook = _native.SetWinEventHook(min, max, _callback);
            if (hook != nint.Zero)
            {
                _hooks.Add(hook);
            }
        }

        if (_hooks.Count < HookedEvents.Count)
        {
            _logger?.LogWarning("{Failed} of {Count} window event hooks could not be installed; wait_for_window polls without those events",
                HookedEvents.Count - _hooks.Count, HookedEvents.Count);
        }
    }

    /// <summary>On the event thread: removes the hooks it installed.</summary>
    private void Unhook()
    {
        foreach (var hook in _hooks)
        {
            _native.UnhookWinEvent(hook);
        }

        _hooks.Clear();
    }

    /// <summary>On the event thread, for every hooked event: calls the subscribers when it is about a window itself.</summary>
    private void OnEvent(uint eventType, nint window, int objectId, int childId)
    {
        if (window == nint.Zero || objectId != ObjIdWindow || childId != ChildIdSelf)
        {
            return;
        }

        foreach (var listener in Volatile.Read(ref _listeners))
        {
            listener.Call();
        }
    }

    /// <summary>One subscriber; disposing it ends the subscription, once.</summary>
    private sealed class Subscription(WindowEventSource source, Action changed) : IDisposable
    {
        private int _disposed;

        public void Call()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                changed();
            }
            catch (Exception)
            {
                // A subscriber only signals; one that throws must not keep the others from the event, nor end the thread
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                source.Unsubscribe(this);
            }
        }
    }
}
