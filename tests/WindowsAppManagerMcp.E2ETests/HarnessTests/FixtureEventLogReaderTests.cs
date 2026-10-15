using System.Text;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>Reading the fixture's event log while it is written. Uses a temp file; no fixture runs.</summary>
public class FixtureEventLogReaderTests : IDisposable
{
    private const string ReadyLine =
        """{"seq":1,"event":"ready","time":"2026-10-10T12:00:00+02:00","pid":4242,"hwnd":4096,"title":"WAM Fixture Main abc","dpi":144,"dpiAwareness":"perMonitorV2","windowRect":{"x":-1913,"y":100,"width":1140,"height":810},"clientRect":{"x":-1902,"y":145,"width":1118,"height":754},"elements":{"editBox":{"x":-1884,"y":190,"width":480,"height":33},"swatchRed":{"x":-1884,"y":600,"width":96,"height":60}}}""";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WamE2E-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public FixtureEventLogReaderTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "events.jsonl");
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ReadAll_BeforeTheFileExists_ReturnsNothing()
    {
        new FixtureEventLogReader(_path).ReadAll().Should().BeEmpty();
    }

    [Fact]
    public void ReadAll_ReturnsOnlyCompleteLines_AndPicksUpTheRestLater()
    {
        // Arrange
        var reader = new FixtureEventLogReader(_path);
        Append("""{"seq":1,"event":"textChanged","id":"editBox","text":"a"}""" + "\n" + """{"seq":2,"event":"text""");

        // Act
        var first = reader.ReadAll();
        Append("""Changed","id":"editBox","text":"ab"}""" + "\n");
        var second = reader.ReadAll();

        // Assert
        first.Should().ContainSingle().Which.GetString("text").Should().Be("a");
        second.Select(e => e.Sequence).Should().Equal(1, 2);
        second[1].GetString("text").Should().Be("ab");
    }

    [Fact]
    public void ReadAll_KeepsAUtf8CharacterThatIsSplitBetweenWrites()
    {
        // Arrange - "æ" is two bytes in UTF-8; the fixture may have flushed only the first one
        var reader = new FixtureEventLogReader(_path);
        var bytes = Encoding.UTF8.GetBytes("""{"seq":1,"event":"textChanged","id":"editBox","text":"æ"}""" + "\n");
        var split = Array.IndexOf(bytes, (byte)0xC3) + 1;

        // Act
        AppendBytes(bytes[..split]);
        var first = reader.ReadAll();
        AppendBytes(bytes[split..]);
        var second = reader.ReadAll();

        // Assert
        first.Should().BeEmpty();
        second.Should().ContainSingle().Which.GetString("text").Should().Be("æ");
    }

    [Fact]
    public void WaitFor_ReturnsTheFirstMatchingEvent()
    {
        Append(ReadyLine + "\n");

        var ready = new FixtureEventLogReader(_path).WaitFor(e => e.Name == "ready", TimeSpan.FromSeconds(1), "ready");

        ready.Sequence.Should().Be(1);
    }

    [Fact]
    public void WaitFor_WhenTheEventNeverComes_TimesOutWithTheLogInTheMessage()
    {
        Append("""{"seq":1,"event":"keyboardFocus","id":"editBox"}""" + "\n");

        var act = () => new FixtureEventLogReader(_path)
            .WaitFor(e => e.Name == "ready", TimeSpan.FromMilliseconds(200), "its ready event");

        act.Should().Throw<TimeoutException>().WithMessage("*its ready event*keyboardFocus*");
    }

    [Fact]
    public void WaitFor_StopsAsSoonAsTheAbortReasonIsSet()
    {
        var reader = new FixtureEventLogReader(_path);

        var act = () => reader.WaitFor(e => e.Name == "ready", TimeSpan.FromMinutes(5), "its ready event",
            () => "the fixture exited with code 2");

        act.Should().Throw<TimeoutException>().WithMessage("*the fixture exited with code 2*");
    }

    [Fact]
    public void FixtureReady_ReadsWhatTheFixtureReports()
    {
        // Arrange
        Append(ReadyLine + "\n");
        var ready = new FixtureEventLogReader(_path).ReadAll().Single();

        // Act
        var parsed = FixtureReady.From(ready);

        // Assert - negative coordinates are normal on a monitor left of the primary one
        parsed.ProcessId.Should().Be(4242);
        parsed.MainWindowHandle.Should().Be((nint)4096);
        parsed.Title.Should().Be("WAM Fixture Main abc");
        parsed.Dpi.Should().Be(144);
        parsed.DpiAwareness.Should().Be("perMonitorV2");
        parsed.WindowRect.Should().Be(new ScreenRect(-1913, 100, 1140, 810));
        parsed.ClientRect.Should().Be(new ScreenRect(-1902, 145, 1118, 754));
        parsed.Element(FixtureContract.Ids.EditBox).Should().Be(new ScreenRect(-1884, 190, 480, 33));
        parsed.Element(FixtureContract.Ids.SwatchRed).CenterX.Should().Be(-1884 + 48);
    }

    [Fact]
    public void FixtureReady_RejectsOtherEvents()
    {
        var act = () => FixtureReady.From(FixtureEvent.Parse("""{"seq":3,"event":"modalOpened","hwnd":1}"""));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FixtureCanvas_ReadsTheLinesAndWordsTheCanvasDrew()
    {
        // Arrange - the canvas window's report (M6.3): where each line and word is on the screen
        var opened = FixtureEvent.Parse(
            """
            {"seq":5,"event":"canvasOpened","hwnd":8192,"title":"WAM Fixture Canvas abc","dpi":96,
             "lines":[{"text":"Glyph quartz 4711","rect":{"x":-500,"y":40,"width":300,"height":38},
                       "words":[{"text":"Glyph","rect":{"x":-500,"y":40,"width":80,"height":38}},
                                {"text":"quartz","rect":{"x":-410,"y":40,"width":90,"height":38}}]}]}
            """);

        // Act
        var canvas = FixtureCanvas.From(opened);

        // Assert
        canvas.WindowHandle.Should().Be((nint)8192);
        canvas.Title.Should().Be("WAM Fixture Canvas abc");
        canvas.Dpi.Should().Be(96);
        canvas.Lines.Should().ContainSingle().Which.Text.Should().Be("Glyph quartz 4711");
        canvas.Lines[0].Rect.Should().Be(new ScreenRect(-500, 40, 300, 38));
        canvas.Word("quartz").Should().Be(new ScreenRect(-410, 40, 90, 38));
    }

    [Fact]
    public void FixtureCanvas_NamesAWordItDidNotDraw()
    {
        var canvas = new FixtureCanvas(1, "t", 96, []);

        var act = () => canvas.Word("missing");

        act.Should().Throw<KeyNotFoundException>().WithMessage("*missing*");
    }

    [Fact]
    public void FixtureCanvas_RejectsOtherEvents()
    {
        var act = () => FixtureCanvas.From(FixtureEvent.Parse(ReadyLine));

        act.Should().Throw<ArgumentException>();
    }

    private void Append(string text) => AppendBytes(Encoding.UTF8.GetBytes(text));

    private void AppendBytes(byte[] bytes)
    {
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }
}
