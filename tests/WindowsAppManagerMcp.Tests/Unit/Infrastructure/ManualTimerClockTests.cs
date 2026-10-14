namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The test clock whose timers fire only when the test moves it (roadmap M3.F29): the deadline tests rely on a timer
/// that never fires before its time and always fires once the clock reaches it.
/// </summary>
public class ManualTimerClockTests
{
    [Fact]
    public void ACancellationSourceMadeWithIt_IsCancelledWhenTheClockReachesItsDelay_AndNotBefore()
    {
        // Arrange
        var clock = new ManualTimerClock();
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);

        // Act
        clock.Advance(TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1));
        var cancelledBefore = source.IsCancellationRequested;
        clock.Advance(TimeSpan.FromTicks(1));

        // Assert
        cancelledBefore.Should().BeFalse();
        source.IsCancellationRequested.Should().BeTrue();
        clock.Elapsed.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AWaitOnIt_TimesOutWhenTheClockPassesItsTimeout()
    {
        // Arrange
        var clock = new ManualTimerClock();
        var never = new TaskCompletionSource();

        // Act
        var wait = never.Task.WaitAsync(TimeSpan.FromMilliseconds(500), clock);
        var completedBefore = wait.IsCompleted;
        clock.Advance(TimeSpan.FromSeconds(1));

        // Assert
        completedBefore.Should().BeFalse();
        await wait.Invoking(w => w.WaitAsync(TimeSpan.FromSeconds(30))).Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public void ATimer_FiresInDueOrder_OncePerPeriodThatPasses_AndNeverAfterItIsDisposed()
    {
        // Arrange
        var clock = new ManualTimerClock();
        var fired = new List<string>();
        using var periodic = clock.CreateTimer(_ => fired.Add($"periodic {clock.Elapsed.TotalSeconds}"), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var once = clock.CreateTimer(_ => fired.Add($"once {clock.Elapsed.TotalSeconds}"), null, TimeSpan.FromSeconds(1.5),
            Timeout.InfiniteTimeSpan);
        var disposed = clock.CreateTimer(_ => fired.Add("disposed"), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        disposed.Dispose();

        // Act
        clock.Advance(TimeSpan.FromSeconds(3));
        once.Dispose();

        // Assert
        fired.Should().Equal("periodic 1", "once 1.5", "periodic 2", "periodic 3");
        disposed.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan).Should().BeFalse();
    }
}
