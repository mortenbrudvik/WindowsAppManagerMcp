using System.Drawing;
using System.Drawing.Imaging;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Behavioral tests for the vision-sized defaults: long-edge scaling, the legacy maxWidth limit, the
/// image format choice and encoding, and annotation scaling. Images are decoded to check that the
/// bytes match the reported size.
/// </summary>
public class ScreenshotScalingTests
{
    private readonly Mock<IScreenCaptureWrapper> _mockCaptureWrapper;
    private readonly Mock<IMonitorService> _mockMonitorService;

    public ScreenshotScalingTests()
    {
        _mockCaptureWrapper = TestDataFactory.CreateMockScreenCaptureWrapper();
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
    }

    private ScreenshotService CreateSut() =>
        new(_mockCaptureWrapper.Object, _mockMonitorService.Object);

    #region Size limits

    [Theory]
    [InlineData(3840, 2160, 1568, 882)] // landscape 4K
    [InlineData(1440, 2560, 882, 1568)] // portrait: the height is the long edge
    [InlineData(1920, 1080, 1568, 882)]
    [InlineData(1568, 900, 1568, 900)] // already within the limit
    [InlineData(800, 600, 800, 600)]
    public void CaptureMonitor_ByDefault_LimitsTheLongEdgeTo1568(int width, int height, int expectedWidth, int expectedHeight)
    {
        // Arrange
        UseMonitor(width, height);
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, new CaptureOptions());

        // Assert
        var metadata = capture.Metadata;
        metadata.Width.Should().Be(expectedWidth);
        metadata.Height.Should().Be(expectedHeight);
        Decode(capture.Image!).Should().Be((expectedWidth, expectedHeight));
        var scaled = expectedWidth != width;
        metadata.OriginalWidth.Should().Be(scaled ? width : null);
        metadata.OriginalHeight.Should().Be(scaled ? height : null);
        metadata.CapturedRegion.Should().Be(new CapturedRegion(0, 0, width, height));
        metadata.ImageToScreen!.ScaleX.Should().BeApproximately((double)width / expectedWidth, 1e-6);
        metadata.ImageToScreen.ScaleY.Should().BeApproximately((double)height / expectedHeight, 1e-6);
    }

    [Fact]
    public void CaptureMonitor_WithMaxDimensionZero_KeepsFullResolution()
    {
        // Arrange
        UseMonitor(3840, 2160);
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, new CaptureOptions { MaxDimension = 0 });

        // Assert
        capture.Metadata.Width.Should().Be(3840);
        capture.Metadata.Height.Should().Be(2160);
        capture.Metadata.OriginalWidth.Should().BeNull();
        capture.Metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(0, 0, 1, 1));
    }

    [Fact]
    public void CaptureWindow_WithMaxDimension_ScalesDownAndKeepsAspectRatio()
    {
        // Arrange — default window capture is 1920x1080
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureWindow(new nint(12345), includeFrame: true, new CaptureOptions { MaxDimension = 960 });

        // Assert
        var metadata = capture.Metadata;
        metadata.Success.Should().BeTrue();
        metadata.Width.Should().Be(960);
        metadata.Height.Should().Be(540);
        metadata.OriginalWidth.Should().Be(1920);
        metadata.OriginalHeight.Should().Be(1080);
        metadata.CapturedRegion!.Width.Should().Be(1920);
        metadata.CapturedRegion.Height.Should().Be(1080);
        metadata.FilePath.Should().BeNull();
        Decode(capture.Image!).Should().Be((960, 540));
    }

    [Fact]
    public void CaptureMonitor_WithOnlyLegacyMaxWidth_LimitsOnlyTheWidth()
    {
        // Arrange — before maxDimension existed, maxWidth was the only limit; a portrait monitor stays tall
        UseMonitor(1440, 2560);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, new CaptureOptions { MaxWidth = 1280 }).Metadata;

        // Assert
        metadata.Width.Should().Be(1280);
        metadata.Height.Should().Be(2276);
        metadata.OriginalWidth.Should().Be(1440);
    }

    [Fact]
    public void CaptureMonitor_WithMaxWidthWiderThanTheDefaultLongEdge_KeepsTheLegacyWidth()
    {
        // Arrange
        UseMonitor(3840, 2160);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, new CaptureOptions { MaxWidth = 2000 }).Metadata;

        // Assert
        metadata.Width.Should().Be(2000);
        metadata.Height.Should().Be(1125);
    }

    [Fact]
    public void CaptureMonitor_WithMaxDimensionAndMaxWidth_AppliesTheTighterLimit()
    {
        // Arrange
        UseMonitor(1920, 1080);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, new CaptureOptions { MaxDimension = 1000, MaxWidth = 500 }).Metadata;

        // Assert
        metadata.Width.Should().Be(500);
        metadata.Height.Should().Be(281);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CaptureMethods_WithNonPositiveMaxWidth_RejectBeforeCapture(int maxWidth)
    {
        // Arrange
        var sut = CreateSut();
        var options = new CaptureOptions { MaxWidth = maxWidth };

        // Act
        var monitor = sut.CaptureMonitor(0, options).Metadata;
        var region = sut.CaptureRegion(0, 0, 100, 50, options).Metadata;
        var window = sut.CaptureWindow(new nint(12345), includeFrame: true, options).Metadata;
        var annotated = sut.CaptureWindowWithElements(new nint(12345), null, [], includeFrame: true,
            highlightInteractable: true, options).Metadata;

        // Assert
        foreach (var (success, error, errorCode) in new[]
                 {
                     (monitor.Success, monitor.Error, monitor.ErrorCode),
                     (region.Success, region.Error, region.ErrorCode),
                     (window.Success, window.Error, window.ErrorCode),
                     (annotated.Success, annotated.Error, annotated.ErrorCode),
                 })
        {
            success.Should().BeFalse();
            error.Should().Be("maxWidth must be a positive number of pixels.");
            errorCode.Should().Be("InvalidImageSize");
        }

        VerifyNothingCaptured();
    }

    [Fact]
    public void CaptureMonitor_WithNegativeMaxDimension_RejectsBeforeCapture()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureMonitor(0, new CaptureOptions { MaxDimension = -1 }).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be("InvalidImageSize");
        metadata.Error.Should().Contain("maxDimension");
        VerifyNothingCaptured();
    }

    [Fact]
    public void CaptureRegion_WhenScaledHeightCollapses_KeepsOnePixelAndMapsEachAxisOnItsOwn()
    {
        // Arrange
        var sut = CreateSut();

        // Act - 1920 x 2 at a width of 100 would be 0.1 pixels high
        var capture = sut.CaptureRegion(0, 0, 1920, 2, new CaptureOptions { MaxWidth = 100 });

        // Assert
        capture.Metadata.Success.Should().BeTrue();
        capture.Metadata.Width.Should().Be(100);
        capture.Metadata.Height.Should().Be(1);
        capture.Metadata.ImageToScreen!.ScaleX.Should().Be(19.2);
        capture.Metadata.ImageToScreen.ScaleY.Should().Be(2);
    }

    #endregion

    #region Format

    [Fact]
    public void CaptureMonitor_ByDefault_ReturnsJpeg()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, new CaptureOptions());

        // Assert
        capture.Metadata.ImageFormat.Should().Be("jpeg");
        capture.Metadata.MimeType.Should().Be("image/jpeg");
        capture.Image!.MimeType.Should().Be("image/jpeg");
        capture.Image.Data.Take(2).Should().Equal(0xFF, 0xD8);
    }

    [Theory]
    [InlineData(512, 300, "png")]
    [InlineData(300, 512, "png")]
    [InlineData(513, 300, "jpeg")]
    [InlineData(40, 20, "png")]
    public void CaptureRegion_WithAutoFormat_UsesPngOnlyForSmallImages(int width, int height, string expected)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, width, height, new CaptureOptions());

        // Assert
        capture.Metadata.ImageFormat.Should().Be(expected);
        capture.Image!.MimeType.Should().Be($"image/{expected}");
        capture.Image.Data.Take(2).Should().Equal(expected == "png" ? new byte[] { 0x89, 0x50 } : new byte[] { 0xFF, 0xD8 });
    }

    [Fact]
    public void CaptureWindow_WhenScaledBelow512_UsesPngForTheOutputSize()
    {
        // Arrange - "auto" looks at the returned image, not at the captured area
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindow(new nint(12345), includeFrame: true, new CaptureOptions { MaxDimension = 400 }).Metadata;

        // Assert
        metadata.Width.Should().Be(400);
        metadata.ImageFormat.Should().Be("png");
    }

    [Theory]
    [InlineData("png", "png")]
    [InlineData("PNG", "png")]
    [InlineData("jpeg", "jpeg")]
    [InlineData("jpg", "jpeg")]
    [InlineData(" Jpg ", "jpeg")]
    public void CaptureMonitor_WithExplicitFormat_UsesIt(string format, string expected)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, new CaptureOptions { Format = format });

        // Assert
        capture.Metadata.ImageFormat.Should().Be(expected);
        capture.Metadata.MimeType.Should().Be($"image/{expected}");
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("gif")]
    [InlineData("bmp")]
    public void CaptureMonitor_WithUnknownFormat_ReturnsInvalidFormatWithoutCapturing(string format)
    {
        // Arrange - the old code silently fell back to PNG
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, new CaptureOptions { Format = format });

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be("InvalidFormat");
        capture.Metadata.Error.Should().Contain(format);
        VerifyNothingCaptured();
    }

    [Theory]
    [InlineData(150)]
    [InlineData(-3)]
    public void CaptureMonitor_ClampsQualityToValidRange(int quality)
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureMonitor(0, new CaptureOptions { Format = "jpeg", Quality = quality });

        // Assert
        capture.Metadata.Success.Should().BeTrue();
    }

    [Fact]
    public void CaptureMonitor_LowerJpegQuality_GivesSmallerImage()
    {
        // Arrange - a noisy image, so quality makes a difference
        var random = new Random(42);
        var pixels = new byte[400 * 300 * 4];
        random.NextBytes(pixels);
        UseMonitor(400, 300);
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(0, 0, 400, 300))
            .Returns(new CaptureResult(pixels, 400, 300, 0, 0, "BitBlt"));
        var sut = CreateSut();

        // Act
        var low = sut.CaptureMonitor(0, new CaptureOptions { Format = "jpeg", Quality = 10 }).Metadata.ByteSize;
        var high = sut.CaptureMonitor(0, new CaptureOptions { Format = "jpeg", Quality = 95 }).Metadata.ByteSize;

        // Assert
        low.Should().BeLessThan(high!.Value);
    }

    #endregion

    #region Pixels

    [Fact]
    public void CaptureRegion_Png_IsOpaqueEvenThoughGdiLeavesTheFourthByteZero()
    {
        // Arrange - BI_RGB pixels: blue 30, green 20, red 10, and an undefined 4th byte of 0
        var pixels = SolidPixels(64, 32, blue: 30, green: 20, red: 10, fourth: 0);
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(0, 0, 64, 32))
            .Returns(new CaptureResult(pixels, 64, 32, 0, 0, "BitBlt"));
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 64, 32, new CaptureOptions { Format = "png" });

        // Assert
        using var bitmap = ToBitmap(capture.Image!);
        foreach (var (x, y) in new[] { (0, 0), (63, 0), (0, 31), (63, 31), (32, 16) })
        {
            bitmap.GetPixel(x, y).Should().Be(Color.FromArgb(255, 10, 20, 30), "pixel ({0}, {1}) must be opaque", x, y);
        }
    }

    [Fact]
    public void CaptureRegion_ScaledPng_KeepsEdgesOpaqueAndUndarkened()
    {
        // Arrange
        var pixels = SolidPixels(1200, 40, blue: 200, green: 150, red: 100, fourth: 0);
        _mockCaptureWrapper.Setup(c => c.CaptureScreenRegion(0, 0, 1200, 40))
            .Returns(new CaptureResult(pixels, 1200, 40, 0, 0, "BitBlt"));
        var sut = CreateSut();

        // Act
        var capture = sut.CaptureRegion(0, 0, 1200, 40, new CaptureOptions { Format = "png", MaxDimension = 300 });

        // Assert
        capture.Metadata.Width.Should().Be(300);
        capture.Metadata.Height.Should().Be(10);
        using var bitmap = ToBitmap(capture.Image!);
        foreach (var (x, y) in new[] { (0, 0), (299, 0), (0, 9), (299, 9) })
        {
            var pixel = bitmap.GetPixel(x, y);
            pixel.A.Should().Be(255);
            pixel.R.Should().BeInRange(98, 102);
            pixel.G.Should().BeInRange(148, 152);
            pixel.B.Should().BeInRange(198, 202);
        }
    }

    #endregion

    #region Annotations

    [Fact]
    public void CaptureWindowWithElements_ScalesImageBoundsAndKeepsScreenBounds()
    {
        // Arrange
        UseWindowCapture(200, 100);
        var sut = CreateSut();
        var element = ElementAt(10, 20, 40, 30);

        // Act
        var capture = sut.CaptureWindowWithElements(new nint(12345), null, [element], includeFrame: true,
            highlightInteractable: true, new CaptureOptions { MaxDimension = 100 });

        // Assert
        var metadata = capture.Metadata;
        metadata.Success.Should().BeTrue();
        metadata.Width.Should().Be(100);
        metadata.Height.Should().Be(50);
        metadata.OriginalWidth.Should().Be(200);
        metadata.OriginalHeight.Should().Be(100);
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(0, 0, 2, 2));
        metadata.ElementCount.Should().Be(1);
        var annotated = metadata.Elements.Should().ContainSingle().Subject;
        annotated.ImageBounds.Should().Be(new BoundsDto(5, 10, 20, 15));
        annotated.ScreenBounds.Should().Be(element.Bounds);
        Decode(capture.Image!).Should().Be((100, 50));
    }

    [Fact]
    public void CaptureWindowWithElements_SmallElementStaysAtLeastOnePixel()
    {
        // Arrange — 200px wide scaled to 80 is 0.4; a 2px box truncates to 0 without a minimum
        UseWindowCapture(200, 100);
        var sut = CreateSut();
        var element = ElementAt(4, 6, 2, 2);

        // Act
        var metadata = sut.CaptureWindowWithElements(new nint(12345), null, [element], includeFrame: true,
            highlightInteractable: true, new CaptureOptions { MaxWidth = 80 }).Metadata;

        // Assert
        metadata.Success.Should().BeTrue();
        var annotated = metadata.Elements.Should().ContainSingle().Subject;
        annotated.ImageBounds.Should().Be(new BoundsDto(2, 2, 1, 1));
        annotated.ScreenBounds.Should().Be(element.Bounds);
    }

    [Fact]
    public void CaptureWindowWithElements_ImageBoundsMapBackToScreenBoundsThroughTheTransform()
    {
        // Arrange - a window at a negative origin, scaled by the default limit
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns(new CaptureResult(new byte[3136 * 1000 * 4], 3136, 1000, -3200, 100, "PrintWindow"));
        var element = ElementAt(-3000, 400, 300, 120);
        var sut = CreateSut();

        // Act
        var metadata = sut.CaptureWindowWithElements(new nint(12345), null, [element], includeFrame: true,
            highlightInteractable: true, new CaptureOptions()).Metadata;

        // Assert - 3136 scaled to 1568 is exactly half
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(-3200, 100, 2, 2));
        var imageBounds = metadata.Elements.Should().ContainSingle().Subject.ImageBounds;
        metadata.ImageToScreen!.ToScreen(imageBounds.X, imageBounds.Y).Should().Be((-3000, 400));
    }

    #endregion

    private void UseMonitor(int width, int height) =>
        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { TestDataFactory.CreateMonitorInfo(boundsWidth: width, boundsHeight: height) });

    private void UseWindowCapture(int width, int height)
    {
        _mockCaptureWrapper.Setup(c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns(new CaptureResult(new byte[width * height * 4], width, height, 0, 0, "PrintWindow"));
    }

    private void VerifyNothingCaptured()
    {
        _mockCaptureWrapper.Verify(
            c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never);
        _mockCaptureWrapper.Verify(
            c => c.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()),
            Times.Never);
    }

    private static UIElementInfo ElementAt(int x, int y, int width, int height) =>
        new(
            Name: "OK",
            ControlType: "Button",
            AutomationId: "ok",
            Bounds: new BoundsDto(x, y, width, height),
            IsEnabled: true,
            IsOffscreen: false);

    private static byte[] SolidPixels(int width, int height, byte blue, byte green, byte red, byte fourth)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = fourth;
        }

        return pixels;
    }

    private static Bitmap ToBitmap(EncodedImage image)
    {
        using var stream = new MemoryStream(image.Data);
        using var decoded = new Bitmap(stream);

        // Copy into a plain ARGB bitmap, so GetPixel reports alpha the way a PNG viewer would see it.
        var copy = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(copy);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
        return copy;
    }

    private static (int Width, int Height) Decode(EncodedImage image)
    {
        using var stream = new MemoryStream(image.Data);
        using var bitmap = new Bitmap(stream);
        return (bitmap.Width, bitmap.Height);
    }
}
