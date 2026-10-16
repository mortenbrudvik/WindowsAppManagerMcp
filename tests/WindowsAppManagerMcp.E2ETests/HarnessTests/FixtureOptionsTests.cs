using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

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

    [Theory]
    [InlineData("--change-after-ms", "300000")]
    [InlineData("--animate-for-ms", "300001")]
    [InlineData("--max-lifetime-seconds", "10", "--change-after-ms", "10000")]
    [InlineData("--change-after-ms", "20000", "--max-lifetime-seconds", "10")]
    [InlineData("--max-lifetime-seconds", "10", "--animate-for-ms", "9990")]
    [InlineData("--max-lifetime-seconds", "60", "--change-after-ms", "45000")]
    public void Parse_RejectsATimedChange_TheFixtureWouldNotLiveToMake(params string[] args)
    {
        // Arrange - the watchdog ends the fixture at its lifetime (5 min unless given), counted from the start, while the
        // change is counted from the ready event, which can come seconds later; a change due within the margin of the
        // lifetime might never be logged and a test would only time out waiting for it. The order of the options does not
        // matter
        var act = () => FixtureOptions.Parse(args);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*lifetime*");
    }

    [Fact]
    public void Parse_AcceptsATimedChange_WithinALongerLifetime()
    {
        var options = FixtureOptions.Parse(["--change-after-ms", "400000", "--max-lifetime-seconds", "600"]);

        options.ChangeAfter.Should().Be(TimeSpan.FromMilliseconds(400000));
    }

    [Theory]
    [InlineData(FixtureOptions.ChangeAfterOption)]
    [InlineData(FixtureOptions.AnimateForOption)]
    public void Parse_HoldsATimedChange_ToTheLifetimeLessTheMargin(string option)
    {
        // Arrange - a 60 s lifetime leaves room for a change due up to the margin before it, and not a millisecond later
        var latest = (int)(TimeSpan.FromSeconds(60) - FixtureOptions.TimedChangeMargin).TotalMilliseconds;
        string[] Args(int milliseconds) =>
            ["--max-lifetime-seconds", "60", option, milliseconds.ToString(CultureInfo.InvariantCulture)];

        // Act
        var accept = () => FixtureOptions.Parse(Args(latest));
        var reject = () => FixtureOptions.Parse(Args(latest + 1));

        // Assert
        accept.Should().NotThrow();
        reject.Should().Throw<ArgumentException>().WithMessage("*lifetime*");
    }

    [Fact]
    public void TimedChangeMargin_CoversTheWaitForTheReadyEvent_AndTheSpinnersLastTick()
    {
        // Assert - the lifetime runs from the start and the change from the ready event, which the harness waits up to
        // ReadyTimeout for; the spinner stops on the first tick at or after AnimateFor
        FixtureOptions.TimedChangeMargin.Should().BeGreaterThanOrEqualTo(
            new FixtureLaunchOptions().ReadyTimeout + TimeSpan.FromMilliseconds(FixtureContract.SpinnerIntervalMs));
    }

    public static TheoryData<TimeSpan?, TimeSpan?> TimedChangesTheFixtureWouldRefuse => new()
    {
        { TimeSpan.Zero, null },
        { TimeSpan.FromMilliseconds(-5), null },
        { TimeSpan.FromMinutes(6), null },
        { null, TimeSpan.Zero },
        { null, TimeSpan.FromMinutes(5) },
        { TimeSpan.FromSeconds(290), null },
        { null, TimeSpan.FromDays(30) }
    };

    [Theory]
    [MemberData(nameof(TimedChangesTheFixtureWouldRefuse))]
    public void ToArguments_RejectsATimedChangeTheFixtureWouldRefuseOrNotLiveToMake(TimeSpan? changeAfter, TimeSpan? animateFor)
    {
        // Arrange - the default lifetime is 5 min; refusing here names the mistake, where the fixture could only exit with
        // code 2 or end before the change
        var options = new FixtureOptions("abc", ChangeAfter: changeAfter, AnimateFor: animateFor);

        // Act
        var act = () => options.ToArguments();

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage(changeAfter != null ? "*ChangeAfter*" : "*AnimateFor*");
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
        // UI Automation could read it or another window could show it, so a hit cannot come from a title or a control.
        // That is every title, every text the contract names, and every string the fixture's code holds (captions,
        // labels, the overlay's text, the web page), apart from the canvas lines themselves
        var words = FixtureContract.CanvasLines.SelectMany(l => FixtureContract.CanvasWords(l.Text)).ToList();
        var uiaTexts = FixtureTitles("abc")
            .Concat(ContractTexts())
            .Concat(FixtureSourceStrings().Except(FixtureContract.CanvasLines.Select(l => l.Text)))
            .ToList();
        var hexDigits = "0123456789abcdef";

        // Assert
        FixtureContract.CanvasLines.Should().HaveCountGreaterThanOrEqualTo(3);
        FixtureContract.CanvasLines.Should().Contain(l => l.LightOnDark, "OCR is checked on light text on a dark band too");
        FixtureContract.CanvasLines.Should().Contain(l => l.FontSize <= 12, "and on small (9 pt) text");
        words.Should().OnlyHaveUniqueItems(w => w.ToUpperInvariant());
        foreach (var text in uiaTexts)
        {
            words.Should().NotContain(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        }

        // The titles carry the harness's instance token, 12 random hex digits, which no fixed instance stands for: a word
        // of hex digits alone (such as "4711") would turn up in some instance's title bar, and OCR ignores case
        words.Should().OnlyContain(
            w => w.Any(c => !hexDigits.Contains(char.ToLowerInvariant(c))),
            "a word made only of hex digits can appear in the random instance token of a title");
    }

    [Fact]
    public void FixtureSourceStrings_FindTheCaptionsTheContractDoesNotName()
    {
        // Assert - the canvas-word check above sees the fixture's own captions, not just the contract's texts
        var strings = FixtureSourceStrings();

        strings.Should().Contain("Topmost overlay").And.Contain("Check box").And.Contain("Right-click here for a context menu");
        strings.Should().Contain(FixtureContract.CanvasLines[0].Text, "the canvas lines are literals too, which the check leaves out");
    }

    private static IEnumerable<string> FixtureTitles(string instance) =>
    [
        FixtureContract.MainTitle(instance),
        FixtureContract.ModalTitle(instance),
        FixtureContract.DocumentTitle(instance, dirty: true),
        FixtureContract.DocumentTitle(instance, dirty: false),
        FixtureContract.SavePromptTitle(instance),
        FixtureContract.SaveAsTitle(instance),
        FixtureContract.TaskDialogTitle(instance),
        FixtureContract.TableTitle(instance),
        FixtureContract.MenuTitle(instance),
        FixtureContract.WebTitle(instance),
        FixtureContract.CanvasTitle(instance),
        FixtureContract.OverlayTitle(instance)
    ];

    /// <summary>
    /// Every text the contract names: its string constants and string lists, in it and its nested classes, and samples of
    /// the generated list items, table cells and row headers.
    /// </summary>
    private static IEnumerable<string> ContractTexts()
    {
        var types = new[] { typeof(FixtureContract) }.Concat(typeof(FixtureContract).GetNestedTypes());
        foreach (var field in types.SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static)))
        {
            switch (field.GetValue(null))
            {
                case string text:
                    yield return text;
                    break;
                case IEnumerable<string> texts:
                    foreach (var text in texts)
                    {
                        yield return text;
                    }
                    break;
            }
        }

        foreach (var index in new[] { 0, 1, 815, 2938, 3141, 4711, FixtureContract.VirtualListItemCount - 1 })
        {
            yield return FixtureContract.VirtualListItem(index);
        }
        foreach (var index in new[] { 0, 1, 815, FixtureContract.TableRowCount - 1 })
        {
            foreach (var cell in FixtureContract.TableRow(index))
            {
                yield return cell;
            }
            yield return FixtureContract.TableRowHeader(index);
        }
    }

    /// <summary>
    /// Every string literal in the fixture app's code (comments left out): the captions, labels and page text its windows
    /// show, which the contract does not all name.
    /// </summary>
    private static IReadOnlyList<string> FixtureSourceStrings()
    {
        var folder = Path.Combine(RepositoryRoot(), "tests", "FixtureApp");
        var literal = new Regex(@"""(?:[^""\\]|\\.)*""");
        return Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(File.ReadLines)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(line => literal.Matches(line).Select(m => m.Value[1..^1]))
            .Where(text => text.Length > 0)
            .Distinct()
            .ToList();
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "WindowsAppManagerMcp.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException($"No WindowsAppManagerMcp.sln above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void CanvasWords_SplitsALineAtSpaces_WithTheirOffsets()
    {
        FixtureContract.CanvasWordSpans("Glyph quartz  4711km").Should().Equal((0, 5), (6, 6), (14, 6));
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
