namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Takeover detection (roadmap M7.4): the mode the user sets, which input counts as the user's, and the monitor thread that
/// installs the low-level hooks only while an operation is watched. Over <see cref="FakeLowLevelInputWrapper"/>: no real
/// hook is installed.
/// </summary>
public class UserTakeoverMonitorTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly FakeLowLevelInputWrapper _native = new();
    private readonly List<UserTakeoverMonitor> _monitors = [];

    public void Dispose()
    {
        foreach (var monitor in _monitors)
        {
            monitor.Dispose();
        }
    }

    private UserTakeoverMonitor Monitor(TakeoverMode mode)
    {
        var monitor = new UserTakeoverMonitor(_native, TakeoverOptions.For(mode));
        _monitors.Add(monitor);
        return monitor;
    }

    private static void Until(Func<bool> condition, string because) =>
        SpinWait.SpinUntil(condition, HangGuard).Should().BeTrue(because);

    // ---- The mode the user sets ----

    [Theory]
    [InlineData(null, TakeoverMode.Off, "default")]
    [InlineData("", TakeoverMode.Off, "default")]
    [InlineData("off", TakeoverMode.Off, TakeoverOptions.EnvironmentVariable)]
    [InlineData("none", TakeoverMode.Off, TakeoverOptions.EnvironmentVariable)]
    [InlineData("0", TakeoverMode.Off, TakeoverOptions.EnvironmentVariable)]
    [InlineData("warn", TakeoverMode.Warn, TakeoverOptions.EnvironmentVariable)]
    [InlineData(" WARN ", TakeoverMode.Warn, TakeoverOptions.EnvironmentVariable)]
    [InlineData("stop", TakeoverMode.Stop, TakeoverOptions.EnvironmentVariable)]
    [InlineData("on", TakeoverMode.Stop, TakeoverOptions.EnvironmentVariable)]
    [InlineData("true", TakeoverMode.Stop, TakeoverOptions.EnvironmentVariable)]
    public void Options_ReadTheMode_FromTheEnvironment(string? value, TakeoverMode mode, string source)
    {
        var options = TakeoverOptions.FromEnvironment(name => name == TakeoverOptions.EnvironmentVariable ? value : null);

        options.Mode.Should().Be(mode);
        options.Source.Should().Be(source);
        options.Warning.Should().BeNull();
    }

    [Fact]
    public void Options_AValueThatIsNoMode_IsStop_AndSaysWhy()
    {
        // The user asked for detection: a typo keeps the stricter mode, as the pause hotkey keeps its default chord
        var options = TakeoverOptions.FromEnvironment(_ => "sotp");

        options.Mode.Should().Be(TakeoverMode.Stop);
        options.Warning.Should().Contain("'sotp'").And.Contain("stop is used");
    }

    // ---- Which input counts ----

    [Theory]
    [InlineData(LowLevelInputKind.KeyDown, true)]
    [InlineData(LowLevelInputKind.ButtonDown, true)]
    [InlineData(LowLevelInputKind.Wheel, true)]
    [InlineData(LowLevelInputKind.KeyUp, false)]
    [InlineData(LowLevelInputKind.ButtonUp, false)]
    [InlineData(LowLevelInputKind.Other, false)]
    public void Filter_CountsTheUsersPresses_NotTheirReleases(LowLevelInputKind kind, bool counts)
    {
        // A release does not count: the user lets go of the Enter that sent the prompt after the agent started
        new UserInputFilter().Counts(new LowLevelInput(kind, Injected: false)).Should().Be(counts);
    }

    [Theory]
    [InlineData(LowLevelInputKind.KeyDown)]
    [InlineData(LowLevelInputKind.ButtonDown)]
    [InlineData(LowLevelInputKind.Wheel)]
    [InlineData(LowLevelInputKind.Move)]
    public void Filter_NeverCountsInjectedInput(LowLevelInputKind kind)
    {
        // The server's own SendInput, another program's, and the on-screen keyboard's
        var filter = new UserInputFilter();

        Enumerable.Range(0, 10).Select(_ => filter.Counts(new LowLevelInput(kind, Injected: true))).Should().OnlyContain(c => !c);
    }

    [Fact]
    public void Filter_CountsTheMouse_OnceItMovesInABurst()
    {
        // Arrange
        var clock = new ManualClock();
        var filter = new UserInputFilter(clock);
        var move = new LowLevelInput(LowLevelInputKind.Move, Injected: false);

        // Act - three moves are a nudge; the fourth within the burst is the user taking the mouse
        var counted = Enumerable.Range(0, UserInputFilter.MovesToCount).Select(_ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            return filter.Counts(move);
        }).ToList();

        // Assert
        counted.Should().Equal(false, false, false, true);
    }

    [Fact]
    public void Filter_MovesFarApart_DoNotAddUp()
    {
        // Arrange - a nudge now and then (the desk, a cable) never adds up to a burst
        var clock = new ManualClock();
        var filter = new UserInputFilter(clock);
        var move = new LowLevelInput(LowLevelInputKind.Move, Injected: false);

        // Act
        var counted = Enumerable.Range(0, 10).Select(_ =>
        {
            clock.Advance(UserInputFilter.MoveBurst + TimeSpan.FromMilliseconds(50));
            return filter.Counts(move) || filter.Counts(move);
        }).ToList();

        // Assert
        counted.Should().OnlyContain(c => !c);
    }

    // ---- The monitor ----

    [Fact]
    public void ModeOff_StartsNothing_AndItsWatchNeverFires()
    {
        // Arrange
        var monitor = Monitor(TakeoverMode.Off);

        // Act
        using var watch = monitor.Watch();
        _native.Raise(LowLevelInputKind.KeyDown);

        // Assert
        watch.Mode.Should().Be(TakeoverMode.Off);
        watch.Token.CanBeCanceled.Should().BeFalse();
        _native.QueueCreated.Should().BeFalse();
        _native.Installed.Should().BeEmpty();
        monitor.Status.Should().Be(new TakeoverStatus(TakeoverMode.Off, TakeoverState.Off, TakeoverOptions.EnvironmentVariable));
    }

    [Fact]
    public void AMonitorNobodyWatchedWith_StartsNoThread_AndInstallsNoHook()
    {
        var monitor = Monitor(TakeoverMode.Stop);

        _native.QueueCreated.Should().BeFalse();
        _native.Installed.Should().BeEmpty();
        monitor.Status.State.Should().Be(TakeoverState.Ready);
    }

    [Fact]
    public void TheFirstWatch_InstallsBothHooks_OnTheMonitorThread_AndTheLastOneRemovesThem()
    {
        // Arrange
        var monitor = Monitor(TakeoverMode.Stop);

        // Act
        var first = monitor.Watch();
        var second = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the monitor thread installs the keyboard and mouse hooks");

        // Assert
        _native.Installed.Select(h => h.Keyboard).Should().BeEquivalentTo([true, false]);
        _native.Installed.Should().OnlyContain(h => h.Thread == _native.LoopThread);
        _native.LoopThread.Should().NotBe(Environment.CurrentManagedThreadId);
        Until(() => monitor.Status.State == TakeoverState.Watching, "the status says the operation is watched");

        first.Dispose();
        _native.ActiveHooks.Should().Be(2, "another operation is still watched");
        second.Dispose();
        Until(() => _native.ActiveHooks == 0, "the last watch to end removes the hooks");
        Until(() => monitor.Status.State == TakeoverState.Ready, "the status says nothing is watched");
    }

    [Theory]
    [InlineData(LowLevelInputKind.KeyDown)]
    [InlineData(LowLevelInputKind.ButtonDown)]
    [InlineData(LowLevelInputKind.Wheel)]
    public void ModeStop_TheUsersInput_CancelsEveryRunningWatch(LowLevelInputKind kind)
    {
        // Arrange
        var monitor = Monitor(TakeoverMode.Stop);
        using var first = monitor.Watch();
        using var second = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the hooks are installed");

        // Act
        _native.Raise(kind);

        // Assert
        first.Token.WaitHandle.WaitOne(HangGuard).Should().BeTrue();
        second.Token.WaitHandle.WaitOne(HangGuard).Should().BeTrue();
        first.Interrupted.Should().BeTrue();
        second.Interrupted.Should().BeTrue();
        first.Warns.Should().BeFalse();
    }

    [Fact]
    public void ModeWarn_TheUsersInput_MarksTheWatch_WithoutStoppingIt()
    {
        // Arrange
        var monitor = Monitor(TakeoverMode.Warn);
        using var watch = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the hooks are installed");

        // Act
        _native.Raise(LowLevelInputKind.KeyDown);

        // Assert
        Until(() => watch.UserActed, "the user's key press marks the watch");
        watch.Warns.Should().BeTrue();
        watch.Interrupted.Should().BeFalse();
        watch.Token.CanBeCanceled.Should().BeFalse("warn never stops the operation");
    }

    [Fact]
    public void InjectedInput_AndReleases_DoNotFireTheWatch()
    {
        // Arrange
        var monitor = Monitor(TakeoverMode.Stop);
        using var watch = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the hooks are installed");

        // Act - the server's own typing and clicks, then a key the user lets go of
        _native.Raise(LowLevelInputKind.KeyDown, injected: true);
        _native.Raise(LowLevelInputKind.ButtonDown, injected: true);
        _native.Raise(LowLevelInputKind.KeyUp);

        // Assert
        watch.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)).Should().BeFalse();
        watch.UserActed.Should().BeFalse();
    }

    [Fact]
    public void AWatchThatEnded_IsNotSignalledAnyMore()
    {
        // Arrange
        var monitor = Monitor(TakeoverMode.Stop);
        var ended = monitor.Watch();
        using var running = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the hooks are installed");
        ended.Dispose();

        // Act
        _native.Raise(LowLevelInputKind.KeyDown);

        // Assert
        running.Token.WaitHandle.WaitOne(HangGuard).Should().BeTrue();
        ended.UserActed.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HooksThatCannotBeInstalled_LeaveNone_AndTheStatusSaysUnavailable(bool keyboardInstalls)
    {
        // Arrange - half a detection would stop on keys and miss the mouse, or the other way round
        _native.OnInstall = keyboard => keyboard == keyboardInstalls;
        var monitor = Monitor(TakeoverMode.Stop);

        // Act
        using var watch = monitor.Watch();

        // Assert
        Until(() => monitor.Status.State == TakeoverState.Unavailable, "the monitor reports the failed hooks");
        _native.ActiveHooks.Should().Be(0);
        monitor.Status.Error.Should().Contain("did not install");
        watch.UserActed.Should().BeFalse();
    }

    [Fact]
    public void HooksThatCannotBeInstalled_SayWhy_WithTheWindowsError()
    {
        // Arrange - the error is read right after SetWindowsHookEx, in the wrapper, and named in the status
        _native.OnInstall = keyboard => !keyboard;
        _native.InstallError = 1428; // ERROR_HOOK_NEEDS_HMOD
        var monitor = Monitor(TakeoverMode.Stop);

        // Act
        using var watch = monitor.Watch();

        // Assert
        Until(() => monitor.Status.State == TakeoverState.Unavailable, "the monitor reports the failed hooks");
        monitor.Status.Error.Should().Contain("keyboard: Windows error 1428").And.Contain("mouse: installed");
    }

    // ---- The monitor thread stopping on its own (review F1, F2) ----

    /// <summary>Ends the monitor thread's loop the way <paramref name="how"/> names, as if Windows or a bug had.</summary>
    private void EndTheLoop(string how)
    {
        switch (how)
        {
            case "exception":
                _native.ThrowInLoop(new InvalidOperationException("the loop broke"));
                break;
            case "failed":
                _native.FailLoop();
                break;
            default:
                _native.QuitLoop();
                break;
        }
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("failed")]
    [InlineData("quit")]
    public void AMonitorThreadThatStopped_SaysSo_AndTheNextWatchStartsItAgain(string how)
    {
        // Arrange - a running watch with its hooks installed
        var monitor = Monitor(TakeoverMode.Stop);
        var first = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the hooks are installed");

        // Act - the thread ends on its own, without the monitor's stop message
        EndTheLoop(how);
        Until(() => _native.ActiveHooks == 0, "the thread removes its hooks as it ends");
        Until(() => monitor.Status.State == TakeoverState.Unavailable, "the status says detection stopped");
        first.Dispose();
        using var second = monitor.Watch();

        // Assert - a new thread, which installs the hooks again and detects the user's input
        Until(() => _native.ActiveHooks == 2, "the next watch starts the thread again");
        _native.LoopThreads.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        Until(() => monitor.Status.State == TakeoverState.Watching, "the status says the operation is watched again");
        monitor.Status.Error.Should().BeNull();
        _native.Raise(LowLevelInputKind.KeyDown);
        second.Token.WaitHandle.WaitOne(HangGuard).Should().BeTrue("detection works again");
    }

    [Fact]
    public void AMonitorThreadThatKeepsStopping_IsStartedAgainOnlyMaxRestartsTimes()
    {
        // Arrange - the thread dies every time it runs
        var monitor = Monitor(TakeoverMode.Stop);
        for (var start = 1; start <= UserTakeoverMonitor.MaxRestarts + 1; start++)
        {
            using var watch = monitor.Watch();
            Until(() => _native.ActiveHooks == 2, $"start {start} installs the hooks");
            _native.ThrowInLoop(new InvalidOperationException("the loop broke"));
            Until(() => _native.ActiveHooks == 0 && monitor.Status.State == TakeoverState.Unavailable, $"start {start} stops");
        }

        // Act
        using var last = monitor.Watch();

        // Assert - no new thread: the status says detection is off until the server restarts
        _native.LoopThreads.Should().HaveCount(UserTakeoverMonitor.MaxRestarts + 1);
        monitor.Status.State.Should().Be(TakeoverState.Unavailable);
        monitor.Status.Error.Should().Contain("not started again").And.Contain("restart");
        SpinWait.SpinUntil(() => _native.ActiveHooks > 0, TimeSpan.FromMilliseconds(200)).Should().BeFalse();
    }

    /// <summary>A clock that cannot be read, which makes the user-input filter throw on a mouse move.</summary>
    private sealed class BrokenClock : TimeProvider
    {
        public override long GetTimestamp() => throw new InvalidOperationException("the clock broke");
    }

    [Fact]
    public void AHookCallbackThatFails_MarksDetectionUnavailable_LogsOnce_AndThrowsNothingIntoWindows()
    {
        // Arrange
        var logger = new RecordingLogger<UserTakeoverMonitor>();
        var monitor = new UserTakeoverMonitor(_native, TakeoverOptions.For(TakeoverMode.Stop), new BrokenClock(), logger);
        _monitors.Add(monitor);
        using var watch = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the hooks are installed");

        // Act - three mouse moves whose handling fails
        _native.Raise(LowLevelInputKind.Move);
        _native.Raise(LowLevelInputKind.Move);
        _native.Raise(LowLevelInputKind.Move);

        // Assert - the hooks stay (key presses are still seen), but the status says input may be missed
        Until(() => monitor.Status.State == TakeoverState.Unavailable, "the failure shows in the status");
        monitor.Status.Error.Should().Contain("failed").And.Contain("input event");
        _native.ActiveHooks.Should().Be(2, "the thread keeps running");
        _native.Raise(LowLevelInputKind.KeyDown);
        watch.Token.WaitHandle.WaitOne(HangGuard).Should().BeTrue("key presses are still detected");
        _native.CallbackExceptions.Should().Be(0, "no exception reaches the hook's caller, Windows");
        logger.Entries.Count(e => e.Message.Contains("callback")).Should().Be(1, "the failure is logged once, not on every event");
    }

    [Fact]
    public void Dispose_StopsTheThread_AndRemovesTheHooks_AndLaterWatchesNeverFire()
    {
        // Arrange
        var monitor = Monitor(TakeoverMode.Stop);
        using var watch = monitor.Watch();
        Until(() => _native.ActiveHooks == 2, "the hooks are installed");

        // Act
        monitor.Dispose();

        // Assert
        _native.StopsPosted.Should().Be(1);
        _native.ActiveHooks.Should().Be(0);
        using var after = monitor.Watch();
        after.Mode.Should().Be(TakeoverMode.Off);
    }
}
