namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A monotonic clock the test moves by hand, with timers: a timer created on it (a CancellationTokenSource made with it,
/// Task.Delay or Task.WaitAsync with it) fires when <see cref="Advance"/> moves the clock to its due time, and never on its
/// own. A test of a deadline then decides when the deadline passes, so a busy machine cannot move it before the code under
/// test is where the test expects it (roadmap M3.F29). <see cref="ManualClock"/> has no timers of its own: a timer created
/// on it runs on the system's clock.
/// </summary>
public sealed class ManualTimerClock : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    /// <summary>The time the clock has been moved by since it was created.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromTicks(_ticks);
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => Start + Elapsed;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Elapsed.Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Lets <paramref name="by"/> pass: each timer that falls due on the way fires on this thread, in the order of its due
    /// time, with the clock at that time; a periodic one fires once per period that passes.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        long target;
        lock (_lock)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            ManualTimer? due;
            lock (_lock)
            {
                due = _timers.Where(t => t.Due <= target).MinBy(t => t.Due);
                if (due == null)
                {
                    _ticks = target;
                    return;
                }

                _ticks = Math.Max(_ticks, due.Due);
                if (due.Period > 0)
                {
                    due.Due += due.Period;
                }
                else
                {
                    _timers.Remove(due);
                }
            }

            // Outside the lock: the callback may change or dispose its timer, or create another
            due.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimerClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;

        /// <summary>The clock's ticks at which the timer fires next.</summary>
        public long Due { get; set; }

        /// <summary>The ticks between two firings; 0 for a timer that fires once.</summary>
        public long Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._lock)
            {
                if (_disposed)
                {
                    return false;
                }

                clock._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = clock._ticks + Math.Max(0, dueTime.Ticks);
                    Period = period == Timeout.InfiniteTimeSpan ? 0 : Math.Max(0, period.Ticks);
                    clock._timers.Add(this);
                }

                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._lock)
            {
                _disposed = true;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
