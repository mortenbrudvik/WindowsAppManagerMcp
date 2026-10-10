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
}
