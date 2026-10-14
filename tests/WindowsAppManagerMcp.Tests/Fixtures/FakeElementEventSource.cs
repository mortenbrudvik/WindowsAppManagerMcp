namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake of the UI Automation events behind wait_for_element's wake-ups (roadmap M3.F22): no handler is registered, a
/// watch is recorded with what it asked for, and a test raises an event with <see cref="Raise"/>, on its own thread, as one
/// of UI Automation's event threads would.
/// </summary>
public sealed class FakeElementEventSource : IElementEventSource
{
    private readonly object _lock = new();
    private readonly List<(nint Window, int TargetProcessId, IReadOnlyCollection<UiaWatchedProperty> Properties)> _watches = [];
    private readonly List<Action> _active = [];

    /// <summary>Thrown by <see cref="Watch"/> when set.</summary>
    public Exception? WatchFails { get; set; }

    /// <summary>When true, <see cref="Watch"/> returns null, as for an application whose handler calls are stuck.</summary>
    public bool Refuses { get; set; }

    /// <summary>Every watch asked for, in order.</summary>
    public IReadOnlyList<(nint Window, int TargetProcessId, IReadOnlyCollection<UiaWatchedProperty> Properties)> Watches
    {
        get
        {
            lock (_lock)
            {
                return [.. _watches];
            }
        }
    }

    /// <summary>The watches not disposed yet.</summary>
    public int Active
    {
        get
        {
            lock (_lock)
            {
                return _active.Count;
            }
        }
    }

    /// <summary>A UI Automation event: every active watch is told.</summary>
    public void Raise()
    {
        List<Action> active;
        lock (_lock)
        {
            active = [.. _active];
        }

        foreach (var changed in active)
        {
            changed();
        }
    }

    public IDisposable? Watch(nint window, int targetProcessId, IReadOnlyCollection<UiaWatchedProperty> properties, Action changed)
    {
        lock (_lock)
        {
            _watches.Add((window, targetProcessId, properties));
        }

        if (WatchFails is { } failure)
        {
            throw failure;
        }

        if (Refuses)
        {
            return null;
        }

        lock (_lock)
        {
            _active.Add(changed);
        }

        return new Ending(() =>
        {
            lock (_lock)
            {
                _active.Remove(changed);
            }
        });
    }

    private sealed class Ending(Action end) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                end();
            }
        }
    }
}
