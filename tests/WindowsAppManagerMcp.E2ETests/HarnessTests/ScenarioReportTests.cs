namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The scenario report (roadmap M7.6): it writes a run as JSON, finds the newest earlier run, and describes a scenario with
/// its change since then. Ordinary tests: no desktop, only a temp folder.
/// </summary>
public class ScenarioReportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WamScenarioReportTests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _before = Environment.GetEnvironmentVariable(ScenarioReport.ResultsVariable);

    public ScenarioReportTests()
    {
        Environment.SetEnvironmentVariable(ScenarioReport.ResultsVariable, _folder);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ScenarioReport.ResultsVariable, _before);
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static ScenarioMeasure Measure(string name, bool success = true, int calls = 4, long wallMs = 900, long tokens = 300) =>
        new(name, success, calls, 0, wallMs, tokens * 4, tokens, 0);

    [Fact]
    public void Describe_NamesTheChangesSinceThePreviousRun()
    {
        var line = ScenarioReport.Describe(Measure("form_fill", calls: 1, wallMs: 700, tokens: 250), Measure("form_fill"));

        line.Should().Contain("1 calls (-3)").And.Contain("700 ms (-200)").And.Contain("250 tokens of text (-50)");
    }

    [Fact]
    public void Describe_WithoutAPreviousRun_SaysSo_AndAFailureShowsIt()
    {
        ScenarioReport.Describe(Measure("calc"), before: null).Should().Contain("no previous run");
        ScenarioReport.Describe(Measure("calc", success: false), Measure("calc")).Should().Contain("FAILED").And.Contain("was a success");
    }

    [Fact]
    public void ARun_IsWrittenOnDispose_AndTheNextReportReadsItAsThePrevious()
    {
        // Arrange - a report with no earlier run
        var first = new ScenarioReport();
        first.Previous.Should().BeNull();
        first.Folder.Should().Be(_folder);

        // Act - nothing measured writes nothing; a measured run is written
        first.Dispose();
        Directory.GetFiles(_folder).Should().BeEmpty();
        File.WriteAllText(Path.Combine(_folder, "scenarios-20261017-120000.json"),
            """{ "startedAt": "2026-10-17T12:00:00+00:00", "serverVersion": "2.5.0", "scenarios": [ { "name": "calc", "success": true, "calls": 8, "failedCalls": 0, "wallMs": 2100, "textBytes": 4000, "textTokens": 1000, "images": 0 } ] }""");
        var second = new ScenarioReport();

        // Assert
        second.Previous.Should().NotBeNull();
        second.Previous!.ServerVersion.Should().Be("2.5.0");
        second.Previous.Scenarios.Should().ContainSingle().Which.Calls.Should().Be(8);
    }
}
