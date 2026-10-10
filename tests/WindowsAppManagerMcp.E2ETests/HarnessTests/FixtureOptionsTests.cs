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
            DpiAwareness: FixtureDpiAwareness.Unaware);

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
            FixtureContract.SavePromptTitle("abc")
        };

        // Assert
        titles.Should().OnlyContain(t => t.Contains("abc"));
        foreach (var title in titles)
        {
            titles.Where(other => other != title).Should().NotContain(other => other.Contains(title));
        }
    }
}
