using System.Text.Json.Nodes;
using WindowsAppManagerMcp.Services;
using static WindowsAppManagerMcp.E2ETests.Scenarios.FixtureMonitorTools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Layout schema version 2 (roadmap M6.8) on the real desktop, through the in-process MCP host, on the fixture's own window
/// only: every save names the fixture's process (includeProcesses, or a placement of its process), so no other window is
/// saved or moved, and the presets go to the harness's temporary layouts folder. The dry run runs in the no-input tier, whose
/// guard refuses any window change, so a dry run that tried one would fail; the others are in the full (input) tier, since
/// they move, maximize and minimize the fixture.
/// </summary>
public class FixtureLayoutTests
{
    /// <summary>How far a restore rectangle may be from the one saved: the fractions of a work area round to the pixel.</summary>
    private const int Tolerance = 2;

    private readonly ITestOutputHelper _output;

    public FixtureLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Dictionary<string, object?> SaveFixture(string name, bool includeMinimized = false) => new()
    {
        ["name"] = name,
        ["includeProcesses"] = FixtureContract.ProcessName,
        ["includeMinimized"] = includeMinimized
    };

    /// <summary>The fixture's main window's placement in an apply_layout result.</summary>
    private static JsonObject PlacementOf(JsonObject result, nint window) =>
        result["placements"]!.AsArray().Select(p => p!.AsObject())
            .Single(p => p["window"]?["handle"]?.GetValue<long>() == (long)window);

    private static void ShouldBeNear(WindowRect? actual, WindowRect expected, string because)
    {
        actual.Should().NotBeNull(because);
        Math.Abs(actual!.X - expected.X).Should().BeLessThanOrEqualTo(Tolerance, because);
        Math.Abs(actual.Y - expected.Y).Should().BeLessThanOrEqualTo(Tolerance, because);
        Math.Abs(actual.Width - expected.Width).Should().BeLessThanOrEqualTo(Tolerance, because);
        Math.Abs(actual.Height - expected.Height).Should().BeLessThanOrEqualTo(Tolerance, because);
    }

    [DesktopFact]
    public async Task AMaximizedWindow_RoundTrips_MaximizedOnItsMonitor_WithItsRestoreRectangle()
    {
        // Arrange - the fixture at a known normal rectangle, then maximized, then saved
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var windows = e2e.Server.Windows;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var work = e2e.Server.Monitors.GetPrimaryMonitor().WorkArea;
        var normal = new WindowRect(work.X + 60, work.Y + 50, 700, 500);
        windows.SetWindowBounds(main, normal.X, normal.Y, normal.Width, normal.Height, bringToFront: false).Success.Should().BeTrue();
        windows.SetWindowState(main, WindowStateRequest.Maximize).Success.Should().BeTrue();
        var monitor = windows.GetWindowInfo(main)!.MonitorIndex;
        await CallAsync(server, _output, "save_layout", SaveFixture("e2e-maximized"));

        // ... restored and moved away
        windows.SetWindowState(main, WindowStateRequest.Restore).Success.Should().BeTrue();
        windows.SetWindowBounds(main, work.X + 300, work.Y + 200, 500, 400, bringToFront: false).Success.Should().BeTrue();

        // Act
        var applied = await CallAsync(server, _output, "apply_layout", new() { ["name"] = "e2e-maximized" });

        // Assert - maximized on its monitor again, and it restores to the rectangle it was saved with
        PlacementOf(applied, main)["status"]!.GetValue<string>().Should().Be("applied");
        var after = windows.GetWindowInfo(main)!;
        after.State.Should().Be(WindowState.Maximized);
        after.MonitorIndex.Should().Be(monitor);
        ShouldBeNear(windows.GetNormalBounds(main), normal, "the restore rectangle is given before the window is maximized");
    }

    [DesktopFact]
    public async Task AMinimizedWindow_GetsItsRestoreRectangle_WithoutBeingShown()
    {
        // Arrange - minimized at rectangle A, saved with includeMinimized
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var windows = e2e.Server.Windows;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var work = e2e.Server.Monitors.GetPrimaryMonitor().WorkArea;
        var saved = new WindowRect(work.X + 80, work.Y + 70, 640, 480);
        windows.SetWindowBounds(main, saved.X, saved.Y, saved.Width, saved.Height, bringToFront: false).Success.Should().BeTrue();
        windows.SetWindowState(main, WindowStateRequest.Minimize).Success.Should().BeTrue();
        await CallAsync(server, _output, "save_layout", SaveFixture("e2e-minimized", includeMinimized: true));

        // ... restored, moved to rectangle B and minimized again, so its restore rectangle is B
        windows.SetWindowState(main, WindowStateRequest.Restore).Success.Should().BeTrue();
        windows.SetWindowBounds(main, work.X + 400, work.Y + 300, 500, 400, bringToFront: false).Success.Should().BeTrue();
        windows.SetWindowState(main, WindowStateRequest.Minimize).Success.Should().BeTrue();

        // Act - the window's state is sampled throughout the apply
        using var stop = new CancellationTokenSource();
        var shown = false;
        var watch = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                shown |= windows.GetWindowInfo(main)?.State != WindowState.Minimized;
                await Task.Delay(5);
            }
        });
        var applied = await CallAsync(server, _output, "apply_layout", new() { ["name"] = "e2e-minimized" });
        await stop.CancelAsync();
        await watch;

        // Assert
        PlacementOf(applied, main)["status"]!.GetValue<string>().Should().Be("applied");
        shown.Should().BeFalse("SetWindowPlacement with SW_SHOWMINNOACTIVE never shows the window");
        windows.GetWindowInfo(main)!.State.Should().Be(WindowState.Minimized);
        ShouldBeNear(windows.GetNormalBounds(main), saved, "it returns to the saved rectangle when it is restored");

        windows.SetWindowState(main, WindowStateRequest.Restore);
    }

    [DesktopNoInputFact]
    public async Task ADryRun_ChangesNoWindow_AndSaysWhereTheFixtureWouldGo()
    {
        // Arrange - the no-input tier's guard refuses every window change: a dry run that tried one would fail
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        await CallAsync(server, _output, "save_layout", new()
        {
            ["name"] = "e2e-dry-run",
            ["placements"] = new JsonArray(new JsonObject
            {
                ["processName"] = FixtureContract.ProcessName,
                ["exactProcessName"] = true,
                ["snap"] = "left_half",
                ["monitor"] = "primary"
            })
        });
        var before = e2e.Server.Windows.GetWindowInfo(main)!;
        var foreground = ForegroundRoot();

        // Act
        var preview = await CallAsync(server, _output, "apply_layout", new() { ["name"] = "e2e-dry-run", ["dryRun"] = true });

        // Assert - what it would do: the fixture snapped to the primary's left half
        preview["status"]!.GetValue<string>().Should().Be("dryRun");
        var placement = PlacementOf(preview, main);
        placement["status"]!.GetValue<string>().Should().Be("wouldApply");
        var half = SnapMath.Calculate(e2e.Server.Monitors.GetPrimaryMonitor().WorkArea, SnapPosition.LeftHalf);
        new WindowRect(Int(placement["target"]!["x"]), Int(placement["target"]!["y"]), Int(placement["target"]!["width"]),
            Int(placement["target"]!["height"])).Should().Be(new WindowRect(half.X, half.Y, half.Width, half.Height));

        // ... and nothing moved, changed state or came to the front
        var after = e2e.Server.Windows.GetWindowInfo(main)!;
        after.Bounds.Should().Be(before.Bounds);
        after.State.Should().Be(before.State);
        ForegroundRoot().Should().Be(foreground);
    }

    [DesktopFact]
    public async Task AnAgentWrittenSnap_LandsWhereItsDryRunSaid()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        await CallAsync(server, _output, "save_layout", new()
        {
            ["name"] = "e2e-snap",
            ["placements"] = new JsonArray(new JsonObject
            {
                ["processName"] = FixtureContract.ProcessName,
                ["exactProcessName"] = true,
                ["snap"] = "right_third",
                ["monitor"] = "primary"
            })
        });
        var preview = PlacementOf(await CallAsync(server, _output, "apply_layout", new() { ["name"] = "e2e-snap", ["dryRun"] = true }), main);

        // Act
        var applied = await CallAsync(server, _output, "apply_layout", new() { ["name"] = "e2e-snap" });

        // Assert - snapped as snap_window snaps: the visible frame is the dry run's target
        PlacementOf(applied, main)["status"]!.GetValue<string>().Should().Be("applied");
        var frame = e2e.Server.Windows.GetWindowInfo(main)!.VisibleBounds;
        frame.Should().Be(new WindowRect(Int(preview["target"]!["x"]), Int(preview["target"]!["y"]), Int(preview["target"]!["width"]),
            Int(preview["target"]!["height"])));
    }

    [DesktopFact(Requires = typeof(MonitorAtAnotherScale))]
    public async Task AWindowSavedOnAMonitorAtAnotherScale_ComesBackThereAtItsSize()
    {
        // Arrange - the fixture on the other-scale monitor, saved there by the monitor's id, then moved to the primary
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var windows = e2e.Server.Windows;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var monitors = e2e.Server.Monitors.GetAllMonitors();
        var primary = monitors.Single(m => m.IsPrimary);
        var other = monitors.First(m => m.Dpi != primary.Dpi);
        windows.MoveWindowToMonitor(main, other.Index, bringToFront: false).Success.Should().BeTrue();
        var there = windows.GetWindowInfo(main)!;
        _output.WriteLine($"on monitor {other.Index} ({other.Id}, {other.Dpi} dpi): {there.Bounds}");
        await CallAsync(server, _output, "save_layout", SaveFixture("e2e-other-scale"));
        windows.MoveWindowToMonitor(main, primary.Index, bringToFront: false).Success.Should().BeTrue();

        // Act
        var applied = await CallAsync(server, _output, "apply_layout", new() { ["name"] = "e2e-other-scale" });

        // Assert - back on that monitor, at its window rect (placed again after the app's DPI change)
        PlacementOf(applied, main)["status"]!.GetValue<string>().Should().Be("applied");
        var after = windows.GetWindowInfo(main)!;
        after.MonitorIndex.Should().Be(other.Index);
        ShouldBeNear(after.Bounds, there.Bounds, "the window rect it was saved with");
    }
}
