using WindowsAppManagerMcp.Services;
using static WindowsAppManagerMcp.E2ETests.Scenarios.FixtureMonitorTools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// set_window_bounds with unit 'percent' (roadmap M6.7) on the real displays, through the in-process MCP host: the full
/// (input) tier, since it moves the fixture's own window (and nothing else), raised without activation. Percentages of a
/// snap position must give exactly snap_window's visible frame, including on a monitor at another scale, where the window
/// is placed a second time after the app's DPI change.
/// </summary>
public class FixturePercentPlacementTests
{
    private readonly ITestOutputHelper _output;

    public FixturePercentPlacementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Dictionary<string, object?> Percent(nint window, object monitor, double x, double y, double width, double height) => new()
    {
        ["handle"] = (long)window,
        ["unit"] = "percent",
        ["monitor"] = monitor,
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height
    };

    [DesktopFact]
    public async Task TheRightHalfInPercent_IsSnapWindowsRightHalf_AndTheThirdsTileLikeTheSnapThirds()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var foreground = ForegroundRoot();
        var cases = new (string Position, double X, double Width)[]
        {
            ("right_half", 50, 50),
            ("left_third", 0, 100.0 / 3),
            ("center_third", 100.0 / 3, 100.0 / 3),
            ("right_third", 200.0 / 3, 100.0 / 3)
        };

        foreach (var (position, x, width) in cases)
        {
            // Act
            var snapped = await CallAsync(server, _output, "snap_window",
                new() { ["handle"] = (long)main, ["position"] = position, ["monitor"] = "primary" });
            var placed = await CallAsync(server, _output, "set_window_bounds", Percent(main, "primary", x, 0, width, 100));

            // Assert
            Frame(placed).Should().Be(Frame(snapped), "{0} in percent is snap_window's {0}", position);
        }

        ForegroundRoot().Should().Be(foreground, "the placements raise the window without activating it");
    }

    [DesktopFact(Requires = typeof(MonitorAtAnotherScale))]
    public async Task APercentPlacement_OnAMonitorAtAnotherScale_IsExact_AfterTheAppsDpiChange()
    {
        // Arrange - the fixture starts on the primary, so the placement crosses the scale change
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var monitors = e2e.Server.Monitors.GetAllMonitors();
        var primary = monitors.Single(m => m.IsPrimary);
        var other = monitors.First(m => m.Dpi != primary.Dpi);
        var expected = RelativeRectMath.FromPercent(other.WorkArea, 10, 10, 50, 50);

        // Act - by the monitor's index: its id may be unknown on some displays
        var placed = await CallAsync(server, _output, "set_window_bounds", Percent(main, other.Index, 10, 10, 50, 50));

        // Assert - converted once against the target monitor, placed again after the app rescaled
        Int(placed["monitorIndex"]).Should().Be(other.Index);
        Frame(placed).Should().Be(new WindowRect(expected.X, expected.Y, expected.Width, expected.Height));
    }
}
