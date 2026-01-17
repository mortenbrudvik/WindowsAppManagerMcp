namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for InputSimulationService.
/// Tests coordinate validation and service behavior.
/// </summary>
public class InputSimulationServiceTests
{
    private readonly Mock<IMonitorService> _mockMonitorService;

    public InputSimulationServiceTests()
    {
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
    }

    private InputSimulationService CreateSut() =>
        new InputSimulationService(_mockMonitorService.Object);

    #region IsValidScreenCoordinate Tests

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesInsideMonitor_ReturnsTrue()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(960, 540);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesAtTopLeftCorner_ReturnsTrue()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(0, 0);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesAtBottomRightEdge_ReturnsFalse()
    {
        // Arrange - coordinate at exact edge should be outside
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act - x=1920 is outside since valid range is 0-1919
        var result = sut.IsValidScreenCoordinate(1920, 1080);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesJustInsideEdge_ReturnsTrue()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act - x=1919 is inside since valid range is 0-1919
        var result = sut.IsValidScreenCoordinate(1919, 1079);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithNegativeCoordinates_ReturnsFalseForStandardMonitor()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(-100, -100);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithNegativeCoordinates_ReturnsTrueForLeftOfPrimaryMonitor()
    {
        // Arrange - monitor positioned to the left of primary has negative X bounds
        var leftMonitor = TestDataFactory.CreateMonitorInfo(
            index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: false,
            boundsX: -1920, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        var primaryMonitor = TestDataFactory.CreateMonitorInfo(
            index: 0, deviceName: @"\\.\DISPLAY1", isPrimary: true,
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { primaryMonitor, leftMonitor });
        var sut = CreateSut();

        // Act
        var result = sut.IsValidScreenCoordinate(-960, 540);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesOnSecondMonitor_ReturnsTrue()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act - coordinate on second monitor (starts at x=1920)
        var result = sut.IsValidScreenCoordinate(2500, 500);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidScreenCoordinate_WithCoordinatesBetweenMonitors_ReturnsFalse()
    {
        // Arrange - gap between monitors
        var monitor1 = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        var monitor2 = TestDataFactory.CreateMonitorInfo(
            index: 1, boundsX: 2000, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor1, monitor2 });
        var sut = CreateSut();

        // Act - coordinate in the gap
        var result = sut.IsValidScreenCoordinate(1950, 500);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Click Tests - Coordinate Validation

    [Fact]
    public void Click_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.Click(5000, 5000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.Error.Should().Contain("outside screen bounds");
        result.ClickType.Should().Be("left");
    }

    [Fact]
    public void Click_ReturnsCorrectCoordinatesInResult()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act - even if click fails due to SendInput, coordinates should be correct
        var result = sut.Click(500, 300);

        // Assert
        result.X.Should().Be(500);
        result.Y.Should().Be(300);
        result.ClickType.Should().Be("left");
    }

    #endregion

    #region RightClick Tests - Coordinate Validation

    [Fact]
    public void RightClick_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.RightClick(-100, -100);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.ClickType.Should().Be("right");
    }

    [Fact]
    public void RightClick_ReturnsCorrectCoordinatesInResult()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.RightClick(750, 400);

        // Assert
        result.X.Should().Be(750);
        result.Y.Should().Be(400);
        result.ClickType.Should().Be("right");
    }

    #endregion

    #region DoubleClick Tests - Coordinate Validation

    [Fact]
    public void DoubleClick_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(10000, 10000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.ClickType.Should().Be("double");
    }

    [Fact]
    public void DoubleClick_ReturnsCorrectCoordinatesInResult()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.DoubleClick(600, 350);

        // Assert
        result.X.Should().Be(600);
        result.Y.Should().Be(350);
        result.ClickType.Should().Be("double");
    }

    [Fact]
    public void DoubleClick_WithDefaultDelay_UsesDefaultValue()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act - just verify it doesn't throw with default delay
        var result = sut.DoubleClick(500, 500);

        // Assert - coordinates should still be correct
        result.X.Should().Be(500);
        result.Y.Should().Be(500);
    }

    #endregion

    #region MoveMouse Tests - Coordinate Validation

    [Fact]
    public void MoveMouse_WithInvalidCoordinates_ReturnsError()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(3000, 3000);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.Error.Should().Contain("outside screen bounds");
    }

    [Fact]
    public void MoveMouse_ReturnsCorrectCoordinatesInResult()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.MoveMouse(800, 600);

        // Assert
        result.X.Should().Be(800);
        result.Y.Should().Be(600);
    }

    #endregion
}
