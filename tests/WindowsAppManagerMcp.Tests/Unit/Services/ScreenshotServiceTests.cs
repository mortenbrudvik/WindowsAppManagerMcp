namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ScreenshotService.
/// Tests T9.2 from BACKLOG-TECHNICAL.md.
/// </summary>
public class ScreenshotServiceTests
{
    private readonly Mock<IScreenCaptureWrapper> _mockCaptureWrapper;
    private readonly Mock<IMonitorService> _mockMonitorService;

    public ScreenshotServiceTests()
    {
        _mockCaptureWrapper = TestDataFactory.CreateMockScreenCaptureWrapper();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
    }

    private ScreenshotService CreateSut() =>
        new ScreenshotService(_mockCaptureWrapper.Object, _mockMonitorService.Object);

    #region T9.2.1-T9.2.2: ListScreens Tests

    [Fact]
    public void ListScreens_ReturnsAllMonitors()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds())
            .Returns((0, 0, 4480, 1440));
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.Screens.Should().HaveCount(2);
    }

    [Fact]
    public void ListScreens_ReturnsPrimaryIndex()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.PrimaryIndex.Should().Be(0);
    }

    [Fact]
    public void ListScreens_IncludesVirtualScreenBounds()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        _mockCaptureWrapper.Setup(c => c.GetVirtualScreenBounds())
            .Returns((-1920, 0, 3840, 1080));
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.VirtualScreen.Should().NotBeNull();
        result.VirtualScreen.X.Should().Be(-1920);
        result.VirtualScreen.Width.Should().Be(3840);
    }

    [Fact]
    public void ListScreens_MapsScreenInfoCorrectly()
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
        var result = sut.ListScreens();

        // Assert
        var screen = result.Screens[0];
        screen.Index.Should().Be(0);
        screen.Name.Should().Be(@"\\.\DISPLAY1");
        screen.IsPrimary.Should().BeTrue();
        screen.X.Should().Be(0);
        screen.Y.Should().Be(0);
        screen.Width.Should().Be(1920);
        screen.Height.Should().Be(1080);
        screen.WorkAreaWidth.Should().Be(1920);
        screen.WorkAreaHeight.Should().Be(1040);
        screen.ScaleFactor.Should().Be(1.25);
    }

    #endregion

    #region T9.2.3-T9.2.6: CaptureMonitor Tests

    [Fact]
    public void CaptureMonitor_WithValidIndex_ReturnsSuccess()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0);

        // Assert
        result.Success.Should().BeTrue();
        result.ImageData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CaptureMonitor_WithInvalidIndex_ReturnsError()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(5);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("out of range");
    }

    [Fact]
    public void CaptureMonitor_CallsCaptureWrapperWithCorrectBounds()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsX: 100, boundsY: 50,
            boundsWidth: 1920, boundsHeight: 1080);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        sut.CaptureMonitor(0);

        // Assert
        _mockCaptureWrapper.Verify(c => c.CaptureScreenRegion(100, 50, 1920, 1080), Times.Once);
    }

    [Fact]
    public void CaptureMonitor_WithPngFormat_ReturnsPngImage()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0, "png");

        // Assert
        result.ImageFormat.Should().Be("png");
    }

    [Fact]
    public void CaptureMonitor_WithJpegFormat_ReturnsJpegImage()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0, "jpeg");

        // Assert
        result.ImageFormat.Should().Be("jpeg");
    }

    [Fact]
    public void CaptureMonitor_ReturnsCorrectDimensions()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(
            boundsWidth: 2560, boundsHeight: 1440);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        // Set up capture wrapper to return appropriately sized data
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(
                It.IsAny<int>(), It.IsAny<int>(), 2560, 1440))
            .Returns(new byte[2560 * 1440 * 4]);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0);

        // Assert
        result.Width.Should().Be(2560);
        result.Height.Should().Be(1440);
    }

    [Fact]
    public void CaptureMonitor_IncludesScaleFactor()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(scaleFactor: 1.5);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0);

        // Assert
        result.ScaleFactor.Should().Be(1.5);
    }

    [Fact]
    public void CaptureMonitor_IncludesMonitorIndex()
    {
        // Arrange
        var monitors = TestDataFactory.CreateDualMonitorSetup();
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(1);

        // Assert
        result.MonitorIndex.Should().Be(1);
    }

    #endregion

    #region T9.2.7-T9.2.8: CaptureRegion Tests

    [Fact]
    public void CaptureRegion_WithValidDimensions_ReturnsSuccess()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureRegion(100, 100, 800, 600);

        // Assert
        result.Success.Should().BeTrue();
        result.ImageData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CaptureRegion_CallsCaptureWrapperWithCorrectCoordinates()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(150, 200, 640, 480);

        // Assert
        _mockCaptureWrapper.Verify(c => c.CaptureScreenRegion(150, 200, 640, 480), Times.Once);
    }

    [Fact]
    public void CaptureRegion_WithZeroWidth_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureRegion(100, 100, 0, 600);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("positive");
    }

    [Fact]
    public void CaptureRegion_WithNegativeHeight_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureRegion(100, 100, 800, -100);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("positive");
    }

    [Fact]
    public void CaptureRegion_IncludesCapturedRegion()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureRegion(50, 75, 400, 300);

        // Assert
        result.CapturedRegion.X.Should().Be(50);
        result.CapturedRegion.Y.Should().Be(75);
        result.CapturedRegion.Width.Should().Be(400);
        result.CapturedRegion.Height.Should().Be(300);
    }

    [Fact]
    public void CaptureRegion_DeterminesMonitorFromCenter()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo();
        _mockMonitorService.Setup(m => m.GetMonitorAt(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(monitor);
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(100, 100, 400, 300);

        // Assert - center would be at (300, 250)
        _mockMonitorService.Verify(m => m.GetMonitorAt(300, 250), Times.Once);
    }

    #endregion

    #region T9.2.9-T9.2.10: CaptureWindow Tests

    [Fact]
    public void CaptureWindow_WithValidHandle_ReturnsSuccess()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(new nint(12345));

        // Assert
        result.Success.Should().BeTrue();
        result.ImageData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CaptureWindow_WithInvalidHandle_ReturnsError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid window handle");
    }

    [Fact]
    public void CaptureWindow_CallsCaptureWrapperWithCorrectParameters()
    {
        // Arrange
        var handle = new nint(12345);
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle, includeFrame: true);

        // Assert
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(handle, true), Times.Once);
    }

    [Fact]
    public void CaptureWindow_WithIncludeFrameFalse_PassesCorrectFlag()
    {
        // Arrange
        var handle = new nint(12345);
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle, includeFrame: false);

        // Assert
        _mockCaptureWrapper.Verify(c => c.CaptureWindow(handle, false), Times.Once);
    }

    [Fact]
    public void CaptureWindow_ReturnsCorrectDimensionsFromWrapper()
    {
        // Arrange
        var handle = new nint(12345);
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(handle, It.IsAny<bool>()))
            .Returns((new byte[1024 * 768 * 4], 1024, 768));
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(handle);

        // Assert
        result.Width.Should().Be(1024);
        result.Height.Should().Be(768);
    }

    [Fact]
    public void CaptureWindow_GetsMonitorForWindow()
    {
        // Arrange
        var handle = new nint(12345);
        var monitor = TestDataFactory.CreateMonitorInfo(index: 1, scaleFactor: 1.25);
        _mockMonitorService.Setup(m => m.GetMonitorForWindow(handle))
            .Returns(monitor);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(handle);

        // Assert
        result.MonitorIndex.Should().Be(1);
        result.ScaleFactor.Should().Be(1.25);
    }

    #endregion

    #region T9.2.11-T9.2.12: Image Encoding Tests

    [Fact]
    public void CaptureMonitor_ProducesValidBase64()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0);

        // Assert
        result.ImageData.Should().NotBeNullOrEmpty();
        // Verify it's valid base64
        var action = () => Convert.FromBase64String(result.ImageData!);
        action.Should().NotThrow();
    }

    [Fact]
    public void CaptureMonitor_DefaultsToUnknownFormatAsPng()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0, "unknown_format");

        // Assert
        result.ImageFormat.Should().Be("png");
    }

    [Fact]
    public void CaptureMonitor_ClampsQualityToValidRange()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        var sut = CreateSut();

        // Act - quality out of range should not throw
        var result = sut.CaptureMonitor(0, "jpeg", quality: 150);

        // Assert - should succeed (quality clamped internally)
        result.Success.Should().BeTrue();
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void CaptureMonitor_WhenCaptureThrows_ReturnsError()
    {
        // Arrange
        var monitors = new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo() };
        _mockMonitorService.Setup(m => m.GetAllMonitors()).Returns(monitors);
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new InvalidOperationException("Capture failed"));
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Capture failed");
    }

    [Fact]
    public void CaptureWindow_WhenCaptureThrows_ReturnsError()
    {
        // Arrange
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("Window capture failed"));
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(new nint(12345));

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Window capture failed");
    }

    #endregion
}
