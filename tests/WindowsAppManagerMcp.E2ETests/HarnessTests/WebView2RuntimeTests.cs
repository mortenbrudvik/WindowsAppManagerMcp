namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// When the WebView2 scenario runs (roadmap M3.F4): only on a machine with the Evergreen WebView2 runtime, else it is
/// skipped with a reason that says what to install. Pure tests of the decision; the registry is not read.
/// </summary>
public class WebView2RuntimeTests
{
    [Theory]
    [InlineData("131.0.2903.70")]
    [InlineData(" 120.0.1.0 ")]
    public void MissingReasonFor_AnInstalledVersion_IsNull(string version)
    {
        WebView2Runtime.MissingReasonFor([null, version, null]).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0.0.0.0")]
    public void MissingReasonFor_NoVersion_SaysTheRuntimeIsMissing_AndWhatToInstall(string? version)
    {
        // Act - 0.0.0.0 is what an uninstalled runtime leaves behind
        var reason = Reason(version);

        // Assert
        reason.Should().Contain("WebView2 runtime is not installed").And.Contain("Evergreen").And.Contain(WebView2Runtime.ClientId);
    }

    [Fact]
    public void MissingReasonFor_NoKeyAtAll_SaysTheRuntimeIsMissing()
    {
        WebView2Runtime.MissingReasonFor([]).Should().NotBeNull();
    }

    [Fact]
    public void TheWebViewScenarios_RequireTheRuntime()
    {
        // A scenario that opens the WebView2 window fails on a machine without the runtime unless it names the requirement
        var scenarios = typeof(WindowsAppManagerMcp.E2ETests.Scenarios.FixtureWebViewTests)
            .GetMethods()
            .Select(m => (m.Name, Gate: m.GetCustomAttributes(inherit: true).OfType<DesktopNoInputFactAttribute>().SingleOrDefault()))
            .Where(m => m.Gate != null)
            .ToList();

        scenarios.Should().NotBeEmpty();
        scenarios.Should().OnlyContain(m => m.Gate!.Requires == typeof(WebView2Runtime));
    }

    private static string? Reason(string? version) => WebView2Runtime.MissingReasonFor([version, null, null]);
}
