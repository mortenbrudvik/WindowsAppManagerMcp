namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for LayoutPresetTools.
/// Tests the MCP tool layer that exposes layout preset functionality.
/// </summary>
public class LayoutPresetToolsTests
{
    private readonly Mock<ILayoutService> _mockLayoutService;

    public LayoutPresetToolsTests()
    {
        _mockLayoutService = new Mock<ILayoutService>();
    }

    private LayoutPresetTools CreateSut() => new LayoutPresetTools(_mockLayoutService.Object);

    #region ListLayouts Tests

    [Fact]
    public void ListLayouts_CallsService()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset>());
        var sut = CreateSut();

        // Act
        sut.ListLayouts();

        // Assert
        _mockLayoutService.Verify(l => l.GetAllPresets(), Times.Once);
    }

    [Fact]
    public void ListLayouts_ReturnsEmptyList_WhenNoPresets()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset>());
        var sut = CreateSut();

        // Act
        var result = sut.ListLayouts();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ListLayouts_MapsPresetToSummaryDto()
    {
        // Arrange
        var preset = TestDataFactory.CreateLayoutPreset(
            name: "Development",
            description: "Dev layout");
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset> { preset });
        var sut = CreateSut();

        // Act
        var result = sut.ListLayouts();

        // Assert
        result.Should().HaveCount(1);
        var dto = result[0];
        dto.Name.Should().Be("Development");
        dto.Description.Should().Be("Dev layout");
        dto.WindowCount.Should().Be(1); // Default preset has 1 placement
    }

    [Fact]
    public void ListLayouts_IncludesTimestamps()
    {
        // Arrange
        var preset = TestDataFactory.CreateLayoutPreset();
        _mockLayoutService.Setup(l => l.GetAllPresets())
            .Returns(new List<LayoutPreset> { preset });
        var sut = CreateSut();

        // Act
        var result = sut.ListLayouts();

        // Assert
        result[0].CreatedAt.Should().NotBeNullOrEmpty();
        result[0].UpdatedAt.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region GetLayout Tests

    [Fact]
    public void GetLayout_ReturnsNull_WhenNotFound()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetPreset(It.IsAny<string>()))
            .Returns((LayoutPreset?)null);
        var sut = CreateSut();

        // Act
        var result = sut.GetLayout("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetLayout_CallsServiceWithName()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.GetPreset("my-layout"))
            .Returns(TestDataFactory.CreateLayoutPreset("my-layout"));
        var sut = CreateSut();

        // Act
        sut.GetLayout("my-layout");

        // Assert
        _mockLayoutService.Verify(l => l.GetPreset("my-layout"), Times.Once);
    }

    [Fact]
    public void GetLayout_MapsPresetToDetailDto()
    {
        // Arrange
        var placement = TestDataFactory.CreateWindowPlacement(
            processName: "code",
            titleContains: "Visual Studio",
            monitorIndex: 1,
            x: 0.0, y: 0.0,
            width: 0.5, height: 1.0,
            launchCommand: "code.exe",
            focus: true,
            order: 1);
        var preset = TestDataFactory.CreateLayoutPreset(
            name: "Development",
            description: "Dev setup",
            placements: new List<WindowPlacement> { placement });
        _mockLayoutService.Setup(l => l.GetPreset("Development"))
            .Returns(preset);
        var sut = CreateSut();

        // Act
        var result = sut.GetLayout("Development");

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Development");
        result.Description.Should().Be("Dev setup");
        result.Placements.Should().HaveCount(1);

        var placementDto = result.Placements[0];
        placementDto.ProcessName.Should().Be("code");
        placementDto.TitleContains.Should().Be("Visual Studio");
        placementDto.MonitorIndex.Should().Be(1);
        placementDto.Position.X.Should().Be(0.0);
        placementDto.Position.Y.Should().Be(0.0);
        placementDto.Position.Width.Should().Be(0.5);
        placementDto.Position.Height.Should().Be(1.0);
        placementDto.LaunchCommand.Should().Be("code.exe");
        placementDto.Focus.Should().BeTrue();
        placementDto.Order.Should().Be(1);
    }

    #endregion

    #region SaveLayout Tests

    [Fact]
    public async Task SaveLayout_CallsServiceWithParameters()
    {
        // Arrange
        var capturedPreset = TestDataFactory.CreateLayoutPreset("test-layout");
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(capturedPreset);
        var sut = CreateSut();

        // Act
        await sut.SaveLayout(
            name: "test-layout",
            description: "Test description",
            includeProcesses: "chrome,code",
            excludeProcesses: "explorer");

        // Assert
        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync(
            "test-layout",
            "Test description",
            It.Is<string[]>(arr => arr.Contains("chrome") && arr.Contains("code")),
            It.Is<string[]>(arr => arr.Contains("explorer")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveLayout_ParsesIncludeProcesses()
    {
        // Arrange
        var capturedPreset = TestDataFactory.CreateLayoutPreset();
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(capturedPreset);
        var sut = CreateSut();

        // Act
        await sut.SaveLayout("test", includeProcesses: " chrome , code , notepad ");

        // Assert
        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.Is<string[]>(arr => arr.Length == 3 && arr[0] == "chrome" && arr[1] == "code" && arr[2] == "notepad"),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveLayout_ReturnsSuccessResult()
    {
        // Arrange
        var placements = new List<WindowPlacement>
        {
            TestDataFactory.CreateWindowPlacement(),
            TestDataFactory.CreateWindowPlacement()
        };
        var capturedPreset = TestDataFactory.CreateLayoutPreset(
            name: "captured-layout",
            placements: placements);
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(capturedPreset);
        var sut = CreateSut();

        // Act
        var result = await sut.SaveLayout("captured-layout");

        // Assert
        result.Success.Should().BeTrue();
        result.LayoutName.Should().Be("captured-layout");
        result.WindowCount.Should().Be(2);
    }

    [Fact]
    public async Task SaveLayout_WithNullFilters_PassesNull()
    {
        // Arrange
        var capturedPreset = TestDataFactory.CreateLayoutPreset();
        _mockLayoutService.Setup(l => l.CaptureCurrentLayoutAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string[]?>(),
            It.IsAny<string[]?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(capturedPreset);
        var sut = CreateSut();

        // Act
        await sut.SaveLayout("test");

        // Assert
        _mockLayoutService.Verify(l => l.CaptureCurrentLayoutAsync(
            "test",
            null,
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ApplyLayout Tests

    [Fact]
    public async Task ApplyLayout_CallsServiceWithParameters()
    {
        // Arrange
        var applyResult = new LayoutApplyResult(
            Success: true,
            PresetName: "test",
            WindowsArranged: 5,
            WindowsNotFound: 0,
            WindowsLaunched: 0,
            Errors: new List<string>());
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(applyResult);
        var sut = CreateSut();

        // Act
        await sut.ApplyLayout(
            name: "test-layout",
            matchBy: "process_only",
            launchMissing: true);

        // Assert
        _mockLayoutService.Verify(l => l.ApplyPresetAsync(
            "test-layout",
            "process_only",
            true,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyLayout_WithDefaults_UsesDefaultValues()
    {
        // Arrange
        var applyResult = new LayoutApplyResult(
            Success: true,
            PresetName: "test",
            WindowsArranged: 0,
            WindowsNotFound: 0,
            WindowsLaunched: 0,
            Errors: null);
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(applyResult);
        var sut = CreateSut();

        // Act
        await sut.ApplyLayout("test");

        // Assert
        _mockLayoutService.Verify(l => l.ApplyPresetAsync(
            "test",
            "process_and_title",
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyLayout_MapsResultToDto()
    {
        // Arrange
        var applyResult = new LayoutApplyResult(
            Success: true,
            PresetName: "dev-setup",
            WindowsArranged: 4,
            WindowsNotFound: 1,
            WindowsLaunched: 2,
            Errors: new List<string> { "Warning: something" });
        _mockLayoutService.Setup(l => l.ApplyPresetAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(applyResult);
        var sut = CreateSut();

        // Act
        var result = await sut.ApplyLayout("dev-setup");

        // Assert
        result.Success.Should().BeTrue();
        result.PresetName.Should().Be("dev-setup");
        result.WindowsArranged.Should().Be(4);
        result.WindowsNotFound.Should().Be(1);
        result.WindowsLaunched.Should().Be(2);
        result.Errors.Should().HaveCount(1);
        result.Errors![0].Should().Be("Warning: something");
    }

    #endregion

    #region DeleteLayout Tests

    [Fact]
    public async Task DeleteLayout_CallsService()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.DeletePresetAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = CreateSut();

        // Act
        await sut.DeleteLayout("to-delete");

        // Assert
        _mockLayoutService.Verify(l => l.DeletePresetAsync(
            "to-delete",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteLayout_ReturnsSuccess_WhenDeleted()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.DeletePresetAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = CreateSut();

        // Act
        var result = await sut.DeleteLayout("test");

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task DeleteLayout_ReturnsFailure_WhenNotDeleted()
    {
        // Arrange
        _mockLayoutService.Setup(l => l.DeletePresetAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = CreateSut();

        // Act
        var result = await sut.DeleteLayout("test");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    #endregion
}
