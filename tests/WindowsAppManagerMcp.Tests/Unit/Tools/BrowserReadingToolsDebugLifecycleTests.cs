using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The tool layer of the debug browser's lifecycle (roadmap M5.4): launch_browser_with_debug's profile, reuseIfRunning
/// and placement, checked before anything is launched, and close_debug_browser's arguments, refusals, confirm fallback and
/// gate. The lifecycle, placement and monitors are mocks; the elicitation path is covered over MCP by
/// UserConfirmationContractTests.
/// </summary>
public class BrowserReadingToolsDebugLifecycleTests
{
    private static readonly WindowInfo BrowserWindow =
        TestDataFactory.CreateWindowInfo(handleValue: 0x5678, title: "New Tab - Google Chrome", processName: "chrome", processId: 4321);

    private static readonly DebugBrowserCloseTarget Target = new(
        4321, "chrome", new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc), @"C:\Temp\WindowsAppManagerMcp\browser-debug\0123456789abcdef0123456789abcdef",
        DebugProfileKind.Temporary, StartedByServer: true, DeletesProfile: true, Helpers: []);

    private readonly Mock<IBrowserReadingService> _reads = new();
    private readonly Mock<IProcessService> _processes = new();
    private readonly Mock<IDebugBrowserLifecycle> _lifecycle = new();
    private readonly Mock<ILaunchPlacementService> _placer = new();
    private readonly Mock<IMonitorService> _monitors = new();
    private readonly List<DebugBrowserLaunchRequest> _launches = [];

    public BrowserReadingToolsDebugLifecycleTests()
    {
        _monitors.Setup(m => m.GetAllMonitors()).Returns([
            new MonitorInfo(0, "\\\\.\\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0)
        ]);
        _lifecycle.Setup(l => l.LaunchAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DebugBrowserLaunchRequest request, CancellationToken _) => _launches.Add(request))
            .ReturnsAsync(new DebugBrowserLaunchResult(true, 4321, BrowserWindow.Handle, 50123, @"C:\Profiles\work", null, null,
                LaunchStatus: LaunchStatus.WindowObserved, CdpReady: true));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((placements, _) =>
                Task.FromResult<IReadOnlyList<LaunchPlacementOutcome>>(placements
                    .Select(p => new LaunchPlacementOutcome(p.Handle, BrowserWindow with { VisibleBounds = new WindowRect(0, 0, 960, 1040) },
                        Focused: false, Reapplied: false))
                    .ToList()));
        _lifecycle.Setup(l => l.CheckClose(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .Returns(new DebugBrowserCloseCheck(Target, null));
        _lifecycle.Setup(l => l.CloseAsync(It.IsAny<DebugBrowserCloseTarget>(), It.IsAny<TimeSpan>(),
                It.IsAny<Func<CancellationToken, Task<IDisposable>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserCloseResult(true, 4321, UserDataDir: Target.UserDataDir, ProfileKind: "temporary",
                ClosedGracefully: true, Forced: false, Exited: true, ProfileDeleted: true));
    }

    private BrowserReadingTools CreateSut(IInputSession? session = null) => new(
        _reads.Object, _processes.Object, debugBrowsers: _lifecycle.Object, placementService: _placer.Object,
        monitorService: _monitors.Object, session: session);

    private void VerifyNothingLaunched() =>
        _lifecycle.Verify(l => l.LaunchAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Never);

    #region launch_browser_with_debug

    [Fact]
    public async Task Launch_PassesTheProfileAndReuseIfRunning_ToTheLifecycle()
    {
        // Act
        var result = await CreateSut().LaunchBrowserWithDebug(profile: "work", reuseIfRunning: true);

        // Assert
        result.Success.Should().BeTrue();
        var request = _launches.Should().ContainSingle().Subject;
        request.ProfileName.Should().Be("work");
        request.ReuseIfRunning.Should().BeTrue();
        request.UserDataDir.Should().BeNull();
        result.Placement.Should().BeNull();
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Launch_AnAlreadyRunningBrowser_IsReportedAsAlreadyRunning()
    {
        _lifecycle.Setup(l => l.LaunchAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(true, 4321, BrowserWindow.Handle, 50123, @"C:\Profiles\work", null, null,
                LaunchStatus: LaunchStatus.AlreadyRunning, CdpReady: true));

        var result = await CreateSut().LaunchBrowserWithDebug(profile: "work", reuseIfRunning: true);

        result.LaunchStatus.Should().Be("alreadyRunning");
    }

    [Theory]
    [InlineData("work", @"C:\Profiles\x", false, "not both")]
    [InlineData(@"..\x", null, false, "profile must be")]
    [InlineData("CON", null, false, "device name")]
    [InlineData(null, null, true, "reuseIfRunning needs profile or userDataDir")]
    public async Task Launch_BadProfileArguments_AreInvalidArguments_AndNothingIsLaunched(
        string? profile, string? userDataDir, bool reuseIfRunning, string message)
    {
        // Act
        var act = () => CreateSut().LaunchBrowserWithDebug(userDataDir: userDataDir, profile: profile, reuseIfRunning: reuseIfRunning);

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>())
            .Which.Should().Match<ToolErrorException>(e => e.ErrorCode == ToolErrorReporting.InvalidArgumentsErrorCode && e.Message.Contains(message));
        VerifyNothingLaunched();
    }

    [Fact]
    public async Task Launch_APlacement_IsAppliedToTheBrowsersWindow()
    {
        // Act
        var result = await CreateSut().LaunchBrowserWithDebug(placement: new LaunchPlacement(Snap: "left_half"));

        // Assert
        _placer.Verify(p => p.PlaceAsync(
            It.Is<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>>(l => l.Single().Handle == BrowserWindow.Handle &&
                                                                                 l.Single().Plan.Snap == SnapPosition.LeftHalf),
            It.IsAny<CancellationToken>()), Times.Once);
        result.Placement.Should().Be(new LaunchPlacementResultDto(new BoundsDto(0, 0, 960, 1040), "normal", 0, Focused: false, Reapplied: false));
    }

    [Fact]
    public async Task Launch_CancelledDuringThePlacement_EndsTheBrowserItStarted()
    {
        // Arrange - the cancelled call sends no result, so nobody learns the browser's PID or its unauthenticated port
        using var cancel = new CancellationTokenSource();
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((_, token) =>
            {
                cancel.Cancel();
                return Task.FromCanceled<IReadOnlyList<LaunchPlacementOutcome>>(token);
            });

        // Act
        var act = () => CreateSut().LaunchBrowserWithDebug(placement: new LaunchPlacement(Snap: "left_half"), cancellationToken: cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _lifecycle.Verify(l => l.AbandonAsync(It.Is<DebugBrowserLaunchResult>(r => r.ProcessId == 4321)), Times.Once);
    }

    [Fact]
    public async Task Launch_OfABrowserAlreadyRunning_CancelledDuringThePlacement_EndsNothing()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        _lifecycle.Setup(l => l.LaunchAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(true, 4321, BrowserWindow.Handle, 50123, @"C:\Profiles\work", null, null,
                LaunchStatus: LaunchStatus.AlreadyRunning, CdpReady: true));
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((_, token) =>
            {
                cancel.Cancel();
                return Task.FromCanceled<IReadOnlyList<LaunchPlacementOutcome>>(token);
            });

        // Act
        var act = () => CreateSut().LaunchBrowserWithDebug(
            profile: "work", reuseIfRunning: true, placement: new LaunchPlacement(Snap: "left_half"), cancellationToken: cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _lifecycle.Verify(l => l.AbandonAsync(It.IsAny<DebugBrowserLaunchResult>()), Times.Never);
    }

    [Theory]
    [InlineData("middle", null, "InvalidSnapPosition")]
    [InlineData(null, 5, "InvalidMonitorIndex")]
    public async Task Launch_ABadPlacement_IsRefused_BeforeAnythingIsLaunched(string? snap, int? monitorIndex, string errorCode)
    {
        var act = () => CreateSut().LaunchBrowserWithDebug(placement: new LaunchPlacement(Snap: snap, Monitor: monitorIndex is { } index ? MonitorSelector.OfIndex(index) : null));

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(errorCode);
        VerifyNothingLaunched();
    }

    [Fact]
    public async Task Launch_AFailedLaunch_IsNotPlaced()
    {
        _lifecycle.Setup(l => l.LaunchAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(false, null, null, null, null, "Chrome is not installed", nameof(BrowserErrorCode.BrowserNotFound)));

        var result = await CreateSut().LaunchBrowserWithDebug(placement: new LaunchPlacement(Snap: "left_half"));

        result.Placement.Should().BeNull();
        _placer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Launch_WithoutALifecycle_GoesStraightToTheProcessService()
    {
        // Arrange
        _processes.Setup(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserLaunchResult(true, 4321, null, 50123, @"C:\Temp\p", null, null));
        var sut = new BrowserReadingTools(_reads.Object, _processes.Object);

        // Act
        var result = await sut.LaunchBrowserWithDebug();

        // Assert
        result.Success.Should().BeTrue();
        _processes.Verify(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region close_debug_browser

    [Theory]
    [InlineData(null, null, 5000, "Pass processId or cdpPort")]
    [InlineData(0, null, 5000, "processId must be")]
    [InlineData(null, 70000, 5000, "cdpPort must be")]
    [InlineData(4321, null, 999, "timeoutMs must be")]
    [InlineData(4321, null, 30001, "timeoutMs must be")]
    public async Task Close_BadArguments_AreInvalidArguments_AndNothingIsChecked(int? processId, int? cdpPort, int timeoutMs, string message)
    {
        var act = () => CreateSut().CloseDebugBrowser(processId, cdpPort, confirm: true, timeoutMs: timeoutMs);

        (await act.Should().ThrowAsync<ToolErrorException>())
            .Which.Should().Match<ToolErrorException>(e => e.ErrorCode == ToolErrorReporting.InvalidArgumentsErrorCode && e.Message.Contains(message));
        _lifecycle.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Close_ARefusedBrowser_IsReported_AndNothingIsClosed()
    {
        // Arrange
        _lifecycle.Setup(l => l.CheckClose(777, null, false)).Returns(DebugBrowserCloseCheck.Refused(
            new DebugBrowserCloseResult(false, 777, "it may be the user's own browser", nameof(BrowserErrorCode.NotADebugBrowser))));

        // Act
        var result = await CreateSut().CloseDebugBrowser(777, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("NotADebugBrowser");
        _lifecycle.Verify(l => l.CloseAsync(It.IsAny<DebugBrowserCloseTarget>(), It.IsAny<TimeSpan>(),
            It.IsAny<Func<CancellationToken, Task<IDisposable>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_WithoutConfirm_WhenTheUserCannotBeAsked_IsConfirmationRequired()
    {
        var result = await CreateSut().CloseDebugBrowser(4321);

        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ConfirmationRequired));
        result.ProfileKind.Should().Be("temporary");
        _lifecycle.Verify(l => l.CloseAsync(It.IsAny<DebugBrowserCloseTarget>(), It.IsAny<TimeSpan>(),
            It.IsAny<Func<CancellationToken, Task<IDisposable>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_WithConfirm_ClosesTheCheckedBrowser_WithTheWaitAndAGateThatHoldsTheInputGate()
    {
        // Arrange
        var flow = InputGate.TagFlow();
        bool? gateHeld = null;
        TimeSpan? wait = null;
        _lifecycle.Setup(l => l.CloseAsync(Target, It.IsAny<TimeSpan>(), It.IsAny<Func<CancellationToken, Task<IDisposable>>>(), It.IsAny<CancellationToken>()))
            .Returns<DebugBrowserCloseTarget, TimeSpan, Func<CancellationToken, Task<IDisposable>>, CancellationToken>(async (_, timeout, enterGate, token) =>
            {
                wait = timeout;
                using (await enterGate(token))
                {
                    gateHeld = InputGate.IsHeldBy(flow);
                }

                return new DebugBrowserCloseResult(true, 4321, ProfileKind: "temporary", Exited: true, ProfileDeleted: true);
            });

        // Act
        var result = await CreateSut().CloseDebugBrowser(cdpPort: 50123, confirm: true, deleteProfile: true, timeoutMs: 2000);

        // Assert
        result.Success.Should().BeTrue();
        result.ProfileDeleted.Should().BeTrue();
        wait.Should().Be(TimeSpan.FromSeconds(2));
        gateHeld.Should().BeTrue();
        _lifecycle.Verify(l => l.CheckClose(null, 50123, true), Times.Once);
    }

    [Fact]
    public async Task Close_WhileTheUserPausedInput_IsPausedByUser_AndNothingIsChecked()
    {
        // Arrange
        var session = new InputSession(new FakeInputWrapper());
        session.Pause("test");

        // Act
        var act = () => CreateSut(session).CloseDebugBrowser(4321, confirm: true);

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be("PausedByUser");
        _lifecycle.VerifyNoOtherCalls();
    }

    #endregion
}
