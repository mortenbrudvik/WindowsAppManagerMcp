namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// Integration tests for LayoutService.
/// Tests T7.1 from BACKLOG-TECHNICAL.md.
///
/// These tests verify end-to-end workflows with real LayoutService
/// behavior (file I/O, caching) combined with mocked window/monitor services.
/// </summary>
public class LayoutServiceIntegrationTests : IDisposable
{
    private readonly string _testLayoutPath;
    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<IProcessService> _mockProcessService;

    public LayoutServiceIntegrationTests()
    {
        _testLayoutPath = Path.Combine(
            Path.GetTempPath(),
            $"IntegrationTests_{Guid.NewGuid()}");

        _mockWindowService = TestDataFactory.CreateMockWindowService();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _mockProcessService = TestDataFactory.CreateMockProcessService();
    }

    private LayoutService CreateSut() => new(
        _mockWindowService.Object,
        _mockMonitorService.Object,
        _mockProcessService.Object,
        _testLayoutPath);

    public void Dispose()
    {
        if (Directory.Exists(_testLayoutPath))
        {
            Directory.Delete(_testLayoutPath, recursive: true);
        }
    }

    #region T7.1.1: Save and Load Preset Roundtrip

    [Fact]
    public async Task SaveAndLoadPreset_RoundTrip_PreservesData()
    {
        // Arrange
        var sut = CreateSut();
        var originalPreset = new LayoutPreset(
            Name: "roundtrip-test",
            Description: "Test description",
            Placements: new List<WindowPlacement>
            {
                new(
                    Matcher: new WindowMatcher("Document", "notepad", null),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0.0, 0.0, 0.5, 1.0),
                    LaunchCommand: "notepad.exe",
                    Focus: true,
                    Order: 0),
                new(
                    Matcher: new WindowMatcher(null, "chrome", null),
                    MonitorIndex: 1,
                    Position: new RelativePosition(0.5, 0.0, 0.5, 1.0),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 1)
            });

        // Act
        await sut.SavePresetAsync(originalPreset);
        var loadedPreset = sut.GetPreset("roundtrip-test");

        // Assert
        loadedPreset.Should().NotBeNull();
        loadedPreset!.Name.Should().Be(originalPreset.Name);
        loadedPreset.Description.Should().Be(originalPreset.Description);
        loadedPreset.Placements.Should().HaveCount(2);

        loadedPreset.Placements[0].Matcher.TitleContains.Should().Be("Document");
        loadedPreset.Placements[0].Matcher.ProcessName.Should().Be("notepad");
        loadedPreset.Placements[0].MonitorIndex.Should().Be(0);
        loadedPreset.Placements[0].Position.X.Should().Be(0.0);
        loadedPreset.Placements[0].Position.Width.Should().Be(0.5);
        loadedPreset.Placements[0].LaunchCommand.Should().Be("notepad.exe");
        loadedPreset.Placements[0].Focus.Should().BeTrue();

        loadedPreset.Placements[1].Matcher.ProcessName.Should().Be("chrome");
        loadedPreset.Placements[1].MonitorIndex.Should().Be(1);
    }

    #endregion

    #region T7.1.2: Save Preset, Reload Service, Preset Persisted

    [Fact]
    public async Task SavePreset_ReloadService_PresetPersisted()
    {
        // Arrange - save with first service instance
        var sut1 = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("persistence-test");
        await sut1.SavePresetAsync(preset);

        // Act - create new service instance (simulates restart)
        var sut2 = CreateSut();
        var loadedPreset = sut2.GetPreset("persistence-test");

        // Assert
        loadedPreset.Should().NotBeNull();
        loadedPreset!.Name.Should().Be("persistence-test");
    }

    #endregion

    #region T7.1.3: Capture Layout, Save, Apply Cycle

    [Fact]
    public async Task CaptureLayout_SavePreset_ApplyLayout_WindowsArranged()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Notepad", "notepad", 1001, 100, 100, 800, 600),
            TestDataFactory.CreateWindowInfo(1002, "Chrome", "chrome", 1002, 200, 200, 1000, 700)
        };
        _mockWindowService.Setup(w => w.GetAllWindows(false)).Returns(testWindows);
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(testWindows);

        var sut = CreateSut();

        // Act - Capture current layout
        var captured = await sut.CaptureCurrentLayoutAsync("captured-layout", "My workspace");

        // Verify capture
        captured.Placements.Should().HaveCount(2);

        // Act - Apply the captured layout
        var result = await sut.ApplyPresetAsync("captured-layout");

        // Assert
        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(2);
        result.WindowsNotFound.Should().Be(0);

        // Verify SetWindowBounds was called for each window
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1001), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Once);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1002), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Once);
    }

    #endregion

    #region T7.1.4: Apply Preset with Launch Missing

    [Fact]
    public async Task ApplyPreset_WithLaunchMissing_LaunchesApplications()
    {
        // Arrange - No windows initially
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo>());

        var launchedWindow = TestDataFactory.CreateWindowInfo(8888, "Launched App", "launched");
        _mockWindowService.Setup(w => w.GetWindowInfo(new nint(8888)))
            .Returns(launchedWindow);

        var sut = CreateSut();
        var preset = new LayoutPreset(
            Name: "launch-test",
            Description: "Test launching apps",
            Placements: new List<WindowPlacement>
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
            p => p.LaunchApplication("launched.exe", null, null, true, 5000),
            Times.Once);
    }

    #endregion

    #region T7.1.5-7: Match By Strategies

    [Fact]
    public async Task ApplyPreset_MatchByProcessOnly_MatchesCorrectWindows()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Random Title", "targetapp"),
            TestDataFactory.CreateWindowInfo(1002, "Other Window", "otherapp")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(testWindows);

        var sut = CreateSut();
        var preset = new LayoutPreset(
            Name: "process-match-test",
            Description: null,
            Placements: new List<WindowPlacement>
            {
                new(
                    Matcher: new WindowMatcher("NonMatching Title", "targetapp", null),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0, 0, 0.5, 1),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });
        await sut.SavePresetAsync(preset);

        // Act - Use process_only matching (ignores title mismatch)
        var result = await sut.ApplyPresetAsync("process-match-test", matchBy: "process_only");

        // Assert
        result.WindowsArranged.Should().Be(1);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1001), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyPreset_MatchByTitleOnly_MatchesCorrectWindows()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Target Document", "someapp"),
            TestDataFactory.CreateWindowInfo(1002, "Other Window", "otherapp")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(testWindows);

        var sut = CreateSut();
        var preset = new LayoutPreset(
            Name: "title-match-test",
            Description: null,
            Placements: new List<WindowPlacement>
            {
                new(
                    Matcher: new WindowMatcher("Target", "wrongprocess", null),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0, 0, 0.5, 1),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });
        await sut.SavePresetAsync(preset);

        // Act - Use title_only matching (ignores process mismatch)
        var result = await sut.ApplyPresetAsync("title-match-test", matchBy: "title_only");

        // Assert
        result.WindowsArranged.Should().Be(1);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1001), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task ApplyPreset_MatchByProcessAndTitle_RequiresBothToMatch()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Target Document", "targetapp"),    // Both match
            TestDataFactory.CreateWindowInfo(1002, "Target Document", "wrongapp"),     // Title matches, process doesn't
            TestDataFactory.CreateWindowInfo(1003, "Wrong Title", "targetapp")          // Process matches, title doesn't
        };
        _mockWindowService.Setup(w => w.GetAllWindows(true)).Returns(testWindows);

        var sut = CreateSut();
        var preset = new LayoutPreset(
            Name: "both-match-test",
            Description: null,
            Placements: new List<WindowPlacement>
            {
                new(
                    Matcher: new WindowMatcher("Target", "targetapp", null),
                    MonitorIndex: 0,
                    Position: new RelativePosition(0, 0, 0.5, 1),
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });
        await sut.SavePresetAsync(preset);

        // Act - Use default process_and_title matching
        var result = await sut.ApplyPresetAsync("both-match-test", matchBy: "process_and_title");

        // Assert - Only window 1001 should match (both criteria)
        result.WindowsArranged.Should().Be(1);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1001), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Once);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1002), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1003), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
    }

    #endregion

    #region T7.1.8: Delete Preset

    [Fact]
    public async Task DeletePreset_RemovesFromDiskAndCache()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("delete-me");
        await sut.SavePresetAsync(preset);

        var filePath = Path.Combine(_testLayoutPath, "delete-me.json");
        File.Exists(filePath).Should().BeTrue();
        sut.GetPreset("delete-me").Should().NotBeNull();

        // Act
        await sut.DeletePresetAsync("delete-me");

        // Assert
        File.Exists(filePath).Should().BeFalse();
        sut.GetPreset("delete-me").Should().BeNull();
    }

    #endregion

    #region T7.1.9: Get All Presets After Multiple Saves

    [Fact]
    public async Task GetAllPresets_AfterMultipleSaves_ReturnsAll()
    {
        // Arrange
        var sut = CreateSut();
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("preset-a"));
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("preset-b"));
        await sut.SavePresetAsync(TestDataFactory.CreateLayoutPreset("preset-c"));

        // Act
        var presets = sut.GetAllPresets();

        // Assert
        presets.Should().HaveCount(3);
        presets.Should().Contain(p => p.Name == "preset-a");
        presets.Should().Contain(p => p.Name == "preset-b");
        presets.Should().Contain(p => p.Name == "preset-c");
    }

    #endregion

    #region T7.1.10: Multiple Presets Independent Storage

    [Fact]
    public async Task MultiplePresets_IndependentStorage_NoConflicts()
    {
        // Arrange
        var sut = CreateSut();
        var preset1 = new LayoutPreset(
            Name: "workspace-1",
            Description: "First workspace",
            Placements: new List<WindowPlacement>
            {
                TestDataFactory.CreateWindowPlacement(processName: "app1")
            });
        var preset2 = new LayoutPreset(
            Name: "workspace-2",
            Description: "Second workspace",
            Placements: new List<WindowPlacement>
            {
                TestDataFactory.CreateWindowPlacement(processName: "app2"),
                TestDataFactory.CreateWindowPlacement(processName: "app3")
            });

        // Act
        await sut.SavePresetAsync(preset1);
        await sut.SavePresetAsync(preset2);

        // Assert
        var loaded1 = sut.GetPreset("workspace-1");
        var loaded2 = sut.GetPreset("workspace-2");

        loaded1.Should().NotBeNull();
        loaded2.Should().NotBeNull();
        loaded1!.Description.Should().Be("First workspace");
        loaded1.Placements.Should().HaveCount(1);
        loaded2!.Description.Should().Be("Second workspace");
        loaded2.Placements.Should().HaveCount(2);
    }

    #endregion

    #region T7.1.11: Multi-Monitor Layout

    [Fact]
    public async Task ApplyPreset_MultiMonitorLayout_CalculatesCorrectBounds()
    {
        // Arrange - Dual monitor setup
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);

        var testWindow = TestDataFactory.CreateWindowInfo(1001, "Test", "testapp", monitorIndex: 1);
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(new List<WindowInfo> { testWindow });

        var sut = CreateSut();
        var preset = new LayoutPreset(
            Name: "multi-monitor-test",
            Description: null,
            Placements: new List<WindowPlacement>
            {
                new(
                    Matcher: new WindowMatcher(null, "testapp", null),
                    MonitorIndex: 1,  // Second monitor
                    Position: new RelativePosition(0, 0, 0.5, 1.0),  // Left half
                    LaunchCommand: null,
                    Focus: false,
                    Order: 0)
            });
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.ApplyPresetAsync("multi-monitor-test");

        // Assert
        result.Success.Should().BeTrue();
        result.WindowsArranged.Should().Be(1);

        // Second monitor starts at x=1920, width=2560, work area height=1400
        // Left half: x=1920, y=0, width=1280, height=1400
        _mockWindowService.Verify(
            w => w.SetWindowBounds(new nint(1001), 1920, 0, 1280, 1400),
            Times.Once);
    }

    #endregion

    #region T7.1.12: Capture Layout with Filters

    [Fact]
    public async Task CaptureLayout_WithFilters_IncludesCorrectWindows()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Notepad", "notepad"),
            TestDataFactory.CreateWindowInfo(1002, "Chrome", "chrome"),
            TestDataFactory.CreateWindowInfo(1003, "Code", "Code"),
            TestDataFactory.CreateWindowInfo(1004, "Explorer", "explorer")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(false)).Returns(testWindows);

        var sut = CreateSut();

        // Act - Include only notepad and Code, exclude chrome
        var result = await sut.CaptureCurrentLayoutAsync(
            "filtered-capture",
            includeProcesses: new[] { "notepad", "Code" });

        // Assert
        result.Placements.Should().HaveCount(2);
        result.Placements.Should().Contain(p => p.Matcher.ProcessName == "notepad");
        result.Placements.Should().Contain(p => p.Matcher.ProcessName == "Code");
        result.Placements.Should().NotContain(p => p.Matcher.ProcessName == "chrome");
    }

    #endregion

    #region T7.1.14: Save Preset Overwrite

    [Fact]
    public async Task SavePreset_Overwrite_UpdatesExistingFile()
    {
        // Arrange
        var sut = CreateSut();
        var original = new LayoutPreset(
            Name: "overwrite-test",
            Description: "Original",
            Placements: new List<WindowPlacement>
            {
                TestDataFactory.CreateWindowPlacement(processName: "original")
            });
        await sut.SavePresetAsync(original);

        var updated = new LayoutPreset(
            Name: "overwrite-test",
            Description: "Updated",
            Placements: new List<WindowPlacement>
            {
                TestDataFactory.CreateWindowPlacement(processName: "updated1"),
                TestDataFactory.CreateWindowPlacement(processName: "updated2")
            });

        // Act
        await sut.SavePresetAsync(updated);
        var loaded = sut.GetPreset("overwrite-test");

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Description.Should().Be("Updated");
        loaded.Placements.Should().HaveCount(2);
        loaded.Placements[0].Matcher.ProcessName.Should().Be("updated1");
    }

    #endregion

    #region T7.1.15: Case Insensitive Lookup

    [Fact]
    public async Task GetPreset_CaseInsensitive_FindsPreset()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("MyWorkspace");
        await sut.SavePresetAsync(preset);

        // Act & Assert - various case variations should all find the preset
        sut.GetPreset("MyWorkspace").Should().NotBeNull();
        sut.GetPreset("myworkspace").Should().NotBeNull();
        sut.GetPreset("MYWORKSPACE").Should().NotBeNull();
        sut.GetPreset("myWORKSPACE").Should().NotBeNull();
    }

    #endregion
}
