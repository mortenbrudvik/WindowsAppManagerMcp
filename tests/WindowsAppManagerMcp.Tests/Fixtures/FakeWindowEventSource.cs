namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of the window events behind wait_for_window's wake-ups (roadmap M3.F22): no hook is installed, and a test raises
/// an event with <see cref="Raise"/>, on its own thread, as the window event thread would.
/// </summary>
public sealed class FakeWindowEventSource : IWindowEventSource
{
    private readonly object _lock = new();
    private readonly List<Action> _listeners = [];
    private int _subscriptions;

    /// <summary>Thrown by <see cref="Subscribe"/> when set.</summary>
    public Exception? SubscribeFails { get; set; }

    /// <summary>How often a wait subscribed.</summary>
    public int Subscriptions => Volatile.Read(ref _subscriptions);

    /// <summary>The subscriptions not disposed yet.</summary>
    public int Active
    {
        get
        {
            lock (_lock)
            {
                return _listeners.Count;
            }
        }
    }

    /// <summary>A window event: every subscriber is told.</summary>
    public void Raise()
    {
        List<Action> listeners;
        lock (_lock)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener();
        }
    }

    public IDisposable Subscribe(Action changed)
    {
        Interlocked.Increment(ref _subscriptions);
        if (SubscribeFails is { } failure)
        {
            throw failure;
        }

        lock (_lock)
        {
            _listeners.Add(changed);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                _listeners.Remove(changed);
            }
        });
    }

    private sealed class Subscription(Action remove) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                remove();
            }
        }
    }
}
