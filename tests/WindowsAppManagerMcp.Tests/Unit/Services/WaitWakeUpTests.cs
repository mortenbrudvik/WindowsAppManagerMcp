namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The waits' event wake-ups (roadmap M3.F22): a signal ends the sleep between two checks early, never sooner than
/// <see cref="WaitWakeUp.MinSleepMs"/>, and a sleep without one lasts as long as it would without wake-ups. The sleeps run
/// on test clocks; nothing waits in real time but the hang guard.
/// </summary>
public class WaitWakeUpTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly ManualClock _clock = new();
    private readonly List<TimeSpan> _delays = [];

    /// <summary>Lets the time pass at once, as the wait tests' delays do.</summary>
    private Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _delays.Add(wait);
        _clock.Advance(wait);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ASleepWithoutASignal_LastsTheWholeInterval()
    {
        var wakeUp = new WaitWakeUp();

        var woken = await wakeUp.SleepAsync(TimeSpan.FromMilliseconds(100), DelayAsync, CancellationToken.None);

        woken.Should().BeFalse();
        _delays.Sum(d => d.TotalMilliseconds).Should().Be(100);
    }

    [Fact]
    public async Task ASignalBeforeTheSleep_EndsItAfterMinSleepMs()
    {
        // Arrange - an event while the check read
        var wakeUp = new WaitWakeUp();
        wakeUp.Signal();

        // Act
        var woken = await wakeUp.SleepAsync(TimeSpan.FromMilliseconds(250), DelayAsync, CancellationToken.None);

        // Assert
        woken.Should().BeTrue();
        _delays.Should().Equal(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
    }

    [Fact]
    public async Task ASleepShorterThanMinSleepMs_IsNotLengthened()
    {
        var wakeUp = new WaitWakeUp();
        wakeUp.Signal();

        await wakeUp.SleepAsync(TimeSpan.FromMilliseconds(10), DelayAsync, CancellationToken.None);

        _delays.Should().Equal(TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public async Task Reset_ForgetsTheSignalsBeforeIt()
    {
        var wakeUp = new WaitWakeUp();
        wakeUp.Signal();
        wakeUp.Reset();

        var woken = await wakeUp.SleepAsync(TimeSpan.FromMilliseconds(100), DelayAsync, CancellationToken.None);

        woken.Should().BeFalse();
        _delays.Sum(d => d.TotalMilliseconds).Should().Be(100);
    }

    [Fact]
    public async Task ASignalDuringTheSleep_EndsIt_WithoutTheRestOfTheInterval()
    {
        // Arrange - a clock whose timers fire only when the test moves it
        var clock = new ManualTimerClock();
        var wakeUp = new WaitWakeUp();
        var sleep = wakeUp.SleepAsync(TimeSpan.FromMilliseconds(500), (wait, token) => Task.Delay(wait, clock, token), CancellationToken.None);

        // Act - the least sleep passes, then an event comes
        clock.Advance(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
        wakeUp.Signal();
        var woken = await sleep.WaitAsync(HangGuard);

        // Assert - the sleep ended without the clock reaching 500 ms
        woken.Should().BeTrue();
        clock.Elapsed.Should().Be(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
    }

    [Fact]
    public async Task ASignalDuringTheLeastSleep_DoesNotEndItEarlier()
    {
        var clock = new ManualTimerClock();
        var wakeUp = new WaitWakeUp();
        var sleep = wakeUp.SleepAsync(TimeSpan.FromMilliseconds(500), (wait, token) => Task.Delay(wait, clock, token), CancellationToken.None);

        wakeUp.Signal();
        clock.Advance(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs - 1));
        sleep.IsCompleted.Should().BeFalse("a burst of events wakes one check, after the least sleep");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await sleep.WaitAsync(HangGuard)).Should().BeTrue();
    }

    [Fact]
    public async Task ACancelledSleep_Throws_AsTheDelayDoes()
    {
        var clock = new ManualTimerClock();
        var wakeUp = new WaitWakeUp();
        using var cancel = new CancellationTokenSource();
        var sleep = wakeUp.SleepAsync(TimeSpan.FromMilliseconds(500), (wait, token) => Task.Delay(wait, clock, token), cancel.Token);

        clock.Advance(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
        cancel.Cancel();

        await sleep.Invoking(s => s.WaitAsync(HangGuard)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Signal_FromManyThreads_OnlySetsTheFlag()
    {
        var wakeUp = new WaitWakeUp();

        Parallel.For(0, 100, _ => wakeUp.Signal());

        wakeUp.IsSignalled.Should().BeTrue();
        wakeUp.Reset();
        wakeUp.IsSignalled.Should().BeFalse();
    }
}
