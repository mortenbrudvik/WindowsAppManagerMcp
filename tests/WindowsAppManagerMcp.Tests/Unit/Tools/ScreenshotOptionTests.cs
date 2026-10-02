namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Tool-layer checks that save and maxWidth options are forwarded, and that an
/// invalid maxWidth does not walk the UI tree.
/// </summary>
public class ScreenshotOptionTests
{
    private const string MaxWidthMustBePositive = "maxWidth must be a positive number of pixels.";
    private const string OutputPath = @"C:\shots\screen.png";

    private readonly Mock<IScreenshotService> _mockScreenshotService;
    private readonly Mock<IUIElementService> _mockUIElementService;
    private readonly Mock<IWindowService> _mockWindowService;

    public ScreenshotOptionTests()
    {
        _mockScreenshotService = TestDataFactory.CreateMockScreenshotService();
        _mockUIElementService = TestDataFactory.CreateMockUIElementService();
        _mockWindowService = TestDataFactory.CreateMockWindowService();
    }

    private ScreenshotTools CreateSut() => new(
        _mockScreenshotService.Object,
        _mockUIElementService.Object,
        _mockWindowService.Object);

    [Fact]
    public void TakeScreenshot_ForwardsSaveOptionsAndMaxWidth()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(monitorIndex: 0, saveToFile: true, outputPath: OutputPath, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(
            s => s.CaptureMonitor(0, "png", 85, true, OutputPath, 800),
            Times.Once);
    }

    [Fact]
    public void CaptureRegion_ForwardsSaveOptionsAndMaxWidth()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(10, 20, 300, 200, saveToFile: true, outputPath: OutputPath, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(
            s => s.CaptureRegion(10, 20, 300, 200, "png", 85, true, OutputPath, 800),
            Times.Once);
    }

    [Fact]
    public void CaptureWindow_ForwardsSaveOptionsAndMaxWidth()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(12345, saveToFile: true, outputPath: OutputPath, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(
            s => s.CaptureWindow(new nint(12345), true, "png", 85, true, OutputPath, 800),
            Times.Once);
    }

    [Fact]
    public void CaptureWithElements_ForwardsSaveOptionsAndMaxWidth()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWithElements(12345, saveToFile: true, outputPath: OutputPath, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            new nint(12345),
            It.IsAny<IReadOnlyList<UIElementInfo>>(),
            It.IsAny<(int, int, int, int)>(),
            true,
            "png",
            85,
            true,
            true,
            OutputPath,
            800), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void CaptureWithElements_WithNonPositiveMaxWidth_DoesNotWalkUiTree(int maxWidth)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWithElements(12345, maxWidth: maxWidth);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be(MaxWidthMustBePositive);
        _mockWindowService.Verify(w => w.GetWindowInfo(It.IsAny<nint>()), Times.Never);
        _mockUIElementService.Verify(
            u => u.GetUIElements(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            It.IsAny<nint>(),
            It.IsAny<IReadOnlyList<UIElementInfo>>(),
            It.IsAny<(int, int, int, int)>(),
            It.IsAny<bool>(),
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<string?>(),
            It.IsAny<int?>()), Times.Never);
    }
}
