using System.Drawing;
using System.Drawing.Imaging;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The capture aids of roadmap M7.1b: the pointer's position in every result and its shape drawn on request, a coordinate
/// grid labeled with screen coordinates, and capture_region's zoom with its image-to-screen transform. The capture layer is
/// a mock, so nothing is captured or drawn on the screen.
/// </summary>
public class CaptureAidsTests
{
    private readonly Mock<IScreenCaptureWrapper> _capture = TestDataFactory.CreateMockScreenCaptureWrapper();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();

    private ScreenshotService CreateSut() =>
        new(_capture.Object, _monitors.Object, Mock.Of<INativeWindowWrapper>(w => w.IsWindowVisible(It.IsAny<nint>()) == true));

    private static CaptureOptions Png(Func<CaptureOptions, CaptureOptions>? with = null)
    {
        var options = new CaptureOptions { Format = CaptureFormat.Png };
        return with == null ? options : with(options);
    }

    private static Bitmap Decode(EncodedImage image) => new(new MemoryStream(image.Data));

    // ---- Zoom ----

    [Theory]
    [InlineData(0.5)]
    [InlineData(4.5)]
    [InlineData(double.NaN)]
    public void Zoom_OutsideOneToFour_IsInvalidImageSize_AndNothingIsCaptured(double zoom)
    {
        var result = CreateSut().CaptureRegion(0, 0, 100, 50, Png(o => o with { Zoom = zoom }));

        result.Metadata.Success.Should().BeFalse();
        result.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Zoom_ThatWouldMakeAnImageOverTheBudget_IsInvalidImageSize_AndNothingIsCaptured()
    {
        // Arrange - capture_region over a 4K monitor at zoom 4 with maxDimension 0 would be a 15360 x 8640 image: about 530 MB
        // of pixels in one bitmap, then a resize, an encoding and base64
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 3840, 2160));

        // Act
        var result = CreateSut().CaptureRegion(0, 0, 3840, 2160, Png(o => o with { Zoom = 4, MaxDimension = 0 }));

        // Assert
        result.Metadata.Success.Should().BeFalse();
        result.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        result.Metadata.Error.Should().Contain("15360x8640").And.Contain("Nothing was captured");
        result.Image.Should().BeNull();
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Zoom_IsHeldToTheBudget_ByTheRegionOnTheScreen_NotTheRegionAsked()
    {
        // Arrange - a 3000 x 3000 region on a 500 x 500 screen is cut to the screen before the budget is checked. Without a
        // long-edge limit, zoom 4 makes the region asked a 12000 x 12000 image (144 megapixels, over the budget), but the part
        // on the screen only a 2000 x 2000 one (4 megapixels)
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 500, 500));

        // Act
        var result = CreateSut().CaptureRegion(0, 0, 3000, 3000, Png(o => o with { Zoom = 4, MaxDimension = 0 }));

        // Assert
        result.Metadata.Success.Should().BeTrue(result.Metadata.Error);
        result.Metadata.Width.Should().Be(2000);
        result.Metadata.Height.Should().Be(2000);
        _capture.Verify(c => c.CaptureScreenRegion(0, 0, 500, 500), Times.Once);
    }

    [Fact]
    public void Zoom_OnAMonitor_IsHeldToTheBudget_BeforeAnythingIsCaptured()
    {
        // Arrange - the service also takes a zoom for a monitor; a 4K monitor at zoom 4 without a long-edge limit is too large
        var monitors = TestDataFactory.CreateMockMonitorService(
            [new MonitorInfo(0, "DISPLAY1", true, new MonitorRect(0, 0, 3840, 2160), new MonitorRect(0, 0, 3840, 2120), 1.0)]);
        var sut = new ScreenshotService(_capture.Object, monitors.Object,
            Mock.Of<INativeWindowWrapper>(w => w.IsWindowVisible(It.IsAny<nint>()) == true));

        // Act
        var result = sut.CaptureMonitor(0, Png(o => o with { Zoom = 4, MaxDimension = 0 }));

        // Assert
        result.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
        _capture.Verify(c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(2.0, null, 200, 100, 0.5)]
    [InlineData(4.0, null, 400, 200, 0.25)]
    [InlineData(4.0, 300, 300, 150, 0.333333)] // maxDimension still caps the long edge
    [InlineData(1.0, null, 100, 50, 1.0)]
    public void Zoom_EnlargesTheImage_AndTheTransformMapsItsPointsBack(double zoom, int? maxDimension, int width, int height, double scale)
    {
        // Act
        var result = CreateSut().CaptureRegion(10, 20, 100, 50, Png(o => o with { Zoom = zoom, MaxDimension = maxDimension }));

        // Assert
        var metadata = result.Metadata;
        metadata.Width.Should().Be(width);
        metadata.Height.Should().Be(height);
        metadata.ImageToScreen!.ScaleX.Should().BeApproximately(scale, 0.000001);
        metadata.ImageToScreen.OffsetX.Should().Be(10);
        using var decoded = Decode(result.Image!);
        decoded.Width.Should().Be(width);
    }

    // ---- The grid ----

    [Theory]
    [InlineData(10)]
    [InlineData(20_000)]
    public void GridSpacing_Outside20To10000_IsInvalidImageSize(int spacing)
    {
        var result = CreateSut().CaptureRegion(0, 0, 100, 50, Png(o => o with { GridSpacing = spacing }));

        result.Metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.InvalidImageSize));
    }

    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(20, 1.0, 20)]
    [InlineData(20, 2.0, 40)] // 10 image pixels apart: doubled to 20
    [InlineData(20, 4.0, 80)]
    public void TheGridSpacing_IsWidenedUntilItsLinesAreApart(int spacing, double screenPerImagePixel, int expected) =>
        CaptureGrid.EffectiveSpacing(spacing, screenPerImagePixel).Should().Be(expected);

    [Fact]
    public void TheGrid_DrawsItsLinesAtMultiplesOfTheSpacing_InScreenCoordinates()
    {
        // Arrange - a white image of the screen from (30, 0), 200x100 pixels at full scale
        using var image = new Bitmap(200, 100, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
        }

        // Act - lines at screen x = 50, 100, 150, 200: image x = 20, 70, 120, 170
        CaptureGrid.Draw(image, 30, 0, 1.0, 1.0, 50);

        // Assert - below the labels, on a line and between lines
        image.GetPixel(70, 60).ToArgb().Should().NotBe(Color.White.ToArgb());
        image.GetPixel(45, 60).ToArgb().Should().Be(Color.White.ToArgb());
        image.GetPixel(120, 75).ToArgb().Should().NotBe(Color.White.ToArgb());
    }

    [Fact]
    public void AGridTooDenseForTheScaledImage_IsWidened_WithAWarning()
    {
        // Arrange - a 1920x1080 monitor scaled to 1568 pixels: 20 screen pixels are about 16.3 image pixels,
        // and with maxDimension 400 about 4
        var result = CreateSut().CaptureMonitor(0, Png(o => o with { GridSpacing = 20, MaxDimension = 400 }));

        // Assert
        result.Metadata.Success.Should().BeTrue();
        result.Metadata.Warnings.Should().ContainSingle(w => w.StartsWith("GridWidened:"))
            .Which.Should().Contain("every 80 screen pixels");
    }

    // ---- The pointer ----

    [Fact]
    public void EveryCapture_SaysWhereThePointerIs_WithoutDrawingIt()
    {
        _capture.Setup(c => c.GetPointer()).Returns(new PointerState(640, 360, Visible: true));

        var metadata = CreateSut().CaptureRegion(0, 0, 100, 50, Png()).Metadata;

        metadata.Cursor.Should().Be(new CursorPosition(640, 360));
        _capture.Verify(c => c.DrawPointer(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void IncludeCursor_DrawsThePointer_AtTheCapturesOrigin_AndSaysSo()
    {
        // Arrange
        _capture.Setup(c => c.GetPointer()).Returns(new PointerState(40, 30, Visible: true));
        _capture.Setup(c => c.DrawPointer(It.IsAny<nint>(), 10, 20)).Returns(true);

        // Act
        var metadata = CreateSut().CaptureRegion(10, 20, 100, 50, Png(o => o with { IncludeCursor = true })).Metadata;

        // Assert
        metadata.Cursor.Should().Be(new CursorPosition(40, 30, Drawn: true));
        _capture.Verify(c => c.DrawPointer(It.Is<nint>(hdc => hdc != nint.Zero), 10, 20), Times.Once);
    }

    [Fact]
    public void AHiddenPointer_IsReportedHidden_AndNotDrawn()
    {
        _capture.Setup(c => c.GetPointer()).Returns(new PointerState(40, 30, Visible: false));

        var metadata = CreateSut().CaptureRegion(10, 20, 100, 50, Png(o => o with { IncludeCursor = true })).Metadata;

        metadata.Cursor.Should().Be(new CursorPosition(40, 30, Hidden: true));
        _capture.Verify(c => c.DrawPointer(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void APointerThatCannotBeRead_IsLeftOut()
    {
        var metadata = CreateSut().CaptureRegion(10, 20, 100, 50, Png(o => o with { IncludeCursor = true })).Metadata;

        metadata.Success.Should().BeTrue();
        metadata.Cursor.Should().BeNull();
    }
}
