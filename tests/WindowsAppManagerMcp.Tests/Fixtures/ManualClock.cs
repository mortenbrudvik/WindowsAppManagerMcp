namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A clock the test moves by hand. It has a wall clock (<see cref="GetUtcNow"/>) and a monotonic clock
/// (<see cref="GetTimestamp"/>), as <see cref="TimeProvider.System"/> does: <see cref="Advance"/> moves both, and
/// <see cref="SetWallClock"/> moves only the wall clock, as a time sync or the user changing the clock would.
/// </summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now = start;
    private long _elapsedTicks;

    /// <summary>A clock at 2026-10-10 12:00 UTC.</summary>
    public ManualClock()
        : this(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset Now
    {
        get
        {
            lock (_lock)
            {
                return _now;
            }
        }
    }

    /// <summary>Lets <paramref name="by"/> pass: both clocks move.</summary>
    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
            _elapsedTicks += by.Ticks;
        }
    }

    /// <summary>Sets the wall clock to <paramref name="now"/>, forward or back; no time passes on the monotonic clock.</summary>
    public void SetWallClock(DateTimeOffset now)
    {
        lock (_lock)
        {
            _now = now;
        }
    }

    public override DateTimeOffset GetUtcNow() => Now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _elapsedTicks;
        }
    }
}
