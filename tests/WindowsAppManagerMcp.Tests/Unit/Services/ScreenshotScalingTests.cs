using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Behavioral tests for maxWidth scaling, save-to-file, and annotated coordinates.
/// </summary>
public class ScreenshotScalingTests
{
    private const string MaxWidthMustBePositive = "maxWidth must be a positive number of pixels.";

    private readonly Mock<IScreenCaptureWrapper> _mockCaptureWrapper;
    private readonly Mock<IMonitorService> _mockMonitorService;

    public ScreenshotScalingTests()
    {
        _mockCaptureWrapper = TestDataFactory.CreateMockScreenCaptureWrapper();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
    }

    private ScreenshotService CreateSut() =>
        new(_mockCaptureWrapper.Object, _mockMonitorService.Object);

    [Fact]
    public void CaptureWindow_WithMaxWidth_ScalesDownAndKeepsAspectRatio()
    {
        // Arrange — default window capture is 1920x1080
        var sut = CreateSut();

        // Act
        var result = sut.CaptureWindow(new nint(12345), maxWidth: 1000);

        // Assert
        result.Success.Should().BeTrue();
        result.Width.Should().Be(1000);
        result.Height.Should().Be(562);
        result.OriginalWidth.Should().Be(1920);
        result.OriginalHeight.Should().Be(1080);
        result.CapturedRegion.Width.Should().Be(1920);
        result.CapturedRegion.Height.Should().Be(1080);
        result.FilePath.Should().BeNull();
        DecodePng(result.ImageData!).Should().Be((1000, 562));
    }

    [Fact]
    public void CaptureMonitor_WithMaxWidth_DecodesAtScaledWidth()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(boundsWidth: 800, boundsHeight: 600);
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        var sut = CreateSut();

        // Act
        var result = sut.CaptureMonitor(0, maxWidth: 400);

        // Assert
        result.Success.Should().BeTrue();
        result.Width.Should().Be(400);
        result.Height.Should().Be(300);
        result.OriginalWidth.Should().Be(800);
        result.OriginalHeight.Should().Be(600);
        result.CapturedRegion.Width.Should().Be(800);
        result.CapturedRegion.Height.Should().Be(600);
        DecodePng(result.ImageData!).Width.Should().Be(400);
    }

    [Fact]
    public void CaptureRegion_WhenMaxWidthIsNotSmaller_LeavesSizeAndOriginalsUnset()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var atWidth = sut.CaptureRegion(0, 0, 80, 40, maxWidth: 80);
        var widerLimit = sut.CaptureRegion(0, 0, 80, 40, maxWidth: 200);
        var unset = sut.CaptureRegion(0, 0, 80, 40);

        // Assert
        atWidth.Width.Should().Be(80);
        atWidth.Height.Should().Be(40);
        atWidth.OriginalWidth.Should().BeNull();
        atWidth.OriginalHeight.Should().BeNull();
        widerLimit.OriginalWidth.Should().BeNull();
        unset.OriginalWidth.Should().BeNull();
        DecodePng(unset.ImageData!).Should().Be((80, 40));
    }

    [Fact]
    public void CaptureRegion_SaveToFileAfterResize_WritesScaledFileAndOmitsBase64()
    {
        // Arrange
        var directory = CreateTempDirectory();
        var outputPath = Path.Combine(directory, "nested", "shot.png");
        var sut = CreateSut();

        try
        {
            // Act
            var result = sut.CaptureRegion(0, 0, 200, 100, saveToFile: true, outputPath: outputPath, maxWidth: 100);

            // Assert
            result.Success.Should().BeTrue();
            result.ImageData.Should().BeNull();
            result.FilePath.Should().Be(outputPath);
            result.Width.Should().Be(100);
            result.Height.Should().Be(50);
            result.OriginalWidth.Should().Be(200);
            result.OriginalHeight.Should().Be(100);
            File.Exists(outputPath).Should().BeTrue();
            DecodeFile(outputPath).Should().Be((100, 50));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CaptureRegion_SaveToFileWithoutPath_WritesTempPng()
    {
        // Arrange
        var sut = CreateSut();
        string? filePath = null;

        try
        {
            // Act
            var result = sut.CaptureRegion(0, 0, 20, 10, saveToFile: true);

            // Assert
            result.Success.Should().BeTrue();
            result.ImageData.Should().BeNull();
            filePath = result.FilePath;
            filePath.Should().NotBeNullOrEmpty();
            filePath.Should().EndWith(".png");
            File.Exists(filePath).Should().BeTrue();
        }
        finally
        {
            if (filePath != null && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public void CaptureRegion_WhenNotSaving_IgnoresOutputPath()
    {
        // Arrange
        var directory = CreateTempDirectory();
        var outputPath = Path.Combine(directory, "ignored.png");
        var sut = CreateSut();

        try
        {
            // Act
            var result = sut.CaptureRegion(0, 0, 20, 10, saveToFile: false, outputPath: outputPath);

            // Assert
            result.Success.Should().BeTrue();
            result.FilePath.Should().BeNull();
            result.ImageData.Should().NotBeNullOrEmpty();
            File.Exists(outputPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CaptureMethods_WithNonPositiveMaxWidth_RejectBeforeCapture(int maxWidth)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var monitor = sut.CaptureMonitor(0, maxWidth: maxWidth);
        var region = sut.CaptureRegion(0, 0, 100, 50, maxWidth: maxWidth);
        var window = sut.CaptureWindow(new nint(12345), maxWidth: maxWidth);
        var annotated = sut.CaptureWindowWithElements(
            new nint(12345),
            Array.Empty<UIElementInfo>(),
            (0, 0, 100, 50),
            maxWidth: maxWidth);

        // Assert
        monitor.Success.Should().BeFalse();
        monitor.Error.Should().Be(MaxWidthMustBePositive);
        region.Error.Should().Be(MaxWidthMustBePositive);
        window.Error.Should().Be(MaxWidthMustBePositive);
        annotated.Error.Should().Be(MaxWidthMustBePositive);
        _mockCaptureWrapper.Verify(
            c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
        _mockCaptureWrapper.Verify(
            c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public void CaptureRegion_WhenScaledHeightCollapses_ReturnsMaxWidthError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.CaptureRegion(0, 0, 1920, 10, maxWidth: 100);

        // Assert
        result.Success.Should().BeFalse();
        result.ImageData.Should().BeNull();
        result.Error.Should().Be("maxWidth 100 would scale a 1920x10 image to height 0.");
    }

    [Fact]
    public void CaptureWindowWithElements_ScalesImageBoundsAndKeepsScreenBounds()
    {
        // Arrange
        UseWindowCapture(200, 100);
        var sut = CreateSut();
        var element = ElementAt(10, 20, 40, 30);

        // Act
        var result = sut.CaptureWindowWithElements(
            new nint(12345),
            new[] { element },
            (0, 0, 200, 100),
            maxWidth: 100);

        // Assert
        result.Success.Should().BeTrue();
        result.Width.Should().Be(100);
        result.Height.Should().Be(50);
        result.OriginalWidth.Should().Be(200);
        result.OriginalHeight.Should().Be(100);
        result.ElementCount.Should().Be(1);
        var annotated = result.Elements.Should().ContainSingle().Subject;
        annotated.ImageBounds.Should().Be(new BoundsDto(5, 10, 20, 15));
        annotated.ScreenBounds.Should().Be(element.Bounds);
    }

    [Fact]
    public void CaptureWindowWithElements_SmallElementStaysAtLeastOnePixel()
    {
        // Arrange — 200px wide scaled to 80 is 0.4; a 2px box truncates to 0 without a minimum
        UseWindowCapture(200, 100);
        var sut = CreateSut();
        var element = ElementAt(4, 6, 2, 2);

        // Act
        var result = sut.CaptureWindowWithElements(
            new nint(12345),
            new[] { element },
            (0, 0, 200, 100),
            maxWidth: 80);

        // Assert
        result.Success.Should().BeTrue();
        var annotated = result.Elements.Should().ContainSingle().Subject;
        annotated.ImageBounds.Should().Be(new BoundsDto(2, 2, 1, 1));
        annotated.ScreenBounds.Should().Be(element.Bounds);
    }

    private void UseWindowCapture(int width, int height)
    {
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns((new byte[width * height * 4], width, height));
    }

    private static UIElementInfo ElementAt(int x, int y, int width, int height) =>
        new(
            Name: "OK",
            ControlType: "Button",
            AutomationId: "ok",
            Bounds: new BoundsDto(x, y, width, height),
            IsEnabled: true,
            IsOffscreen: false);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "wam-screenshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static (int Width, int Height) DecodePng(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        using var stream = new MemoryStream(bytes);
        using var bitmap = new Bitmap(stream);
        return (bitmap.Width, bitmap.Height);
    }

    private static (int Width, int Height) DecodeFile(string path)
    {
        using var bitmap = new Bitmap(path);
        return (bitmap.Width, bitmap.Height);
    }
}
