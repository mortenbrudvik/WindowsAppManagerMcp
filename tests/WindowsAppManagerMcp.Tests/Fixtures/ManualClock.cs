namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now = start;

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

    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
        }
    }

    public override DateTimeOffset GetUtcNow() => Now;
}
