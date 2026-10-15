using System.Globalization;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The fixture's command line: the harness writes it with ToArguments and the fixture reads it with
/// Parse (both compile the same FixtureOptions.cs). Pure tests; nothing is started.
/// </summary>
public class FixtureOptionsTests
{
    [Fact]
    public void ToArguments_ThenParse_RoundTripsEveryOption()
    {
        // Arrange
        var options = new FixtureOptions(
            Instance: "a1b2-c3",
            EventLogPath: @"C:\Temp\Wam E2E\events.jsonl",
            ParentProcessId: 4242,
            MaxLifetime: TimeSpan.FromSeconds(90),
            NoActivate: true,
            OpenModal: true,
            OpenDocument: true,
            DpiAwareness: FixtureDpiAwareness.Unaware,
            OpenTable: true,
            OpenMenuWindow: true,
            OpenWebView: true,
            OtherScaleMonitor: true,
            OpenCanvas: true,
            ChangeAfter: TimeSpan.FromMilliseconds(800),
            AnimateFor: TimeSpan.FromMilliseconds(1500),
            OpenOverlay: true);

        // Act
        var parsed = FixtureOptions.Parse(options.ToArguments());

        // Assert
        parsed.Should().Be(options);
    }

    [Fact]
    public void ToArguments_ThenParse_RoundTripsTheDefaults()
    {
        var options = new FixtureOptions("x");

        FixtureOptions.Parse(options.ToArguments()).Should().Be(options);
    }

    [Theory]
    [InlineData(FixtureDpiAwareness.PerMonitorV2)]
    [InlineData(FixtureDpiAwareness.PerMonitor)]
    [InlineData(FixtureDpiAwareness.System)]
    [InlineData(FixtureDpiAwareness.Unaware)]
    public void DpiAwareness_RoundTrips(FixtureDpiAwareness awareness)
    {
        var options = new FixtureOptions("x", DpiAwareness: awareness);

        FixtureOptions.Parse(options.ToArguments()).DpiAwareness.Should().Be(awareness);
    }

    [Fact]
    public void Parse_WithNoArguments_UsesTheManualDefaults()
    {
        // Act
        var options = FixtureOptions.Parse([]);

        // Assert - starting the exe by hand works, with no log, no watchdog parent and the default lifetime
        options.Instance.Should().Be(FixtureOptions.DefaultInstance);
        options.EventLogPath.Should().BeNull();
        options.ParentProcessId.Should().BeNull();
        options.EffectiveMaxLifetime.Should().Be(FixtureOptions.DefaultMaxLifetime);
        options.NoActivate.Should().BeFalse();
        options.DpiAwareness.Should().Be(FixtureDpiAwareness.PerMonitorV2);
        options.OpenTable.Should().BeFalse("an ordinary start opens no extra window");
        options.OpenMenuWindow.Should().BeFalse();
        options.OpenWebView.Should().BeFalse("only --open-web-view starts WebView2");
        options.OpenCanvas.Should().BeFalse();
        options.OpenOverlay.Should().BeFalse("no topmost window unless a scenario asks for one");
        options.ChangeAfter.Should().BeNull("the main window's pixels do not change by themselves");
        options.AnimateFor.Should().BeNull();
    }

    [Theory]
    [InlineData("--open-table")]
    [InlineData("--open-menu-window")]
    [InlineData("--open-web-view")]
    [InlineData("--open-canvas")]
    [InlineData("--open-overlay")]
    public void Parse_ReadsAnOpenOption_AsAFlag_ThatWritesBackAlone(string option)
    {
        // Act
        var options = FixtureOptions.Parse([option]);

        // Assert - the flag takes no value, opens a window an ordinary start does not, and is all ToArguments adds
        options.Should().NotBe(new FixtureOptions(FixtureOptions.DefaultInstance));
        options.ToArguments().Should().Equal(FixtureOptions.InstanceOption, FixtureOptions.DefaultInstance, option);
    }

    [Theory]
    [InlineData("--change-after-ms", "800")]
    [InlineData("--animate-for-ms", "1500")]
    public void Parse_ReadsATimedChange_InMilliseconds_ThatWritesBackAlone(string option, string milliseconds)
    {
        // Act
        var options = FixtureOptions.Parse([option, milliseconds]);

        // Assert
        var expected = TimeSpan.FromMilliseconds(int.Parse(milliseconds, CultureInfo.InvariantCulture));
        (option == FixtureOptions.ChangeAfterOption ? options.ChangeAfter : options.AnimateFor).Should().Be(expected);
        options.ToArguments().Should().Equal(FixtureOptions.InstanceOption, FixtureOptions.DefaultInstance, option, milliseconds);
    }

    [Fact]
    public void Parse_IgnoresEverythingAfterTheEndOfOptions_SoATestCanPlantArguments()
    {
        // Arrange - what M6.6's command-line redaction check plants: a secret-looking switch and a URL with userinfo,
        // and even a known option's spelling, which after "--" is only text
        var args = new[]
        {
            "--instance", "abc", "--no-activate",
            FixtureOptions.EndOfOptions, "--wam-test-token=s3cr3t", "http://u:p@host/", "--instance", "--unknown"
        };

        // Act
        var options = FixtureOptions.Parse(args);

        // Assert
        options.Should().Be(new FixtureOptions("abc", NoActivate: true));
        FixtureOptions.IgnoredArgumentCount(args).Should().Be(4);
    }

    [Fact]
    public void IgnoredArgumentCount_IsZero_WithoutTheEndOfOptions()
    {
        FixtureOptions.IgnoredArgumentCount(new FixtureOptions("abc").ToArguments()).Should().Be(0);
        FixtureOptions.IgnoredArgumentCount(["--instance", "abc", FixtureOptions.EndOfOptions]).Should().Be(0);
    }

    [Fact]
    public void Parse_NamesTheEndOfOptions_WhenAnOptionIsUnknown()
    {
        // Arrange - an unknown option before "--" is still refused, so a misspelt option cannot be silently ignored
        var act = () => FixtureOptions.Parse(["--wam-test-token=s3cr3t"]);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown option*--*");
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--instance")]
    [InlineData("--instance", "--no-activate")]
    [InlineData("--instance", "has space")]
    [InlineData("--instance", "way-too-long-instance-token-0123456789")]
    [InlineData("--parent-pid", "0")]
    [InlineData("--parent-pid", "-5")]
    [InlineData("--parent-pid", "abc")]
    [InlineData("--max-lifetime-seconds", "3601")]
    [InlineData("--dpi-awareness", "perMonitorV3")]
    [InlineData("--no-activate", "--no-activate")]
    [InlineData("--open-table", "--open-table")]
    [InlineData("--open-table", "yes")]
    [InlineData("--open-menu-window", "--open-menu-window")]
    [InlineData("--open-web-view", "1")]
    [InlineData("--open-canvas", "--open-canvas")]
    [InlineData("--open-overlay", "yes")]
    [InlineData("--change-after-ms")]
    [InlineData("--change-after-ms", "0")]
    [InlineData("--change-after-ms", "1.5")]
    [InlineData("--change-after-ms", "800", "--change-after-ms", "900")]
    [InlineData("--animate-for-ms", "-1")]
    [InlineData("--animate-for-ms", "3600001")]
    [InlineData("--animate-for-ms", "--open-canvas")]
    [InlineData("--wam-test-token=s3cr3t", "--")]
    public void Parse_RejectsInvalidCommandLines(params string[] args)
    {
        var act = () => FixtureOptions.Parse(args);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToArguments_RejectsAnInstanceTheFixtureWouldRefuse()
    {
        var act = () => new FixtureOptions("not valid!").ToArguments();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Titles_CarryTheInstanceAndDoNotContainEachOther()
    {
        // Arrange - find_windows matches titles by substring
        var titles = new[]
        {
            FixtureContract.MainTitle("abc"),
            FixtureContract.ModalTitle("abc"),
            FixtureContract.DocumentTitle("abc", dirty: true),
            FixtureContract.SavePromptTitle("abc"),
            FixtureContract.TableTitle("abc"),
            FixtureContract.MenuTitle("abc"),
            FixtureContract.SaveAsTitle("abc"),
            FixtureContract.TaskDialogTitle("abc"),
            FixtureContract.WebTitle("abc"),
            FixtureContract.CanvasTitle("abc"),
            FixtureContract.OverlayTitle("abc")
        };

        // Assert
        titles.Should().OnlyContain(t => t.Contains("abc"));
        foreach (var title in titles)
        {
            titles.Where(other => other != title).Should().NotContain(other => other.Contains(title));
        }
    }

    [Fact]
    public void CanvasLines_HaveWordsFoundOnlyOnce_AndNoTextAnyTitleOrControlCarries()
    {
        // Arrange - an OCR check finds a word by its text and expects one hit; the canvas text must also be nowhere that
        // UI Automation could read it, so a hit cannot come from a title or a control
        var words = FixtureContract.CanvasLines.SelectMany(l => FixtureContract.CanvasWords(l.Text)).ToList();
        var uiaTexts = new[] { FixtureContract.CanvasTitle("abc"), FixtureContract.MainTitle("abc") }
            .Concat(FixtureContract.ComboBoxItems);

        // Assert
        FixtureContract.CanvasLines.Should().HaveCountGreaterThanOrEqualTo(3);
        FixtureContract.CanvasLines.Should().Contain(l => l.LightOnDark, "OCR is checked on light text on a dark band too");
        FixtureContract.CanvasLines.Should().Contain(l => l.FontSize <= 12, "and on small (9 pt) text");
        words.Should().OnlyHaveUniqueItems(w => w.ToUpperInvariant());
        foreach (var text in uiaTexts)
        {
            words.Should().NotContain(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CanvasWords_SplitsALineAtSpaces_WithTheirOffsets()
    {
        FixtureContract.CanvasWordSpans("Glyph quartz  4711").Should().Equal((0, 5), (6, 6), (14, 4));
    }

    [Fact]
    public void ChangePanel_LooksDifferentAfterTheChange()
    {
        // Assert - a pixel diff and a reader both see the change: another color and another label
        var before = FixtureContract.ChangePanelBefore;
        var after = FixtureContract.ChangePanelAfter;
        (before.R, before.G, before.B).Should().NotBe((after.R, after.G, after.B));
        FixtureContract.ChangePanelBeforeText.Should().NotBe(FixtureContract.ChangePanelAfterText);
    }
}
