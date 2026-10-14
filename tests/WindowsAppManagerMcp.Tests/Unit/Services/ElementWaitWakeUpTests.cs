namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// wait_for_element's event wake-ups (roadmap M3.F22): a UI Automation event ends the sleep between two checks early, so
/// the next check comes sooner, and nothing else changes: the same checks decide, stableMs is still measured by checks, the
/// back-off counts only the sleeps that ran their length, and a wait whose watch fails, is refused or never raises an event
/// ends as it does without one. Over an in-memory tree with the real search and registry, a mocked window wait and a
/// <see cref="FakeElementEventSource"/>, on test clocks; nothing reads the real desktop and no handler is registered.
/// </summary>
public class ElementWaitWakeUpTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly nint Handle = 0x7200;
    private const int ProcessId = 300;

    private readonly Desktop _desktop = new();
    private readonly FakeElementEventSource _events = new();

    private static ElementWaitRequest Request(
        ElementWaitCondition condition = ElementWaitCondition.Exists, string name = "OK", int timeoutMs = 5000, int stableMs = 0,
        string? expected = null) => new()
    {
        Window = new WindowSelector { ProcessName = "editor" },
        Criteria = new ElementQuery(nint.Zero, null, name),
        Condition = condition,
        Expected = expected,
        TimeoutMs = timeoutMs,
        StableMs = stableMs
    };

    [Fact]
    public async Task AnEvent_EndsTheSleep_SoTheNextCheckSeesTheElementSooner()
    {
        // Arrange - the button appears 110 ms into the wait, and UI Automation reports it
        var body = _desktop.BuildWindow();
        _desktop.At(110, () =>
        {
            body.With(Desktop.Button("OK"));
            _events.Raise();
        });

        // Act
        var result = await _desktop.CreateSut(_events).WaitAsync(Request());

        // Assert - checked at 0 and 100; the event woke the sleep after its least 25 ms, so the check at 125 saw it, not 250
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(125);
        result.Polls.Should().Be(3);
        _desktop.Delays.Select(d => (int)d.TotalMilliseconds).Should().Equal(25, 75, 25);
    }

    [Fact]
    public async Task WithoutAnEventSource_TheSameWait_ChecksAsBefore()
    {
        var body = _desktop.BuildWindow();
        _desktop.At(110, () =>
        {
            body.With(Desktop.Button("OK"));
            _events.Raise();
        });

        var result = await _desktop.CreateSut(null).WaitAsync(Request());

        result.ElapsedMs.Should().Be(250);
        _desktop.Delays.Select(d => (int)d.TotalMilliseconds).Should().Equal(100, 150);
        _events.Watches.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEventDuringTheSleep_WakesTheCheck_BeforeTheIntervalIsUp()
    {
        // Arrange - a clock whose timers fire only when the test moves it; the test learns of each sleep the wait starts
        var clock = new ManualTimerClock();
        using var sleeps = new SemaphoreSlim(0);
        var body = _desktop.BuildWindow();
        var sut = _desktop.CreateSut(_events, clock, (wait, token) =>
        {
            var delay = Task.Delay(wait, clock, token);
            sleeps.Release();
            return delay;
        });
        var wait = sut.WaitAsync(Request());

        // Act - the least sleep passes; then the button appears and UI Automation reports it
        (await sleeps.WaitAsync(HangGuard)).Should().BeTrue();
        clock.Advance(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
        body.With(Desktop.Button("OK"));
        _events.Raise();
        var result = await wait.WaitAsync(HangGuard);

        // Assert - the clock never reached the 100 ms the next check was due at
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(WaitWakeUp.MinSleepMs);
        clock.Elapsed.Should().Be(TimeSpan.FromMilliseconds(WaitWakeUp.MinSleepMs));
    }

    [Fact]
    public async Task StableMs_IsStillMeasuredByChecks_HoweverOftenEventsWakeThem()
    {
        // Arrange - the button appears at 110 ms, and the app raises events all the time
        var body = _desktop.BuildWindow();
        _desktop.At(110, () => body.With(Desktop.Button("OK")));
        for (var at = 0; at < 2000; at += 10)
        {
            _desktop.At(at, _events.Raise);
        }

        // Act
        var result = await _desktop.CreateSut(_events).WaitAsync(Request(stableMs: 300));

        // Assert - first seen by the check at 125, so it holds from 125, checked every 25 ms and never sooner
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(425);
        _desktop.Delays.Should().OnlyContain(d => d.TotalMilliseconds >= WaitWakeUp.MinSleepMs);
    }

    [Fact]
    public async Task TheBackOff_CountsOnlyTheSleepsThatRanTheirLength()
    {
        // Arrange - one event, during the first sleep
        _desktop.BuildWindow();
        _desktop.At(10, _events.Raise);

        // Act
        await _desktop.CreateSut(_events).WaitAsync(Request(timeoutMs: 600));

        // Assert - the woken sleep (25) does not move the back-off on: the next interval is still 100, then 150, then 225
        _desktop.Delays.Select(d => (int)d.TotalMilliseconds).Should().StartWith([25, 25, 75, 25, 125, 25, 200]);
    }

    [Theory]
    [InlineData(ElementWaitCondition.Exists, new[] { UiaWatchedProperty.Name })]
    [InlineData(ElementWaitCondition.Gone, new[] { UiaWatchedProperty.Name })]
    [InlineData(ElementWaitCondition.Enabled, new[] { UiaWatchedProperty.IsEnabled })]
    [InlineData(ElementWaitCondition.Visible, new[] { UiaWatchedProperty.IsOffscreen, UiaWatchedProperty.BoundingRectangle })]
    [InlineData(ElementWaitCondition.Focused, new[] { UiaWatchedProperty.HasKeyboardFocus })]
    [InlineData(ElementWaitCondition.NameEquals, new[] { UiaWatchedProperty.Name })]
    [InlineData(ElementWaitCondition.ValueEquals, new[] { UiaWatchedProperty.Value })]
    [InlineData(ElementWaitCondition.ToggleState, new[] { UiaWatchedProperty.ToggleState })]
    public async Task TheWatch_CoversTheWindow_AndThePropertiesTheConditionReads(ElementWaitCondition condition, UiaWatchedProperty[] properties)
    {
        _desktop.BuildWindow(Desktop.Button("Cancel"));
        var expected = condition switch
        {
            ElementWaitCondition.ToggleState => "on",
            ElementWaitCondition.NameEquals or ElementWaitCondition.ValueEquals => "x",
            _ => null
        };

        // A condition the first check does not see, so the wait sleeps: gone of the button there, the others of one that is not
        var name = condition == ElementWaitCondition.Gone ? "Cancel" : "Print";

        await _desktop.CreateSut(_events).WaitAsync(Request(condition, name, timeoutMs: 300, expected: expected));

        _events.Watches.Should().ContainSingle();
        _events.Watches[0].Window.Should().Be(Handle);
        _events.Watches[0].TargetProcessId.Should().Be(ProcessId);
        _events.Watches[0].Properties.Should().Equal(properties);
    }

    [Fact]
    public async Task AWaitTheFirstCheckAnswers_WatchesNothing()
    {
        _desktop.BuildWindow(Desktop.Button("OK"));

        var result = await _desktop.CreateSut(_events).WaitAsync(Request());

        result.Satisfied.Should().BeTrue();
        _events.Watches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("refuses")]
    public async Task AWatchThatCannotStart_LeavesTheChecksAsTheyWere(string how)
    {
        // Arrange
        _events.WatchFails = how == "throws" ? new InvalidOperationException("no events") : null;
        _events.Refuses = how == "refuses";
        var body = _desktop.BuildWindow();
        _desktop.At(110, () => body.With(Desktop.Button("OK")));

        // Act
        var result = await _desktop.CreateSut(_events).WaitAsync(Request());

        // Assert
        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(250);
        _desktop.Delays.Select(d => (int)d.TotalMilliseconds).Should().Equal(100, 150);
    }

    [Fact]
    public async Task TheWatch_EndsWithTheWait_SatisfiedTimedOutOrEnded()
    {
        var body = _desktop.BuildWindow();
        _desktop.At(150, () => body.With(Desktop.Button("OK")));
        var satisfied = await _desktop.CreateSut(_events).WaitAsync(Request());
        var timedOut = await _desktop.CreateSut(_events).WaitAsync(Request(name: "Print", timeoutMs: 500));
        _desktop.At(_desktop.NowMs + 120, () => _desktop.Windows.Setup(w => w.IsWindow(Handle)).Returns(false));
        _desktop.At(_desktop.NowMs + 120, () => _desktop.Tree.FromHandleException = FakeUiaTree.Vanished("window"));
        var ended = await _desktop.CreateSut(_events).WaitAsync(Request(ElementWaitCondition.Enabled, name: "Print"));

        satisfied.Satisfied.Should().BeTrue();
        timedOut.TimedOut.Should().BeTrue();
        ended.ErrorCode.Should().Be("WindowNotFound");
        _events.Watches.Should().HaveCount(3);
        _events.Active.Should().Be(0);
    }

    [Fact]
    public async Task TheWatch_EndsWithACancelledWait()
    {
        _desktop.BuildWindow();
        using var cancel = new CancellationTokenSource();
        _desktop.At(300, cancel.Cancel);

        var wait = () => _desktop.CreateSut(_events).WaitAsync(Request(), cancellationToken: cancel.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
        _events.Watches.Should().ContainSingle();
        _events.Active.Should().Be(0);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("gone held")]
    [InlineData("enabled timeout")]
    [InlineData("toggle by ref")]
    public async Task EventsThatNeverCome_EndTheWaitAsWithoutAnEventSource(string scenario)
    {
        // Act - the same tree and changes, once with an event source that raises nothing and once without one
        var withEvents = await new Desktop().RunScenario(scenario, new FakeElementEventSource());
        var withoutEvents = await new Desktop().RunScenario(scenario, null);

        // Assert
        withEvents.Should().BeEquivalentTo(withoutEvents);
    }

    /// <summary>One window in a fake tree, the real search and registry over it, and changes at given times.</summary>
    private sealed class Desktop
    {
        private readonly InlineUiaWorker _worker = new();
        private readonly Mock<IWindowWaitService> _windowWait = new();
        private readonly Mock<IAccessibilityWarmup> _warmup = new();
        private readonly ElementRegistry _registry;
        private readonly ElementSearchService _search;
        private readonly ManualClock _clock = new();
        private readonly long _start;
        private readonly List<(long AtMs, Action Change)> _changes = [];

        public Desktop()
        {
            _start = _clock.GetTimestamp();
            Windows.Setup(w => w.IsWindow(Handle)).Returns(true);
            Windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns((uint)ProcessId);
            Windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
            _registry = new ElementRegistry(Tree, _worker, Windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
            _search = new ElementSearchService(Tree, _worker, _registry, Windows.Object, _warmup.Object);
            _windowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WindowConditionResult
                {
                    Satisfied = true, MatchCount = 1, Window = TestDataFactory.CreateWindowInfo((int)Handle, "Editor", "editor", ProcessId)
                });
        }

        public FakeUiaTree Tree { get; } = new();

        public Mock<INativeWindowWrapper> Windows { get; } = new();

        /// <summary>The delays the wait asked for, in order.</summary>
        public List<TimeSpan> Delays { get; } = [];

        public long NowMs => (long)_clock.GetElapsedTime(_start).TotalMilliseconds;

        public void At(long atMs, Action change) => _changes.Add((atMs, change));

        public ElementWaitService CreateSut(
            IElementEventSource? events, TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
            new(_search, _registry, _windowWait.Object, Tree, _worker, Windows.Object, clock ?? _clock, delay ?? DelayAsync, events: events);

        /// <summary>Window "Editor" with a Pane "Body"; returns the pane.</summary>
        public FakeUiaNode BuildWindow(params FakeUiaNode[] children)
        {
            var body = new FakeUiaNode("Pane", "Body").With(children);
            Tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(body));
            return body;
        }

        public static FakeUiaNode Button(string name, bool enabled = true) =>
            new("Button", name) { AutomationId = name + "Btn", Bounds = new UiaRect(100, 40, 80, 24), ActionPatterns = ["Invoke"], IsEnabled = enabled };

        /// <summary>One wait, with the changes of <paramref name="scenario"/>.</summary>
        public async Task<WaitForElementResult> RunScenario(string scenario, IElementEventSource? events)
        {
            var sut = CreateSut(events);
            switch (scenario)
            {
                case "exists":
                {
                    var body = BuildWindow();
                    At(400, () => body.With(Button("OK")));
                    return await sut.WaitAsync(Request());
                }
                case "gone held":
                {
                    var ok = Button("OK");
                    BuildWindow(ok);
                    At(300, ok.Vanish);
                    return await sut.WaitAsync(Request(ElementWaitCondition.Gone, stableMs: 400));
                }
                case "enabled timeout":
                    BuildWindow(Button("OK", enabled: false));
                    return await sut.WaitAsync(Request(ElementWaitCondition.Enabled, timeoutMs: 1500));
                default:
                {
                    var toggle = new FakeTogglePattern(UiaToggleState.Off);
                    var box = new FakeUiaNode("CheckBox", "Wrap") { AutomationId = "WrapBox", ActionPatterns = ["Toggle"] }.WithPattern<IUiaTogglePattern>(toggle);
                    BuildWindow(box);
                    var found = await _search.FindAsync(new ElementQuery(Handle, AutomationId: "WrapBox", VisibleOnly: false));
                    At(700, () => toggle.ToggleState = UiaToggleState.On);
                    return await sut.WaitAsync(new ElementWaitRequest
                    {
                        Ref = found.Elements!.Single().Ref, Condition = ElementWaitCondition.ToggleState, Expected = "on", TimeoutMs = 3000
                    });
                }
            }
        }

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
    }
}
