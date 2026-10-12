using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Roadmap M3.11, part 2, in the screenshot service: capture_with_elements draws its marks (a box and a filled tag with the
/// element's ref) on the returned image after it was scaled, or nothing at all with Draw false; and capture_window's ref
/// crop (<see cref="ScreenshotService.CaptureWindowArea"/>) cuts a window capture to an area, clipped to the window, and
/// registers the crop's origin under its captureId.
/// </summary>
public class ScreenshotMarksTests
{
    private static readonly CaptureOptions Png = new() { Format = "png" };
    private static readonly nint Window = 0x1234;

    private readonly Mock<IScreenCaptureWrapper> _capture = new();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly CaptureRegistry _registry = new();

    public ScreenshotMarksTests()
    {
        _capture.Setup(c => c.IsValidWindow(Window)).Returns(true);
        _capture.Setup(c => c.GetVirtualScreenBounds()).Returns((0, 0, 3840, 2160));
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetWindowRect(Window)).Returns((100, 100, 300, 200));
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(4242u);
    }

    private ScreenshotService CreateSut() =>
        new(_capture.Object, _monitors.Object, _windows.Object, captureRegistry: _registry);

    /// <summary>A black window capture of <paramref name="width"/> x <paramref name="height"/> at (x, y).</summary>
    private void BlackWindow(int width, int height, int x = 0, int y = 0) =>
        _capture.Setup(c => c.CaptureWindow(Window, It.IsAny<bool>()))
            .Returns(() => new CaptureResult(new byte[width * height * 4], width, height, x, y, "PrintWindow"));

    private static UIElementInfo Button(int x, int y, int width, int height, string? elementRef = "w1000001e7") =>
        new("Save", "Button", "btnSave", new BoundsDto(x, y, width, height), IsEnabled: true, IsOffscreen: false, Ref: elementRef);

    private static Bitmap Decode(ScreenCapture<AnnotatedScreenshotResult> capture) =>
        new(new MemoryStream(capture.Image!.Data));

    private static bool IsTagGreen(Color pixel) => pixel.G >= 150 && pixel.R <= 60 && pixel.B <= 60;

    /// <summary>How many pixels straight above (x, top) are the tag's green fill, counted upward until one is not.</summary>
    private static int GreenRunAbove(Bitmap image, int x, int top)
    {
        var run = 0;
        for (var y = top; y >= 0 && IsTagGreen(image.GetPixel(x, y)); y--)
        {
            run++;
        }

        return run;
    }

    #region Marks

    [Fact]
    public void Marks_AreDrawnAfterScaling_SoTheTagKeepsItsSizeInTheReturnedImage()
    {
        // Arrange - a 3136 x 400 window scaled by half to the default 1568 long edge; the button is at output (200, 100)
        BlackWindow(3136, 400);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null, [Button(400, 200, 800, 100)], includeFrame: true,
            ElementOverlayOptions.Default, Png);

        // Assert - drawn before scaling, an 11-pixel tag would shrink to about 6 pixels
        capture.Metadata.Width.Should().Be(1568);
        capture.Metadata.Elements.Should().ContainSingle().Which.ImageBounds.Should().Be(new BoundsDto(200, 100, 400, 50));
        using var image = Decode(capture);
        GreenRunAbove(image, 201, 97).Should().BeInRange(10, 24, "the tag is a filled box about one 11-pixel line high");
    }

    [Fact]
    public void Marks_AtFullResolution_PutTheTagAboveTheBox_WithTheBoxLineTwoPixelsWide()
    {
        // Arrange
        BlackWindow(400, 200);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null, [Button(100, 100, 80, 30)], includeFrame: true,
            ElementOverlayOptions.Default, Png);

        // Assert
        using var image = Decode(capture);
        GreenRunAbove(image, 101, 97).Should().BeGreaterThanOrEqualTo(10);
        Enumerable.Range(95, 10).Count(x => image.GetPixel(x, 115).G >= 100).Should().BeInRange(1, 3,
            "the box's left edge is a line about 2 pixels wide");
    }

    [Fact]
    public void Marks_WithoutLabels_DrawOnlyTheBoxes()
    {
        // Arrange
        BlackWindow(400, 200);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null, [Button(100, 100, 80, 30)], includeFrame: true,
            new ElementOverlayOptions(Labels: false), Png);

        // Assert
        using var image = Decode(capture);
        GreenRunAbove(image, 101, 97).Should().Be(0);
        image.GetPixel(100, 115).G.Should().BeGreaterThanOrEqualTo(100, "the box is still drawn");
    }

    [Fact]
    public void Marks_OfAnElementWithoutARef_AreABoxWithoutATag()
    {
        // Arrange - an element whose provider reports no runtime id has no ref to show
        BlackWindow(400, 200);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null, [Button(100, 100, 80, 30, elementRef: null)],
            includeFrame: true, ElementOverlayOptions.Default, Png);

        // Assert
        using var image = Decode(capture);
        GreenRunAbove(image, 101, 97).Should().Be(0);
        capture.Metadata.Elements.Should().ContainSingle();
    }

    [Fact]
    public void Marks_NotDrawn_ReturnTheCleanImage_WithTheMarksInTheMetadata()
    {
        // Arrange
        BlackWindow(400, 200);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null, [Button(100, 100, 80, 30)], includeFrame: true,
            new ElementOverlayOptions(Draw: false), Png);

        // Assert
        using var image = Decode(capture);
        Enumerable.Range(0, 200).SelectMany(y => Enumerable.Range(0, 400).Select(x => image.GetPixel(x, y)))
            .Should().OnlyContain(p => p.R == 0 && p.G == 0 && p.B == 0);
        var mark = capture.Metadata.Elements.Should().ContainSingle().Subject;
        mark.Ref.Should().Be("w1000001e7");
        mark.ImageBounds.Should().Be(new BoundsDto(100, 100, 80, 30));
    }

    [Fact]
    public void TagPlacement_PrefersAboveTheBox_ThenInsideIt_AndStaysInTheImage()
    {
        var image = new Size(400, 200);
        var tag = new Size(60, 15);

        // Room above
        ElementMarkRenderer.PlaceTag(tag, new Rectangle(100, 100, 80, 30), image, [])
            .Should().Be(new Rectangle(100, 85, 60, 15));

        // A box at the top edge: inside its top-left corner
        ElementMarkRenderer.PlaceTag(tag, new Rectangle(100, 0, 80, 30), image, [])
            .Should().Be(new Rectangle(100, 0, 60, 15));

        // Above is taken by another mark's tag: inside
        ElementMarkRenderer.PlaceTag(tag, new Rectangle(100, 100, 80, 30), image, [new Rectangle(90, 80, 60, 15)])
            .Should().Be(new Rectangle(100, 100, 60, 15));

        // A box at the right edge: the tag is moved left into the image
        ElementMarkRenderer.PlaceTag(tag, new Rectangle(380, 100, 20, 20), image, [])
            .Should().Be(new Rectangle(340, 85, 60, 15));
    }

    [Theory]
    [InlineData(0, 200, 0, "Black")] // the buttons' green
    [InlineData(0, 150, 255, "Black")] // the text inputs' blue
    [InlineData(200, 0, 200, "White")] // the lists' purple
    [InlineData(0, 0, 139, "White")]
    [InlineData(255, 200, 0, "Black")] // the tabs' yellow
    public void TagText_ContrastsWithItsFill(int r, int g, int b, string expected)
    {
        ElementMarkRenderer.TextColorFor(Color.FromArgb(r, g, b)).Name.Should().Be(expected);
    }

    #endregion

    #region The ref crop (capture_window with a ref)

    [Fact]
    public void CaptureWindowArea_CropsToTheArea_AndRegistersTheCropsOrigin()
    {
        // Arrange - the window is 200 x 100 at (100, 100)
        BlackWindow(200, 100, 100, 100);

        // Act
        var metadata = CreateSut().CaptureWindowArea(Window, includeFrame: true, new BoundsDto(150, 120, 40, 20), Png).Metadata;

        // Assert
        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.Width.Should().Be(40);
        metadata.Height.Should().Be(20);
        metadata.CapturedRegion.Should().Be(new CapturedRegion(150, 120, 40, 20));
        metadata.ImageToScreen.Should().Be(new ImageToScreenTransform(150, 120, 1, 1));
        var geometry = _registry.Find(metadata.CaptureId!)!.Geometry;
        geometry.ImageToScreen.Should().Be(new ImageToScreenTransform(150, 120, 1, 1));
        geometry.CapturedRegion.Should().Be(new CapturedRegion(150, 120, 40, 20));
        geometry.Window.Should().Be(new CapturedWindow(0x1234, 4242, WindowRect.FromLTRB(100, 100, 300, 200), true),
            "a click on the crop is refused once the window moved, as for the whole window");
    }

    [Fact]
    public void CaptureWindowArea_ClipsTheAreaToTheWindow()
    {
        // Arrange
        BlackWindow(200, 100, 100, 100);

        // Act - the area reaches 30 pixels past the window's right and bottom edges
        var metadata = CreateSut().CaptureWindowArea(Window, includeFrame: true, new BoundsDto(250, 160, 80, 70), Png).Metadata;

        // Assert
        metadata.CapturedRegion.Should().Be(new CapturedRegion(250, 160, 50, 40));
    }

    [Fact]
    public void CaptureWindowArea_OutsideTheCapturedWindow_IsElementOffscreen()
    {
        // Arrange
        BlackWindow(200, 100, 100, 100);

        // Act
        var capture = CreateSut().CaptureWindowArea(Window, includeFrame: false, new BoundsDto(500, 500, 40, 20), Png);

        // Assert
        capture.Metadata.Success.Should().BeFalse();
        capture.Metadata.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementOffscreen));
        capture.Metadata.Error.Should().Contain("client area");
        capture.Image.Should().BeNull();
        capture.Metadata.CaptureId.Should().BeNull();
    }

    [Fact]
    public void CaptureWindowArea_OfAMinimizedWindow_IsRefusedAsForTheWholeWindow()
    {
        // Arrange
        BlackWindow(200, 100, 100, 100);
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);

        // Act
        var metadata = CreateSut().CaptureWindowArea(Window, includeFrame: true, new BoundsDto(150, 120, 40, 20), Png).Metadata;

        // Assert
        metadata.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.WindowMinimized));
    }

    #endregion
}
