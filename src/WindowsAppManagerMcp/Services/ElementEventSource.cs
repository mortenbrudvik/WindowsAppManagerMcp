using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The server's UI Automation events for wait_for_element's wake-ups (roadmap M3.F22; see <see cref="IElementEventSource"/>):
/// a StructureChanged and a PropertyChanged handler on the waited window's subtree, through <see cref="IUiaTree"/>.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Adding a handler and removing it cross into the provider's process, and a hung provider can block
/// either for as long as it hangs (the managed client has no timeout of its own). So each watch has a background MTA thread
/// of its own (UI Automation clients must not run on an STA thread), which opens the window, adds the handlers, waits until
/// the watch is disposed and removes them, all on that thread: never on the caller's, on the read worker, or on one of UI
/// Automation's event threads, where removing a handler can deadlock. <see cref="Watch"/> and the watch's Dispose only
/// start that thread or signal it, so a wait never waits for either.</para>
/// <para><b>The bound.</b> A call (the window and its handlers, or their removal) that has not returned within
/// <see cref="CallTimeout"/> is abandoned: it counts as stuck against its application until it returns, as the read
/// worker counts its stuck reads (<see cref="UiaWorker"/>). While one call of an application is stuck, or
/// <see cref="MaxStuckCalls"/> calls of any are, <see cref="Watch"/> returns null and that wait polls without events, so a
/// hung application never holds more than one thread here. A registration that returns after its wait ended is removed at
/// once.</para>
/// <para><b>Events.</b> The handlers only call the watch's callback, which only signals the wait; no UI Automation call is
/// made on an event thread. A watch that could not add its handlers (the window is gone, the provider refuses) gives no
/// events, and its wait polls as it would without them.</para>
/// </remarks>
public sealed class ElementEventSource : IElementEventSource
{
    /// <summary>How long a call to add or remove the handlers may take before it counts as stuck.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    /// <summary>While this many calls of all applications are stuck, no new watch starts.</summary>
    public const int MaxStuckCalls = 8;

    private readonly IUiaTree _tree;
    private readonly TimeProvider _time;
    private readonly ILogger<ElementEventSource>? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<int, int> _stuckByTarget = [];
    private int _stuckCalls;

    /// <param name="tree">Adds and removes the handlers.</param>
    /// <param name="time">The clock of the bound; default the system clock.</param>
    /// <param name="logger">The logger.</param>
    public ElementEventSource(IUiaTree tree, TimeProvider? time = null, ILogger<ElementEventSource>? logger = null)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Handler calls that missed their bound and have not returned yet, over all applications.</summary>
    public int StuckCalls
    {
        get
        {
            lock (_lock)
            {
                return _stuckCalls;
            }
        }
    }

    /// <summary>Handler calls for <paramref name="targetProcessId"/> that missed their bound and have not returned yet.</summary>
    public int StuckCallsFor(int targetProcessId)
    {
        lock (_lock)
        {
            return _stuckByTarget.GetValueOrDefault(Key(targetProcessId));
        }
    }

    /// <inheritdoc />
    public IDisposable? Watch(nint window, int targetProcessId, IReadOnlyCollection<UiaWatchedProperty> properties, Action changed)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(changed);
        var target = Key(targetProcessId);
        lock (_lock)
        {
            if (_stuckByTarget.GetValueOrDefault(target) > 0 || _stuckCalls >= MaxStuckCalls)
            {
                return null;
            }
        }

        var watch = new ElementWatch(this, window, target, [.. properties], changed);
        var thread = new Thread(watch.Run) { IsBackground = true, Name = "UI Automation events" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return watch;
    }

    private static int Key(int targetProcessId) => targetProcessId > 0 ? targetProcessId : 0;

    private void MarkStuck(ElementWatch watch, int call)
    {
        lock (_lock)
        {
            // The bound of a call that already returned (its timer fired just as it did) is not this one's
            if (watch.Call != call || !watch.InCall || watch.Stuck)
            {
                return;
            }

            watch.Stuck = true;
            _stuckByTarget[watch.Target] = _stuckByTarget.GetValueOrDefault(watch.Target) + 1;
            _stuckCalls++;
        }

        _logger?.LogWarning("UI Automation did not add or remove the event handlers of process {ProcessId} within {Timeout} s; " +
                            "its waits poll without events until the call returns", watch.Target, CallTimeout.TotalSeconds);
    }

    private int BeginCall(ElementWatch watch)
    {
        lock (_lock)
        {
            watch.InCall = true;
            return ++watch.Call;
        }
    }

    private void EndCall(ElementWatch watch)
    {
        lock (_lock)
        {
            watch.InCall = false;
            if (!watch.Stuck)
            {
                return;
            }

            watch.Stuck = false;
            var remaining = _stuckByTarget.GetValueOrDefault(watch.Target) - 1;
            if (remaining > 0)
            {
                _stuckByTarget[watch.Target] = remaining;
            }
            else
            {
                _stuckByTarget.Remove(watch.Target);
            }

            _stuckCalls--;
        }
    }

    /// <summary>One watch: its thread adds the handlers, waits for the end, and removes them.</summary>
    private sealed class ElementWatch(
        ElementEventSource source, nint window, int target, IReadOnlyCollection<UiaWatchedProperty> properties, Action changed) : IDisposable
    {
        private readonly ManualResetEventSlim _ended = new();
        private readonly List<IDisposable> _registrations = [];
        private int _disposed;

        public int Target => target;

        /// <summary>Under the source's lock: a call to UI Automation is running.</summary>
        public bool InCall { get; set; }

        /// <summary>Under the source's lock: the number of the last call started.</summary>
        public int Call { get; set; }

        /// <summary>Under the source's lock: that call missed its bound and is counted as stuck.</summary>
        public bool Stuck { get; set; }

        /// <summary>The wait ended; its handlers are removed on the watch's thread. Never blocks.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ended.Set();
            }
        }

        public void Run()
        {
            try
            {
                Bounded(Add);
                _ended.Wait();
                Bounded(Remove);
            }
            catch (Exception ex)
            {
                source._logger?.LogDebug(ex, "The UI Automation event watch of window {Window} ended", (long)window);
            }
        }

        /// <summary>A UI Automation call, under the bound: past it, the call counts as stuck until it returns.</summary>
        private void Bounded(Action call)
        {
            var number = source.BeginCall(this);
            using var bound = source._time.CreateTimer(_ => source.MarkStuck(this, number), null, CallTimeout, Timeout.InfiniteTimeSpan);
            try
            {
                call();
            }
            finally
            {
                source.EndCall(this);
            }
        }

        private void Add()
        {
            try
            {
                if (source._tree.FromHandle(window) is not { } node)
                {
                    return;
                }

                _registrations.Add(source._tree.AddStructureChangedHandler(node, Signal));
                if (properties.Count > 0)
                {
                    _registrations.Add(source._tree.AddPropertyChangedHandler(node, properties, Signal));
                }
            }
            catch (Exception ex)
            {
                // Events only make the wait quicker: it polls without them
                source._logger?.LogDebug(ex, "UI Automation did not add the event handlers of window {Window}", (long)window);
            }
        }

        private void Remove()
        {
            foreach (var registration in _registrations)
            {
                try
                {
                    registration.Dispose();
                }
                catch (Exception ex)
                {
                    source._logger?.LogDebug(ex, "UI Automation did not remove an event handler of window {Window}", (long)window);
                }
            }

            _registrations.Clear();
        }

        /// <summary>On UI Automation's event threads: only signals, and only while the wait runs.</summary>
        private void Signal()
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
                // Nothing may leave an event handler for UI Automation's event thread
            }
        }
    }
}
