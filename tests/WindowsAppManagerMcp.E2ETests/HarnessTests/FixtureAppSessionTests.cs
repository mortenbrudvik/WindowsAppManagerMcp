namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// How the harness starts the fixture, checked without starting it: the gate runs before anything is
/// located or started, and the command line always carries the watchdog settings.
/// </summary>
public class FixtureAppSessionTests
{
    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void Launch_WhenTheTierIsNotOptedIn_RefusesBeforeLocatingTheFixture(DesktopTier tier)
    {
        // Arrange - if the gate ever let this through, locating would throw, so nothing can start
        var located = false;
        string Locate()
        {
            located = true;
            throw new InvalidOperationException("locate must not be reached");
        }

        // Act
        var act = () => FixtureAppSession.Launch(tier, new FixtureLaunchOptions(), null, Locate, _ => null);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Refusing to touch the desktop*");
        located.Should().BeFalse();
    }

    [Fact]
    public void BuildOptions_SetsTheEventLogAndTheWatchdog()
    {
        // Arrange
        var launch = new FixtureLaunchOptions { MaxLifetime = TimeSpan.FromSeconds(120) };

        // Act
        var options = FixtureAppSession.BuildOptions(DesktopTier.NoInput, launch, "abc", @"C:\t\events.jsonl", 1234);

        // Assert
        options.Instance.Should().Be("abc");
        options.EventLogPath.Should().Be(@"C:\t\events.jsonl");
        options.ParentProcessId.Should().Be(1234);
        options.MaxLifetime.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void BuildOptions_ByDefault_ShowsTheFixtureWithoutActivatingIt(DesktopTier tier)
    {
        var options = FixtureAppSession.BuildOptions(tier, new FixtureLaunchOptions(), "abc", "events.jsonl", 1);

        options.NoActivate.Should().BeTrue();
    }

    [Fact]
    public void BuildOptions_ForANoInputTest_RefusesToActivateTheFixture()
    {
        var act = () => FixtureAppSession.BuildOptions(
            DesktopTier.NoInput, new FixtureLaunchOptions { Activate = true }, "abc", "events.jsonl", 1);

        act.Should().Throw<ArgumentException>().WithMessage("*cannot activate*");
    }

    [Fact]
    public void BuildOptions_ForAnInputTest_CanActivateTheFixture()
    {
        var options = FixtureAppSession.BuildOptions(
            DesktopTier.Input, new FixtureLaunchOptions { Activate = true }, "abc", "events.jsonl", 1);

        options.NoActivate.Should().BeFalse();
    }

    [Fact]
    public void BuildOptions_PassesTheModalDocumentAndDpiChoices()
    {
        // Arrange
        var launch = new FixtureLaunchOptions
        {
            OpenModal = true,
            OpenDocument = true,
            DpiAwareness = FixtureDpiAwareness.System
        };

        // Act
        var options = FixtureAppSession.BuildOptions(DesktopTier.NoInput, launch, "abc", "events.jsonl", 1);

        // Assert
        options.OpenModal.Should().BeTrue();
        options.OpenDocument.Should().BeTrue();
        options.DpiAwareness.Should().Be(FixtureDpiAwareness.System);
    }

    [Fact]
    public void BuildOptions_PassesTheSurfacesToOpenAtStart()
    {
        // Arrange - the M3.F4 surfaces a no-input test cannot open with a click
        var launch = new FixtureLaunchOptions { OpenTable = true, OpenMenuWindow = true, OpenWebView = true, OtherScaleMonitor = true };

        // Act
        var options = FixtureAppSession.BuildOptions(DesktopTier.NoInput, launch, "abc", "events.jsonl", 1);

        // Assert
        options.OpenTable.Should().BeTrue();
        options.OpenMenuWindow.Should().BeTrue();
        options.OpenWebView.Should().BeTrue();
        options.OtherScaleMonitor.Should().BeTrue();
    }

    [Fact]
    public void BuildOptions_ByDefault_OpensNoExtraSurface()
    {
        var options = FixtureAppSession.BuildOptions(DesktopTier.Input, new FixtureLaunchOptions(), "abc", "events.jsonl", 1);

        options.OpenTable.Should().BeFalse();
        options.OpenMenuWindow.Should().BeFalse();
        options.OpenWebView.Should().BeFalse("only a scenario that asks for it starts WebView2");
        options.OtherScaleMonitor.Should().BeFalse("the fixture opens on the primary unless a scenario asks");
        options.OpenCanvas.Should().BeFalse();
        options.OpenOverlay.Should().BeFalse("no topmost window unless a scenario asks for one");
        options.ExcludeOverlayFromCapture.Should().BeFalse("no window keeps out of captures unless a scenario asks for one");
        options.ChangeAfter.Should().BeNull();
        options.AnimateFor.Should().BeNull();
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void BuildOptions_PassesTheM6Surfaces(DesktopTier tier)
    {
        // Arrange - the canvas (M6.3), the timed change and animation (M6.4), the topmost overlay (M6.7) and its exclusion
        // from capture (issue #17) only show something, so a no-input test may ask for them
        var launch = new FixtureLaunchOptions
        {
            OpenCanvas = true,
            ChangeAfter = TimeSpan.FromMilliseconds(800),
            AnimateFor = TimeSpan.FromMilliseconds(1500),
            OpenOverlay = true,
            ExcludeOverlayFromCapture = true
        };

        // Act
        var options = FixtureAppSession.BuildOptions(tier, launch, "abc", "events.jsonl", 1);

        // Assert
        options.OpenCanvas.Should().BeTrue();
        options.ChangeAfter.Should().Be(TimeSpan.FromMilliseconds(800));
        options.AnimateFor.Should().Be(TimeSpan.FromMilliseconds(1500));
        options.OpenOverlay.Should().BeTrue();
        options.ExcludeOverlayFromCapture.Should().BeTrue();
        options.NoActivate.Should().BeTrue("the overlay and the canvas open without taking the foreground too");
    }

    [Theory]
    [InlineData(0, null, 300)]
    [InlineData(-1, null, 300)]
    [InlineData(360_000, null, 300)]
    [InlineData(null, 0, 300)]
    [InlineData(null, 30_000, 30)]
    [InlineData(null, 9_990, 10)]
    [InlineData(280_000, null, 300)]
    public void BuildOptions_RejectsATimedChangeTheFixtureWouldRefuseOrNotLiveToMake(
        int? changeAfterMs, int? animateForMs, int maxLifetimeSeconds)
    {
        // Arrange - "change at once" (0) is refused by the fixture with exit code 2, and a change due near or after the
        // fixture's lifetime may never happen (the lifetime runs from the start, the change from the ready event); both
        // must fail here, in the test, not as an exit code or a timed-out wait
        var launch = new FixtureLaunchOptions
        {
            ChangeAfter = changeAfterMs is { } change ? TimeSpan.FromMilliseconds(change) : null,
            AnimateFor = animateForMs is { } animate ? TimeSpan.FromMilliseconds(animate) : null,
            MaxLifetime = TimeSpan.FromSeconds(maxLifetimeSeconds)
        };

        // Act
        var act = () => FixtureAppSession.BuildOptions(DesktopTier.NoInput, launch, "abc", "events.jsonl", 1);

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage(changeAfterMs != null ? "*ChangeAfter*" : "*AnimateFor*");
    }

    [Fact]
    public void CommandLine_WithoutExtraArguments_IsTheOptionsAlone()
    {
        var options = new FixtureOptions("abc", NoActivate: true);

        FixtureAppSession.CommandLine(options, []).Should().Equal(options.ToArguments());
    }

    [Fact]
    public void CommandLine_PlantsExtraArgumentsAfterTheEndOfOptions_WhereTheFixtureIgnoresThem()
    {
        // Arrange - M6.6's redaction check: arguments that only the server's list_processes should see
        var options = new FixtureOptions("abc", EventLogPath: @"C:\t\events.jsonl", ParentProcessId: 7, NoActivate: true);
        string[] planted = ["--wam-test-token=s3cr3t", "http://u:p@host/", "--instance"];

        // Act
        var commandLine = FixtureAppSession.CommandLine(options, planted);

        // Assert
        commandLine.Should().Equal([.. options.ToArguments(), FixtureOptions.EndOfOptions, .. planted]);
        FixtureOptions.Parse(commandLine).Should().Be(options, "the fixture reads the same options and ignores the plant");
        FixtureOptions.IgnoredArgumentCount(commandLine).Should().Be(planted.Length);
    }

    [Fact]
    public void CommandLine_RefusesANullExtraArgument()
    {
        var act = () => FixtureAppSession.CommandLine(new FixtureOptions("abc"), [null!]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FixtureLaunchOptions_ByDefault_PlantsNothing()
    {
        new FixtureLaunchOptions().ExtraArguments.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FixtureDpiAwareness.PerMonitorV2, "perMonitorV2")]
    [InlineData(FixtureDpiAwareness.PerMonitor, "perMonitor")]
    [InlineData(FixtureDpiAwareness.System, "system")]
    [InlineData(FixtureDpiAwareness.Unaware, "unaware")]
    public void DpiAwarenessMismatch_IsNull_WhenTheFixtureGotTheAwarenessItAskedFor(FixtureDpiAwareness requested, string reported)
    {
        // Act
        var mismatch = FixtureAppSession.DpiAwarenessMismatch(requested, reported);

        // Assert
        mismatch.Should().BeNull();
    }

    [Fact]
    public void DpiAwarenessMismatch_NamesBothAwarenesses_WhenTheFixtureGotAnother()
    {
        // Arrange - what a WPF fixture gets when its Main names WPF types: WPF made it system aware before Main ran, so
        // its window rectangles are scaled on a monitor whose scale differs from the primary's

        // Act
        var mismatch = FixtureAppSession.DpiAwarenessMismatch(FixtureDpiAwareness.PerMonitorV2, "system");

        // Assert
        mismatch.Should().Contain("permonitorv2").And.Contain("system");
    }
}
