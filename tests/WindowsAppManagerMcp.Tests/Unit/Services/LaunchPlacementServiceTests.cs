namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="LaunchPlacementService"/> (roadmap M3.13, M3.F27): a launch's window is placed through the
/// window service, watched until it stays where it was put (placed again each time the app moves it back, a bounded
/// number of times, within the settle time), then focused through the foreground service. Every window here lives in a
/// dictionary behind mocks, and the waits move a manual clock; nothing reaches the desktop and nothing waits.
/// </summary>
public class LaunchPlacementServiceTests
{
    private static readonly nint Code = 0x1001;
    private static readonly nint Terminal = 0x1002;
    private static readonly WindowRect LeftHalf = new(0, 0, 960, 1040);
    private static readonly WindowRect SavedBounds = new(300, 200, 1200, 800);

    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IForegroundService> _foreground = new();
    private readonly Mock<IInputSession> _session = new();
    private readonly Dictionary<nint, WindowInfo> _desktop = [];
    private readonly List<string> _calls = [];
    private readonly List<TimeSpan> _delays = [];
    private readonly ManualClock _clock = new();
    private readonly DateTimeOffset _start;
    private nint _inFront;

    /// <summary>What the app does on its own, and when (time since the placement): each runs once, during the wait that reaches it.</summary>
    private readonly List<(TimeSpan At, Action Act)> _app = [];

    /// <summary>What the app does during every wait (an app that fights the placement).</summary>
    private Action? _duringEveryDelay;

    public LaunchPlacementServiceTests()
    {
        _start = _clock.Now;
        Add(Code, "Code", SavedBounds);
        Add(Terminal, "WindowsTerminal", new WindowRect(50, 50, 700, 500));

        _windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns<nint>(h => _desktop.ContainsKey(h));
        _windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns<nint>(h => _desktop.GetValueOrDefault(h));
        _windows.Setup(w => w.EvaluateTargetPolicy(It.IsAny<nint>())).Returns(TargetPolicyVerdict.Allow);
        _windows.Setup(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .Returns<nint, SnapPosition, int?, bool>((h, position, monitor, _) =>
            {
                _calls.Add($"snap {position} {monitor}");
                return Place(h, LeftHalf);
            });
        _windows.Setup(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns<nint, int, int, int, int, bool>((h, x, y, width, height, _) =>
            {
                _calls.Add($"frame {x},{y},{width},{height}");
                return Place(h, new WindowRect(x, y, width, height));
            });
        _windows.Setup(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()))
            .Returns<nint, int, MonitorPositioning, bool>((h, monitor, positioning, _) =>
            {
                _calls.Add($"monitor {monitor} {positioning}");
                _desktop[h] = _desktop[h] with
                {
                    MonitorIndex = monitor,
                    State = positioning == MonitorPositioning.Maximize ? WindowState.Maximized : WindowState.Normal
                };
                return WindowChangeResult.Done(h);
            });
        _windows.Setup(w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowStateRequest>()))
            .Returns<nint, WindowStateRequest>((h, state) =>
            {
                _calls.Add($"state {state}");
                _desktop[h] = _desktop[h] with
                {
                    State = state switch
                    {
                        WindowStateRequest.Minimize => WindowState.Minimized,
                        WindowStateRequest.Maximize => WindowState.Maximized,
                        _ => WindowState.Normal
                    }
                };
                return WindowChangeResult.Done(h);
            });

        _foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns<nint, int>((h, _) =>
            {
                _calls.Add($"focus {h}");
                _inFront = h;
                return new FocusOutcome { Target = h, IsForeground = true, Method = FocusMethod.SetForeground, ForegroundHandle = h };
            });
        _foreground.Setup(f => f.IsInFront(It.IsAny<nint>())).Returns<nint>(h => h == _inFront);
    }

    private void Add(nint handle, string process, WindowRect bounds) =>
        _desktop[handle] = TestDataFactory.CreateWindowInfo(handleValue: (int)handle, title: process, processName: process,
            x: bounds.X, y: bounds.Y, width: bounds.Width, height: bounds.Height) with { VisibleBounds = bounds };

    private WindowChangeResult Place(nint handle, WindowRect bounds)
    {
        _desktop[handle] = _desktop[handle] with { Bounds = bounds, VisibleBounds = bounds, State = WindowState.Normal };
        return WindowChangeResult.Done(handle);
    }

    private LaunchPlacementService CreateSut() => new(_windows.Object, _foreground.Object, _session.Object, _clock, (delay, token) =>
    {
        token.ThrowIfCancellationRequested();
        _delays.Add(delay);
        _calls.Add("delay");
        _clock.Advance(delay);
        foreach (var move in _app.Where(m => m.At <= Elapsed).ToList())
        {
            _app.Remove(move);
            move.Act();
        }

        _duringEveryDelay?.Invoke();
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    });

    /// <summary>The time the service has waited since the test began.</summary>
    private TimeSpan Elapsed => _clock.Now - _start;

    /// <summary>The app does <paramref name="act"/> on its own <paramref name="ms"/> milliseconds after the placement.</summary>
    private void At(int ms, Action act) => _app.Add((TimeSpan.FromMilliseconds(ms), act));

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>The calls other than the waits.</summary>
    private IEnumerable<string> Actions => _calls.Where(c => c != "delay");

    private static LaunchPlacementPlan Plan(LaunchPlacement placement)
    {
        LaunchPlacementPlan.TryCreate(placement, out var plan, out _, out var error).Should().BeTrue(error);
        return plan!;
    }

    private async Task<LaunchPlacementOutcome> PlaceOneAsync(nint handle, LaunchPlacement placement) =>
        (await CreateSut().PlaceAsync([(handle, Plan(placement))])).Single();

    [Fact]
    public void TheTimes_AreWhatTheDocumentationSays()
    {
        LaunchPlacementService.FirstCheckDelay.Should().Be(Ms(250));
        LaunchPlacementService.MaxCheckInterval.Should().Be(TimeSpan.FromSeconds(1));
        LaunchPlacementService.StableTime.Should().Be(TimeSpan.FromSeconds(1));
        LaunchPlacementService.SettleTime.Should().Be(TimeSpan.FromSeconds(3));
        LaunchPlacementService.MaxReapplies.Should().Be(3);
    }

    [Fact]
    public async Task Snap_PlacesTheWindow_WatchesItUntilItStaysPut_AndReportsWhereItIs()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", MonitorIndex: 1));

        outcome.Success.Should().BeTrue();
        outcome.Reapplied.Should().BeFalse("the app left the window where it was put");
        outcome.Warning.Should().BeNull();
        outcome.Window!.VisibleBounds.Should().Be(LeftHalf);
        outcome.Focused.Should().BeFalse();
        _calls.Should().Equal("snap LeftHalf 1", "delay", "delay", "delay");

        // Checks at increasing intervals, and the last one when the window has stayed put for the stable time
        _delays.Should().Equal(Ms(250), Ms(500), Ms(250));
        Elapsed.Should().Be(LaunchPlacementService.StableTime);
    }

    [Fact]
    public async Task AnAppThatRestoresItsSavedBounds_IsPlacedOnceMore_AndWatchedUntilItStaysPut()
    {
        // The app puts its window back where it saved it shortly after it shows it (VS Code, Chrome)
        At(100, () => Place(Code, SavedBounds));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half"));

        outcome.Success.Should().BeTrue();
        outcome.Reapplied.Should().BeTrue();
        outcome.Warning.Should().BeNull();
        outcome.Window!.VisibleBounds.Should().Be(LeftHalf);
        _calls.Should().Equal("snap LeftHalf ", "delay", "snap LeftHalf ", "delay", "delay", "delay");

        // Placed again at 250 ms; the checks start over from the first interval and end once it stayed put for a second
        _delays.Should().Equal(Ms(250), Ms(250), Ms(500), Ms(250));
        Elapsed.Should().Be(Ms(1250));
    }

    [Fact]
    public async Task AnAppThatMovesItsWindowLater_ThanTheFirstChecks_IsFollowed()
    {
        // Before, the one check after 500 ms missed a window the app moved after it
        At(900, () => Place(Code, SavedBounds));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half"));

        outcome.Success.Should().BeTrue();
        outcome.Reapplied.Should().BeTrue();
        outcome.Window!.VisibleBounds.Should().Be(LeftHalf);
        Actions.Should().Equal("snap LeftHalf ", "snap LeftHalf ");
        Elapsed.Should().Be(Ms(2000), "placed again at 1 s, the window then stayed put for a second");
    }

    [Fact]
    public async Task AnAppThatMovesItsWindowTwice_IsFollowedBothTimes()
    {
        At(100, () => Place(Code, SavedBounds));
        At(600, () => _desktop[Code] = _desktop[Code] with { State = WindowState.Maximized });

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half"));

        outcome.Success.Should().BeTrue();
        outcome.Reapplied.Should().BeTrue();
        outcome.Warning.Should().BeNull();
        outcome.Window!.VisibleBounds.Should().Be(LeftHalf);
        outcome.Window.State.Should().Be(WindowState.Normal);
        Actions.Should().Equal("snap LeftHalf ", "snap LeftHalf ", "snap LeftHalf ");
    }

    [Fact]
    public async Task AnAppThatFightsThePlacement_IsPlacedAgainAtMostMaxReappliesTimes_AndReportedWithAWarning()
    {
        // The app puts its window back each time it is placed
        _duringEveryDelay = () => Place(Code, SavedBounds);

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", Focus: true));

        outcome.Success.Should().BeTrue("the window was placed each time; the app moved it");
        outcome.Reapplied.Should().BeTrue();
        outcome.Window!.VisibleBounds.Should().Be(SavedBounds, "it is left where the app put it");
        outcome.Warning.Should().Contain("moved its window again").And.Contain("4 times").And.Contain("do not launch it again");
        outcome.Focused.Should().BeTrue("focus still comes last");
        _calls.Should().Equal(
            "snap LeftHalf ", "delay", "snap LeftHalf ", "delay", "snap LeftHalf ", "delay", "snap LeftHalf ", "delay", $"focus {Code}");
    }

    [Fact]
    public async Task TheWatch_EndsAtTheSettleTime_EvenWhenTheWindowHasNotStayedPutForLongEnough()
    {
        // Moved back three times, each about a second after the last placement; the third one is placed again at 2.25 s
        At(100, () => Place(Code, SavedBounds));
        At(1100, () => Place(Code, SavedBounds));
        At(2100, () => Place(Code, SavedBounds));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half"));

        outcome.Success.Should().BeTrue();
        outcome.Reapplied.Should().BeTrue();
        outcome.Warning.Should().BeNull("the app did not move it more often than it is placed again");
        Actions.Should().HaveCount(4);
        Elapsed.Should().Be(LaunchPlacementService.SettleTime, "the last wait is cut to the settle time");
        _delays.Last().Should().Be(Ms(500));
    }

    [Fact]
    public async Task AnAppThatMaximizesItself_AfterThePlacement_IsPlacedOnceMore()
    {
        At(100, () => _desktop[Code] = _desktop[Code] with { State = WindowState.Maximized });

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Bounds: new BoundsDto(10, 20, 800, 600)));

        outcome.Reapplied.Should().BeTrue();
        outcome.Window!.State.Should().Be(WindowState.Normal);
        Actions.Should().Equal("frame 10,20,800,600", "frame 10,20,800,600");
    }

    [Fact]
    public async Task AWindowThatClosesWhileItIsWatched_IsInvalidHandle()
    {
        At(600, () => _desktop.Remove(Code));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", Focus: true));

        outcome.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        outcome.Error.Should().Contain("closed");
        outcome.Window.Should().BeNull();
        Actions.Should().Equal("snap LeftHalf ");
        Elapsed.Should().Be(Ms(750), "nothing is left to watch");
    }

    [Fact]
    public async Task Cancelled_WhileTheWindowIsWatched_Throws_AndLeavesItPlaced()
    {
        using var cancel = new CancellationTokenSource();
        At(600, cancel.Cancel);

        var call = async () => await CreateSut().PlaceAsync([(Code, Plan(new LaunchPlacement(Snap: "left_half", Focus: true)))], cancel.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
        Actions.Should().Equal("snap LeftHalf ");
        _desktop[Code].VisibleBounds.Should().Be(LeftHalf);
    }

    [Fact]
    public async Task Focus_ComesAfterThePlacementAndTheWatch_AndIsReported()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", Focus: true));

        outcome.Success.Should().BeTrue();
        outcome.Focused.Should().BeTrue();
        _calls.Should().Equal("snap LeftHalf ", "delay", "delay", "delay", $"focus {Code}");
    }

    [Fact]
    public async Task FocusAlone_DoesNotWait()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Focus: true));

        outcome.Focused.Should().BeTrue();
        _calls.Should().Equal($"focus {Code}");
        _delays.Should().BeEmpty();
    }

    [Fact]
    public async Task AFocusThatFails_IsReportedWithTheForegroundServicesErrorCode()
    {
        _foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns<nint, int>((h, _) => new FocusOutcome { Target = h, ErrorCode = WindowErrorCode.Cloaked, Error = "cloaked" });

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Focus: true));

        outcome.Success.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.Cloaked);
        outcome.Error.Should().Be("cloaked");
        outcome.Focused.Should().BeFalse();
        outcome.Window.Should().NotBeNull("the window is still reported");
    }

    [Fact]
    public async Task APlacementThatFails_EndsThere_WithoutFocusOrRecheck()
    {
        _windows.Setup(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .Returns<nint, SnapPosition, int?, bool>((h, _, _, _) => WindowChangeResult.Failed(h, WindowErrorCode.AccessDenied, "elevated"));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", Focus: true));

        outcome.ErrorCode.Should().Be(WindowErrorCode.AccessDenied);
        outcome.Error.Should().Be("elevated");
        outcome.Window!.VisibleBounds.Should().Be(SavedBounds);
        _foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        _delays.Should().BeEmpty();
    }

    [Fact]
    public async Task AWindowThatDoesNotAnswer_IsNotResponding()
    {
        _windows.Setup(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Throws(new WindowNotRespondingException(Code, "The window did not answer within 5 s."));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Bounds: new BoundsDto(0, 0, 100, 100)));

        outcome.ErrorCode.Should().Be(WindowErrorCode.NotResponding);
        outcome.Error.Should().Contain("did not answer");
    }

    [Fact]
    public async Task AnUnexpectedFailureWhilePlacing_IsMoveFailed_AndTheOtherWindowsArePlaced()
    {
        _windows.Setup(w => w.SnapWindow(Code, It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("The monitor list changed."));

        var outcomes = await CreateSut().PlaceAsync([
            (Code, Plan(new LaunchPlacement(Snap: "left_half"))),
            (Terminal, Plan(new LaunchPlacement(Bounds: new BoundsDto(10, 20, 800, 600), Focus: true)))
        ]);

        outcomes[0].ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
        outcomes[0].Error.Should().Contain("The monitor list changed.");
        outcomes[1].Success.Should().BeTrue();
        outcomes[1].Focused.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnexpectedFailureWhileFocusing_IsFocusFailed()
    {
        _foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>())).Throws(new InvalidOperationException("boom"));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Focus: true));

        outcome.ErrorCode.Should().Be(WindowErrorCode.FocusFailed);
        outcome.Error.Should().Contain("boom");
    }

    [Fact]
    public async Task TheAgentsOwnHostWindow_IsNeitherMovedNorFocused()
    {
        _desktop[Terminal] = _desktop[Terminal] with { IsAgentHost = true };

        var outcome = await PlaceOneAsync(Terminal, new LaunchPlacement(Snap: "right_half", Focus: true));

        outcome.ErrorCode.Should().Be(WindowErrorCode.ProtectedWindow);
        outcome.Error.Should().Contain("agent's own host").And.Contain("snap_window");
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AWindowThePolicyDenies_IsNotTouched()
    {
        _windows.Setup(w => w.EvaluateTargetPolicy(Code)).Returns(TargetPolicyVerdict.Deny("Code", "Code is on the deny list"));

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Focus: true));

        outcome.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WhileInputIsPaused_NothingIsPlaced()
    {
        _session.SetupGet(s => s.IsPaused).Returns(true);

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half"));

        outcome.ErrorCode.Should().Be(WindowErrorCode.PausedByUser);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task APauseDuringTheWatch_StopsTheSecondPlacement()
    {
        At(100, () =>
        {
            Place(Code, SavedBounds);
            _session.SetupGet(s => s.IsPaused).Returns(true);
        });

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half"));

        outcome.ErrorCode.Should().Be(WindowErrorCode.PausedByUser);
        outcome.Reapplied.Should().BeFalse();
        _calls.Should().Equal("snap LeftHalf ", "delay");
    }

    [Fact]
    public async Task AWindowThatClosed_IsInvalidHandle()
    {
        var outcome = await PlaceOneAsync(0x9999, new LaunchPlacement(Snap: "left_half"));

        outcome.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        outcome.Window.Should().BeNull();
        _calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "maximized", "monitor 2 Maximize")]
    [InlineData(null, null, "monitor 2 Center")]
    public async Task MonitorIndexWithoutSnap_MovesTheWindowToThatMonitor(string? snap, string? state, string expected)
    {
        await PlaceOneAsync(Code, new LaunchPlacement(Snap: snap, MonitorIndex: 2, State: state));

        _calls.First().Should().Be(expected);
    }

    [Fact]
    public async Task MaximizedAlone_MaximizesTheWindow()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(State: "maximized"));

        outcome.Window!.State.Should().Be(WindowState.Maximized);
        Actions.Should().Equal("state Maximize");
    }

    [Fact]
    public async Task Minimized_PlacesTheWindowFirst_ThenMinimizesIt()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", State: "minimized"));

        outcome.Window!.State.Should().Be(WindowState.Minimized);
        Actions.Should().Equal("snap LeftHalf ", "state Minimize");
    }

    [Fact]
    public async Task NormalAlone_RestoresAWindowMinimizedFromMaximized_ToNormal()
    {
        // The first Restore returns such a window to maximized
        var restores = 0;
        _desktop[Code] = _desktop[Code] with { State = WindowState.Minimized };
        _windows.Setup(w => w.SetWindowState(Code, WindowStateRequest.Restore)).Returns(() =>
        {
            _calls.Add("state Restore");
            _desktop[Code] = _desktop[Code] with { State = ++restores == 1 ? WindowState.Maximized : WindowState.Normal };
            return WindowChangeResult.Done(Code);
        });

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(State: "normal"));

        outcome.Success.Should().BeTrue();
        outcome.Window!.State.Should().Be(WindowState.Normal);
        Actions.Should().Equal("state Restore", "state Restore");
    }

    [Fact]
    public async Task NormalAlone_LeavesANormalWindowAlone()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(State: "normal"));

        outcome.Success.Should().BeTrue();
        Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task SeveralWindows_ArePlacedInOrder_WatchedTogether_AndTheLastFocusEndsInFront()
    {
        var plans = new List<(nint, LaunchPlacementPlan)>
        {
            (Code, Plan(new LaunchPlacement(Snap: "left_half"))),
            (Terminal, Plan(new LaunchPlacement(Bounds: new BoundsDto(960, 0, 960, 1040), Focus: true)))
        };

        var outcomes = await CreateSut().PlaceAsync(plans);

        outcomes.Select(o => o.Handle).Should().Equal(Code, Terminal);
        outcomes.Should().OnlyContain(o => o.Success);
        outcomes[1].Focused.Should().BeTrue();
        outcomes[0].Focused.Should().BeFalse();
        _calls.Should().Equal("snap LeftHalf ", "frame 960,0,960,1040", "delay", "delay", "delay", $"focus {Terminal}");
    }

    [Fact]
    public async Task SeveralWindows_EachIsWatchedUntilItStaysPut_OnItsOwnClock()
    {
        At(100, () => Place(Code, SavedBounds));
        var plans = new List<(nint, LaunchPlacementPlan)>
        {
            (Code, Plan(new LaunchPlacement(Snap: "left_half"))),
            (Terminal, Plan(new LaunchPlacement(Bounds: new BoundsDto(960, 0, 960, 1040))))
        };

        var outcomes = await CreateSut().PlaceAsync(plans);

        outcomes[0].Reapplied.Should().BeTrue();
        outcomes[1].Reapplied.Should().BeFalse();
        outcomes.Should().OnlyContain(o => o.Success && o.Warning == null);
        Actions.Should().Equal("snap LeftHalf ", "frame 960,0,960,1040", "snap LeftHalf ");

        // The terminal stayed put from the start (1 s); the editor from its second placement at 250 ms (1.25 s)
        _delays.Should().Equal(Ms(250), Ms(250), Ms(500), Ms(250));
    }

    [Fact]
    public async Task AWindowTwoLaunchesShare_EndsWhereTheLaterPlacementPutIt_WithoutPingPong()
    {
        var plans = new List<(nint, LaunchPlacementPlan)>
        {
            (Code, Plan(new LaunchPlacement(Snap: "left_half"))),
            (Code, Plan(new LaunchPlacement(Bounds: new BoundsDto(10, 20, 800, 600))))
        };

        var outcomes = await CreateSut().PlaceAsync(plans);

        outcomes.Should().OnlyContain(o => o.Success && !o.Reapplied);
        outcomes.Should().OnlyContain(o => o.Window!.VisibleBounds == new WindowRect(10, 20, 800, 600));
        Actions.Should().Equal("snap LeftHalf ", "frame 10,20,800,600");
    }

    [Fact]
    public async Task OneWindowThatFails_DoesNotStopTheOthers()
    {
        var plans = new List<(nint, LaunchPlacementPlan)>
        {
            (0x9999, Plan(new LaunchPlacement(Snap: "left_half"))),
            (Code, Plan(new LaunchPlacement(Snap: "left_half")))
        };

        var outcomes = await CreateSut().PlaceAsync(plans);

        outcomes[0].ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        outcomes[1].Success.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelled_BeforeTheWindows_PlacesNothing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var call = async () => await CreateSut().PlaceAsync([(Code, Plan(new LaunchPlacement(Snap: "left_half")))], cancelled.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
        _calls.Should().BeEmpty();
    }
}
