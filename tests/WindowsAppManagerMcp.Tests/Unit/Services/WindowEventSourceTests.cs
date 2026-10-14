namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The server's window events for wait_for_window's wake-ups (roadmap M3.F22): one thread of its own, started by the first
/// subscription, that installs the hooks while a wait subscribes and removes them when the last one ends, calls the
/// subscribers on window events only, and stops when the source is disposed. Over <see cref="FakeWinEventWrapper"/>: no
/// real hook is installed.
/// </summary>
public class WindowEventSourceTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectNameChange = 0x800C;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventSystemForeground = 0x0003;
    private const int ObjIdClient = -4;

    private readonly FakeWinEventWrapper _native = new();
    private readonly WindowEventSource _sut;

    public WindowEventSourceTests()
    {
        _sut = new WindowEventSource(_native);
    }

    public void Dispose() => _sut.Dispose();

    private static void Until(Func<bool> condition, string because) =>
        SpinWait.SpinUntil(condition, HangGuard).Should().BeTrue(because);

    [Fact]
    public void ASourceNobodySubscribedTo_StartsNoThread_AndInstallsNoHook()
    {
        using var source = new WindowEventSource(_native);

        _native.QueueCreated.Should().BeFalse();
        _native.Installed.Should().BeEmpty();
    }

    [Fact]
    public void TheFirstSubscription_InstallsTheHooks_OnTheEventThread()
    {
        // Act
        using var subscription = _sut.Subscribe(() => { });

        // Assert - one hook per range, installed by the loop's own thread, not the subscriber's
        Until(() => _native.ActiveHooks == WindowEventSource.HookedEvents.Count, "the event thread installs the hooks");
        _native.Installed.Select(h => (h.Min, h.Max)).Should().Equal(WindowEventSource.HookedEvents);
        _native.Installed.Should().OnlyContain(h => h.Thread == _native.LoopThread);
        _native.LoopThread.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void TheHookedEvents_LeaveOutLocationChanges()
    {
        WindowEventSource.HookedEvents.Should().NotContain(range => range.Min <= EventObjectLocationChange && EventObjectLocationChange <= range.Max,
            "no condition reads a window's position, and location changes are the noisiest events");
    }

    [Fact]
    public void AWindowEvent_CallsEverySubscriber()
    {
        // Arrange
        using var first = new CountdownEvent(1);
        using var second = new CountdownEvent(1);
        using var a = _sut.Subscribe(() => first.Signal());
        using var b = _sut.Subscribe(() => second.Signal());
        Until(() => _native.ActiveHooks > 0, "the hooks are installed");

        // Act
        _native.Raise(EventObjectCreate);

        // Assert
        first.Wait(HangGuard).Should().BeTrue();
        second.Wait(HangGuard).Should().BeTrue();
    }

    [Fact]
    public void AnEventOfAnObjectInsideAWindow_CallsNoSubscriber()
    {
        // Arrange
        var calls = 0;
        using var subscription = _sut.Subscribe(() => Interlocked.Increment(ref calls));
        Until(() => _native.ActiveHooks > 0, "the hooks are installed");

        // Act - a control's name changes, a list item is created; then the foreground changes
        _native.Raise(EventObjectNameChange, objectId: ObjIdClient);
        _native.Raise(EventObjectCreate, childId: 3);
        _native.Raise(EventSystemForeground);

        // Assert - only the last one is about a window itself (the queue keeps their order)
        Until(() => Volatile.Read(ref calls) == 1, "the foreground change calls the subscriber");
        Thread.Sleep(50);
        Volatile.Read(ref calls).Should().Be(1);
    }

    [Fact]
    public void TheLastSubscriptionEnding_RemovesTheHooks_OnTheEventThread_AndTheNextOneInstallsThemAgain()
    {
        // Arrange
        var first = _sut.Subscribe(() => { });
        var second = _sut.Subscribe(() => { });
        Until(() => _native.ActiveHooks == WindowEventSource.HookedEvents.Count, "the hooks are installed");

        // Act - one of two ends: the hooks stay; the other ends: they go
        first.Dispose();
        _native.Post(WinEventLoopMessage.Other);
        Thread.Sleep(50);
        _native.ActiveHooks.Should().Be(WindowEventSource.HookedEvents.Count);
        second.Dispose();

        // Assert
        Until(() => _native.ActiveHooks == 0, "the last subscription's end removes the hooks");
        _native.Removed.Should().OnlyContain(r => r.Thread == _native.LoopThread);

        using var third = _sut.Subscribe(() => { });
        Until(() => _native.ActiveHooks == WindowEventSource.HookedEvents.Count, "a new subscription installs them again");
        _native.Installed.Should().HaveCount(2 * WindowEventSource.HookedEvents.Count);
    }

    [Fact]
    public void AnEndedSubscription_IsNotCalled()
    {
        // Arrange
        var calls = 0;
        using var marker = new CountdownEvent(1);
        var ended = _sut.Subscribe(() => Interlocked.Increment(ref calls));
        using var other = _sut.Subscribe(() => marker.Signal());
        Until(() => _native.ActiveHooks > 0, "the hooks are installed");

        // Act
        ended.Dispose();
        ended.Dispose();
        _native.Raise(EventObjectCreate);

        // Assert
        marker.Wait(HangGuard).Should().BeTrue();
        Volatile.Read(ref calls).Should().Be(0);
    }

    [Fact]
    public void Dispose_StopsTheThread_AndRemovesItsHooks()
    {
        // Arrange
        var subscription = _sut.Subscribe(() => { });
        Until(() => _native.ActiveHooks > 0, "the hooks are installed");

        // Act
        _sut.Dispose();

        // Assert - the thread removed its own hooks before it ended
        _native.StopsPosted.Should().Be(1);
        _native.ActiveHooks.Should().Be(0);
        _native.Removed.Should().OnlyContain(r => r.Thread == _native.LoopThread);
        subscription.Dispose();
        _sut.Invoking(s => s.Subscribe(() => { })).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void HooksThatCannotBeInstalled_LeaveSubscriptionsWorking_WithoutEvents()
    {
        // Arrange - every SetWinEventHook fails
        _native.OnInstall = (_, _) => false;
        var calls = 0;

        // Act
        using var subscription = _sut.Subscribe(() => Interlocked.Increment(ref calls));
        Until(() => _native.QueueCreated, "the thread started");
        _native.Raise(EventObjectCreate);
        Thread.Sleep(50);

        // Assert
        _native.ActiveHooks.Should().Be(0);
        Volatile.Read(ref calls).Should().Be(0);
    }

    [Fact]
    public void Subscribe_NeverWaitsForTheEventThread()
    {
        // Arrange - the event thread is stuck installing the first hook
        using var installing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _native.OnInstall = (_, _) =>
        {
            installing.Set();
            release.Wait(HangGuard);
            return true;
        };
        using var first = _sut.Subscribe(() => { });
        installing.Wait(HangGuard).Should().BeTrue();

        // Act - more subscriptions come and go meanwhile
        var others = Enumerable.Range(0, 3).Select(_ => _sut.Subscribe(() => { })).ToList();
        others.ForEach(o => o.Dispose());

        // Assert - nothing waited; the thread installs the hooks once it is free
        release.Set();
        Until(() => _native.ActiveHooks == WindowEventSource.HookedEvents.Count, "the hooks are installed once the thread is free");
    }

    [Fact]
    public void ASubscriberThatThrows_DoesNotKeepTheOthersFromTheEvent()
    {
        using var reached = new CountdownEvent(1);
        using var thrower = _sut.Subscribe(() => throw new InvalidOperationException("boom"));
        using var other = _sut.Subscribe(() => reached.Signal());
        Until(() => _native.ActiveHooks > 0, "the hooks are installed");

        _native.Raise(EventObjectCreate);

        reached.Wait(HangGuard).Should().BeTrue();
    }
}
