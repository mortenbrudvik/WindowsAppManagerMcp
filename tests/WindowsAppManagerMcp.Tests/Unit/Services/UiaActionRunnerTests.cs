namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The action runner (roadmap M3.6): pattern calls run on a background MTA thread of their own, an action that blocks past
/// the deadline is left running and counted, the count is capped per application and overall, and it drops when the
/// action returns. The actions here are plain delegates; nothing touches UI Automation.
/// </summary>
public class UiaActionRunnerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);
    private const int App = 4242;
    private const int OtherApp = 5151;

    [Fact]
    public async Task RunAsync_RunsTheActionOnABackgroundMtaThread_AndReportsItCompleted()
    {
        // Arrange
        var sut = new UiaActionRunner();
        var caller = Environment.CurrentManagedThreadId;
        (int Thread, bool Background, ApartmentState Apartment) seen = default;

        // Act
        var outcome = await sut.RunAsync(App, () => seen = (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsBackground,
            Thread.CurrentThread.GetApartmentState()), Long, CancellationToken.None);

        // Assert
        outcome.Status.Should().Be(UiaActionStatus.Completed);
        outcome.Error.Should().BeNull();
        seen.Thread.Should().NotBe(caller);
        seen.Background.Should().BeTrue();
        seen.Apartment.Should().Be(ApartmentState.MTA);
        sut.Pending.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ReportsTheActionsException_AsCompletedWithTheError()
    {
        var outcome = await new UiaActionRunner().RunAsync(App, () => throw new InvalidOperationException("refused"), Long, CancellationToken.None);

        outcome.Status.Should().Be(UiaActionStatus.Completed);
        outcome.Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("refused");
    }

    [Fact]
    public async Task AnActionBlockedPastTheDeadline_IsPending_AndCountedUntilItReturns()
    {
        // Arrange - an Invoke that waits for the dialog it opened
        var sut = new UiaActionRunner();
        using var dialogClosed = new ManualResetEventSlim();

        // Act
        var outcome = await sut.RunAsync(App, () => dialogClosed.Wait(Long), Short, CancellationToken.None);

        // Assert
        outcome.Status.Should().Be(UiaActionStatus.Pending);
        sut.Pending.Should().Be(1);
        dialogClosed.Set();
        await WaitUntil(() => sut.Pending == 0);
    }

    [Fact]
    public async Task WhileTwoActionsOnAnApplicationAreBlocked_ANewOneOnItIsRefused_ButOtherApplicationsAreNot()
    {
        // Arrange
        var sut = new UiaActionRunner();
        using var release = new ManualResetEventSlim();
        await sut.RunAsync(App, () => release.Wait(Long), Short, CancellationToken.None);
        await sut.RunAsync(App, () => release.Wait(Long), Short, CancellationToken.None);
        var ran = false;

        // Act
        var refused = await sut.RunAsync(App, () => ran = true, Long, CancellationToken.None);
        var other = await sut.RunAsync(OtherApp, () => { }, Long, CancellationToken.None);

        // Assert
        refused.Status.Should().Be(UiaActionStatus.Refused);
        refused.Message.Should().Contain("dialog");
        ran.Should().BeFalse("a refused action is never started");
        other.Status.Should().Be(UiaActionStatus.Completed);
        release.Set();
        await WaitUntil(() => sut.Pending == 0);
    }

    [Fact]
    public async Task TheOverallCap_RefusesEveryNewAction()
    {
        // Arrange
        var sut = new UiaActionRunner(maxPendingPerTarget: 1, maxPending: 2);
        using var release = new ManualResetEventSlim();
        await sut.RunAsync(1, () => release.Wait(Long), Short, CancellationToken.None);
        await sut.RunAsync(2, () => release.Wait(Long), Short, CancellationToken.None);

        // Act
        var refused = await sut.RunAsync(3, () => { }, Long, CancellationToken.None);

        // Assert
        refused.Status.Should().Be(UiaActionStatus.Refused);
        release.Set();
        await WaitUntil(() => sut.Pending == 0);
    }

    [Fact]
    public async Task ACancelledRequest_StopsWaiting_AndLeavesTheActionCounted()
    {
        // Arrange - the request is cancelled once the action runs, not by a timer, so a slow start cannot cancel it before
        // the action is started
        var sut = new UiaActionRunner();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();

        // Act
        var run = sut.RunAsync(App, () =>
        {
            started.Set();
            release.Wait(Long);
        }, Long, cancellation.Token);
        started.Wait(Long).Should().BeTrue();
        cancellation.Cancel();

        // Assert
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        sut.Pending.Should().Be(1);
        release.Set();
        await WaitUntil(() => sut.Pending == 0);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Long;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(10);
        }
    }
}
