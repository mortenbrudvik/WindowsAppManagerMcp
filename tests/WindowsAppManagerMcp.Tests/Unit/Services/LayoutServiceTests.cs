namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for LayoutService.
/// Tests T5.1-T5.6 from BACKLOG-TECHNICAL.md.
/// </summary>
public class LayoutServiceTests : IDisposable
{
    private readonly string _testLayoutPath;
    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<IProcessService> _mockProcessService;
    private readonly Mock<IForegroundService> _mockForegroundService;

    public LayoutServiceTests()
    {
        _testLayoutPath = Path.Combine(
            Path.GetTempPath(),
            $"LayoutServiceTests_{Guid.NewGuid()}");

        _mockWindowService = TestDataFactory.CreateMockWindowService();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _mockProcessService = TestDataFactory.CreateMockProcessService();
        _mockForegroundService = TestDataFactory.CreateMockForegroundService();
    }

    // The launched windows' placement watches them on a manual clock, so a launch test does not wait (roadmap M3.F27)
    private LayoutService CreateSut() => new LayoutService(
        _mockWindowService.Object,
        _mockMonitorService.Object,
        _mockProcessService.Object,
        _mockForegroundService.Object,
        _testLayoutPath,
        TestDataFactory.CreateInstantLaunchPlacement(_mockWindowService.Object, _mockForegroundService.Object));

    public void Dispose()
    {
        if (Directory.Exists(_testLayoutPath))
        {
            Directory.Delete(_testLayoutPath, recursive: true);
        }
    }

    #region T5.1: GetAllPresets Tests

    [Fact]
    public async Task GetAllPresets_WithNoPresets_ReturnsEmptyList()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetAllPresets();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllPresets_AfterSavingPresets_ReturnsAllSavedPresets()
    {
        // Arrange
        var sut = CreateSut();
        var preset1 = TestDataFactory.CreateLayoutPreset("preset-1");
        var preset2 = TestDataFactory.CreateLayoutPreset("preset-2");
        await sut.SavePresetAsync(preset1);
        await sut.SavePresetAsync(preset2);

        // Act
        var result = sut.GetAllPresets();

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(p => p.Name == "preset-1");
        result.Should().Contain(p => p.Name == "preset-2");
    }

    #endregion

    #region T5.2: GetPreset Tests

    [Fact]
    public async Task GetPreset_WhenNotExists_ReturnsNull()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetPreset("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPreset_AfterSave_ReturnsPreset()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("test-preset");
        await sut.SavePresetAsync(preset);

        // Act
        var result = sut.GetPreset("test-preset");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("test-preset");
    }

    [Fact]
    public async Task GetPreset_IsCaseInsensitive()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("My-Preset");
        await sut.SavePresetAsync(preset);

        // Act
        var result = sut.GetPreset("MY-PRESET");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("My-Preset");
    }

    #endregion

    #region T5.3: SavePresetAsync Tests

    [Fact]
    public async Task SavePresetAsync_CreatesJsonFile()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("save-test");

        // Act
        var result = await sut.SavePresetAsync(preset);

        // Assert
        result.Success.Should().BeTrue();
        var filePath = Path.Combine(_testLayoutPath, "save-test.json");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task SavePresetAsync_ReturnsSuccess_OnSuccess()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("success-test");

        // Act
        var result = await sut.SavePresetAsync(preset);

        // Assert
        result.Success.Should().BeTrue();
        result.Replaced.Should().BeFalse();
        result.ErrorCode.Should().BeNull();
        result.Preset.Should().Be(preset);
    }

    [Fact]
    public async Task SavePresetAsync_OverwritesExistingPreset()
    {
        // Arrange
        var sut = CreateSut();
        var preset1 = TestDataFactory.CreateLayoutPreset("overwrite-test", description: "Original");
        var preset2 = TestDataFactory.CreateLayoutPreset("overwrite-test", description: "Updated");

        // Act
        await sut.SavePresetAsync(preset1);
        await sut.SavePresetAsync(preset2);

        // Assert
        var result = sut.GetPreset("overwrite-test");
        result.Should().NotBeNull();
        result!.Description.Should().Be("Updated");
    }

    [Fact]
    public async Task SavePresetAsync_SanitizesFilename()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("test:with*invalid?chars");

        // Act
        var result = await sut.SavePresetAsync(preset);

        // Assert
        result.Success.Should().BeTrue();
        // File should exist with sanitized name
        var files = Directory.GetFiles(_testLayoutPath, "*.json");
        files.Should().HaveCount(1);
    }

    /// <summary>
    /// T5.3.6: Test SavePresetAsync respects CancellationToken.
    /// A pre-cancelled token should cause the operation to throw OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task SavePresetAsync_WithCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("cancellation-test");
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel the token

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.SavePresetAsync(preset, cts.Token));
    }

    #endregion

    #region T5.4: DeletePresetAsync Tests

    [Fact]
    public async Task DeletePresetAsync_RemovesFile()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("delete-test");
        await sut.SavePresetAsync(preset);
        var filePath = Path.Combine(_testLayoutPath, "delete-test.json");
        File.Exists(filePath).Should().BeTrue();

        // Act
        var result = await sut.DeletePresetAsync("delete-test");

        // Assert
        result.Success.Should().BeTrue();
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public async Task DeletePresetAsync_ReturnsSuccess_WhenPresetExists()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("exists-test");
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.DeletePresetAsync("exists-test");

        // Assert
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task DeletePresetAsync_ReturnsPresetNotFound_WhenPresetNotFound()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.DeletePresetAsync("nonexistent");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
        result.ErrorMessage.Should().Contain("nonexistent");
    }

    [Fact]
    public async Task DeletePresetAsync_RemovesFromCache()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("cache-test");
        await sut.SavePresetAsync(preset);
        sut.GetPreset("cache-test").Should().NotBeNull();

        // Act
        await sut.DeletePresetAsync("cache-test");

        // Assert
        sut.GetPreset("cache-test").Should().BeNull();
    }

    #endregion

    #region T5.5: CaptureCurrentLayoutAsync Tests

    [Fact]
    public async Task CaptureCurrentLayoutAsync_CreatesPresetWithGivenName()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureCurrentLayoutAsync("captured-layout");

        // Assert
        result.Success.Should().BeTrue();
        result.Preset!.Name.Should().Be("captured-layout");
    }

    [Fact]
    public async Task CaptureCurrentLayoutAsync_CapturesVisibleWindows()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1, "App1", "app1"),
            TestDataFactory.CreateWindowInfo(2, "App2", "app2")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(false)).Returns(testWindows);
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureCurrentLayoutAsync("capture-test");

        // Assert
        result.Preset!.Placements.Should().HaveCount(2);
    }

    [Fact]
    public async Task CaptureCurrentLayoutAsync_WithIncludeFilter_OnlyIncludesSpecifiedProcesses()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1, "Notepad", "notepad"),
            TestDataFactory.CreateWindowInfo(2, "Chrome", "chrome")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(false)).Returns(testWindows);
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureCurrentLayoutAsync(
            "filter-test",
            includeProcesses: new[] { "notepad" });

        // Assert
        result.Preset!.Placements.Should().HaveCount(1);
        result.Preset.Placements[0].Matcher.ProcessName.Should().Be("notepad");
    }

    [Theory]
    [InlineData("ApplicationFrameHost")]
    [InlineData("CalculatorApp")]
    public async Task CaptureCurrentLayoutAsync_ProcessFilters_MatchTheUwpAppAndItsFrameHost(string processName)
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1, "Calculator", "CalculatorApp") with { HostProcessName = "ApplicationFrameHost", HostProcessId = 4300 },
            TestDataFactory.CreateWindowInfo(2, "Notepad", "notepad")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(false)).Returns(testWindows);
        var sut = CreateSut();

        // Act
        var included = await sut.CaptureCurrentLayoutAsync("uwp-include", includeProcesses: [processName]);
        var excluded = await sut.CaptureCurrentLayoutAsync("uwp-exclude", excludeProcesses: [processName]);

        // Assert - the placement stores the app's own process name
        included.Preset!.Placements.Select(p => p.Matcher.ProcessName).Should().Equal("CalculatorApp");
        excluded.Preset!.Placements.Select(p => p.Matcher.ProcessName).Should().Equal("notepad");
    }

    [Fact]
    public async Task CaptureCurrentLayoutAsync_WithExcludeFilter_ExcludesSpecifiedProcesses()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1, "Notepad", "notepad"),
            TestDataFactory.CreateWindowInfo(2, "Chrome", "chrome")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(false)).Returns(testWindows);
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureCurrentLayoutAsync(
            "exclude-test",
            excludeProcesses: new[] { "chrome" });

        // Assert
        result.Preset!.Placements.Should().HaveCount(1);
        result.Preset.Placements[0].Matcher.ProcessName.Should().Be("notepad");
    }

    [Fact]
    public async Task CaptureCurrentLayoutAsync_SetsDescriptionIfProvided()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.CaptureCurrentLayoutAsync(
            "description-test",
            description: "My custom description");

        // Assert
        result.Preset!.Description.Should().Be("My custom description");
    }

    #endregion

    #region T5.6: ApplyPresetAsync Tests

    [Fact]
    public async Task ApplyPresetAsync_WhenPresetNotFound_ReturnsFailure()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.ApplyPresetAsync("nonexistent");

        // Assert
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("not found"));
    }

    [Fact]
    public async Task ApplyPresetAsync_ReportsEachPlacementsOutcome()
    {
        // Arrange - the first placement's window is open, the second's is not
        var testWindow = TestDataFactory.CreateWindowInfo(processName: "testapp");
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(new List<WindowInfo> { testWindow });
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("progress-test", placements:
        [
            TestDataFactory.CreateWindowPlacement(processName: "testapp", order: 1),
            TestDataFactory.CreateWindowPlacement(processName: "missing", order: 2)
        ]));
        var progress = new RecordingProgress<OperationProgress>();

        // Act
        await sut.ApplyPresetAsync("progress-test", progress: progress);

        // Assert - "i of n" with what happened to each placement, in placement order
        progress.Reports.Select(p => (p.Completed, p.Total)).Should().Equal((1, 2), (2, 2));
        progress.Reports.Select(p => p.Message).Should().Equal(
            "1 of 2: placement 1 (process 'testapp') was arranged",
            "2 of 2: placement 2 (process 'missing') was not found");
    }

    [Fact]
    public async Task ApplyPresetAsync_CancelledWhileALaunchWaitsForItsWindow_Throws()
    {
        // Arrange - the launched application's window never appears; the launch waits until the call is cancelled
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(new List<WindowInfo>());
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(
                It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string _, string[]? _, string? _, bool _, int _, CancellationToken token) =>
            {
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return new LaunchResult(true);
            });
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("cancel-test", placements:
        [
            TestDataFactory.CreateWindowPlacement(processName: "slow", launchCommand: "slow.exe")
        ]));
        using var call = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        var act = () => sut.ApplyPresetAsync("cancel-test", launchMissing: true, cancellationToken: call.Token);

        // Assert - the launch got the call's token
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ApplyPresetAsync_CallsSetWindowBounds_ForMatchedWindows()
    {
        // Arrange
        var testWindow = TestDataFactory.CreateWindowInfo(processName: "testapp");
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo> { testWindow });

        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("apply-test");
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.ApplyPresetAsync("apply-test");

        // Assert
        _mockWindowService.Verify(
            w => w.SetWindowBounds(
                testWindow.Handle,
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyPresetAsync_AWindowThePolicyDenies_IsNeitherArrangedNorFocused()
    {
        // Arrange - a layout that places (and focuses) a password manager the user denied to the agent
        var keePass = TestDataFactory.CreateWindowInfo(processName: "KeePass");
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns([keePass]);
        _mockWindowService.Setup(w => w.EvaluateTargetPolicy(keePass.Handle))
            .Returns(TargetPolicyVerdict.Deny("KeePass", "KeePass is on the deny list"));

        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset(
            "denied", placements: [TestDataFactory.CreateWindowPlacement(processName: "KeePass", focus: true)]));

        // Act
        var result = await sut.ApplyPresetAsync("denied");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.TargetDeniedByPolicy));
        result.Errors.Should().ContainSingle().Which.Should().Contain("KeePass is on the deny list");
        result.WindowsArranged.Should().Be(0);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
        _mockForegroundService.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ApplyPresetAsync_PlacementSavedForTheUwpFrameHost_StillMatchesTheUwpWindow()
    {
        // Arrange - presets saved before UWP windows were attributed to the app name ApplicationFrameHost
        var calculator = TestDataFactory.CreateWindowInfo(4100, "Calculator", "CalculatorApp", processId: 4200) with
        {
            HostProcessName = "ApplicationFrameHost",
            HostProcessId = 4300
        };
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns([calculator]);

        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset(
            "legacy-uwp",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "ApplicationFrameHost")]));

        // Act
        var result = await sut.ApplyPresetAsync("legacy-uwp");

        // Assert
        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(1);
        result.WindowsNotFound.Should().Be(0);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(calculator.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyPresetAsync_ReturnsWindowsArrangedCount()
    {
        // Arrange
        var testWindow = TestDataFactory.CreateWindowInfo(processName: "testapp");
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo> { testWindow });

        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("count-test");
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.ApplyPresetAsync("count-test");

        // Assert
        result.WindowsArranged.Should().Be(1);
    }

    [Fact]
    public async Task ApplyPresetAsync_WithLaunchMissingTrue_LaunchesApp()
    {
        // Arrange
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo>()); // No windows

        var launchedWindow = TestDataFactory.CreateWindowInfo(8888, "Launched", "launched");
        _mockWindowService.Setup(w => w.GetWindowInfo(new nint(8888)))
            .Returns(launchedWindow);

        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset(
            "launch-test",
            placements: new List<WindowPlacement>
            {
                TestDataFactory.CreateWindowPlacement(
                    processName: "launched",
                    launchCommand: "launched.exe")
            });
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.ApplyPresetAsync("launch-test", launchMissing: true);

        // Assert
        result.WindowsLaunched.Should().Be(1);
        _mockProcessService.Verify(
            p => p.LaunchApplicationAsync(
                "launched.exe",
                It.IsAny<string[]?>(),
                It.IsAny<string?>(),
                true,
                5000,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyPresetAsync_WithLaunchMissingFalse_DoesNotLaunchApp()
    {
        // Arrange
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo>()); // No windows

        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset(
            "no-launch-test",
            placements: new List<WindowPlacement>
            {
                TestDataFactory.CreateWindowPlacement(
                    processName: "missing",
                    launchCommand: "missing.exe")
            });
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.ApplyPresetAsync("no-launch-test", launchMissing: false);

        // Assert
        result.WindowsLaunched.Should().Be(0);
        result.WindowsNotFound.Should().Be(1);
        _mockProcessService.Verify(
            p => p.LaunchApplicationAsync(
                It.IsAny<string>(),
                It.IsAny<string[]?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyPresetAsync_AWindowThatDoesNotRespond_FailsItsPlacement_AndTheOthersAreArranged()
    {
        // Arrange - the first window's application is hung: WindowService gives up on it at the deadline
        var hung = TestDataFactory.CreateWindowInfo(handleValue: 1001, title: "Hung - App", processName: "hungapp", processId: 5001);
        var fine = TestDataFactory.CreateWindowInfo(handleValue: 1002, title: "Fine - App", processName: "fineapp", processId: 5002);
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(new List<WindowInfo> { hung, fine });
        _mockWindowService.Setup(w => w.SetWindowBounds(hung.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Throws(new WindowNotRespondingException(hung.Handle, "Window 1001 did not answer within 5 s."));
        _mockWindowService.Setup(w => w.SetWindowBounds(fine.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(WindowChangeResult.Done(fine.Handle));
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset(
            "hung-test",
            placements: new List<WindowPlacement>
            {
                new(new WindowMatcher(ProcessName: "hungapp"), 0, new RelativePosition(0, 0, 0.5, 1.0), null, Focus: true, Order: 0),
                new(new WindowMatcher(ProcessName: "fineapp"), 0, new RelativePosition(0.5, 0, 0.5, 1.0), null, Focus: false, Order: 1)
            }));

        // Act
        var result = await sut.ApplyPresetAsync("hung-test", matchBy: LayoutMatchBy.ProcessOnly);

        // Assert - not focused either: the focus step needs the window placed first
        result.Success.Should().BeFalse();
        result.WindowsArranged.Should().Be(1);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        result.Errors.Should().ContainSingle().Which.Should().Contain("Hung - App").And.Contain("did not answer");
        _mockForegroundService.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// T5.6.4: Test ApplyPresetAsync with matchBy: process_only.
    /// Should match windows by process name only, ignoring title.
    /// </summary>
    [Fact]
    public async Task ApplyPresetAsync_WithMatchByProcessOnly_MatchesByProcessNameOnly()
    {
        // Arrange - Window with different title but matching process name
        var testWindow = TestDataFactory.CreateWindowInfo(
            handleValue: 1001,
            title: "Different Title - Something Else",
            processName: "targetapp",
            processId: 5001);
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo> { testWindow });

        var sut = CreateSut();
        // Create preset looking for specific title that doesn't match, but process name does
        var preset = TestDataFactory.CreateLayoutPreset(
            "process-only-test",
            placements: new List<WindowPlacement>
            {
                new WindowPlacement(
                    Matcher: new WindowMatcher(
                        TitleContains: "Nonexistent Title", // Title doesn't match
                        ProcessName: "targetapp"            // Process does match
                    ),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0, 0, 0.5, 1.0),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });
        await sut.SavePresetAsync(preset);

        // Act - Use process_only matching
        var result = await sut.ApplyPresetAsync("process-only-test", matchBy: LayoutMatchBy.ProcessOnly);

        // Assert - Should find and arrange the window despite title mismatch
        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(1);
        result.WindowsNotFound.Should().Be(0);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(testWindow.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
    }

    /// <summary>
    /// T5.6.5: Test ApplyPresetAsync with matchBy: title_only.
    /// Should match windows by title only, ignoring process name.
    /// </summary>
    [Fact]
    public async Task ApplyPresetAsync_WithMatchByTitleOnly_MatchesByTitleOnly()
    {
        // Arrange - Window with matching title but different process name
        var testWindow = TestDataFactory.CreateWindowInfo(
            handleValue: 1001,
            title: "My Document - Editor",
            processName: "differentapp",  // Different process
            processId: 5001);
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo> { testWindow });

        var sut = CreateSut();
        // Create preset looking for specific process that doesn't match, but title does
        var preset = TestDataFactory.CreateLayoutPreset(
            "title-only-test",
            placements: new List<WindowPlacement>
            {
                new WindowPlacement(
                    Matcher: new WindowMatcher(
                        TitleContains: "My Document",  // Title matches
                        ProcessName: "nonexistent"    // Process doesn't match
                    ),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0, 0, 0.5, 1.0),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });
        await sut.SavePresetAsync(preset);

        // Act - Use title_only matching
        var result = await sut.ApplyPresetAsync("title-only-test", matchBy: LayoutMatchBy.TitleOnly);

        // Assert - Should find and arrange the window despite process mismatch
        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(1);
        result.WindowsNotFound.Should().Be(0);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(testWindow.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
    }

    /// <summary>
    /// T5.6.6: Test ApplyPresetAsync with matchBy: process_and_title (default).
    /// Should require BOTH process name AND title to match.
    /// </summary>
    [Fact]
    public async Task ApplyPresetAsync_WithMatchByProcessAndTitle_RequiresBothToMatch()
    {
        // Arrange - Multiple windows: only one matches both process AND title
        var matchingWindow = TestDataFactory.CreateWindowInfo(
            handleValue: 1001,
            title: "Project - Editor",
            processName: "editor",
            processId: 5001);
        var processOnlyMatch = TestDataFactory.CreateWindowInfo(
            handleValue: 1002,
            title: "Different Title",
            processName: "editor",  // Same process, different title
            processId: 5002);
        var titleOnlyMatch = TestDataFactory.CreateWindowInfo(
            handleValue: 1003,
            title: "Project - Other",
            processName: "otherapp",  // Different process, similar title
            processId: 5003);

        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo> { matchingWindow, processOnlyMatch, titleOnlyMatch });

        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset(
            "both-test",
            placements: new List<WindowPlacement>
            {
                new WindowPlacement(
                    Matcher: new WindowMatcher(
                        TitleContains: "Project",  // Must contain "Project"
                        ProcessName: "editor"      // Must be from "editor" process
                    ),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0, 0, 0.5, 1.0),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });
        await sut.SavePresetAsync(preset);

        // Act - Use process_and_title matching (default)
        var result = await sut.ApplyPresetAsync("both-test", matchBy: LayoutMatchBy.ProcessAndTitle);

        // Assert - Should only match the window that has BOTH conditions met
        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(1);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(matchingWindow.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
        // Verify other windows were NOT arranged
        _mockWindowService.Verify(
            w => w.SetWindowBounds(processOnlyMatch.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(titleOnlyMatch.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Never);
    }

    /// <summary>
    /// T5.6.10: Test ApplyPresetAsync handles multiple windows per process.
    /// When a preset has multiple placements for the same process (with different title matchers),
    /// each placement should find and arrange its specific window.
    /// </summary>
    [Fact]
    public async Task ApplyPresetAsync_WithMultipleWindowsPerProcess_ArrangesEachCorrectly()
    {
        // Arrange - Two windows from the same process with different titles
        var codeWindow = TestDataFactory.CreateWindowInfo(
            handleValue: 1001,
            title: "main.cs - MyProject",
            processName: "Code",
            processId: 5001);
        var settingsWindow = TestDataFactory.CreateWindowInfo(
            handleValue: 1002,
            title: "Settings - MyProject",
            processName: "Code",  // Same process
            processId: 5001);     // Same PID
        var terminalWindow = TestDataFactory.CreateWindowInfo(
            handleValue: 1003,
            title: "Terminal",
            processName: "WindowsTerminal",
            processId: 5002);

        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo> { codeWindow, settingsWindow, terminalWindow });

        var sut = CreateSut();
        // Create preset with multiple placements targeting different windows from same process
        var preset = TestDataFactory.CreateLayoutPreset(
            "multi-window-test",
            placements: new List<WindowPlacement>
            {
                new WindowPlacement(
                    Matcher: new WindowMatcher(TitleContains: "main.cs", ProcessName: "Code"),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0, 0, 0.5, 1.0),  // Left half
                    LaunchCommand: null, Focus: false, Order: 0),
                new WindowPlacement(
                    Matcher: new WindowMatcher(TitleContains: "Settings", ProcessName: "Code"),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0.5, 0, 0.5, 0.5),  // Top-right quarter
                    LaunchCommand: null, Focus: false, Order: 1),
                new WindowPlacement(
                    Matcher: new WindowMatcher(TitleContains: null, ProcessName: "WindowsTerminal"),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0.5, 0.5, 0.5, 0.5),  // Bottom-right quarter
                    LaunchCommand: null, Focus: false, Order: 2)
            });
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.ApplyPresetAsync("multi-window-test", matchBy: LayoutMatchBy.ProcessAndTitle);

        // Assert - All three windows should be arranged
        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(3);
        result.WindowsNotFound.Should().Be(0);

        // Verify each window was arranged exactly once
        _mockWindowService.Verify(
            w => w.SetWindowBounds(codeWindow.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(settingsWindow.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(terminalWindow.Handle, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once);
    }

    #endregion

    #region Honest save results

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SavePresetAsync_WithBlankName_ReturnsInvalidName(string name)
    {
        var sut = CreateSut();

        var result = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset(name));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidName));
        Directory.Exists(_testLayoutPath).Should().BeFalse("nothing should be written for an invalid name");
    }

    [Fact]
    public async Task SavePresetAsync_WithTooLongName_ReturnsInvalidName()
    {
        var sut = CreateSut();

        var result = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset(new string('a', LayoutService.MaxNameLength + 1)));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidName));
    }

    [Fact]
    public async Task SavePresetAsync_WhenFileCannotBeWritten_ReturnsSaveFailed()
    {
        // Arrange - make the existing preset file read-only so it cannot be replaced
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("locked", "Original"));
        var filePath = Path.Combine(_testLayoutPath, "locked.json");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        try
        {
            // Act
            var result = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("locked", "Updated"));

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(nameof(LayoutErrorCode.SaveFailed));
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            sut.GetPreset("locked")!.Description.Should().Be("Original");
            Directory.GetFiles(_testLayoutPath, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task SavePresetAsync_WhenNameExists_ReportsReplaced()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("again"));

        var result = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("AGAIN"));

        result.Success.Should().BeTrue();
        result.Replaced.Should().BeTrue();
        Directory.GetFiles(_testLayoutPath, "*.json").Should().HaveCount(1);
    }

    [Fact]
    public async Task SavePresetAsync_NamesThatSanitizeToSameFile_DoNotOverwriteEachOther()
    {
        var sut = CreateSut();

        (await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("a:b", "colon"))).Success.Should().BeTrue();
        (await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("a_b", "underscore"))).Success.Should().BeTrue();

        sut.GetPreset("a:b")!.Description.Should().Be("colon");
        sut.GetPreset("a_b")!.Description.Should().Be("underscore");
        Directory.GetFiles(_testLayoutPath, "*.json").Should().HaveCount(2);
    }

    [Fact]
    public async Task SavePresetAsync_ExistingPresetInDifferentlyNamedFile_IsUpdatedInPlace()
    {
        // Arrange - a hand-written file whose name does not match the preset name
        Directory.CreateDirectory(_testLayoutPath);
        var handWritten = Path.Combine(_testLayoutPath, "My Work Layout.json");
        File.WriteAllText(handWritten, ToJson(TestDataFactory.CreateLayoutPreset("work", "Old")));
        var sut = CreateSut();

        // Act
        var result = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("work", "New"));

        // Assert
        result.Replaced.Should().BeTrue();
        Directory.GetFiles(_testLayoutPath, "*.json").Should().ContainSingle().Which.Should().Be(handWritten);
        sut.GetPreset("work")!.Description.Should().Be("New");
    }

    [Fact]
    public async Task SavePresetAsync_DoesNotOverwriteUnreadableJsonFileWithSameFileName()
    {
        Directory.CreateDirectory(_testLayoutPath);
        var broken = Path.Combine(_testLayoutPath, "draft.json");
        File.WriteAllText(broken, "{ half-edited");
        var sut = CreateSut();

        var result = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("draft"));

        result.Success.Should().BeTrue();
        File.ReadAllText(broken).Should().Be("{ half-edited");
        sut.GetPreset("draft").Should().NotBeNull();
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("com1")]
    public async Task SavePresetAsync_WithReservedDeviceName_SavesToRegularFile(string name)
    {
        var sut = CreateSut();

        var result = await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset(name));

        result.Success.Should().BeTrue();
        sut.GetPreset(name).Should().NotBeNull();
        Directory.GetFiles(_testLayoutPath, "*.json").Should().ContainSingle();
    }

    #endregion

    #region Honest capture results

    [Fact]
    public async Task CaptureCurrentLayoutAsync_WhenSaveFails_ReturnsSaveFailed()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("locked"));
        var filePath = Path.Combine(_testLayoutPath, "locked.json");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        try
        {
            var result = await sut.CaptureCurrentLayoutAsync("locked");

            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(nameof(LayoutErrorCode.SaveFailed));
            result.Preset.Should().NotBeNull("the captured layout is still reported");
        }
        finally
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task CaptureCurrentLayoutAsync_WithNoMatchingWindows_ReturnsNoWindowsCaptured_AndKeepsExistingPreset()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("keep-me", "Good layout"));

        var result = await sut.CaptureCurrentLayoutAsync("keep-me", includeProcesses: ["no-such-process"]);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.NoWindowsCaptured));
        sut.GetPreset("keep-me")!.Description.Should().Be("Good layout");
    }

    [Fact]
    public async Task CaptureCurrentLayoutAsync_WithBlankName_ReturnsInvalidName()
    {
        var sut = CreateSut();

        var result = await sut.CaptureCurrentLayoutAsync(" ");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidName));
    }

    [Fact]
    public async Task CaptureCurrentLayoutAsync_WhenNameExists_ReportsReplaced()
    {
        var sut = CreateSut();
        await sut.CaptureCurrentLayoutAsync("twice");

        var result = await sut.CaptureCurrentLayoutAsync("twice");

        result.Success.Should().BeTrue();
        result.Replaced.Should().BeTrue();
    }

    #endregion

    #region Honest delete results

    [Fact]
    public async Task DeletePresetAsync_DeletesTheFileThePresetWasLoadedFrom()
    {
        Directory.CreateDirectory(_testLayoutPath);
        var handWritten = Path.Combine(_testLayoutPath, "Hand Written.json");
        File.WriteAllText(handWritten, ToJson(TestDataFactory.CreateLayoutPreset("hand-written")));
        var sut = CreateSut();

        var result = await sut.DeletePresetAsync("HAND-WRITTEN");

        result.Success.Should().BeTrue();
        File.Exists(handWritten).Should().BeFalse();
        sut.GetAllPresets().Should().BeEmpty();
    }

    [Fact]
    public async Task DeletePresetAsync_WhenFileCannotBeDeleted_ReturnsDeleteFailed()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("stuck"));
        var filePath = Path.Combine(_testLayoutPath, "stuck.json");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        try
        {
            var result = await sut.DeletePresetAsync("stuck");

            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(nameof(LayoutErrorCode.DeleteFailed));
            sut.GetPreset("stuck").Should().NotBeNull();
        }
        finally
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task DeletePresetAsync_WithBlankName_ReturnsInvalidName()
    {
        var sut = CreateSut();

        var result = await sut.DeletePresetAsync("  ");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.InvalidName));
    }

    #endregion

    #region Honest apply results

    [Fact]
    public async Task ApplyPresetAsync_WhenPresetNotFound_SetsPresetNotFoundErrorCode()
    {
        var sut = CreateSut();

        var result = await sut.ApplyPresetAsync("nonexistent");

        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.PresetNotFound));
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenNoWindowMatches_ReportsFailureWithWindowNotFound()
    {
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(new List<WindowInfo>());
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("nothing-open",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "chrome", launchCommand: "chrome")]));

        var result = await sut.ApplyPresetAsync("nothing-open");

        result.Success.Should().BeFalse();
        result.WindowsArranged.Should().Be(0);
        result.WindowsNotFound.Should().Be(1);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.WindowNotFound));
        result.Errors.Should().ContainSingle().Which.Should().Contain("chrome").And.Contain("launchMissing");
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenSomeWindowsMissing_ReportsPartialFailure()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("partial", placements:
        [
            TestDataFactory.CreateWindowPlacement(processName: "notepad", order: 0),
            TestDataFactory.CreateWindowPlacement(processName: "missingapp", order: 1)
        ]));

        var result = await sut.ApplyPresetAsync("partial");

        result.Success.Should().BeFalse();
        result.WindowsArranged.Should().Be(1);
        result.WindowsNotFound.Should().Be(1);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.WindowNotFound));
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenSetWindowBoundsFails_SetsArrangeFailed()
    {
        _mockWindowService.Setup(w => w.SetWindowBounds(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint handle, int _, int _, int _, int _, bool _) =>
                WindowChangeResult.Failed(handle, WindowErrorCode.AccessDenied, "Windows blocked the change"));
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("stuck-window",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "notepad")]));

        var result = await sut.ApplyPresetAsync("stuck-window");

        result.Success.Should().BeFalse();
        result.WindowsArranged.Should().Be(0);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        result.Errors.Should().ContainSingle().Which.Should().Contain("AccessDenied: Windows blocked the change",
            "the window change's own code and reason are named in the placement's error");
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenFocusFails_ReportsTheVerifiedOutcome()
    {
        _mockForegroundService.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) => new FocusOutcome
            {
                Target = handle,
                ForegroundHandle = new nint(0x200),
                ForegroundTitle = "Terminal",
                ErrorCode = WindowErrorCode.FocusFailed,
                Error = "The window could not be brought to the foreground; 'Terminal' (WindowsTerminal) is in front"
            });
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("focus",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "notepad", focus: true)]));

        var result = await sut.ApplyPresetAsync("focus");

        result.WindowsArranged.Should().Be(1);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.ArrangeFailed));
        result.Errors.Should().ContainSingle().Which.Should().Contain("foreground").And.Contain("FocusFailed").And.Contain("Terminal");
        var focus = result.Focus.Should().ContainSingle().Subject;
        focus.Success.Should().BeFalse();
        focus.IsForeground.Should().BeFalse();
        focus.ErrorCode.Should().Be(nameof(WindowErrorCode.FocusFailed));
        focus.ForegroundHandle.Should().Be(0x200);
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenFocusSucceeds_ReportsSuccessAndHowTheWindowCameToTheFront()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("focus-ok",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "notepad", focus: true)]));

        var result = await sut.ApplyPresetAsync("focus-ok");

        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Errors.Should().BeNull();
        var focus = result.Focus.Should().ContainSingle().Subject;
        focus.IsForeground.Should().BeTrue();
        focus.Method.Should().Be(FocusMethod.SetForeground);
        _mockForegroundService.Verify(f => f.EnsureForeground(new nint(focus.Handle), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task ApplyPresetAsync_WithoutFocusPlacements_ActivatesNothingAndReportsNoFocus()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("no-focus",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "notepad", focus: false)]));

        var result = await sut.ApplyPresetAsync("no-focus");

        result.Success.Should().BeTrue();
        result.Focus.Should().BeNull();
        _mockForegroundService.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenLaunchFails_SetsLaunchFailed()
    {
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(new List<WindowInfo>());
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(
                It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(Success: false, ErrorMessage: "Executable not found"));
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("launch-fail",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "app", launchCommand: "app.exe")]));

        var result = await sut.ApplyPresetAsync("launch-fail", launchMissing: true);

        result.Success.Should().BeFalse();
        result.WindowsLaunched.Should().Be(0);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchFailed));
        result.Errors.Should().ContainSingle().Which.Should().Contain("Executable not found");
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenLaunchedWindowCannotBeFound_ReportsLaunchFailedWithoutThrowing()
    {
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(new List<WindowInfo>());
        _mockWindowService.Setup(w => w.GetWindowInfo(new nint(8888))).Returns((WindowInfo?)null);
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("vanished",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "app", launchCommand: "app.exe")]));

        var result = await sut.ApplyPresetAsync("vanished", launchMissing: true);

        result.Success.Should().BeFalse();
        result.WindowsLaunched.Should().Be(1);
        result.WindowsArranged.Should().Be(0);
        result.ErrorCode.Should().Be(nameof(LayoutErrorCode.LaunchFailed));
    }

    [Fact]
    public async Task ApplyPresetAsync_WhenAllPlacementsArranged_HasNoErrorCode()
    {
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("all-good",
            placements: [TestDataFactory.CreateWindowPlacement(processName: "notepad")]));

        var result = await sut.ApplyPresetAsync("all-good");

        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.Errors.Should().BeNull();
    }

    #endregion

    private static string ToJson(LayoutPreset preset) => System.Text.Json.JsonSerializer.Serialize(
        preset,
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
}
