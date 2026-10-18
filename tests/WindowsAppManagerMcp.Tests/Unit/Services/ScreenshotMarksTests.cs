using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Roadmap M3.11, part 2, in the screenshot service: capture_with_elements draws its marks (a box and a filled tag with the
/// element's ref) on the returned image after it was scaled, or nothing at all with Draw false; the tags leave out the window
/// part the refs share (markPrefix) and go outside their boxes where there is room, and not over an earlier tag (roadmap
/// M3.F38); and capture_window's ref
/// crop (<see cref="ScreenshotService.CaptureWindowArea"/>) cuts a window capture to an area, clipped to the window, and
/// registers the crop's origin under its captureId.
/// </summary>
public class ScreenshotMarksTests
{
    private static readonly CaptureOptions Png = new() { Format = CaptureFormat.Png };
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

    /// <summary>
    /// The width of the tag that ends just above (x, bottom): its top row, which holds no text (the tag's padding), counted
    /// right from x until a pixel is not its green fill.
    /// </summary>
    private static int TagWidthAbove(Bitmap image, int x, int bottom)
    {
        var top = bottom - GreenRunAbove(image, x, bottom) + 1;
        var run = 0;
        for (var i = x; i < image.Width && IsTagGreen(image.GetPixel(i, top)); i++)
        {
            run++;
        }

        return run;
    }

    /// <summary>A tag of three characters, such as e44.</summary>
    private static readonly Size Tag = new(27, 15);

    private static IReadOnlyList<Rectangle?> Place(Size image, params (Rectangle Box, Size? Tag)[] marks) =>
        ElementMarkRenderer.PlaceTags(marks.Select(m => m.Box).ToList(), marks.Select(m => m.Tag).ToList(), image);

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
    public void Marks_ThatShareTheirWindowPart_DrawOnlyTheElementPart_AndReportTheWindowPartAsTheMarkPrefix()
    {
        // Arrange - File Explorer's 79 tags all showed w1399829 and covered each other (roadmap M3.F38)
        BlackWindow(400, 200);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null,
            [Button(100, 100, 80, 30, "w1000001e7"), Button(250, 100, 80, 30, "w1000001e12")], includeFrame: true,
            ElementOverlayOptions.Default, Png);

        // Assert - the tag of e7 is about two characters wide; the whole ref w1000001e7 would be about 70 pixels
        capture.Metadata.MarkPrefix.Should().Be("w1000001");
        capture.Metadata.Elements!.Select(e => e.Ref).Should().Equal("w1000001e7", "w1000001e12");
        using var image = Decode(capture);
        TagWidthAbove(image, 101, 97).Should().BeInRange(10, 30, "the tag shows e7 alone");
    }

    [Fact]
    public void Marks_OfRefsOfTwoWindows_DrawTheWholeRefs_WithoutAMarkPrefix()
    {
        // Arrange
        BlackWindow(400, 200);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null,
            [Button(100, 100, 80, 30, "w1000001e7"), Button(250, 100, 80, 30, "w1000002e12")], includeFrame: true,
            ElementOverlayOptions.Default, Png);

        // Assert
        capture.Metadata.MarkPrefix.Should().BeNull();
        using var image = Decode(capture);
        TagWidthAbove(image, 101, 97).Should().BeGreaterThan(45, "the tag shows the whole ref w1000001e7");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Marks_WithoutTags_HaveNoMarkPrefix(bool draw, bool labels)
    {
        // Arrange
        BlackWindow(400, 200);

        // Act
        var capture = CreateSut().CaptureWindowWithElements(Window, null, [Button(100, 100, 80, 30)], includeFrame: true,
            new ElementOverlayOptions(Draw: draw, Labels: labels), Png);

        // Assert - no tag leaves anything out
        capture.Metadata.MarkPrefix.Should().BeNull();
    }

    [Fact]
    public void TagText_LeavesOutTheWindowPartTheMarksShare()
    {
        ElementMarkRenderer.SharedWindowPart(["w1399829e44", "w1399829e7", null, "W1399829E3"]).Should().Be("w1399829");
        ElementMarkRenderer.TagText("w1399829e44", "w1399829").Should().Be("e44");

        // Refs of two windows, or none at all: the tags show whole refs
        ElementMarkRenderer.SharedWindowPart(["w1399829e44", "w1399830e7"]).Should().BeNull();
        ElementMarkRenderer.SharedWindowPart([null, "not a ref"]).Should().BeNull();
        ElementMarkRenderer.TagText("w1399829e44", null).Should().Be("w1399829e44");
        ElementMarkRenderer.TagText("w1399830e7", "w1399829").Should().Be("w1399830e7");
    }

    [Fact]
    public void TagPlacement_WithRoomAboveTheBox_PutsTheTagThere_OutsideTheElement()
    {
        Place(new Size(400, 200), (new Rectangle(100, 100, 80, 30), Tag))[0].Should().Be(new Rectangle(100, 85, 27, 15));
    }

    [Fact]
    public void TagPlacement_WithAnotherElementAbove_PutsTheTagLeftOfTheBox()
    {
        // The tag above the lower button would cover the upper one
        var tags = Place(new Size(400, 200), (new Rectangle(100, 70, 80, 30), Tag), (new Rectangle(100, 100, 80, 30), Tag));

        tags[0].Should().Be(new Rectangle(100, 55, 27, 15));
        tags[1].Should().Be(new Rectangle(73, 100, 27, 15));
    }

    [Fact]
    public void TagPlacement_MayCoverAnElementThatHoldsTheBox()
    {
        // A list around its item: the item's tag above it lies inside the list, which is no other element's content
        var tags = Place(new Size(400, 200), (new Rectangle(50, 50, 300, 140), null), (new Rectangle(100, 100, 80, 30), Tag));

        tags[0].Should().BeNull("a mark without a label gets no tag");
        tags[1].Should().Be(new Rectangle(100, 85, 27, 15));
    }

    [Fact]
    public void TagPlacement_InACrowdedRowOfSmallButtons_DrawsNoTagOverAnother()
    {
        // A toolbar of ten touching 24-pixel buttons, each tag wider than its button
        var buttons = Enumerable.Range(0, 10).Select(i => (new Rectangle(100 + 24 * i, 100, 24, 24), (Size?)Tag)).ToArray();

        var tags = Place(new Size(400, 200), buttons);

        tags.Should().OnlyContain(t => t.HasValue);
        for (var i = 0; i < tags.Count; i++)
        {
            for (var j = i + 1; j < tags.Count; j++)
            {
                tags[i]!.Value.IntersectsWith(tags[j]!.Value).Should().BeFalse($"the tags of buttons {i} and {j} would cover each other");
            }
        }
    }

    [Fact]
    public void TagPlacement_WithoutRoomOutside_FallsBackInsideTheBox_AtTheRightOfAWideOne()
    {
        // Full-width list rows filling the image: above, left and below are other rows or off the image. A row's text starts
        // at its left, so a box three tags wide gets its tag at its right end
        var tags = Place(new Size(400, 66),
            (new Rectangle(0, 0, 400, 22), Tag), (new Rectangle(0, 22, 400, 22), Tag), (new Rectangle(0, 44, 400, 22), Tag));

        tags.Should().Equal(new Rectangle(373, 0, 27, 15), new Rectangle(373, 22, 27, 15), new Rectangle(373, 44, 27, 15));
    }

    [Fact]
    public void TagPlacement_WithoutRoomOutside_FallsBackInsideTheLeftOfANarrowBox()
    {
        var tags = Place(new Size(120, 20), (new Rectangle(0, 0, 60, 20), Tag), (new Rectangle(60, 0, 60, 20), Tag));

        tags.Should().Equal(new Rectangle(0, 0, 27, 15), new Rectangle(60, 0, 27, 15));
    }

    [Fact]
    public void TagPlacement_InsideABox_KeepsClearOfAnEarlierTag()
    {
        // Two marks of one box (a list item and its name): the second tag goes to another corner inside
        var tags = Place(new Size(100, 30), (new Rectangle(0, 0, 100, 30), Tag), (new Rectangle(0, 0, 100, 30), Tag));

        tags[0].Should().Be(new Rectangle(73, 0, 27, 15));
        tags[1]!.Value.IntersectsWith(tags[0]!.Value).Should().BeFalse();
        new Rectangle(0, 0, 100, 30).Contains(tags[1]!.Value).Should().BeTrue();
    }

    [Fact]
    public void TagPlacement_StaysInTheImage()
    {
        // A box at the right edge: the tag above is moved left into the image
        Place(new Size(400, 200), (new Rectangle(380, 100, 20, 20), Tag))[0].Should().Be(new Rectangle(373, 85, 27, 15));

        // A box smaller than its tag in the image's top-left corner: below it
        Place(new Size(400, 200), (new Rectangle(0, 0, 10, 10), Tag))[0].Should().Be(new Rectangle(0, 10, 27, 15));
    }

    [Fact]
    public void TagPlacement_OfTheMostMarksAWalkReturns_IsFast()
    {
        // A grid of 5000 small marks
        var marks = Enumerable.Range(0, 5000)
            .Select(i => (new Rectangle(i % 100 * 15, i / 100 * 20, 14, 18), (Size?)Tag)).ToArray();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var tags = Place(new Size(1568, 1000), marks);

        tags.Should().HaveCount(5000);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
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
    public void CaptureWindowArea_OfTheClientArea_KeepsTheClientAreaItWasCutFrom_SoItsPointsStillConvert()
    {
        // Arrange - the client area is the whole 200 x 100 capture at (100, 100); the crop is a part of it
        BlackWindow(200, 100, 100, 100);
        var hitTest = new Mock<INativeHitTestWrapper>();
        hitTest.Setup(h => h.GetClientAreaOnScreen(Window)).Returns(new WindowRect(100, 100, 200, 100));
        _windows.Setup(w => w.IsWindow(Window)).Returns(true);

        // Act
        var metadata = CreateSut().CaptureWindowArea(Window, includeFrame: false, new BoundsDto(150, 120, 40, 20), Png).Metadata;
        var point = new CoordinateSpaceResolver(_registry, _windows.Object, hitTest.Object, _capture.Object)
            .FromImage(metadata.CaptureId!, 20, 10);

        // Assert
        _registry.Find(metadata.CaptureId!)!.Geometry.Window!.ClientArea.Should().Be(new WindowRect(100, 100, 200, 100));
        point.Resolved.Should().BeTrue(point.Error);
        (point.X, point.Y).Should().Be((170, 130));
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
