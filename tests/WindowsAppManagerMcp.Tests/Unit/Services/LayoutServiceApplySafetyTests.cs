namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// apply_layout on presets a person edited by hand (review findings F2, F3, F4): a placement with focus and state minimized
/// is minimized and not brought to the front, whether its window was open or launched; a rectangle less than a pixel on its
/// monitor fails with InvalidPlacement and nothing is arranged; a matcher that is blank once normalized arranges no window.
/// The services are mocks; only the preset files are real.
/// </summary>
public class LayoutServiceApplySafetyTests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"LayoutServiceApplySafetyTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<IProcessService> _processes = TestDataFactory.CreateMockProcessService();
    private readonly Mock<IForegroundService> _foreground = TestDataFactory.CreateMockForegroundService();
    private readonly Mock<ILaunchPlacementService> _placer = new();
    private readonly List<(nint Handle, LaunchPlacementPlan Plan)> _placed = [];

    private static readonly WindowInfo Notepad = TestDataFactory.CreateWindowInfo(0x102, "notes.txt - Notepad", "notepad", 502);

    public LayoutServiceApplySafetyTests()
    {
        _windows.Setup(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint h, int _, int _, int _, int _, bool _) => WindowChangeResult.Done(h));
        _windows.Setup(w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowStateRequest>()))
            .Returns((nint h, WindowStateRequest _) => WindowChangeResult.Done(h));
        _windows.Setup(w => w.GetWindowInfo(Notepad.Handle)).Returns(Notepad);
        _placer.Setup(p => p.PlaceAsync(It.IsAny<IReadOnlyList<(nint, LaunchPlacementPlan)>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<(nint Handle, LaunchPlacementPlan Plan)>, CancellationToken>((placements, _) =>
            {
                _placed.AddRange(placements);
                return Task.FromResult<IReadOnlyList<LaunchPlacementOutcome>>(placements
                    .Select(p => new LaunchPlacementOutcome(p.Handle, Notepad, false, false, WindowErrorCode.None, null)).ToList());
            });
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, ProcessId: 502, WindowHandle: Notepad.Handle));
    }

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService CreateSut() => new(_windows.Object, _monitors.Object, _processes.Object, _foreground.Object, _layoutsPath, _placer.Object);

    private async Task<LayoutApplyResult> ApplyAsync(WindowPlacement placement, bool launchMissing = false)
    {
        var sut = CreateSut();
        (await sut.SavePresetAsync(new LayoutPreset("hand-edited", null, [placement]))).Success.Should().BeTrue();
        return await sut.ApplyPresetAsync("hand-edited", launchMissing: launchMissing);
    }

    private static WindowPlacement Placement(string? processName = "notepad", string? titleContains = null, double width = 0.5) =>
        new(new WindowMatcher(titleContains, processName), 0, new RelativePosition(0, 0, width, 1)) { Launch = new LayoutLaunch("notepad.exe") };

    [Fact]
    public async Task AnOpenWindowsPlacement_WithFocusAndStateMinimized_IsMinimized_AndNotBroughtToTheFront()
    {
        // Arrange
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([Notepad]);

        // Act
        var result = await ApplyAsync(Placement() with { State = WindowState.Minimized, Focus = true });

        // Assert - minimized wins, as for a launched window
        result.Success.Should().BeTrue(string.Join("; ", result.Errors ?? []));
        _windows.Verify(w => w.SetWindowState(Notepad.Handle, WindowStateRequest.Minimize), Times.Once);
        _foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        result.Focus.Should().BeNull();
        result.Placements!.Single().Focused.Should().BeNull();
    }

    [Fact]
    public async Task ALaunchedWindowsPlacement_WithFocusAndStateMinimized_IsPlannedMinimized_WithoutFocus()
    {
        // Act
        var result = await ApplyAsync(Placement() with { State = WindowState.Minimized, Focus = true }, launchMissing: true);

        // Assert
        result.WindowsLaunched.Should().Be(1);
        var plan = _placed.Single().Plan;
        plan.State.Should().Be(LaunchWindowState.Minimized);
        plan.Focus.Should().BeFalse();
        result.Placements!.Single().Focused.Should().BeNull();
    }

    [Fact]
    public async Task ARectangleLessThanAPixel_FailsWithInvalidPlacement_AndNothingIsArranged()
    {
        // Arrange
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([Notepad]);

        // Act
        var result = await ApplyAsync(Placement(width: 0.0001));

        // Assert
        result.Success.Should().BeFalse();
        var placement = result.Placements!.Single();
        placement.Status.Should().Be(LayoutPlacementStatus.Failed);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidPlacement));
        placement.Error.Should().Contain("less than one pixel");
        _windows.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ARectangleLessThanAPixel_IsNotLaunchedEither()
    {
        var result = await ApplyAsync(Placement(width: 0.0001), launchMissing: true);

        result.Placements!.Single().ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidPlacement));
        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ABlankProcessName_ArrangesNoWindow()
    {
        // Arrange - ".exe" normalizes to "", which used to match the first window of every process
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([Notepad]);

        // Act
        var result = await ApplyAsync(Placement(processName: ".exe"), launchMissing: true);

        // Assert
        var placement = result.Placements!.Single();
        placement.Status.Should().Be(LayoutPlacementStatus.NotApplicable);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoMatchCriterion));
        _windows.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>()), Times.Never);
        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABlankProcessNameBesideARealTitle_LaunchesNothing_SoNoNewWindowOfAnyProcessIsTakenForIt(bool dryRun)
    {
        // Arrange - no window has the title, so launchMissing would launch; its expected window would have been any process's
        // ('.exe' is "" once normalized, and every process name contains "")
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([Notepad]);
        var sut = CreateSut();
        (await sut.SavePresetAsync(new LayoutPreset("hand-edited", null, [Placement(processName: ".exe", titleContains: "Quarterly report")])))
            .Success.Should().BeTrue();

        // Act
        var result = await sut.ApplyPresetAsync("hand-edited", launchMissing: true, options: new LayoutApplyOptions(DryRun: dryRun));

        // Assert
        var placement = result.Placements!.Single();
        placement.Status.Should().Be(LayoutPlacementStatus.NotApplicable);
        placement.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoMatchCriterion));
        placement.Error.Should().Contain("processName '.exe'").And.Contain("every window");
        _processes.Verify(p => p.CheckLaunch(It.IsAny<LaunchRequest>()), Times.Never);
        _processes.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ALaunchedPlacementsExpectedWindow_IsItsProcess()
    {
        // Arrange - the hint the launch waits with names the placement's process, as before
        LaunchRequest? request = null;
        _processes.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<LaunchRequest, bool, int, CancellationToken>((r, _, _, _) => request = r)
            .ReturnsAsync(new LaunchResult(true, ProcessId: 502, WindowHandle: Notepad.Handle));

        // Act
        var result = await ApplyAsync(Placement(processName: "notepad.exe"), launchMissing: true);

        // Assert
        result.WindowsLaunched.Should().Be(1);
        request!.ExpectWindow.Should().Be(new LaunchWindowHint(ProcessName: "notepad.exe"));
    }
}
