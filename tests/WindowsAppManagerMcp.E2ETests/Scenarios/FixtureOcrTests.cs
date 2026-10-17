using System.Text.RegularExpressions;
using Moq;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// read_screen_text and find_text_on_screen (roadmap M6.3) against the fixture's canvas window, which draws
/// <see cref="FixtureContract.CanvasLines"/> as pixels only and reports where each line and word landed: OCR reads every
/// line inside its rectangle, at the primary monitor's scale and at another, and a find's click point lies on its word. The
/// no-input scenarios only read; the input-tier ones cover the canvas, minimize it, click a word through the captureId,
/// and mask the canvas as if the user's policy denied it, all on the fixture's own windows. Every scenario needs an
/// installed Windows OCR language (<see cref="WindowsOcrLanguage"/>).
/// </summary>
public class FixtureOcrTests
{
    /// <summary>
    /// How far an OCR box may stick out of the cell box the canvas reports: OCR's boxes are rounded outward to whole pixels,
    /// and at ocrScale 2 a half pixel of the enlarged image is a pixel of the screen.
    /// </summary>
    private const int BoxTolerance = 2;

    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureOcrTests(ITestOutputHelper output) => _output = output;

    [DesktopNoInputFact(Requires = typeof(WindowsOcrLanguage))]
    public async Task ReadScreenText_ReadsEveryCanvasLine_InsideTheRectangleTheFixtureDrewItIn()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenCanvas = true });
        var canvas = OpenedCanvas(e2e);

        // Act
        var result = await Ocr(e2e).ReadTextAsync(
            new OcrRequest(new CaptureTarget.Window(canvas.WindowHandle, IncludeFrame: false), null), OcrDetail.Words, 150, null,
            CancellationToken.None);

        // Assert
        Log(result);
        result.Success.Should().BeTrue(result.Error);
        result.OcrScale.Should().Be(canvas.Dpi < 144 ? 2 : 1, "small text below 150% scale is read at twice the size");
        result.CaptureId.Should().NotBeNullOrEmpty();
        AssertEveryCanvasLineIsRead(canvas, result);
    }

    [DesktopNoInputFact(Requires = typeof(MonitorAtAnotherScaleAndOcrLanguage))]
    public async Task ReadScreenText_OnAMonitorAtAnotherScale_ReadsEveryCanvasLine_InsideItsRectangle()
    {
        // Arrange - the canvas follows --other-scale-monitor
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output,
            new FixtureLaunchOptions { OpenCanvas = true, OtherScaleMonitor = true });
        var canvas = OpenedCanvas(e2e);
        canvas.Dpi.Should().NotBe((int)MonitorAtAnotherScale.PrimaryDpi, "the canvas is on the monitor at another scale");

        // Act
        var result = await Ocr(e2e).ReadTextAsync(
            new OcrRequest(new CaptureTarget.Window(canvas.WindowHandle, IncludeFrame: false), null), OcrDetail.Words, 150, null,
            CancellationToken.None);

        // Assert
        Log(result);
        result.Success.Should().BeTrue(result.Error);
        result.OcrScale.Should().Be(canvas.Dpi < 144 ? 2 : 1);
        AssertEveryCanvasLineIsRead(canvas, result);
    }

    [DesktopNoInputFact(Requires = typeof(WindowsOcrLanguage))]
    public async Task FindTextOnScreen_AWordsClickPoint_LiesOnTheWord_AndConvertsThroughTheCaptureId()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenCanvas = true });
        var canvas = OpenedCanvas(e2e);
        var lantern = canvas.Word("lantern");

        // Act
        var result = await Find(e2e, canvas.WindowHandle, "lantern", OcrTextMatch.Exact);

        // Assert - the match is the word, its click point is inside the word's cell, and the image point converts back to it
        result.Success.Should().BeTrue(result.Error);
        var match = result.Matches.Should().ContainSingle().Subject;
        _output.WriteLine($"lantern drawn at {lantern}; found {match}");
        match.Text.Should().BeEquivalentTo("lantern");
        Inside(match.Bounds, lantern).Should().BeTrue($"the match's bounds {match.Bounds} lie in the word's cell {lantern}");
        (match.ClickPoint.X, match.ClickPoint.Y).Should().Match<(int X, int Y)>(p =>
            p.X >= lantern.X && p.X < lantern.X + lantern.Width && p.Y >= lantern.Y && p.Y < lantern.Y + lantern.Height);

        var point = e2e.Server.Get<ICoordinateSpaceResolver>().FromImage(result.CaptureId!, match.ImagePoint.X, match.ImagePoint.Y);
        point.Resolved.Should().BeTrue(point.Error);
        (point.X, point.Y).Should().Be((match.ClickPoint.X, match.ClickPoint.Y), "the OCR capture is registered 1:1");
        point.CapturedWindow.Should().Be(canvas.WindowHandle);
    }

    [DesktopNoInputFact(Requires = typeof(WindowsOcrLanguage))]
    public async Task FindTextOnScreen_ARegex_FindsTheNumbersWithTheirUnits()
    {
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenCanvas = true });
        var canvas = OpenedCanvas(e2e);

        // A regex runs on the line as OCR read it, and real Windows OCR splits numbers ("471 1 km" for 4711km): the pattern
        // allows a space between the digits and before the unit, and the matches are compared without their spaces
        var result = await Find(e2e, canvas.WindowHandle, @"\d(?:\s?\d){3}\s?(?:km|kg|mm|pt)", OcrTextMatch.Regex);

        result.Success.Should().BeTrue(result.Error);
        _output.WriteLine(string.Join(", ", result.Matches!.Select(m => m.Text)));
        result.Matches!.Select(m => Regex.Replace(m.Text, @"\s+", "")).Should().Contain(["4711km", "2938kg"],
            "the two large dark lines are the ones OCR reads most reliably");
    }

    [DesktopFact(Requires = typeof(WindowsOcrLanguage))]
    public async Task ReadScreenText_ACoveredCanvas_StillReads_ButAMinimizedOneIsRefused()
    {
        // Arrange - the main window in front, over the middle of the canvas (both open centred on the primary monitor)
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenCanvas = true });
        var canvas = OpenedCanvas(e2e);
        BringToFront(e2e, e2e.Fixture.MainWindowHandle);
        var mainRect = e2e.Server.Windows.GetWindowInfo(e2e.Fixture.MainWindowHandle)!.Bounds;
        var canvasLine = canvas.Lines[0].Rect;
        _output.WriteLine($"main window {mainRect}, first canvas line {canvasLine}");
        (mainRect.X < canvasLine.X + canvasLine.Width && canvasLine.X < mainRect.X + mainRect.Width &&
         mainRect.Y < canvasLine.Y + canvasLine.Height && canvasLine.Y < mainRect.Y + mainRect.Height)
            .Should().BeTrue("the main window must cover part of the canvas's first line for this scenario to mean anything");

        // Act - covered: the window draws itself (PrintWindow), so its text still reads
        var covered = await Ocr(e2e).ReadTextAsync(
            new OcrRequest(new CaptureTarget.Window(canvas.WindowHandle, IncludeFrame: false), null), OcrDetail.Lines, 150, null,
            CancellationToken.None);

        // Assert
        Log(covered);
        covered.Success.Should().BeTrue(covered.Error);
        covered.Lines!.Select(l => Squash(l.Text)).Should().Contain(Squash(FixtureContract.CanvasLines[0].Text));

        // Act - minimized: refused, and nothing restores it
        e2e.Server.Windows.SetWindowState(canvas.WindowHandle, WindowStateRequest.Minimize).Success.Should().BeTrue();
        Wait.Until(() => e2e.Server.Get<INativeWindowWrapper>().IsIconic(canvas.WindowHandle), SettleTimeout).Should().BeTrue();
        var minimized = await Ocr(e2e).ReadTextAsync(
            new OcrRequest(new CaptureTarget.Window(canvas.WindowHandle, IncludeFrame: true), null), OcrDetail.Lines, 150, null,
            CancellationToken.None);

        // Assert
        minimized.Success.Should().BeFalse();
        minimized.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.WindowMinimized));
        e2e.Server.Get<INativeWindowWrapper>().IsIconic(canvas.WindowHandle).Should().BeTrue("the OCR tools only read: a minimized window stays minimized");
    }

    [DesktopFact(Requires = typeof(WindowsOcrLanguage))]
    public async Task MouseClick_ThroughAFindsCaptureId_HitsTheWord_AndIsStaleOnceTheCanvasMoved()
    {
        // Arrange - the canvas in front, so the click reaches it
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenCanvas = true });
        var canvas = OpenedCanvas(e2e);
        BringToFront(e2e, canvas.WindowHandle);
        var found = await Find(e2e, canvas.WindowHandle, "quartz", OcrTextMatch.Exact);
        found.Success.Should().BeTrue(found.Error);
        var match = found.Matches.Should().ContainSingle().Subject;
        var resolver = e2e.Server.Get<ICoordinateSpaceResolver>();
        var after = e2e.Fixture.Events.ReadAll().LastOrDefault()?.Sequence ?? 0;

        // Act - as mouse_click does with x, y and captureId: the image point converts to the screen, then the click goes there
        var point = resolver.FromImage(found.CaptureId!, match.ImagePoint.X, match.ImagePoint.Y);
        point.Resolved.Should().BeTrue(point.Error);
        var click = e2e.Server.Input.MouseClick(point.X, point.Y, MouseButton.Primary, 1, [], InputTarget.Window(canvas.WindowHandle),
            releaseHeldModifiers: false, CancellationToken.None);

        // Assert - the canvas logged a press on the word
        click.Success.Should().BeTrue(click.Error);
        var press = e2e.Fixture.WaitForEvent(FixtureContract.Events.CanvasMouseDown, e => e.Sequence > after);
        _output.WriteLine(press.ToString());
        press.GetString("word").Should().Be("quartz");

        // Act - the canvas moves: the same image point is now stale, and nothing would be clicked
        var bounds = e2e.Server.Windows.GetWindowInfo(canvas.WindowHandle)!.Bounds;
        e2e.Server.Windows.MoveWindow(canvas.WindowHandle, bounds.X + 40, bounds.Y + 30, bringToFront: false).Success.Should().BeTrue();
        var stale = resolver.FromImage(found.CaptureId!, match.ImagePoint.X, match.ImagePoint.Y);

        // Assert
        stale.Resolved.Should().BeFalse();
        stale.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
    }

    [DesktopFact(Requires = typeof(WindowsOcrLanguage))]
    public async Task ReadScreenText_ARegionOverACanvasThePolicyDenies_BlacksItOut()
    {
        // Arrange - the canvas in front, and an OCR service whose policy denies the canvas window (as a deny list naming
        // WamFixtureApp would; the harness's own policy denies nothing of the fixture)
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenCanvas = true });
        var canvas = OpenedCanvas(e2e);
        BringToFront(e2e, canvas.WindowHandle);
        var policy = new Mock<ICaptureTargetPolicy>();
        policy.Setup(p => p.Check(It.IsAny<nint>())).Returns((nint handle) => handle == canvas.WindowHandle
            ? new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "the scenario denies WamFixtureApp")
            : null);
        var ocr = new OcrService(e2e.Server.Screenshots, e2e.Server.Get<IOcrEngineWrapper>(), e2e.Server.Get<INativeWindowWrapper>(),
            policy.Object, null);
        var lines = canvas.Lines.Select(l => l.Rect).ToList();
        var region = new CaptureTarget.Region(
            lines.Min(r => r.X), lines.Min(r => r.Y),
            lines.Max(r => r.X + r.Width) - lines.Min(r => r.X), lines.Max(r => r.Y + r.Height) - lines.Min(r => r.Y));

        // Act
        var result = await ocr.ReadTextAsync(new OcrRequest(region, null), OcrDetail.Lines, 150, null, CancellationToken.None);

        // Assert - none of the canvas's words was read, and the canvas counts as masked
        Log(result);
        result.Success.Should().BeTrue(result.Error);
        result.MaskedWindows.Should().BeGreaterThanOrEqualTo(1);
        var words = FixtureContract.CanvasLines.SelectMany(l => FixtureContract.CanvasWords(l.Text)).ToList();
        result.Lines!.SelectMany(l => l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => words.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Should().BeEmpty("the denied window's pixels were black before OCR read them");
    }

    private static IOcrService Ocr(E2ESession e2e) => e2e.Server.Get<IOcrService>();

    private static FixtureCanvas OpenedCanvas(E2ESession e2e) =>
        FixtureCanvas.From(e2e.Fixture.WaitForEvent(FixtureContract.Events.CanvasOpened));

    private static async Task<TextFindResult> Find(E2ESession e2e, nint canvas, string text, OcrTextMatch match)
    {
        var matcher = OcrTextMatcher.TryCreate(text, match, caseSensitive: false, out var invalid)
                      ?? throw new ArgumentException(invalid, nameof(text));
        return await Ocr(e2e).FindTextAsync(new OcrRequest(new CaptureTarget.Window(canvas, IncludeFrame: false), null), matcher, 20,
            null, CancellationToken.None);
    }

    private static void BringToFront(E2ESession e2e, nint window)
    {
        var focus = e2e.Server.Foreground.EnsureForeground(window);
        Wait.Until(() => E2ENative.GetAncestor(E2ENative.GetForegroundWindow(), E2ENative.GA_ROOTOWNER) == window, SettleTimeout)
            .Should().BeTrue($"focus_window reported isForeground={focus.IsForeground} ({focus.ErrorCode}: {focus.Error})");
    }

    /// <summary>
    /// Each canvas line is an OCR line with the same text as real Windows OCR reads it (<see cref="Squash"/>: look-alike
    /// characters folded and its spaces inside numbers ignored, on every line), whose box, and each whole word's, lies in the
    /// drawn cell. The desktop check records the misreads; the boxes stay strict.
    /// </summary>
    private void AssertEveryCanvasLineIsRead(FixtureCanvas canvas, ScreenTextResult result)
    {
        var problems = new List<string>();
        foreach (var drawn in canvas.Lines)
        {
            var read = result.Lines!.FirstOrDefault(l => Squash(l.Text) == Squash(drawn.Text));
            if (read == null)
            {
                problems.Add($"'{drawn.Text}' was not read as one line");
                continue;
            }

            if (!Inside(read.Bounds, drawn.Rect))
            {
                problems.Add($"'{drawn.Text}' was read at {read.Bounds}, outside its cell {drawn.Rect}");
            }

            foreach (var word in drawn.Words)
            {
                var readWord = read.Words?.FirstOrDefault(w => Fold(w.Text.ToUpperInvariant()) == Fold(word.Text.ToUpperInvariant()));
                if (readWord != null && !Inside(readWord.Bounds, word.Rect))
                {
                    problems.Add($"'{word.Text}' was read at {readWord.Bounds}, outside its cell {word.Rect}");
                }
            }
        }

        problems.Should().BeEmpty("OCR read: " + string.Join(" | ", result.Lines!.Select(l => $"'{l.Text}' {l.Bounds}")));
    }

    private static bool Inside(BoundsDto box, ScreenRect cell) =>
        box.X >= cell.X - BoxTolerance && box.Y >= cell.Y - BoxTolerance &&
        box.X + box.Width <= cell.X + cell.Width + BoxTolerance && box.Y + box.Height <= cell.Y + cell.Height + BoxTolerance;

    private static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ").ToUpperInvariant();

    /// <summary>
    /// A line as real Windows OCR may read it, for comparison: upper case, look-alike characters folded (<see cref="Fold"/>),
    /// and no whitespace next to a digit, since OCR splits numbers and their units ("471 1 km" for "4711km", "314 Ipt" for
    /// "3141pt").
    /// </summary>
    private static string Squash(string text) =>
        Regex.Replace(Fold(Normalize(text)), @"(?<=\d)\s+|\s+(?=\d)", "");

    /// <summary>Upper-case text with the characters OCR confuses made one: O as 0, and L and I as 1.</summary>
    private static string Fold(string upper) => upper.Replace('O', '0').Replace('L', '1').Replace('I', '1');

    private void Log(ScreenTextResult result) =>
        _output.WriteLine($"{result.ErrorCode} {result.Error} language={result.Language} ocrScale={result.OcrScale} " +
                          $"region={result.CapturedRegion} masked={result.MaskedWindows} warnings={string.Join("; ", result.Warnings ?? [])}" +
                          Environment.NewLine + string.Join(Environment.NewLine, (result.Lines ?? []).Select(l => $"  '{l.Text}' {l.Bounds}")));
}
