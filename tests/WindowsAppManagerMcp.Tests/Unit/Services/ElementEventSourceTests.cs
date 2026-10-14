namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The server's UI Automation events for wait_for_element's wake-ups (roadmap M3.F22): each watch adds its handlers on a
/// thread of its own (an MTA thread, never the caller's), removes them there when it ends, never makes its caller wait, and
/// gives up on an application whose handler call has not returned within <see cref="ElementEventSource.CallTimeout"/>,
/// while that call is stuck. Over <see cref="FakeUiaTree"/> and a <see cref="ManualTimerClock"/>: no real handler is
/// registered, and the bound passes when the test moves the clock.
/// </summary>
public class ElementEventSourceTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly nint Window = 0x7200;
    private static readonly nint OtherWindow = 0x7300;
    private const int ProcessId = 300;
    private const int OtherProcessId = 400;

    private readonly FakeUiaTree _tree = new();
    private readonly ManualTimerClock _clock = new();
    private readonly ElementEventSource _sut;
    private readonly FakeUiaNode _window = new("Window", "Editor");

    public ElementEventSourceTests()
    {
        _tree.WithWindow(Window, _window);
        _tree.WithWindow(OtherWindow, new FakeUiaNode("Window", "Other"));
        _sut = new ElementEventSource(_tree, _clock);
    }

    private static void Until(Func<bool> condition, string because) =>
        SpinWait.SpinUntil(condition, HangGuard).Should().BeTrue(because);

    [Fact]
    public void Watch_AddsAStructureAndAPropertyHandler_OnTheWindow_OnAnMtaThreadOfItsOwn()
    {
        // Act
        using var watch = _sut.Watch(Window, ProcessId, [UiaWatchedProperty.IsEnabled], () => { });

        // Assert
        watch.Should().NotBeNull();
        Until(() => _tree.Handlers.Count == 2, "the watch's thread adds both handlers");
        _tree.Handlers.Select(h => h.Kind).Should().Equal("Structure", "Property");
        _tree.Handlers.Should().OnlyContain(h => h.Node == _window);
        _tree.Handlers[1].Properties.Should().Equal(UiaWatchedProperty.IsEnabled);
        _tree.Handlers.Should().OnlyContain(h => h.Thread != Environment.CurrentManagedThreadId && h.Apartment == ApartmentState.MTA);
    }

    [Fact]
    public void Watch_WithNoProperties_AddsOnlyTheStructureHandler()
    {
        using var watch = _sut.Watch(Window, ProcessId, [], () => { });

        Until(() => _tree.Handlers.Count == 1, "the structure handler is added");
        Thread.Sleep(50);
        _tree.Handlers.Should().ContainSingle().Which.Kind.Should().Be("Structure");
    }

    [Fact]
    public void AnEvent_CallsChanged_AndMakesNoUiAutomationCall()
    {
        // Arrange
        var calls = 0;
        using var watch = _sut.Watch(Window, ProcessId, [UiaWatchedProperty.Name], () => Interlocked.Increment(ref calls));
        Until(() => _tree.Handlers.Count == 2, "the handlers are added");
        var readsBefore = _tree.Reads.Count;

        // Act - on this thread, as on one of UI Automation's event threads
        _tree.RaiseStructureChanged();
        _tree.RaisePropertyChanged(UiaWatchedProperty.Name);

        // Assert
        calls.Should().Be(2);
        _tree.Reads.Should().HaveCount(readsBefore, "an event handler only signals");
    }

    [Fact]
    public void Dispose_RemovesTheHandlers_OnTheWatchsThread_WithoutWaitingForIt()
    {
        // Arrange
        var watch = _sut.Watch(Window, ProcessId, [UiaWatchedProperty.Name], () => { })!;
        Until(() => _tree.Handlers.Count == 2, "the handlers are added");
        var addingThread = _tree.Handlers[0].Thread;

        // Act
        watch.Dispose();

        // Assert
        Until(() => _tree.Handlers.Count == 0, "the watch's thread removes the handlers");
        _tree.RemovedHandlers.Should().HaveCount(2).And.OnlyContain(r => r.Thread == addingThread);
    }

    [Fact]
    public void AnEndedWatch_IsNotCalled_EvenBeforeItsHandlersAreRemoved()
    {
        // Arrange - removing hangs, as with a hung provider
        using var release = new ManualResetEventSlim();
        _tree.OnRemoveHandler = _ => release.Wait(HangGuard);
        var calls = 0;
        var watch = _sut.Watch(Window, ProcessId, [], () => Interlocked.Increment(ref calls))!;
        Until(() => _tree.Handlers.Count == 1, "the handler is added");

        // Act
        watch.Dispose();
        _tree.RaiseStructureChanged();
        release.Set();

        // Assert
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_NeverWaitsForAHungRemoval()
    {
        // Arrange
        using var release = new ManualResetEventSlim();
        _tree.OnRemoveHandler = _ => release.Wait(HangGuard);
        var watch = _sut.Watch(Window, ProcessId, [], () => { })!;
        Until(() => _tree.Handlers.Count == 1, "the handler is added");

        // Act - on a thread of the test, which a blocking Dispose would hold until the guard
        await Task.Run(watch.Dispose).WaitAsync(HangGuard);

        // Assert
        _tree.Handlers.Should().HaveCount(1, "the removal is still stuck in the provider");
        release.Set();
        Until(() => _tree.Handlers.Count == 0, "the removal finishes once the provider answers");
    }

    [Fact]
    public void AHungRegistration_IsAbandonedAfterItsBound_AndItsApplicationGetsNoNewWatchWhileItIsStuck()
    {
        // Arrange - the provider of the first window does not answer the first Add
        using var adding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _tree.OnAddHandler = (_, node) =>
        {
            if (node == _window && !adding.IsSet)
            {
                adding.Set();
                release.Wait(HangGuard);
            }
        };
        var watch = _sut.Watch(Window, ProcessId, [UiaWatchedProperty.Name], () => { });
        watch.Should().NotBeNull("the watch never waits for its registration");
        adding.Wait(HangGuard).Should().BeTrue();

        // Act - the bound passes
        _clock.Advance(ElementEventSource.CallTimeout - TimeSpan.FromMilliseconds(1));
        _sut.StuckCallsFor(ProcessId).Should().Be(0, "the bound has not passed yet");
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert - stuck: that application gets no new watch (its wait polls), another one does
        _sut.StuckCallsFor(ProcessId).Should().Be(1);
        _sut.Watch(Window, ProcessId, [], () => { }).Should().BeNull();
        using var other = _sut.Watch(OtherWindow, OtherProcessId, [], () => { });
        other.Should().NotBeNull();

        // The wait ends; then the provider answers: the late handlers are removed at once, and the application is no longer stuck
        watch!.Dispose();
        release.Set();
        Until(() => _sut.StuckCallsFor(ProcessId) == 0, "the stuck call returned");
        Until(() => _tree.Handlers.All(h => h.Node != _window), "the late handlers are removed");
        using var again = _sut.Watch(Window, ProcessId, [], () => { });
        again.Should().NotBeNull();
    }

    [Fact]
    public void AHungRemoval_IsCountedStuckAfterItsBound()
    {
        // Arrange
        using var removing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _tree.OnRemoveHandler = _ =>
        {
            removing.Set();
            release.Wait(HangGuard);
        };
        var watch = _sut.Watch(Window, ProcessId, [], () => { })!;
        Until(() => _tree.Handlers.Count == 1, "the handler is added");

        // Act
        watch.Dispose();
        removing.Wait(HangGuard).Should().BeTrue();
        _clock.Advance(ElementEventSource.CallTimeout);

        // Assert
        _sut.StuckCallsFor(ProcessId).Should().Be(1);
        _sut.Watch(Window, ProcessId, [], () => { }).Should().BeNull();
        release.Set();
        Until(() => _sut.StuckCallsFor(ProcessId) == 0, "the removal returned");
    }

    [Fact]
    public void TooManyStuckCalls_OverAllApplications_StopEveryNewWatch()
    {
        // Arrange - every Add hangs
        using var release = new ManualResetEventSlim();
        var started = 0;
        _tree.OnAddHandler = (_, _) =>
        {
            Interlocked.Increment(ref started);
            release.Wait(HangGuard);
        };
        var watches = new List<IDisposable?>();
        for (var target = 1; target <= ElementEventSource.MaxStuckCalls; target++)
        {
            watches.Add(_sut.Watch(Window, 1000 + target, [], () => { }));
        }

        Until(() => Volatile.Read(ref started) == ElementEventSource.MaxStuckCalls, "every watch's thread is in its Add");

        // Act
        _clock.Advance(ElementEventSource.CallTimeout);

        // Assert
        _sut.StuckCalls.Should().Be(ElementEventSource.MaxStuckCalls);
        _sut.Watch(OtherWindow, OtherProcessId, [], () => { }).Should().BeNull();
        watches.ForEach(w => w?.Dispose());
        release.Set();
        Until(() => _sut.StuckCalls == 0, "the stuck calls returned");
    }

    [Fact]
    public void ARegistrationTheProviderRefuses_GivesNoEvents_AndRemovesWhatWasAdded()
    {
        // Arrange - the structure handler is added, the property handler refused
        _tree.OnAddHandler = (kind, _) =>
        {
            if (kind == "Property")
            {
                throw new NotSupportedException("refused");
            }
        };

        // Act
        var watch = _sut.Watch(Window, ProcessId, [UiaWatchedProperty.Name], () => { })!;
        Until(() => _tree.Handlers.Count == 1, "the structure handler is added");
        watch.Dispose();

        // Assert
        Until(() => _tree.Handlers.Count == 0, "what was added is removed when the watch ends");
        _sut.StuckCalls.Should().Be(0);
    }

    [Fact]
    public void AWindowUiAutomationCannotOpen_GetsNoHandler()
    {
        _tree.FromHandleException = FakeUiaTree.Vanished("window");

        using var watch = _sut.Watch(Window, ProcessId, [UiaWatchedProperty.Name], () => { });

        watch.Should().NotBeNull();
        Thread.Sleep(50);
        _tree.Handlers.Should().BeEmpty();
    }
}
