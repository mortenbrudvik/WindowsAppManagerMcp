using System.Drawing;
namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// read_screen_text's and find_text_on_screen's service (roadmap M6.3) over a fake OCR engine and a mocked raw pixel seam: the
/// language, the capture's refusals, the mask of denied windows, the 2x enlargement, the tiles, the mapping of OCR's boxes to
/// screen pixels and the registration of the capture.
/// </summary>
public class OcrServiceTests
{
    private const nint Window = 0x1234;

    private readonly Mock<IScreenshotService> _screenshots = new();
    private readonly FakeOcrEngine _engine = new();
    private readonly List<string> _calls = [];
    private DeniedWindowMaskTests.FakeWindow[] _desktop = [];

    public OcrServiceTests()
    {
        _screenshots.Setup(s => s.RegisterRaw(It.IsAny<RawCapture>())).Returns("c7-3fa91c").Callback(() => _calls.Add("register"));
        _engine.OnRecognize = _ => _calls.Add("recognize");
    }

    private OcrService CreateSut()
    {
        var (native, windows) = DeniedWindowMaskTests.Desktop(_desktop);
        return new OcrService(_screenshots.Object, _engine, native.Object, windows.Object);
    }

    /// <summary>The raw capture CapturePixels returns: white pixels of the size and place given.</summary>
    private RawCapture Captures(CaptureTarget target, int width, int height, int x = 100, int y = 200, double? scale = 1.0,
        string method = "PrintWindow", params string[] warnings)
    {
        var raw = new RawCapture(target, new CaptureResult(Enumerable.Repeat((byte)255, width * height * 4).ToArray(), width, height, x, y, method),
            0, scale, warnings, null);
        _screenshots.Setup(s => s.CapturePixels(target)).Returns(raw).Callback(() => _calls.Add("capture"));
        return raw;
    }

    private static OcrRequest Request(CaptureTarget target, string? language = null) => new(target, language);

    private static CaptureTarget WindowTarget => new CaptureTarget.Window(Window, true);

    private static OcrTextMatcher Matcher(string text, OcrTextMatch match = OcrTextMatch.Contains) =>
        OcrTextMatcher.TryCreate(text, match, false, out var error) ?? throw new InvalidOperationException(error);

    #region Reading and mapping

    [Fact]
    public async Task ReadText_SmallScaleWindow_IsReadAt2x_AndBoxesMapBackToScreenPixels()
    {
        Captures(WindowTarget, 800, 600);
        // In pixels of the 2x image: a word at (20, 40) of 100 x 40 is (10, 20) of 50 x 20 on the capture
        _engine.Read = (_, _, _, _) => new OcrPage(null, [FakeOcrEngine.Line(20, 40, "Canvas", "alpha")]);

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Words, 150, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Source.Should().Be("ocr");
        result.OcrScale.Should().Be(2);
        result.Language.Should().Be("en-US");
        result.CaptureId.Should().Be("c7-3fa91c");
        result.CapturedRegion.Should().Be(new CapturedRegion(100, 200, 800, 600));
        result.WindowHandle.Should().Be(Window);
        _engine.Recognitions.Should().ContainSingle().Which.Should().Match<FakeOcrEngine.Recognition>(r => r.Width == 1600 && r.Height == 1200);
        var line = result.Lines.Should().ContainSingle().Subject;
        line.Text.Should().Be("Canvas alpha");
        line.Bounds.Should().Be(new BoundsDto(110, 220, 60, 10));
        line.Words!.Select(w => w.Bounds).Should().Equal(new BoundsDto(110, 220, 30, 10), new BoundsDto(145, 220, 25, 10));
    }

    [Theory]
    [InlineData(1.5, 800, 600, 10_000)]
    [InlineData(2.0, 800, 600, 10_000)]
    [InlineData(1.0, 3000, 2000, 10_000)]
    [InlineData(1.0, 2600, 1000, 5_000)]
    public async Task ReadText_LargeTextOrALargeImage_IsReadAsCaptured(double scale, int width, int height, int maxDimension)
    {
        Captures(WindowTarget, width, height, scale: scale);
        _engine.MaxImageDimension = maxDimension;

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.OcrScale.Should().Be(1);
        _engine.Recognitions.Should().ContainSingle().Which.Should().Match<FakeOcrEngine.Recognition>(r => r.Width == width && r.Height == height);
    }

    [Fact]
    public async Task ReadText_ACaptureWithoutAScale_CountsAs100Percent()
    {
        var target = new CaptureTarget.Monitor(ScreenshotService.VirtualScreenIndex);
        Captures(target, 400, 300, scale: null, method: "ScreenBitBlt");

        var result = await CreateSut().ReadTextAsync(Request(target), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.OcrScale.Should().Be(2);
        result.WindowHandle.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_RoundsBoxesOutward_AndKeepsThemInTheCapture()
    {
        Captures(WindowTarget, 100, 50, x: 0, y: 0, scale: 2.0);
        _engine.Read = (_, _, _, _) => new OcrPage(null,
        [
            new OcrPageLine("a", [new OcrPageWord("a", 10.4, 5.6, 20.2, 9.1)]),
            new OcrPageLine("edge", [new OcrPageWord("edge", 90.5, 45.5, 30, 10)])
        ]);

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.Lines!.Select(l => l.Bounds).Should().Equal(new BoundsDto(10, 5, 21, 10), new BoundsDto(90, 45, 10, 5));
    }

    [Fact]
    public async Task ReadText_MaxLines_KeepsTheFirstLines_AndSaysHowManyThereWere()
    {
        Captures(WindowTarget, 800, 600, scale: 2.0);
        _engine.Read = (_, _, _, _) => new OcrPage(null, Enumerable.Range(0, 5).Select(i => FakeOcrEngine.Line(10, 10 + i * 30, $"line{i}")).ToArray());

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 2, null, CancellationToken.None);

        result.Lines!.Select(l => l.Text).Should().Equal("line0", "line1");
        result.Lines.Should().OnlyContain(l => l.Words == null, "detail 'lines' leaves the words out");
        result.LineCount.Should().Be(5);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task ReadText_PassesOnTheCapturesWarnings_AndTheTextAngle()
    {
        Captures(WindowTarget, 800, 600, scale: 2.0, method: "ScreenBitBlt", warnings: "WindowNotResponding: copied from the screen");
        _engine.Read = (_, _, _, _) => new OcrPage(3.5, []);

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.Warnings.Should().Equal("WindowNotResponding: copied from the screen");
        result.TextAngle.Should().Be(3.5);
        result.Lines.Should().BeEmpty();
        result.LineCount.Should().Be(0);
    }

    [Fact]
    public async Task ReadText_RegistersTheCapture_OnlyAfterOcr()
    {
        Captures(WindowTarget, 800, 600);

        await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        _calls.Should().Equal("capture", "recognize", "register");
    }

    [Fact]
    public async Task ReadText_AWordTheLinesTextDoesNotHold_RebuildsTheLineFromItsWords()
    {
        Captures(WindowTarget, 800, 600, scale: 2.0);
        _engine.Read = (_, _, _, _) => new OcrPage(null,
            [new OcrPageLine("garbled", [new OcrPageWord("Save", 0, 0, 40, 20), new OcrPageWord("As", 50, 0, 20, 20)])]);

        var result = await CreateSut().FindTextAsync(Request(WindowTarget), Matcher("save as", OcrTextMatch.Exact), 20, null, CancellationToken.None);

        result.Matches.Should().ContainSingle().Which.Line.Should().Be("Save As");
    }

    #endregion

    #region Languages

    [Theory]
    [InlineData("nb", "nb")]
    [InlineData("EN-us", "en-US")]
    [InlineData("en", "en-US")]
    public async Task ReadText_AnInstalledLanguage_IsUsed(string requested, string used)
    {
        Captures(WindowTarget, 800, 600);

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget, requested), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.Language.Should().Be(used);
        _engine.Recognitions.Should().OnlyContain(r => r.Language == used);
    }

    [Fact]
    public async Task ReadText_ALanguageThatIsNotInstalled_ListsTheInstalledOnes_AndCapturesNothing()
    {
        Captures(WindowTarget, 800, 600);

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget, "de-DE"), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.OcrLanguageNotInstalled));
        result.InstalledLanguages.Should().Equal("en-US", "nb");
        result.Error.Should().Contain("de-DE").And.Contain("en-US, nb").And.Contain("never installs");
        _screenshots.Verify(s => s.CapturePixels(It.IsAny<CaptureTarget>()), Times.Never);
    }

    [Fact]
    public async Task ReadText_NoLanguageInstalled_IsOcrUnavailable_WithHowToAddOne()
    {
        Captures(WindowTarget, 800, 600);
        _engine.Languages = [];
        _engine.ProfileLanguage = null;

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.OcrUnavailable));
        result.Error.Should().Contain("Add-WindowsCapability").And.Contain("never installs");
        _screenshots.Verify(s => s.CapturePixels(It.IsAny<CaptureTarget>()), Times.Never);
    }

    [Fact]
    public async Task ReadText_WithoutWindowsOcr_IsOcrUnavailable()
    {
        _engine.Unavailable = new OcrUnavailableException("Windows OCR (Windows.Media.Ocr) cannot be used on this system: class not registered");

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.OcrUnavailable));
        result.Error.Should().Contain("class not registered");
    }

    [Fact]
    public async Task ReadText_ARecognitionThatFails_IsOcrUnavailable()
    {
        Captures(WindowTarget, 800, 600);
        _engine.OnRecognize = _ => throw new OcrUnavailableException("Windows OCR could not read the image: out of memory");

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.OcrUnavailable));
        result.Error.Should().Contain("out of memory");
        _screenshots.Verify(s => s.RegisterRaw(It.IsAny<RawCapture>()), Times.Never);
    }

    [Fact]
    public async Task ReadText_NoProfileLanguageRecognizer_FallsBackToTheFirstInstalledOne_WithAWarning()
    {
        Captures(WindowTarget, 800, 600);
        _engine.ProfileLanguage = null;

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.Language.Should().Be("en-US");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith(CaptureWarnings.OcrLanguageFallback + ":");
    }

    #endregion

    #region The capture's refusals and the mask of denied windows

    [Theory]
    [InlineData(ScreenshotErrorCode.WindowMinimized)]
    [InlineData(ScreenshotErrorCode.TargetDeniedByPolicy)]
    [InlineData(ScreenshotErrorCode.RegionOffScreen)]
    public async Task ReadText_ARefusedCapture_IsTheCapturesCode(ScreenshotErrorCode code)
    {
        _screenshots.Setup(s => s.CapturePixels(It.IsAny<CaptureTarget>())).Throws(new CaptureException(code, "refused for a reason"));

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(code.ToString());
        result.Error.Should().Be("refused for a reason");
        result.WindowHandle.Should().Be(Window);
        _engine.Recognitions.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadText_AMonitor_BlacksOutTheDeniedWindowsBeforeOcr()
    {
        var target = new CaptureTarget.Monitor(0);
        Captures(target, 400, 300, x: 0, y: 0, scale: 2.0, method: "ScreenBitBlt");
        _desktop =
        [
            new DeniedWindowMaskTests.FakeWindow(1, new Rectangle(100, 50, 100, 50), Denied: true),
            new DeniedWindowMaskTests.FakeWindow(2, new Rectangle(0, 0, 400, 300))
        ];

        var result = await CreateSut().ReadTextAsync(Request(target), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.MaskedWindows.Should().Be(1);
        var seen = _engine.Recognitions.Single().Bgra;
        PixelAt(seen, 400, 150, 75).Should().Be(0, "the denied window's area is black");
        PixelAt(seen, 400, 99, 75).Should().Be(255);
        PixelAt(seen, 400, 200, 75).Should().Be(255);
    }

    [Fact]
    public async Task ReadText_ADeniedWindowThatCannotBeLocated_RefusesTheRead()
    {
        var target = new CaptureTarget.Monitor(0);
        Captures(target, 400, 300, x: 0, y: 0, scale: 2.0, method: "ScreenBitBlt");
        _desktop = [new DeniedWindowMaskTests.FakeWindow(1, new Rectangle(100, 50, 100, 50), Denied: true, RectUnreadable: true)];

        var result = await CreateSut().ReadTextAsync(Request(target), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.CaptureFailed));
        result.Error.Should().Contain("denies");
        _engine.Recognitions.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadText_AWindowThatDrewItself_IsNotMasked()
    {
        Captures(WindowTarget, 400, 300, x: 0, y: 0, scale: 2.0, method: "PrintWindow");
        _desktop = [new DeniedWindowMaskTests.FakeWindow(1, new Rectangle(100, 50, 100, 50), Denied: true)];

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.MaskedWindows.Should().BeNull("PrintWindow shows no other window");
        _engine.Recognitions.Single().Bgra.Should().OnlyContain(b => b == 255);
    }

    [Fact]
    public async Task ReadText_AWindowCopiedFromTheScreen_IsMaskedLikeARegion()
    {
        Captures(WindowTarget, 400, 300, x: 0, y: 0, scale: 2.0, method: "ScreenBitBlt", warnings: "WindowNotResponding: copied");
        _desktop = [new DeniedWindowMaskTests.FakeWindow(1, new Rectangle(100, 50, 100, 50), Denied: true)];

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, CancellationToken.None);

        result.MaskedWindows.Should().Be(1);
        PixelAt(_engine.Recognitions.Single().Bgra, 400, 150, 75).Should().Be(0);
    }

    private static byte PixelAt(byte[] bgra, int width, int x, int y) => bgra[(y * width + x) * 4];

    #endregion

    #region Tiles, progress and cancellation

    /// <summary>
    /// Words placed in pixels of the whole image; each recognition returns the words that lie whole in its tile, in the tile's
    /// own pixels, as OCR would read the tile.
    /// </summary>
    private void PlaceWords(params (double X, double Y, string Text)[] words)
    {
        var tiles = new Queue<Rectangle>();
        _engine.Read = (_, width, height, _) =>
        {
            var tile = tiles.Dequeue();
            var inside = words
                .Where(w => w.X >= tile.Left && w.X + w.Text.Length * 10 <= tile.Right && w.Y >= tile.Top && w.Y + 20 <= tile.Bottom)
                .Select(w => FakeOcrEngine.Line(w.X - tile.Left, w.Y - tile.Top, w.Text))
                .ToArray();
            return new OcrPage(null, inside);
        };
        foreach (var tile in OcrImage.Tiles(1000, 100, _engine.MaxImageDimension, OcrImage.OverlapFor(_engine.MaxImageDimension)))
        {
            tiles.Enqueue(tile.Area);
        }
    }

    [Fact]
    public async Task ReadText_AnImageLargerThanOcrTakes_IsReadInTiles_AndEachWordOnce()
    {
        _engine.MaxImageDimension = 600;
        Captures(WindowTarget, 1000, 100, x: 0, y: 0, scale: 2.0);
        // Tiles 0-575 and 425-1000 (an overlap of 150, a quarter of 600; cores 0-500, 500-1000): "middle" lies in both
        PlaceWords((100, 10, "left"), (460, 50, "middle"), (800, 10, "right"));
        var progress = new RecordingProgress<OperationProgress>();

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, progress, CancellationToken.None);

        result.Lines!.Select(l => l.Text).Should().BeEquivalentTo("left", "middle", "right");
        result.Lines!.Single(l => l.Text == "middle").Bounds.Should().Be(new BoundsDto(460, 50, 60, 20));
        result.Warnings.Should().ContainSingle().Which.Should().StartWith(CaptureWarnings.OcrTiled + ":");
        _engine.Recognitions.Should().HaveCount(2).And.OnlyContain(r => r.Width <= 600);
        progress.Reports.Select(p => (p.Completed, p.Total)).Should().Equal((1, 2), (2, 2));
    }

    [Fact]
    public async Task ReadText_ALineAcrossATilesEdge_KeepsTheWordsOfItsCore()
    {
        _engine.MaxImageDimension = 600;
        Captures(WindowTarget, 1000, 100, x: 0, y: 0, scale: 2.0);
        // One line of three words at 400-440, 450-490 and 500-540 of the image; both tiles (0-575 and 425-1000) read part of it
        _engine.Read = (_, _, _, _) => _engine.Recognitions.Count == 1
            ? new OcrPage(null, [FakeOcrEngine.Line(400, 10, "aaaa", "bbbb", "cccc")])
            : new OcrPage(null, [FakeOcrEngine.Line(25, 10, "bbbb", "cccc")]);

        var result = await CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Words, 150, null, CancellationToken.None);

        // bbbb's centre (470) lies in the first tile's core (0-500), cccc's (520) in the second's (500-1000)
        result.Lines!.Select(l => l.Text).Should().Equal("aaaa bbbb", "cccc");
    }

    [Fact]
    public async Task ReadText_CancelledBetweenTiles_StopsBeforeTheNextOne()
    {
        _engine.MaxImageDimension = 600;
        Captures(WindowTarget, 1000, 100, x: 0, y: 0, scale: 2.0);
        using var cancel = new CancellationTokenSource();
        _engine.OnRecognize = _ => cancel.Cancel();

        var act = () => CreateSut().ReadTextAsync(Request(WindowTarget), OcrDetail.Lines, 150, null, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _engine.Recognitions.Should().ContainSingle();
        _screenshots.Verify(s => s.RegisterRaw(It.IsAny<RawCapture>()), Times.Never);
    }

    #endregion

    #region find_text_on_screen

    [Fact]
    public async Task FindText_ReturnsTheMatches_TopToBottomThenLeftToRight_WithClickAndImagePoints()
    {
        Captures(WindowTarget, 800, 600, x: 100, y: 200, scale: 2.0);
        _engine.Read = (_, _, _, _) => new OcrPage(null,
        [
            FakeOcrEngine.Line(300, 100, "Save"),
            FakeOcrEngine.Line(10, 100, "Save", "draft"),
            FakeOcrEngine.Line(10, 10, "Autosave", "on")
        ]);

        var result = await CreateSut().FindTextAsync(Request(WindowTarget), Matcher("save"), 20, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(3);
        result.Hint.Should().BeNull();
        result.Matches!.Select(m => (m.Text, m.Line, m.Bounds.X, m.Bounds.Y)).Should().Equal(
            ("save", "Autosave on", 150, 210),
            ("Save", "Save draft", 110, 300),
            ("Save", "Save", 400, 300));
        var first = result.Matches![1];
        first.Bounds.Should().Be(new BoundsDto(110, 300, 40, 20));
        first.ClickPoint.Should().Be(new ScreenPoint(130, 310));
        first.ImagePoint.Should().Be(new ScreenPoint(30, 110), "the capture is registered 1:1 from its corner (100, 200)");
    }

    [Fact]
    public async Task FindText_PartOfAWord_IsBoxedByItsShareOfTheWord()
    {
        Captures(WindowTarget, 800, 600, x: 0, y: 0, scale: 2.0);
        _engine.Read = (_, _, _, _) => new OcrPage(null, [FakeOcrEngine.Line(100, 10, "Autosave")]);

        var result = await CreateSut().FindTextAsync(Request(WindowTarget), Matcher("save"), 20, null, CancellationToken.None);

        // "Autosave" is 80 pixels wide: "save" is its last half
        result.Matches.Should().ContainSingle().Which.Bounds.Should().Be(new BoundsDto(140, 10, 40, 20));
    }

    [Fact]
    public async Task FindText_AcrossWords_IsTheUnionOfTheirBoxes()
    {
        Captures(WindowTarget, 800, 600, x: 0, y: 0, scale: 2.0);
        _engine.Read = (_, _, _, _) => new OcrPage(null, [FakeOcrEngine.Line(10, 10, "Canvas", "alpha", "4711")]);

        var result = await CreateSut().FindTextAsync(Request(WindowTarget), Matcher("alpha 4711", OcrTextMatch.Exact), 20, null, CancellationToken.None);

        result.Matches.Should().ContainSingle().Which.Should().Match<OcrMatchDto>(m =>
            m.Text == "alpha 4711" && m.Bounds == new BoundsDto(80, 10, 100, 20));
    }

    [Fact]
    public async Task FindText_MaxResults_KeepsTheFirstMatches_AndCountsThemAll()
    {
        Captures(WindowTarget, 800, 600, x: 0, y: 0, scale: 2.0);
        _engine.Read = (_, _, _, _) => new OcrPage(null, Enumerable.Range(0, 5).Select(i => FakeOcrEngine.Line(10, 10 + i * 30, "ok")).ToArray());

        var result = await CreateSut().FindTextAsync(Request(WindowTarget), Matcher("ok"), 2, null, CancellationToken.None);

        result.Matches!.Select(m => m.Bounds.Y).Should().Equal(10, 40);
        result.MatchCount.Should().Be(5);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task FindText_NoMatch_IsSuccessWithAHint()
    {
        Captures(WindowTarget, 800, 600);

        var result = await CreateSut().FindTextAsync(Request(WindowTarget), Matcher("nowhere"), 20, null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Matches.Should().BeEmpty();
        result.MatchCount.Should().Be(0);
        result.Hint.Should().Contain("read_screen_text");
        result.CaptureId.Should().Be("c7-3fa91c");
    }

    [Fact]
    public async Task FindText_CancelledWhileMatching_StopsBeforeTheNextLine()
    {
        Captures(WindowTarget, 800, 600, scale: 2.0);
        using var cancel = new CancellationTokenSource();
        _engine.OnRecognize = _ => cancel.Cancel();

        var act = () => CreateSut().FindTextAsync(Request(WindowTarget), Matcher("Canvas"), 20, null, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FindText_ARefusedCapture_IsAFailedResult()
    {
        _screenshots.Setup(s => s.CapturePixels(It.IsAny<CaptureTarget>()))
            .Throws(new CaptureException(ScreenshotErrorCode.Cloaked, "on another desktop"));

        var result = await CreateSut().FindTextAsync(Request(WindowTarget), Matcher("x"), 20, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.Cloaked));
        result.Matches.Should().BeNull();
    }

    #endregion
}
