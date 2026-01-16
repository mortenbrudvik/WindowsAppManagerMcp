namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for ScreenshotTools.
/// Tests T9.3 from BACKLOG-TECHNICAL.md.
/// </summary>
public class ScreenshotToolsTests
{
    private readonly Mock<IScreenshotService> _mockScreenshotService;

    public ScreenshotToolsTests()
    {
        _mockScreenshotService = TestDataFactory.CreateMockScreenshotService();
    }

    private ScreenshotTools CreateSut() => new ScreenshotTools(_mockScreenshotService.Object);

    #region T9.3.1: ListScreens Tests

    [Fact]
    public void ListScreens_CallsService()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.ListScreens();

        // Assert
        _mockScreenshotService.Verify(s => s.ListScreens(), Times.Once);
    }

    [Fact]
    public void ListScreens_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = TestDataFactory.CreateScreenListResult();
        _mockScreenshotService.Setup(s => s.ListScreens()).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void ListScreens_ReturnsMultipleScreens()
    {
        // Arrange
        var screens = new List<ScreenInfo>
        {
            TestDataFactory.CreateScreenInfo(0, true),
            TestDataFactory.CreateScreenInfo(1, false, x: 1920),
            TestDataFactory.CreateScreenInfo(2, false, x: 3840)
        };
        var expectedResult = TestDataFactory.CreateScreenListResult(screens, 0);
        _mockScreenshotService.Setup(s => s.ListScreens()).Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.ListScreens();

        // Assert
        result.Screens.Should().HaveCount(3);
    }

    #endregion

    #region T9.3.2-T9.3.4: TakeScreenshot Tests

    [Fact]
    public void TakeScreenshot_WithDefaults_UsesPrimaryMonitor()
    {
        // Arrange
        var screenList = TestDataFactory.CreateScreenListResult(primaryIndex: 1);
        _mockScreenshotService.Setup(s => s.ListScreens()).Returns(screenList);
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot();

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(1, "png", 85), Times.Once);
    }

    [Fact]
    public void TakeScreenshot_WithMonitorIndex_UsesSpecifiedMonitor()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(monitorIndex: 2);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(2, "png", 85), Times.Once);
    }

    [Fact]
    public void TakeScreenshot_WithFormat_PassesFormat()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(format: "jpeg");

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(It.IsAny<int>(), "jpeg", 85), Times.Once);
    }

    [Fact]
    public void TakeScreenshot_WithQuality_PassesQuality()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(format: "jpeg", quality: 95);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(It.IsAny<int>(), "jpeg", 95), Times.Once);
    }

    [Fact]
    public void TakeScreenshot_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = TestDataFactory.CreateScreenshotResult();
        _mockScreenshotService.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.TakeScreenshot();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void TakeScreenshot_WithAllParameters_PassesAllCorrectly()
    {
        // Arrange
        var screenList = TestDataFactory.CreateScreenListResult();
        _mockScreenshotService.Setup(s => s.ListScreens()).Returns(screenList);
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(monitorIndex: 1, format: "jpeg", quality: 75);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(1, "jpeg", 75), Times.Once);
    }

    #endregion

    #region T9.3.5: CaptureRegion Tests

    [Fact]
    public void CaptureRegion_CallsServiceWithCorrectParameters()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(100, 200, 800, 600);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureRegion(100, 200, 800, 600, "png", 85), Times.Once);
    }

    [Fact]
    public void CaptureRegion_WithFormat_PassesFormat()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(0, 0, 400, 300, format: "jpeg");

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureRegion(0, 0, 400, 300, "jpeg", 85), Times.Once);
    }

    [Fact]
    public void CaptureRegion_WithQuality_PassesQuality()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(0, 0, 400, 300, format: "jpeg", quality: 90);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureRegion(0, 0, 400, 300, "jpeg", 90), Times.Once);
    }

    [Fact]
    public void CaptureRegion_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = TestDataFactory.CreateScreenshotResult(
            width: 800, height: 600, regionX: 100, regionY: 200);
        _mockScreenshotService.Setup(s => s.CaptureRegion(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<int>()))
            .Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureRegion(100, 200, 800, 600);

        // Assert
        result.Should().Be(expectedResult);
        result.CapturedRegion.X.Should().Be(100);
        result.CapturedRegion.Y.Should().Be(200);
    }

    #endregion

    #region T9.3.6-T9.3.7: CaptureWindow Tests

    [Fact]
    public void CaptureWindow_CallsServiceWithCorrectHandle()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle: 12345);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(
            new nint(12345), true, "png", 85), Times.Once);
    }

    [Fact]
    public void CaptureWindow_WithIncludeFrameFalse_PassesCorrectFlag()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle: 12345, includeFrame: false);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(
            new nint(12345), false, "png", 85), Times.Once);
    }

    [Fact]
    public void CaptureWindow_WithFormat_PassesFormat()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle: 12345, format: "jpeg");

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(
            new nint(12345), true, "jpeg", 85), Times.Once);
    }

    [Fact]
    public void CaptureWindow_WithQuality_PassesQuality()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle: 12345, format: "jpeg", quality: 70);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(
            new nint(12345), true, "jpeg", 70), Times.Once);
    }

    [Fact]
    public void CaptureWindow_ReturnsServiceResult()
    {
        // Arrange
        var expectedResult = TestDataFactory.CreateScreenshotResult(width: 1024, height: 768);
        _mockScreenshotService.Setup(s => s.CaptureWindow(
                It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(expectedResult);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(handle: 12345);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void CaptureWindow_WithAllParameters_PassesAllCorrectly()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(handle: 99999, includeFrame: false, format: "jpeg", quality: 50);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(
            new nint(99999), false, "jpeg", 50), Times.Once);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void TakeScreenshot_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = TestDataFactory.CreateScreenshotResult(
            success: false, imageData: null, error: "Monitor not found");
        _mockScreenshotService.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.TakeScreenshot(monitorIndex: 99);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Monitor not found");
    }

    [Fact]
    public void CaptureWindow_WhenServiceReturnsError_ReturnsError()
    {
        // Arrange
        var errorResult = TestDataFactory.CreateScreenshotResult(
            success: false, imageData: null, error: "Invalid window handle");
        _mockScreenshotService.Setup(s => s.CaptureWindow(
                It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(errorResult);
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(handle: 0);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid window handle");
    }

    #endregion
}
