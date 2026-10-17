namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Roadmap M3.F27: apply_layout places the windows that launchMissing launched through the launch tools' placement
/// service, so they get its checks (a window of the agent's own host or one the user's policy denies is never placed), its
/// watch for an app that moves its window back, and focus last; a window that was already open is arranged as before.
/// What the service reports is turned into apply_layout's placement results, which keep their shape. The services are
/// mocks (and, in the last tests, the real placement service on a manual clock); only the preset files are real.
/// </summary>
public class LayoutServiceLaunchPlacementTests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"LayoutServiceLaunchPlacementTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<IProcessService> _processes = new();
    private readonly Mock<IForegroundService> _foreground = TestDataFactory.CreateMockForegroundService();
    private readonly Mock<ILaunchPlacementService> _placer = new();
    private readonly List<(nint Handle, LaunchPlacementPlan Plan)> _placed = [];

    // The one monitor's work area is 1920x1040 at 0,0
    private static readonly WindowInfo Notepad = Window(0x501, "Untitled - Notepad", "notepad", 900);
    private static readonly WindowInfo Chrome = Window(0x101, "Inbox - Google Chrome", "chrome", 501);

    /// <summary>What the placement service reports for the launched window; by default it did everything asked.</summary>
    private Func<nint, LaunchPlacementPlan, LaunchPlacementOutcome> _outcome = (handle, plan) => new LaunchPlacementOutcome(
        handle, Notepad, Focused: plan.Focus, Reapplied: false,
        Activation: plan.Focus ? new FocusOutcome { Target = handle, IsForeground = true, Method = FocusMethod.SetForeground } : null);

    public LayoutServiceLaunchPlacementTests()
    {
        _windows.Setup(w => w.GetWindowInfo(Notepad.Handle)).Returns(Notepad);
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, ProcessId: 900, WindowHandle: Notepad.Handle));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((placements, _) =>
            {
                _placed.AddRange(placements);
                return Task.FromResult<IReadOnlyList<LaunchPlacementOutcome>>(placements.Select(p => _outcome(p.Handle, p.Plan)).ToList());
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService CreateSut(ILaunchPlacementService? placer = null) =>
        new(_windows.Object, _monitors.Object, _processes.Object, _foreground.Object, _layoutsPath, placer ?? _placer.Object);

    private static WindowInfo Window(int handle, string title, string process, int pid) =>
        TestDataFactory.CreateWindowInfo(handle, title, process, pid, 100, 100, 800, 600);

    private static WindowPlacement Placement(string process, int order = 0, bool focus = false, string? launchCommand = "app.exe") =>
        new(new WindowMatcher(ProcessName: process), 0, new RelativePosition(0, 0, 0.5, 1.0), launchCommand, focus, order);

    private async Task<LayoutApplyResult> ApplyAsync(params WindowPlacement[] placements)
    {
        var sut = CreateSut();
        (await sut.SavePresetAsync(new LayoutPreset("launch", null, [.. placements]))).Success.Should().BeTrue();
        return await sut.ApplyPresetAsync("launch", launchMissing: true);
    }

    private void Reports(LaunchPlacementOutcome outcome) => _outcome = (_, _) => outcome;

    [Fact]
    public async Task ALaunchedWindow_IsPlacedByThePlacementService_AtThePresetsWindowRect()
    {
        var result = await ApplyAsync(Placement("notepad"));

        result.Success.Should().BeTrue();
        result.Placements.Should().Equal(new LayoutPlacementResult(0, LayoutPlacementStatus.Applied, LayoutWindowRef.From(Notepad),
            Launched: true, LaunchCommand: "app.exe"));
        result.WindowsLaunched.Should().Be(1);
        result.WindowsArranged.Should().Be(1);

        // The window rect, as apply_layout places an open window (SetWindowBounds), not the visible frame
        _placed.Should().Equal((Notepad.Handle,
            new LaunchPlacementPlan(null, null, new WindowRect(0, 0, 960, 1040), null, false) { Frame = BoundsFrame.Outer }));
        _windows.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never, "the placement service places it");
        _windows.Verify(w => w.EvaluateTargetPolicy(It.IsAny<nint>()), Times.Never, "the placement service checks it");
    }

    [Fact]
    public async Task AnOpenWindow_IsStillArrangedByTheLayout_NotByThePlacementService()
    {
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([Chrome]);
        _windows.Setup(w => w.GetWindowInfo(Chrome.Handle)).Returns(Chrome);

        var result = await ApplyAsync(Placement("chrome"));

        result.Success.Should().BeTrue();
        _placed.Should().BeEmpty();
        _windows.Verify(w => w.SetWindowBounds(Chrome.Handle, 0, 0, 960, 1040, It.IsAny<bool>()), Times.Once);
        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.Normal)]
    public async Task ThePlacementsState_GoesIntoThePlan(WindowState state)
    {
        await ApplyAsync(Placement("notepad") with { State = state });

        _placed.Single().Plan.Should().Be(state == WindowState.Maximized
            // The monitor is found again by its key when the window is placed (roadmap M6.8); this one has no id
            ? new LaunchPlacementPlan(null, 0, null, LaunchWindowState.Maximized, false) { MonitorKey = @"\\.\DISPLAY1" }
            : new LaunchPlacementPlan(null, null, new WindowRect(0, 0, 960, 1040),
                state == WindowState.Minimized ? LaunchWindowState.Minimized : null, false) { Frame = BoundsFrame.Outer });
    }

    [Fact]
    public async Task Focus_GoesIntoThePlan_AndTheActivationIsReported()
    {
        var result = await ApplyAsync(Placement("notepad", focus: true));

        _placed.Single().Plan.Focus.Should().BeTrue();
        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Focused.Should().BeTrue();
        result.Focus.Should().ContainSingle().Which.Should().Match<FocusResult>(f => f.IsForeground && f.Handle == Notepad.Handle.ToInt64());
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task AWindowThePolicyDenies_FailsWithTargetDeniedByPolicy()
    {
        Reports(new LaunchPlacementOutcome(Notepad.Handle, Notepad, false, false, WindowErrorCode.TargetDeniedByPolicy,
            "notepad is on the deny list", FailedStep: LaunchPlacementStep.Check));

        var result = await ApplyAsync(Placement("notepad"));

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(WindowErrorCode.TargetDeniedByPolicy));
        placement.Error.Should().Be("Did not arrange window 'Untitled - Notepad' for placement 0 (process 'notepad'): notepad is on the deny list");
        placement.Launched.Should().BeTrue();
        result.WindowsArranged.Should().Be(0);
    }

    [Fact]
    public async Task AWindowOfTheAgentsOwnHost_IsNotArranged()
    {
        // A launch command such as wt can open a tab in the terminal the agent runs in
        Reports(new LaunchPlacementOutcome(Notepad.Handle, Notepad, false, false, WindowErrorCode.ProtectedWindow,
            "belongs to the agent's own host", FailedStep: LaunchPlacementStep.Check));

        var result = await ApplyAsync(Placement("notepad"));

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Be("Did not arrange window 'Untitled - Notepad' for placement 0 (process 'notepad'): ProtectedWindow: " +
                                    "belongs to the agent's own host");
    }

    [Fact]
    public async Task APlacementThatFails_FailsThePlacement_AsBefore()
    {
        Reports(new LaunchPlacementOutcome(Notepad.Handle, Notepad, false, false, WindowErrorCode.AccessDenied, "elevated",
            FailedStep: LaunchPlacementStep.Place));

        var result = await ApplyAsync(Placement("notepad"));

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Be("Failed to arrange window 'Untitled - Notepad' for placement 0 (process 'notepad'): AccessDenied: elevated");
        placement.Launched.Should().BeTrue();
        placement.LaunchCommand.Should().Be("app.exe");
    }

    [Fact]
    public async Task AMinimizeThatFails_LeavesThePlacementApplied_AsBefore()
    {
        Reports(new LaunchPlacementOutcome(Notepad.Handle, Notepad, false, false, WindowErrorCode.NotResponding, "did not answer",
            FailedStep: LaunchPlacementStep.Minimize));

        var result = await ApplyAsync(Placement("notepad") with { State = WindowState.Minimized });

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Applied);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Contain("could not minimize it (NotResponding)").And.Contain("did not answer");
        result.WindowsArranged.Should().Be(1);
        result.Status.Should().Be(LayoutApplyStatus.Partial);
    }

    [Fact]
    public async Task AFocusThatFails_LeavesThePlacementApplied_WithTheActivation()
    {
        Reports(new LaunchPlacementOutcome(Notepad.Handle, Notepad, false, false, WindowErrorCode.FocusFailed, "kept back",
            FailedStep: LaunchPlacementStep.Focus,
            Activation: new FocusOutcome { Target = Notepad.Handle, ErrorCode = WindowErrorCode.FocusFailed, Error = "kept back" }));

        var result = await ApplyAsync(Placement("notepad", focus: true));

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Applied);
        placement.Focused.Should().BeFalse();
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Contain("could not bring it to the foreground (FocusFailed): kept back");
        result.Focus.Should().ContainSingle().Which.IsForeground.Should().BeFalse();
    }

    [Fact]
    public async Task AWindowTheAppMovedBack_AndThatCouldNotBePlacedAgain_IsAppliedWithTheReason()
    {
        Reports(new LaunchPlacementOutcome(Notepad.Handle, Notepad, false, true, WindowErrorCode.AccessDenied,
            "The window was placed, but the app moved it again on its own", FailedStep: LaunchPlacementStep.Settle));

        var result = await ApplyAsync(Placement("notepad"));

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Applied);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Be("Arranged window 'Untitled - Notepad' for placement 0 (process 'notepad'), but AccessDenied: The window " +
                                    "was placed, but the app moved it again on its own");
    }

    [Fact]
    public async Task AWindowThatClosedWhileItWasWatched_Fails()
    {
        Reports(new LaunchPlacementOutcome(Notepad.Handle, null, false, false, WindowErrorCode.InvalidHandle, "The launch's window closed",
            FailedStep: LaunchPlacementStep.Settle));

        var result = await ApplyAsync(Placement("notepad"));

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Contain("InvalidHandle: The launch's window closed");
        placement.Window.Should().Be(LayoutWindowRef.From(Notepad), "the window the launch found is still named");
    }

    [Fact]
    public async Task AnAppThatFoughtThePlacement_IsAppliedWithArrangeFailed_AndTheWarning()
    {
        // apply_layout has no warnings: the window is not where the layout puts it, so the placement says so
        Reports(new LaunchPlacementOutcome(Notepad.Handle, Notepad, false, true, Warning: "The app moved its window again each time."));

        var result = await ApplyAsync(Placement("notepad"));

        var placement = result.Placements.Should().ContainSingle().Subject;
        placement.Status.Should().Be(LayoutPlacementStatus.Applied);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        placement.Error.Should().Be("Arranged window 'Untitled - Notepad' for placement 0 (process 'notepad'), but its app kept moving it back: " +
                                    "The app moved its window again each time.");
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ThePlacementServiceGetsTheCallsToken()
    {
        using var call = new CancellationTokenSource();
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((_, token) =>
            {
                call.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The token was not the call's.");
            });
        var sut = CreateSut();
        (await sut.SavePresetAsync(new LayoutPreset("launch", null, [Placement("notepad")]))).Success.Should().BeTrue();

        var act = () => sut.ApplyPresetAsync("launch", launchMissing: true, cancellationToken: call.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WithTheRealPlacementService_ALaunchedHostWindow_IsNeverMoved()
    {
        _windows.Setup(w => w.GetWindowInfo(Notepad.Handle)).Returns(Notepad with { IsAgentHost = true });
        var sut = CreateSut(TestDataFactory.CreateInstantLaunchPlacement(_windows.Object, _foreground.Object));
        (await sut.SavePresetAsync(new LayoutPreset("launch", null, [Placement("notepad", focus: true)]))).Success.Should().BeTrue();

        var result = await sut.ApplyPresetAsync("launch", launchMissing: true);

        result.Placements!.Single().Error.Should().Contain("ProtectedWindow");
        _windows.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
        _foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task WithTheRealPlacementService_AnAppThatRestoresItsSavedBounds_IsPlacedAgain()
    {
        // The app moves the launched window back to where it saved it while the first placement is watched, once
        var saved = new WindowRect(100, 100, 800, 600);
        var bounds = saved;
        var placements = 0;
        var movedBack = false;
        _windows.Setup(w => w.GetWindowInfo(Notepad.Handle)).Returns(() => Notepad with { Bounds = bounds, VisibleBounds = bounds });
        _windows.Setup(w => w.SetWindowBounds(Notepad.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns<nint, int, int, int, int, bool>((h, x, y, width, height, _) =>
            {
                placements++;
                bounds = new WindowRect(x, y, width, height);
                return WindowChangeResult.Done(h);
            });
        var placer = TestDataFactory.CreateInstantLaunchPlacement(_windows.Object, _foreground.Object, duringWait: () =>
        {
            if (!movedBack)
            {
                movedBack = true;
                bounds = saved;
            }
        });
        var sut = CreateSut(placer);
        (await sut.SavePresetAsync(new LayoutPreset("launch", null, [Placement("notepad")]))).Success.Should().BeTrue();

        var result = await sut.ApplyPresetAsync("launch", launchMissing: true);

        result.Success.Should().BeTrue();
        placements.Should().Be(2, "the app undid the first placement, so the window was placed again");
        bounds.Should().Be(new WindowRect(0, 0, 960, 1040));
    }
}
