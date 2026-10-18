namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A takeover monitor (roadmap M7.4) whose watches the test fires itself (<see cref="TrackedWatch.Signal"/>), as the user's
/// keyboard or mouse input would. No hook, no thread.
/// </summary>
public sealed class StubTakeoverMonitor(TakeoverMode mode) : ITakeoverMonitor
{
    public TakeoverMode Mode => mode;

    /// <summary>The state <see cref="Status"/> reports: Ready unless the test says otherwise (Unavailable: detection does not work).</summary>
    public TakeoverState State { get; set; } = TakeoverState.Ready;

    /// <summary>The error <see cref="Status"/> reports with it.</summary>
    public string? Error { get; set; }

    public TakeoverStatus Status => new(mode, State, TakeoverOptions.EnvironmentVariable, Error);

    /// <summary>How many watches were started.</summary>
    public int Watches { get; private set; }

    /// <summary>The last watch started; null before the first.</summary>
    public TrackedWatch? Last { get; private set; }

    public TakeoverWatch Watch()
    {
        Watches++;
        Last = new TrackedWatch(mode);
        return Last.Watch;
    }
}

/// <summary>A watch, and whether the operation that took it disposed it.</summary>
public sealed class TrackedWatch
{
    public TrackedWatch(TakeoverMode mode) => Watch = new TakeoverWatch(mode, _ => Disposed = true);

    public TakeoverWatch Watch { get; }

    public bool Disposed { get; private set; }

    /// <summary>The user's input: fires the watch.</summary>
    public void Signal() => Watch.Signal();
}
