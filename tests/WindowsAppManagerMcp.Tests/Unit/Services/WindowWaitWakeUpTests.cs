namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// wait_for_window's event wake-ups (roadmap M3.F22): a window event ends the sleep between two polls early, so the next
/// poll comes sooner, and nothing else changes: the same polls decide, stableMs is still measured by polls, and a wait whose
/// events fail, or never come, ends as it does without them. The windows are a <see cref="FakeWindowDesktop"/> and the
/// events a <see cref="FakeWindowEventSource"/>, on test clocks; no hook is installed and no real window is read.
/// </summary>
public class WindowWaitWakeUpTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly FakeWindowDesktop _desktop = new();
    private readonly FakeWindowEventSource _events = new();
    private readonly ManualClock _clock = new();
    private readonly long _start;
    private readonly List<(long AtMs, Action Change)> _changes = [];
    private readonly Mock<INativeProcessWrapper> _processes = new();

    public WindowWaitWakeUpTests()
    {
        _start = _clock.GetTimestamp();
        _processes.Setup(p => p.WaitForInputIdle(It.IsAny<int>(), It.IsAny<int>())).Returns(true);
    }

    /// <summary>The delays the wait asked for, in order.</summary>
    private List<TimeSpan> Delays { get; } = [];

    private long NowMs => (long)_clock.GetElapsedTime(_start).TotalMilliseconds;

    private void At(long atMs, Action change) => _changes.Add((atMs, change));

    /// <summary>The service, with the fake's events, or without an event source (today's polling).</summary>
    private WindowWaitService CreateService(bool withEvents = true) =>
        new(_desktop.Native.Object, _desktop.CreateService(), _processes.Object, _clock, DelayAsync, withEvents ? _events : null);

    private static WindowConditionRequest Request(
        WindowSelector window, WindowWaitCondition condition = WindowWaitCondition.Exists, int timeoutMs = 5000, int stableMs = 0) => new()
    {
        Window = window, Condition = condition, TimeoutMs = timeoutMs, StableMs = stableMs
    };

    /// <summary>Lets the time pass and applies the changes due by then.</summary>
    private Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(wait);
        _clock.Advance(wait);
        foreach (var change in _changes.Where(c => c.AtMs <= NowMs).ToList())
        {
            _changes.Remove(change);
            change.Change();
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AWindowEvent_EndsTheSleep_SoTheNextPollSeesTheWindowSooner()
    {
        // Arrange - the dialog opens 110 ms into the wait, and Windows reports it
        At(110, () =>
        {
            _desktop.Add("Save As", "#32770", processName: "notepad");
            _events.Raise();
        });

        // Act
        var result = await CreateService().WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Save As" }));

        // Assert - polled at 0 and 100; the event woke the sleep after its least 25 ms, so the poll at 125 saw it, not 200
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(125);
        Delays.Select(d => (int)d.TotalMilliseconds).Should().Equal(25, 75, 25);
    }

    [Fact]
    public async Task WithoutAnEventSource_TheSameWait_PollsAsBefore()
    {
        At(110, () =>
        {
            _desktop.Add("Save As", "#32770", processName: "notepad");
            _events.Raise();
        });

        var result = await CreateService(withEvents: false).WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Save As" }));

        result.ElapsedMs.Should().Be(200);
        Delays.Select(d => (int)d.TotalMilliseconds).Should().Equal(100, 100);
        _events.Subscriptions.Should().Be(0);
    }

    [Fact]
    public async Task AWindowEventDuringTheSleep_WakesThePoll_BeforeTheIntervalIsUp()
    {
        // Arrange - a clock whose timers fire only when the test moves it, so the poll at 100 ms cannot come on its own
        var clock = new ManualTimerClock();
        var sut = new WindowWaitService(_desktop.Native.Object, _desktop.CreateService(), _processes.Object, clock,
            (wait, token) => Task.Delay(wait, clock, token), _events);
        var wait = sut.WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Save As" }));

        // Act - the least sleep passes; then the dialog opens and Windows reports it
        clock.Advance(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
        var dialog = _desktop.Add("Save As", "#32770", processName: "notepad");
        _events.Raise();
        var result = await wait.WaitAsync(HangGuard);

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Window!.Handle.Should().Be(dialog);
        result.ElapsedMs.Should().Be(WaitWakeUp.MinSleepMs);
        clock.Elapsed.Should().Be(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
    }

    [Fact]
    public async Task StableMs_IsStillMeasuredByPolls_HoweverOftenEventsWakeThem()
    {
        // Arrange - the dialog opens at 110 ms, and the app raises events all the time
        At(110, () => _desktop.Add("Save As", "#32770", processName: "notepad"));
        for (var at = 0; at < 2000; at += 10)
        {
            At(at, () => _events.Raise());
        }

        // Act
        var result = await CreateService().WaitForConditionAsync(
            Request(new WindowSelector { TitleEquals = "Save As" }, stableMs: 300));

        // Assert - first seen by the poll at 125, so it holds from 125; polls every 25 ms, never sooner
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(425);
        Delays.Should().OnlyContain(d => d.TotalMilliseconds >= WaitWakeUp.MinSleepMs);
    }

    [Theory]
    [InlineData("exists held")]
    [InlineData("gone")]
    [InlineData("timeout")]
    [InlineData("title_changed")]
    [InlineData("foreground")]
    public async Task EventsThatNeverCome_EndTheWaitAsWithoutAnEventSource(string scenario)
    {
        // Act - the same desktop and changes, once with an event source that raises nothing and once without one
        var withEvents = await RunScenario(scenario, withEvents: true);
        var withoutEvents = await RunScenario(scenario, withEvents: false);

        // Assert
        withEvents.Should().BeEquivalentTo(withoutEvents);
    }

    [Fact]
    public async Task ASubscriptionThatFails_LeavesThePollingAsItWas()
    {
        // Arrange
        _events.SubscribeFails = new InvalidOperationException("no hook");
        At(110, () => _desktop.Add("Save As", "#32770", processName: "notepad"));

        // Act
        var result = await CreateService().WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Save As" }));

        // Assert
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(200);
        Delays.Select(d => (int)d.TotalMilliseconds).Should().Equal(100, 100);
    }

    [Fact]
    public async Task AWaitTheFirstPollAnswers_SubscribesToNothing()
    {
        _desktop.Add("Save As", "#32770", processName: "notepad");

        var result = await CreateService().WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Save As" }));

        result.Satisfied.Should().BeTrue();
        _events.Subscriptions.Should().Be(0);
    }

    [Fact]
    public async Task TheSubscription_EndsWithTheWait_SatisfiedOrTimedOut()
    {
        At(150, () => _desktop.Add("Save As", "#32770", processName: "notepad"));
        var satisfied = await CreateService().WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Save As" }));

        var timedOut = await CreateService().WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Print" }, timeoutMs: 500));

        satisfied.Satisfied.Should().BeTrue();
        timedOut.TimedOut.Should().BeTrue();
        _events.Subscriptions.Should().Be(2);
        _events.Active.Should().Be(0);
    }

    [Fact]
    public async Task TheSubscription_EndsWithACancelledWait()
    {
        using var cancel = new CancellationTokenSource();
        At(300, cancel.Cancel);

        var wait = () => CreateService().WaitForConditionAsync(
            Request(new WindowSelector { TitleEquals = "Save As" }), cancellationToken: cancel.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
        _events.Subscriptions.Should().Be(1);
        _events.Active.Should().Be(0);
    }

    /// <summary>One wait on a fresh desktop, with the changes of <paramref name="scenario"/>.</summary>
    private async Task<WindowConditionResult> RunScenario(string scenario, bool withEvents)
    {
        var desktop = new FakeWindowDesktop();
        var clock = new ManualClock();
        var start = clock.GetTimestamp();
        var changes = new List<(long AtMs, Action Change)>();
        Task Delay(TimeSpan wait, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(wait);
            var now = (long)clock.GetElapsedTime(start).TotalMilliseconds;
            foreach (var change in changes.Where(c => c.AtMs <= now).ToList())
            {
                changes.Remove(change);
                change.Change();
            }

            return Task.CompletedTask;
        }

        var sut = new WindowWaitService(desktop.Native.Object, desktop.CreateService(), _processes.Object, clock, Delay,
            withEvents ? new FakeWindowEventSource() : null);
        var notepad = desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        switch (scenario)
        {
            case "exists held":
                changes.Add((300, () => desktop.Add("Save As", "#32770", processName: "notepad")));
                changes.Add((450, () => desktop.Close(notepad)));
                return await sut.WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Save As" }, stableMs: 300));
            case "gone":
                changes.Add((700, () => desktop.Close(notepad)));
                return await sut.WaitForConditionAsync(
                    Request(new WindowSelector { ProcessName = "notepad" }, WindowWaitCondition.Gone, stableMs: 300));
            case "timeout":
                return await sut.WaitForConditionAsync(Request(new WindowSelector { TitleEquals = "Print" }, timeoutMs: 3000, stableMs: 300));
            case "title_changed":
                changes.Add((2600, () => desktop.SetTitle(notepad, "*notes.txt - Notepad")));
                return await sut.WaitForConditionAsync(
                    Request(new WindowSelector { Handle = notepad }, WindowWaitCondition.TitleChanged, stableMs: 300));
            default:
                changes.Add((900, () => desktop.SetForeground(notepad)));
                return await sut.WaitForConditionAsync(
                    Request(new WindowSelector { ProcessName = "notepad" }, WindowWaitCondition.Foreground, stableMs: 300));
        }
    }
}
