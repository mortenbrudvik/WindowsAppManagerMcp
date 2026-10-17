using System.Text.Json.Nodes;
using static WindowsAppManagerMcp.E2ETests.Scenarios.FixtureMonitorTools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// set_window_zorder (roadmap M6.7, owner decision D14) against the fixture and its overlay (--open-overlay: a small unowned
/// WS_EX_TOPMOST tool window of the fixture itself): the full (input) tier, since the z-order of the fixture's windows
/// changes. Only the fixture's windows are changed (the harness's guard refuses any other), the foreground never moves, and
/// every test unpins the main window again before the session closes the fixture.
/// </summary>
public class FixtureZOrderTests
{
    private readonly ITestOutputHelper _output;

    public FixtureZOrderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static int ZOrder(E2ESession e2e, nint window) =>
        e2e.Server.Windows.GetWindowDetails(window)?.ZOrder ?? throw new InvalidOperationException($"Window 0x{window:X} has no z-order.");

    private static Dictionary<string, object?> Args(nint window, string mode, nint? above = null)
    {
        var arguments = new Dictionary<string, object?> { ["handle"] = (long)window, ["mode"] = mode };
        if (above is { } other)
            arguments["aboveHandle"] = (long)other;
        return arguments;
    }

    private static bool Pinned(JsonObject serverInfo, nint window) =>
        serverInfo["pinnedWindows"] is JsonArray pins && pins.Any(p => p!["handle"]!.GetValue<long>() == window);

    [DesktopFact]
    public async Task Topmost_PutsTheMainWindowAboveTheOverlay_AndPinsIt_NotopmostUndoesIt_AndTheFocusNeverMoves()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenOverlay = true });
        var overlay = e2e.Fixture.WaitForEvent(FixtureContract.Events.OverlayOpened).GetHandle("hwnd");
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var foreground = ForegroundRoot();
        ZOrder(e2e, main).Should().BeGreaterThan(ZOrder(e2e, overlay), "the overlay is topmost and the main window is not");

        JsonObject unpinned;
        try
        {
            // Act
            var pinned = await CallAsync(server, _output, "set_window_zorder", Args(main, "topmost"));
            var info = await CallAsync(server, _output, "get_server_info", []);

            // Assert
            pinned["success"]!.GetValue<bool>().Should().BeTrue();
            pinned["isTopmost"]!.GetValue<bool>().Should().BeTrue();
            ZOrder(e2e, main).Should().BeLessThan(ZOrder(e2e, overlay), "HWND_TOPMOST takes it to the front of the topmost windows");
            Pinned(info, main).Should().BeTrue("get_server_info lists the windows the server pinned");
            ForegroundRoot().Should().Be(foreground, "a z-order change never activates the window");
        }
        finally
        {
            unpinned = await CallAsync(server, _output, "set_window_zorder", Args(main, "notopmost"), expectSuccess: false);
        }

        unpinned["success"]!.GetValue<bool>().Should().BeTrue();
        unpinned["isTopmost"]!.GetValue<bool>().Should().BeFalse();
        ZOrder(e2e, main).Should().BeGreaterThan(ZOrder(e2e, overlay), "unpinned, it is below the topmost overlay again");
        Pinned(await CallAsync(server, _output, "get_server_info", []), main).Should().BeFalse();
        ForegroundRoot().Should().Be(foreground);
    }

    [DesktopFact]
    public async Task Above_TheTopmostOverlay_PlacesTheMainWindowDirectlyAboveIt_AsATopmostWindow()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenOverlay = true });
        var overlay = e2e.Fixture.WaitForEvent(FixtureContract.Events.OverlayOpened).GetHandle("hwnd");
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var foreground = ForegroundRoot();

        try
        {
            // Act
            var above = await CallAsync(server, _output, "set_window_zorder", Args(main, "above", overlay));

            // Assert
            above["success"]!.GetValue<bool>().Should().BeTrue();
            above["isTopmost"]!.GetValue<bool>().Should().BeTrue("it can only be above a topmost window as one");
            ZOrder(e2e, main).Should().Be(ZOrder(e2e, overlay) - 1, "directly above the overlay");
            ForegroundRoot().Should().Be(foreground);
        }
        finally
        {
            await CallAsync(server, _output, "set_window_zorder", Args(main, "notopmost"), expectSuccess: false);
        }
    }

    [DesktopFact]
    public async Task Bottom_SendsTheMainWindowBehindEveryListedWindow_AndTopRaisesItAgain_WithoutActivatingIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var foreground = ForegroundRoot();

        // Act
        var bottom = await CallAsync(server, _output, "set_window_zorder", Args(main, "bottom"));
        var others = e2e.Server.Windows.FindWindows(new WindowQuery()).Where(w => w.Handle != main).ToList();
        var behindAll = others.All(w => (e2e.Server.Windows.GetWindowDetails(w.Handle)?.ZOrder ?? -1) < ZOrder(e2e, main));
        var top = await CallAsync(server, _output, "set_window_zorder", Args(main, "top"));
        var normalOthers = others.Where(w => e2e.Server.Windows.GetWindowDetails(w.Handle) is { IsTopmost: false }).ToList();

        // Assert
        bottom["success"]!.GetValue<bool>().Should().BeTrue();
        behindAll.Should().BeTrue("bottom puts it behind every window find_windows lists");
        top["success"]!.GetValue<bool>().Should().BeTrue();
        normalOthers.Should().OnlyContain(w => e2e.Server.Windows.GetWindowDetails(w.Handle)!.ZOrder > ZOrder(e2e, main),
            "top raises it above every window that is not topmost");
        ForegroundRoot().Should().Be(foreground, "neither activates the window");
    }
}
