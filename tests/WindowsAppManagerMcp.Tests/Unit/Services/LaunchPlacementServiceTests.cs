namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="LaunchPlacementService"/> (roadmap M3.13): a launch's window is placed through the window
/// service, checked again once after the recheck delay and placed a second time when the app moved it back, then
/// focused through the foreground service. Every window here lives in a dictionary behind mocks; nothing reaches the
/// desktop.
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
    private nint _inFront;

    /// <summary>What the app does while the service waits before its recheck (an app that restores its saved bounds).</summary>
    private Action? _duringDelay;

    public LaunchPlacementServiceTests()
    {
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

    private LaunchPlacementService CreateSut() => new(_windows.Object, _foreground.Object, _session.Object, delay: (delay, _) =>
    {
        _delays.Add(delay);
        _calls.Add("delay");
        _duringDelay?.Invoke();
        return Task.CompletedTask;
    });

    private static LaunchPlacementPlan Plan(LaunchPlacement placement)
    {
        LaunchPlacementPlan.TryCreate(placement, out var plan, out _, out var error).Should().BeTrue(error);
        return plan!;
    }

    private async Task<LaunchPlacementOutcome> PlaceOneAsync(nint handle, LaunchPlacement placement) =>
        (await CreateSut().PlaceAsync([(handle, Plan(placement))])).Single();

    [Fact]
    public async Task Snap_PlacesTheWindow_WaitsOnce_AndReportsWhereItIs()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", MonitorIndex: 1));

        outcome.Success.Should().BeTrue();
        outcome.Reapplied.Should().BeFalse("the app left the window where it was put");
        outcome.Window!.VisibleBounds.Should().Be(LeftHalf);
        outcome.Focused.Should().BeFalse();
        _calls.Should().Equal("snap LeftHalf 1", "delay");
        _delays.Should().Equal(LaunchPlacementService.DefaultRecheckDelay);
        LaunchPlacementService.DefaultRecheckDelay.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task AnAppThatRestoresItsSavedBounds_IsPlacedOnceMore_AndOnlyOnce()
    {
        // The app puts its window back where it saved it, each time it is moved (VS Code, Chrome)
        _duringDelay = () => Place(Code, SavedBounds);

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half"));

        outcome.Success.Should().BeTrue();
        outcome.Reapplied.Should().BeTrue();
        outcome.Window!.VisibleBounds.Should().Be(LeftHalf);
        _calls.Should().Equal("snap LeftHalf ", "delay", "snap LeftHalf ");
    }

    [Fact]
    public async Task AnAppThatMaximizesItself_AfterThePlacement_IsPlacedOnceMore()
    {
        _duringDelay = () => _desktop[Code] = _desktop[Code] with { State = WindowState.Maximized };

        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Bounds: new BoundsDto(10, 20, 800, 600)));

        outcome.Reapplied.Should().BeTrue();
        outcome.Window!.State.Should().Be(WindowState.Normal);
        _calls.Should().Equal("frame 10,20,800,600", "delay", "frame 10,20,800,600");
    }

    [Fact]
    public async Task Focus_ComesAfterThePlacementAndTheRecheck_AndIsReported()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", Focus: true));

        outcome.Success.Should().BeTrue();
        outcome.Focused.Should().BeTrue();
        _calls.Should().Equal("snap LeftHalf ", "delay", $"focus {Code}");
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
    public async Task APauseDuringTheRecheck_StopsTheSecondPlacement()
    {
        _duringDelay = () =>
        {
            Place(Code, SavedBounds);
            _session.SetupGet(s => s.IsPaused).Returns(true);
        };

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
        _calls.Should().Equal("state Maximize", "delay");
    }

    [Fact]
    public async Task Minimized_PlacesTheWindowFirst_ThenMinimizesIt()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(Snap: "left_half", State: "minimized"));

        outcome.Window!.State.Should().Be(WindowState.Minimized);
        _calls.Should().Equal("snap LeftHalf ", "state Minimize", "delay");
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
        _calls.Should().Equal("state Restore", "state Restore", "delay");
    }

    [Fact]
    public async Task NormalAlone_LeavesANormalWindowAlone()
    {
        var outcome = await PlaceOneAsync(Code, new LaunchPlacement(State: "normal"));

        outcome.Success.Should().BeTrue();
        _calls.Should().Equal("delay");
    }

    [Fact]
    public async Task SeveralWindows_ArePlacedInOrder_CheckedTogetherOnce_AndTheLastFocusEndsInFront()
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
        _calls.Should().Equal("snap LeftHalf ", "frame 960,0,960,1040", "delay", $"focus {Terminal}");
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
        _calls.Should().Equal("snap LeftHalf ", "frame 10,20,800,600", "delay");
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
