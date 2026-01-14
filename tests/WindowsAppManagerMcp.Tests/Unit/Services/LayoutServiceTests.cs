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

    public LayoutServiceTests()
    {
        _testLayoutPath = Path.Combine(
            Path.GetTempPath(),
            $"LayoutServiceTests_{Guid.NewGuid()}");

        _mockWindowService = TestDataFactory.CreateMockWindowService();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _mockProcessService = TestDataFactory.CreateMockProcessService();
    }

    private LayoutService CreateSut() => new LayoutService(
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

    #region T5.1: GetAllPresets Tests

    [Fact]
    public void GetAllPresets_WithNoPresets_ReturnsEmptyList()
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
    public void GetPreset_WhenNotExists_ReturnsNull()
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
        result.Should().BeTrue();
        var filePath = Path.Combine(_testLayoutPath, "save-test.json");
        File.Exists(filePath).Should().BeTrue();
    }

    [Fact]
    public async Task SavePresetAsync_ReturnsTrue_OnSuccess()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("success-test");

        // Act
        var result = await sut.SavePresetAsync(preset);

        // Assert
        result.Should().BeTrue();
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
        result.Should().BeTrue();
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
        result.Should().BeTrue();
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact]
    public async Task DeletePresetAsync_ReturnsTrue_WhenPresetExists()
    {
        // Arrange
        var sut = CreateSut();
        var preset = TestDataFactory.CreateLayoutPreset("exists-test");
        await sut.SavePresetAsync(preset);

        // Act
        var result = await sut.DeletePresetAsync("exists-test");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeletePresetAsync_ReturnsTrue_WhenPresetNotFound()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.DeletePresetAsync("nonexistent");

        // Assert
        // Note: Current implementation returns true even if file doesn't exist
        result.Should().BeTrue();
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
        result.Name.Should().Be("captured-layout");
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
        result.Placements.Should().HaveCount(2);
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
        result.Placements.Should().HaveCount(1);
        result.Placements[0].Matcher.ProcessName.Should().Be("notepad");
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
        result.Placements.Should().HaveCount(1);
        result.Placements[0].Matcher.ProcessName.Should().Be("notepad");
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
        result.Description.Should().Be("My custom description");
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
                It.IsAny<int>(), It.IsAny<int>()),
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
            p => p.LaunchApplication(
                "launched.exe",
                It.IsAny<string[]?>(),
                It.IsAny<string?>(),
                true,
                5000),
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
            p => p.LaunchApplication(
                It.IsAny<string>(),
                It.IsAny<string[]?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<int>()),
            Times.Never);
    }

    #endregion
}
