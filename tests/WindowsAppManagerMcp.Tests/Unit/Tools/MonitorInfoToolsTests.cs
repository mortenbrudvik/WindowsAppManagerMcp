namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for MonitorInfoTools.
/// Tests the MCP tool layer that exposes monitor information functionality.
/// </summary>
public class MonitorInfoToolsTests
{
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<IScreenCaptureWrapper> _screen = new();

    public MonitorInfoToolsTests()
    {
        _mockMonitorService = new Mock<IMonitorService>();
        _screen.Setup(s => s.GetVirtualScreenBounds()).Returns((0, 0, 1920, 1080));
    }

    private MonitorInfoTools CreateSut() => new MonitorInfoTools(_mockMonitorService.Object, _screen.Object);

    #region GetMonitors Tests

    [Fact]
    public void GetMonitors_ReturnsAllMonitors()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.GetMonitors();

        // Assert
        result.Monitors.Should().HaveCount(2);
    }

    [Fact]
    public void GetMonitors_ReturnsPrimaryIndex()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.GetMonitors();

        // Assert
        result.PrimaryIndex.Should().Be(0);
    }

    [Fact]
    public void GetMonitors_MapsMonitorInfoToDto()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            index: 0,
            deviceName: @"\\.\DISPLAY1",
            isPrimary: true,
            boundsX: 0, boundsY: 0,
            boundsWidth: 1920, boundsHeight: 1080,
            workAreaX: 0, workAreaY: 0,
            workAreaWidth: 1920, workAreaHeight: 1040,
            scaleFactor: 1.25);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.GetMonitors();

        // Assert
        var dto = result.Monitors[0];
        dto.Index.Should().Be(0);
        dto.Name.Should().Be(@"\\.\DISPLAY1");
        dto.IsPrimary.Should().BeTrue();
        dto.Bounds.X.Should().Be(0);
        dto.Bounds.Y.Should().Be(0);
        dto.Bounds.Width.Should().Be(1920);
        dto.Bounds.Height.Should().Be(1080);
        dto.WorkArea.X.Should().Be(0);
        dto.WorkArea.Y.Should().Be(0);
        dto.WorkArea.Width.Should().Be(1920);
        dto.WorkArea.Height.Should().Be(1040);
        dto.ScaleFactor.Should().Be(1.25);
    }

    [Fact]
    public void GetMonitors_HandlesSingleMonitor()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo();
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.GetMonitors();

        // Assert
        result.Monitors.Should().HaveCount(1);
        result.PrimaryIndex.Should().Be(0);
    }

    [Fact]
    public void GetMonitors_HandlesEmptyList()
    {
        // Arrange
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo>());
        var sut = CreateSut();

        // Act
        var result = sut.GetMonitors();

        // Assert
        result.Monitors.Should().BeEmpty();
        result.PrimaryIndex.Should().Be(0);
    }

    [Fact]
    public void GetMonitors_ReportsTheVirtualScreen()
    {
        // Arrange - roadmap M6.1: the bounds list_screens reports, the area take_screenshot captures with monitorIndex -1
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(TestDataFactory.CreateDualMonitorSetup());
        _screen.Setup(s => s.GetVirtualScreenBounds()).Returns((-1920, -120, 4480, 1560));

        // Act
        var result = CreateSut().GetMonitors();

        // Assert
        result.VirtualScreen.Should().Be(new VirtualScreenBounds(-1920, -120, 4480, 1560));
    }

    [Fact]
    public void GetMonitors_ReportsTheVirtualScreen_AsListScreensDoes()
    {
        // Arrange - the same monitors and capture wrapper behind both tools
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        _screen.Setup(s => s.GetVirtualScreenBounds()).Returns((0, -300, 4480, 1740));
        var screenshots = new ScreenshotService(_screen.Object, _mockMonitorService.Object, Mock.Of<INativeWindowWrapper>());

        // Act
        var monitorsResult = CreateSut().GetMonitors();
        var screens = screenshots.ListScreens();

        // Assert
        monitorsResult.VirtualScreen.Should().Be(screens.VirtualScreen);
        monitorsResult.PrimaryIndex.Should().Be(screens.PrimaryIndex);
        monitorsResult.Monitors.Select(m => m.Index).Should().Equal(screens.Screens.Select(s => s.Index));
    }

    [Fact]
    public void GetMonitors_FindsCorrectPrimaryIndex_WhenNotFirst()
    {
        // Arrange
        var monitors = new List<MonitorInfo>
        {
            TestDataFactory.CreateMonitorInfo(index: 0, isPrimary: false),
            TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: true),
            TestDataFactory.CreateMonitorInfo(index: 2, isPrimary: false)
        };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.GetMonitors();

        // Assert
        result.PrimaryIndex.Should().Be(1);
    }

    #endregion

    #region GetPrimaryMonitor Tests

    [Fact]
    public void GetPrimaryMonitor_CallsService()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(isPrimary: true);
        _mockMonitorService.Setup(m => m.GetPrimaryMonitor()).Returns(monitor);
        var sut = CreateSut();

        // Act
        sut.GetPrimaryMonitor();

        // Assert
        _mockMonitorService.Verify(m => m.GetPrimaryMonitor(), Times.Once);
    }

    [Fact]
    public void GetPrimaryMonitor_ReturnsPrimaryMonitorDto()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            index: 0,
            deviceName: @"\\.\DISPLAY1",
            isPrimary: true);
        _mockMonitorService.Setup(m => m.GetPrimaryMonitor()).Returns(monitor);
        var sut = CreateSut();

        // Act
        var result = sut.GetPrimaryMonitor();

        // Assert
        result.Index.Should().Be(0);
        result.Name.Should().Be(@"\\.\DISPLAY1");
        result.IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void GetPrimaryMonitor_MapsAllProperties()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 0, boundsY: 0,
            boundsWidth: 2560, boundsHeight: 1440,
            workAreaX: 0, workAreaY: 0,
            workAreaWidth: 2560, workAreaHeight: 1400,
            scaleFactor: 1.5);
        _mockMonitorService.Setup(m => m.GetPrimaryMonitor()).Returns(monitor);
        var sut = CreateSut();

        // Act
        var result = sut.GetPrimaryMonitor();

        // Assert
        result.Bounds.Width.Should().Be(2560);
        result.Bounds.Height.Should().Be(1440);
        result.WorkArea.Width.Should().Be(2560);
        result.WorkArea.Height.Should().Be(1400);
        result.ScaleFactor.Should().Be(1.5);
    }

    #endregion
}
