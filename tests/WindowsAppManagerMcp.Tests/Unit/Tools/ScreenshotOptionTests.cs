namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Tool-layer checks that every capture tool forwards the size, output and file options, including
/// the legacy maxWidth and saveToFile aliases, to the screenshot service unchanged.
/// </summary>
public class ScreenshotOptionTests
{
    private const string OutputPath = @"C:\shots\screen.png";

    private static readonly CaptureOptions Expected = new()
    {
        Format = "png",
        Quality = 60,
        MaxDimension = 1200,
        Output = "both",
        OutputPath = OutputPath,
        Overwrite = true,
        SaveToFile = true,
        MaxWidth = 800
    };

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
    public void TakeScreenshot_ForwardsAllOptions()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(monitorIndex: 0, format: "png", quality: 60, maxDimension: 1200, output: "both",
            outputPath: OutputPath, overwrite: true, saveToFile: true, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(0, Expected), Times.Once);
    }

    [Fact]
    public void CaptureRegion_ForwardsAllOptions()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureRegion(10, 20, 300, 200, format: "png", quality: 60, maxDimension: 1200, output: "both",
            outputPath: OutputPath, overwrite: true, saveToFile: true, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureRegion(10, 20, 300, 200, Expected), Times.Once);
    }

    [Fact]
    public void CaptureWindow_ForwardsAllOptions()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        sut.CaptureWindow(12345, format: "png", quality: 60, maxDimension: 1200, output: "both",
            outputPath: OutputPath, overwrite: true, saveToFile: true, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindow(new nint(12345), true, Expected), Times.Once);
    }

    [Fact]
    public void CaptureWithElements_ForwardsAllOptions()
    {
        // Arrange - this tool checks the options itself before walking the UI tree, and C:\shots does
        // not exist, so the call leaves outputPath out
        var sut = CreateSut();
        var options = Expected with { OutputPath = null };

        // Act
        sut.CaptureWithElements(12345, format: "png", quality: 60, maxDimension: 1200, output: "both",
            overwrite: true, saveToFile: true, maxWidth: 800);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureWindowWithElements(
            new nint(12345),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<UIElementInfo>>(),
            true,
            true,
            options), Times.Once);
    }

    [Fact]
    public void LegacyCall_WithSaveToFileAndMaxWidth_KeepsItsMeaning()
    {
        // Arrange - how a 1.12 client asked for a small file
        var sut = CreateSut();

        // Act
        sut.TakeScreenshot(saveToFile: true, maxWidth: 1280);

        // Assert
        _mockScreenshotService.Verify(s => s.CaptureMonitor(It.IsAny<int>(),
            It.Is<CaptureOptions>(o => o.SaveToFile && o.MaxWidth == 1280 && o.MaxDimension == null)), Times.Once);
        var request = CaptureRequest.Resolve(new CaptureOptions { SaveToFile = true, MaxWidth = 1280 });
        request.Output.Should().Be(CaptureOutputMode.File);
        request.MaxWidth.Should().Be(1280);
        request.MaxDimension.Should().BeNull("maxWidth alone keeps its old meaning: a width limit only");
    }
}
