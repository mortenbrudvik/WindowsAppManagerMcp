using System.Text.Json.Nodes;
using WindowsAppManagerMcp.Services;
using static WindowsAppManagerMcp.E2ETests.Scenarios.FixtureMonitorTools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// A window row's monitor, zone and restore (issue #12) on the real displays, through the in-process MCP host: a snapped
/// window names its monitor's id and the zone it fills, a minimized one keeps the monitor it restores to and get_window_info
/// 'full' tells where it comes back. The fixture's own window only (input tier: the window tools move, maximize and minimize
/// it, and never activate it).
/// </summary>
public class FixtureWindowRowTests
{
    private readonly ITestOutputHelper _output;

    public FixtureWindowRowTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Dictionary<string, object?> Info(nint window, string detail = "standard") =>
        new() { ["handle"] = (long)window, ["detail"] = detail };

    [DesktopFact]
    public async Task ASnappedWindow_NamesItsMonitorsId_AndTheZoneItFills()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var monitor = e2e.Server.Monitors.GetAllMonitors().FirstOrDefault(m => !m.IsPrimary) ??
                      e2e.Server.Monitors.GetPrimaryMonitor();

        foreach (var position in new[] { "left_half", "right_third", "top_left_quarter", "fullscreen" })
        {
            // Act
            await CallAsync(server, _output, "snap_window",
                new() { ["handle"] = (long)main, ["position"] = position, ["monitor"] = monitor.Index, ["bringToFront"] = false });
            var row = await CallAsync(server, _output, "get_window_info", Info(main));

            // Assert - read back as the position snap_window took, on the monitor get_monitors names
            Int(row["monitorIndex"]).Should().Be(monitor.Index);
            ((string?)row["monitorId"]).Should().Be(monitor.Id);
            ((string?)row["zone"]).Should().Be(position);
            row.ContainsKey("spansMonitors").Should().BeFalse();
        }
    }

    [DesktopFact]
    public async Task AWindowMovedOffEveryZone_HasNone()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var work = e2e.Server.Monitors.GetPrimaryMonitor().WorkArea;

        // Act
        await CallAsync(server, _output, "set_window_bounds", new()
        {
            ["handle"] = (long)main, ["x"] = work.X + 61, ["y"] = work.Y + 47, ["width"] = 703, ["height"] = 509, ["bringToFront"] = false
        });
        var row = await CallAsync(server, _output, "get_window_info", Info(main));

        // Assert
        row.ContainsKey("zone").Should().BeFalse();
    }

    [DesktopFact]
    public async Task AMinimizedWindow_KeepsTheMonitorItRestoresTo_AndFullTellsWhereItComesBack()
    {
        // Arrange - the fixture at a known rectangle on the monitor that is not the primary (the primary when there is one)
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var windows = e2e.Server.Windows;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var monitor = e2e.Server.Monitors.GetAllMonitors().FirstOrDefault(m => !m.IsPrimary) ??
                      e2e.Server.Monitors.GetPrimaryMonitor();
        var placed = await CallAsync(server, _output, "set_window_bounds", new()
        {
            ["handle"] = (long)main, ["x"] = monitor.WorkArea.X + 80, ["y"] = monitor.WorkArea.Y + 60, ["width"] = 640, ["height"] = 480,
            ["bringToFront"] = false
        });
        var outer = placed["outerBounds"]!;

        try
        {
            // Act - minimized from normal
            windows.SetWindowState(main, WindowStateRequest.Minimize).Success.Should().BeTrue();
            var minimized = await CallAsync(server, _output, "get_window_info", Info(main, "full"));

            // Assert - its row keeps the monitor it restores to (the icon at -32000 is nearest another one), with no zone
            Int(minimized["monitorIndex"]).Should().Be(monitor.Index);
            ((string?)minimized["monitorId"]).Should().Be(monitor.Id);
            minimized.ContainsKey("zone").Should().BeFalse();
            var restore = minimized["restore"]!.AsObject();
            ((string?)restore["state"]).Should().Be("normal");
            Int(restore["monitorIndex"]).Should().Be(monitor.Index);
            Int(restore["bounds"]!["x"]).Should().Be(Int(outer["x"]));
            Int(restore["bounds"]!["y"]).Should().Be(Int(outer["y"]));
            Int(restore["bounds"]!["width"]).Should().Be(Int(outer["width"]));
            Int(restore["bounds"]!["height"]).Should().Be(Int(outer["height"]));

            // Act - minimized from maximized
            windows.SetWindowState(main, WindowStateRequest.Restore).Success.Should().BeTrue();
            windows.SetWindowState(main, WindowStateRequest.Maximize).Success.Should().BeTrue();
            ((string?)(await CallAsync(server, _output, "get_window_info", Info(main)))["zone"]).Should().Be("fullscreen");
            windows.SetWindowState(main, WindowStateRequest.Minimize).Success.Should().BeTrue();
            var fromMaximized = await CallAsync(server, _output, "get_window_info", Info(main, "full"));

            // Assert
            ((string?)fromMaximized["restore"]!["state"]).Should().Be("maximized");
            Int(fromMaximized["monitorIndex"]).Should().Be(monitor.Index);
        }
        finally
        {
            windows.SetWindowState(main, WindowStateRequest.Restore);
            windows.SetWindowState(main, WindowStateRequest.Restore);
        }
    }

    [DesktopFact(Requires = typeof(TwoMonitors))]
    public async Task AWindowAcrossTwoMonitors_SpansMonitors()
    {
        // Arrange - two side by side monitors: the fixture straddles the edge between them
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var monitors = e2e.Server.Monitors.GetAllMonitors();
        var primary = e2e.Server.Monitors.GetPrimaryMonitor();
        var beside = monitors.FirstOrDefault(m => m.Bounds.X == primary.Bounds.X + primary.Bounds.Width ||
                                                  m.Bounds.X + m.Bounds.Width == primary.Bounds.X);
        if (beside == null)
        {
            _output.WriteLine("No monitor beside the primary: nothing to straddle.");
            return;
        }

        var edge = beside.Bounds.X > primary.Bounds.X ? beside.Bounds.X : primary.Bounds.X;
        var top = Math.Max(primary.WorkArea.Y, beside.WorkArea.Y) + 50;

        // Act
        await CallAsync(server, _output, "set_window_bounds", new()
        {
            ["handle"] = (long)main, ["x"] = edge - 300, ["y"] = top, ["width"] = 600, ["height"] = 400, ["bringToFront"] = false
        });
        var row = await CallAsync(server, _output, "get_window_info", Info(main));

        // Assert
        row["spansMonitors"]!.GetValue<bool>().Should().BeTrue();
        row.ContainsKey("zone").Should().BeFalse();
    }
}
