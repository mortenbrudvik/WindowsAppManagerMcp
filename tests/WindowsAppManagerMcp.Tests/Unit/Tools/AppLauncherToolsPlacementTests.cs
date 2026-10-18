using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// launch_application's and launch_applications_batch's placement and reuseExisting (roadmap M3.13): the placement is
/// checked before anything is launched, implies the window wait, goes to the placement service once the window is
/// found, and a placement that fails never hides the launch that succeeded. Mocked services only.
/// </summary>
public class AppLauncherToolsPlacementTests
{
    private static readonly WindowInfo CodeWindow =
        TestDataFactory.CreateWindowInfo(handleValue: 0x5000, title: "Visual Studio Code", processName: "Code", processId: 600);

    private static readonly WindowInfo TerminalWindow =
        TestDataFactory.CreateWindowInfo(handleValue: 0x6000, title: "Terminal", processName: "WindowsTerminal", processId: 700);

    private readonly Mock<IProcessService> _processes = new();
    private readonly Mock<ILaunchPlacementService> _placer = new();
    private readonly Mock<IMonitorService> _monitors = new();
    private readonly List<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>> _placed = [];

    public AppLauncherToolsPlacementTests()
    {
        _monitors.Setup(m => m.GetAllMonitors()).Returns([
            new MonitorInfo(0, "\\\\.\\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0),
            new MonitorInfo(1, "\\\\.\\DISPLAY2", false, new MonitorRect(1920, 0, 1920, 1080), new MonitorRect(1920, 0, 1920, 1040), 1.0)
        ]);
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((placements, _) =>
            {
                _placed.Add(placements);
                return Task.FromResult<IReadOnlyList<LaunchPlacementOutcome>>(placements
                    .Select(p => new LaunchPlacementOutcome(p.Handle,
                        CodeWindow with { Handle = p.Handle, VisibleBounds = new WindowRect(0, 0, 960, 1040) },
                        Focused: p.Plan.Focus, Reapplied: true))
                    .ToList());
            });
    }

    private AppLauncherTools CreateSut() =>
        new(_processes.Object, placementService: _placer.Object, monitorService: _monitors.Object);

    private void LaunchReturns(LaunchResult result) =>
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void BatchReturns(params LaunchResult[] results) =>
        _processes.Setup(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(results);

    private void VerifyNothingLaunched()
    {
        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _processes.Verify(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task APlacement_WaitsForTheWindow_PlacesIt_AndReportsWhereItEndedUp()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));

        var result = await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half", Focus: true));

        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), true, 5000, It.IsAny<CancellationToken>()), Times.Once,
            "a placement needs the window, so it implies waitForWindow");
        LaunchPlacementPlan.TryCreate(new LaunchPlacement(Snap: "left_half", Focus: true), out var expected, out _, out _).Should().BeTrue();
        _placed.Single().Single().Should().Be((CodeWindow.Handle, expected!));
        result.Success.Should().BeTrue();
        result.Placement.Should().Be(new LaunchPlacementResultDto(new BoundsDto(0, 0, 960, 1040), "normal", 0, Focused: true, Reapplied: true));
    }

    [Fact]
    public async Task WithoutAPlacement_NothingIsPlaced_AndTheResultHasNoPlacement()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));

        var result = await CreateSut().LaunchApplication("code", waitForWindow: true);

        result.Placement.Should().BeNull();
        _placed.Should().BeEmpty();
    }

    [Fact]
    public async Task ReuseExisting_GoesToTheService()
    {
        LaunchReturns(new LaunchResult(true, WindowHandle: CodeWindow.Handle, Status: LaunchStatus.AlreadyOpen, Window: CodeWindow));

        var result = await CreateSut().LaunchApplication("code", reuseExisting: true);

        _processes.Verify(p => p.LaunchApplicationAsync(It.Is<LaunchRequest>(r => r.ReuseExisting), false, 5000, It.IsAny<CancellationToken>()));
        result.LaunchStatus.Should().Be("alreadyOpen");
        result.ProcessId.Should().BeNull();
    }

    [Theory]
    [InlineData("middle", null, "InvalidSnapPosition")]
    [InlineData("left_half", 5, "InvalidMonitorIndex")]
    public async Task AnInvalidPlacement_IsAToolError_AndNothingIsLaunched(string snap, int? monitorIndex, string expected)
    {
        var call = async () => await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: snap, Monitor: monitorIndex is { } index ? MonitorSelector.OfIndex(index) : null));

        var error = (await call.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(expected);
        error.Message.Should().EndWith("Nothing was launched.");
        VerifyNothingLaunched();
    }

    [Fact]
    public async Task APlacementsMonitorName_IsResolvedBeforeTheLaunch_AndPlacesOnThatMonitor()
    {
        // Arrange (roadmap M6.7)
        _monitors.Setup(m => m.ResolveMonitor(It.Is<MonitorSelector>(s => s.Name == "right")))
            .Returns(MonitorResolution.Found(1, null));
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));

        // Act
        await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half", Monitor: MonitorSelector.Of("right")));

        // Assert
        _placed.Single().Single().Plan.MonitorIndex.Should().Be(1);
    }

    [Theory]
    [InlineData("m-0a1b2c3d", "m-0a1b2c3d")]
    [InlineData(null, @"\\.\DISPLAY2")]
    public async Task APlacementsMonitorName_KeepsTheMonitorsKey_SoItIsFoundAgainWhenTheWindowIsPlaced(string? id, string key)
    {
        // Roadmap M6.7, M6.8: the window appears up to the wait later, and the displays can change meanwhile; the placement
        // service finds the monitor again by its id, or by its device name when it has none
        var right = new MonitorInfo(1, @"\\.\DISPLAY2", false, new MonitorRect(1920, 0, 1920, 1080), new MonitorRect(1920, 0, 1920, 1040), 1.0)
        {
            Id = id
        };
        _monitors.Setup(m => m.ResolveMonitor(It.Is<MonitorSelector>(s => s.Name == "right"))).Returns(MonitorResolution.Found(1, right));
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));

        await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half", Monitor: MonitorSelector.Of("right")));

        var plan = _placed.Single().Single().Plan;
        plan.MonitorIndex.Should().Be(1);
        plan.MonitorKey.Should().Be(key);
    }

    [Fact]
    public async Task APlacementsMonitorIndex_HasNoKey()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));

        await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half", Monitor: MonitorSelector.OfIndex(1)));

        _placed.Single().Single().Plan.MonitorKey.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(WindowErrorCode.MonitorNotFound))]
    [InlineData(nameof(WindowErrorCode.AmbiguousMonitor))]
    public async Task APlacementsMonitorNameThatNamesNoSingleMonitor_IsAToolError_AndNothingIsLaunched(string code)
    {
        // Arrange
        _monitors.Setup(m => m.ResolveMonitor(It.IsAny<MonitorSelector>()))
            .Returns(MonitorResolution.Failed(code, "monitor 'left' names none. Nothing was done."));

        // Act
        var call = async () => await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Monitor: MonitorSelector.Of("left")));

        // Assert
        var error = (await call.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(code);
        error.Message.Should().StartWith("placement.monitor:").And.EndWith("Nothing was launched.");
        VerifyNothingLaunched();
    }

    [Fact]
    public async Task APlacementOnMonitorMinusOne_IsInvalidMonitorIndex_AndNothingIsLaunched()
    {
        // Act - -1 is every monitor, which only the captures take
        var call = async () => await CreateSut().LaunchApplication("code",
            placement: new LaunchPlacement(Snap: "left_half", Monitor: MonitorSelector.OfIndex(-1)));

        // Assert
        (await call.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidMonitorIndex));
        VerifyNothingLaunched();
    }

    [Fact]
    public async Task ABatchItemsMonitorThatIsNoMonitor_FailsThatItemAlone()
    {
        // Arrange
        BatchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));

        // Act
        var result = await CreateSut().LaunchApplicationsBatch(
        [
            new ApplicationLaunchRequest("code", Placement: new LaunchPlacement(Monitor: MonitorSelector.OfIndex(1))),
            new ApplicationLaunchRequest("notepad", Placement: new LaunchPlacement(Monitor: MonitorSelector.OfIndex(7)))
        ]);

        // Assert
        result.Results[1].Success.Should().BeFalse();
        result.Results[1].ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidMonitorIndex));
        _placed.Single().Single().Plan.MonitorIndex.Should().Be(1);
    }

    [Fact]
    public async Task ALaunchThatFoundNoWindow_StillSucceeds_AndThePlacementSaysWindowNotFound()
    {
        LaunchReturns(new LaunchResult(true, 600, Status: LaunchStatus.TimedOut, LauncherExited: false));

        var result = await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half"));

        result.Success.Should().BeTrue("the app was launched; launching again would open a second copy");
        result.Placement!.ErrorCode.Should().Be(nameof(WindowErrorCode.WindowNotFound));
        result.Placement.Error.Should().Contain("timedOut").And.Contain("do not launch it again");
        result.Placement.FinalBounds.Should().BeNull();
        _placed.Should().BeEmpty();
    }

    [Fact]
    public async Task APlacementThatFails_KeepsTheLaunchASuccess()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LaunchPlacementOutcome(CodeWindow.Handle, CodeWindow, false, false, WindowErrorCode.AccessDenied, "elevated")]);

        var result = await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half"));

        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Placement!.ErrorCode.Should().Be("AccessDenied");
        result.Placement.Error.Should().Be("elevated");
        result.Placement.FinalBounds.Should().Be(new BoundsDto(100, 100, 800, 600));
    }

    [Fact]
    public async Task AnAppThatFoughtThePlacement_IsReportedInThePlacementsWarning()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LaunchPlacementOutcome(CodeWindow.Handle, CodeWindow, false, true, Warning: "The app moved its window again.")]);

        var result = await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half"));

        result.Success.Should().BeTrue();
        result.Placement!.ErrorCode.Should().BeNull();
        result.Placement.Reapplied.Should().BeTrue();
        result.Placement.Warning.Should().Be("The app moved its window again.");
    }

    [Fact]
    public async Task APlacementThatThrows_KeepsTheLaunchASuccess()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The monitor list changed."));

        var result = await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half"));

        result.Success.Should().BeTrue("the app was launched; an exception in the placement must not hide that");
        result.ProcessId.Should().Be(600);
        result.WindowHandle.Should().Be(CodeWindow.Handle.ToInt64());
        result.LaunchStatus.Should().Be("windowObserved");
        result.Placement!.ErrorCode.Should().Be(nameof(WindowErrorCode.MoveFailed));
        result.Placement.Error.Should().Contain("The monitor list changed.").And.Contain("do not launch it again");
    }

    [Fact]
    public async Task APlacementThatThrows_IsLoggedWithItsException_AndEachItemStillReportsIt()
    {
        // Review F5: the item's result carries only the message, so a bug's stack trace goes to the log
        BatchReturns(
            new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow),
            new LaunchResult(true, 700, TerminalWindow.Handle, Status: LaunchStatus.WindowObserved, Window: TerminalWindow));
        var bug = new NullReferenceException("Object reference not set to an instance of an object.");
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(bug);
        var logger = new ExceptionRecordingLogger();
        var sut = new AppLauncherTools(_processes.Object, placementService: _placer.Object, monitorService: _monitors.Object, logger: logger);

        var result = await sut.LaunchApplicationsBatch(
        [
            new ApplicationLaunchRequest("code", Placement: new LaunchPlacement(Snap: "left_half")),
            new ApplicationLaunchRequest("wt", Placement: new LaunchPlacement(Snap: "right_half"))
        ]);

        result.Results.Should().OnlyContain(r => r.Success && r.Placement!.ErrorCode == nameof(WindowErrorCode.MoveFailed));
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Which.Exception.Should().BeSameAs(bug);
    }

    /// <summary>A logger that keeps each entry's level and exception.</summary>
    private sealed class ExceptionRecordingLogger : Microsoft.Extensions.Logging.ILogger<AppLauncherTools>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, exception));
    }

    [Fact]
    public async Task ACancelledPlacement_IsStillCancelled()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var call = async () => await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(Snap: "left_half"));

        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AFailedLaunch_HasNoPlacement()
    {
        LaunchReturns(new LaunchResult(false, ErrorMessage: "not found", ErrorCode: nameof(LaunchErrorCode.ExecutableNotFound)));

        var result = await CreateSut().LaunchApplication("nope", placement: new LaunchPlacement(Focus: true));

        result.Success.Should().BeFalse();
        result.Placement.Should().BeNull();
        _placed.Should().BeEmpty();
    }

    [Fact]
    public async Task AMinimizedWindow_HasNoFinalBounds()
    {
        LaunchReturns(new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LaunchPlacementOutcome(CodeWindow.Handle, CodeWindow with { State = WindowState.Minimized }, false, false)]);

        var result = await CreateSut().LaunchApplication("code", placement: new LaunchPlacement(State: "minimized"));

        result.Placement!.FinalBounds.Should().BeNull();
        result.Placement.FinalState.Should().Be("minimized");
    }

    [Fact]
    public async Task Batch_PlacesTheWindowsTogether_AfterAllLaunches_AndAnInvalidItemFailsAlone()
    {
        BatchReturns(
            new LaunchResult(true, 600, CodeWindow.Handle, Status: LaunchStatus.WindowObserved, Window: CodeWindow),
            new LaunchResult(true, WindowHandle: TerminalWindow.Handle, Status: LaunchStatus.AlreadyOpen, Window: TerminalWindow),
            new LaunchResult(true, 900, Status: LaunchStatus.Started));

        var result = await CreateSut().LaunchApplicationsBatch(
        [
            new ApplicationLaunchRequest("code", Placement: new LaunchPlacement(Snap: "left_two_thirds")),
            new ApplicationLaunchRequest("wt", Placement: new LaunchPlacement(Snap: "right_third", Focus: true), ReuseExisting: true),
            new ApplicationLaunchRequest("bad", Placement: new LaunchPlacement(Snap: "left_half", Bounds: new PlacementBounds(0, 0, 10, 10))),
            new ApplicationLaunchRequest("notepad")
        ]);

        // The invalid item is not launched; the others go to the service, which waits for every window
        _processes.Verify(p => p.LaunchApplicationsBatchAsync(
            It.Is<IReadOnlyList<LaunchRequest>>(r => r.Select(x => x.Executable).SequenceEqual(new[] { "code", "wt", "notepad" }) &&
                                                     r[1].ReuseExisting && !r[0].ReuseExisting),
            true, 5000, It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _placed.Single().Select(p => p.Handle).Should().Equal(CodeWindow.Handle, TerminalWindow.Handle);

        result.Results.Select(r => r.Executable).Should().Equal("code", "wt", "bad", "notepad");
        result.Results[0].Placement!.Reapplied.Should().BeTrue();
        result.Results[1].Placement!.Focused.Should().BeTrue();
        result.Results[1].LaunchStatus.Should().Be("alreadyOpen");
        result.Results[2].Success.Should().BeFalse();
        result.Results[2].ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Results[2].Error.Should().StartWith("Item 2:").And.EndWith("Nothing was launched for it.");
        result.Results[3].Placement.Should().BeNull();
        result.Succeeded.Should().Be(3);
        result.Failed.Should().Be(1);
    }

    [Fact]
    public async Task Batch_TwoItemsAskingForFocus_IsAToolError_AndNothingIsLaunched()
    {
        var call = async () => await CreateSut().LaunchApplicationsBatch(
        [
            new ApplicationLaunchRequest("code", Placement: new LaunchPlacement(Focus: true)),
            new ApplicationLaunchRequest("wt", Placement: new LaunchPlacement(Focus: true))
        ]);

        var error = (await call.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("Items 0, 1");
        VerifyNothingLaunched();
    }

    [Fact]
    public async Task Batch_WithoutPlacements_DoesNotWaitUnlessAsked()
    {
        BatchReturns(new LaunchResult(true, 900, Status: LaunchStatus.Started));

        await CreateSut().LaunchApplicationsBatch([new ApplicationLaunchRequest("notepad")]);

        _processes.Verify(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), false, 5000,
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _placed.Should().BeEmpty();
    }
}
