using System.Text.Json.Nodes;
using WindowsAppManagerMcp.Services;
using static WindowsAppManagerMcp.E2ETests.Scenarios.FixtureMonitorTools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The `monitor` parameter and get_monitors' identity (roadmap M6.7, owner decision D13) on the real displays, through the
/// in-process MCP host. get_monitors only reads (no-input tier); the snaps move the fixture's own window and nothing else
/// (input tier), and never activate it.
/// </summary>
public class FixtureMonitorSelectorTests
{
    private readonly ITestOutputHelper _output;

    public FixtureMonitorSelectorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public async Task GetMonitors_ReportsEachMonitorsIdentity_AndTheSameIdsOnTheNextCall()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        await using var server = await e2e.Server.StartMcpServerAsync();

        // Act
        var first = (await CallAsync(server, _output, "get_monitors", []))["monitors"]!.AsArray();
        var second = (await CallAsync(server, _output, "get_monitors", []))["monitors"]!.AsArray();

        // Assert - an id is missing only where the display configuration cannot be read (remote and virtual displays); the
        // owner checks by eye that friendlyName names the panels and relation where they stand
        first.Select(m => (string?)m!["id"]).Should().Equal(second.Select(m => (string?)m!["id"]), "an id stays with its monitor");
        foreach (var monitor in first)
        {
            _output.WriteLine($"monitor {monitor!["index"]}: id={monitor["id"]}, friendlyName={monitor["friendlyName"]}, " +
                              $"dpi={monitor["dpi"]}, isInternal={monitor["isInternal"]}, relation={monitor["relation"]}");
            Int(monitor["dpi"]).Should().Be((int)Math.Round(monitor["scaleFactor"]!.GetValue<double>() * 96));
            ((string?)monitor["relation"] == null).Should().Be(monitor["isPrimary"]!.GetValue<bool>(), "only the primary has no relation");
        }
    }

    [DesktopFact]
    public async Task SnapWindow_ByPrimary_ById_ByDeviceName_AndByCurrent_LandsOnTheMonitorItNames()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var monitors = e2e.Server.Monitors.GetAllMonitors();
        var primary = monitors.Single(m => m.IsPrimary);
        var other = monitors.FirstOrDefault(m => !m.IsPrimary) ?? primary;
        var current = e2e.Server.Monitors.GetMonitorForWindow(E2ENative.GetForegroundWindow())!;
        var foreground = ForegroundRoot();
        var cases = new List<(object Monitor, MonitorInfo Expected)>
        {
            ("primary", primary),
            (other.DeviceName, other),
            ("current", current)
        };
        if (other.Id is { } id)
        {
            cases.Add((id, other));
        }

        foreach (var (monitor, expected) in cases)
        {
            // Act
            var result = await CallAsync(server, _output, "snap_window",
                new() { ["handle"] = (long)main, ["position"] = "left_half", ["monitor"] = monitor });

            // Assert
            Int(result["monitorIndex"]).Should().Be(expected.Index, "monitor '{0}' names monitor {1}", monitor, expected.Index);
            var tile = SnapMath.Calculate(expected.WorkArea, SnapPosition.LeftHalf);
            Frame(result).Should().Be(new WindowRect(tile.X, tile.Y, tile.Width, tile.Height));
        }

        ForegroundRoot().Should().Be(foreground, "snapping raises the window without activating it");
    }

    [DesktopFact(Requires = typeof(TwoMonitors))]
    public async Task SnapWindow_ToTheLeftAndTheRightMonitor_LandsOnTheOuterMonitors()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var monitors = e2e.Server.Monitors.GetAllMonitors();

        foreach (var side in new[] { "left", "right" })
        {
            var expected = MonitorSelection.Resolve(monitors, MonitorSelector.Of(side), () => null);
            _output.WriteLine($"'{side}' resolves to {expected.Index} {expected.ErrorCode} {expected.Error}");

            // Act
            var result = await CallAsync(server, _output, "snap_window",
                new() { ["handle"] = (long)main, ["position"] = "right_half", ["monitor"] = side }, expectSuccess: expected.Succeeded);

            // Assert - stacked monitors are AmbiguousMonitor, never a guess
            if (expected.Succeeded)
            {
                Int(result["monitorIndex"]).Should().Be(expected.Index);
                var outermost = monitors.Select(m => m.Bounds.X + m.Bounds.Width / 2.0);
                var centre = expected.Monitor!.Bounds.X + expected.Monitor.Bounds.Width / 2.0;
                centre.Should().Be(side == "left" ? outermost.Min() : outermost.Max());
            }
            else
            {
                ((string?)result["errorCode"]).Should().Be(nameof(WindowErrorCode.AmbiguousMonitor));
            }
        }
    }
}
