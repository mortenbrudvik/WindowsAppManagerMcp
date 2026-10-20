using static WindowsAppManagerMcp.E2ETests.Scenarios.FixtureMonitorTools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// wait_for_window on a set of windows (issue #14) against the fixture's own two windows, its main window and the document
/// window it opens, through the in-process MCP host: count waits for both, all with minimized for both minimized, all with
/// visible for both restored, maximized for the main window. Input tier: the window service minimizes, restores and
/// maximizes the fixture's windows, and never activates another app's.
/// </summary>
public class FixtureWindowSetWaitTests
{
    private readonly ITestOutputHelper _output;

    public FixtureWindowSetWaitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static Dictionary<string, object?> Wait(int processId, string condition, bool all = false, Dictionary<string, object?>? count = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processId"] = processId },
            ["condition"] = condition,
            ["timeoutMs"] = (int)E2ESession.FindWindowTimeout.TotalMilliseconds
        };
        if (all)
            arguments["all"] = true;
        if (count != null)
            arguments["count"] = count;
        return arguments;
    }

    [DesktopFact]
    public async Task Count_WaitsForTheFixturesTwoWindows_AndListsThemWithTheirMonitor()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenDocument = true });
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        await using var server = await e2e.Server.StartMcpServerAsync();

        // Act
        var result = await CallAsync(server, _output, "wait_for_window",
            Wait(e2e.Fixture.ProcessId, "exists", count: new() { ["exactly"] = 2 }));

        // Assert
        result.ContainsKey("window").Should().BeFalse();
        Int(result["matchCount"]).Should().Be(2);
        var candidates = result["candidates"]!.AsArray().Select(c => c!.AsObject()).ToList();
        candidates.Select(c => (long)c["handle"]!).Should().BeEquivalentTo([(long)e2e.Fixture.MainWindowHandle, (long)document]);
        candidates.Should().OnlyContain(c => c.ContainsKey("state") && c.ContainsKey("monitorId"));
    }

    [DesktopFact]
    public async Task AllMinimized_ThenAllRestored_ThenOneMaximized()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenDocument = true });
        var main = e2e.Fixture.MainWindowHandle;
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        var windows = e2e.Server.Windows;
        await using var server = await e2e.Server.StartMcpServerAsync();
        var processId = e2e.Fixture.ProcessId;

        try
        {
            // Act - both minimized
            windows.SetWindowState(main, WindowStateRequest.Minimize).Success.Should().BeTrue();
            windows.SetWindowState(document, WindowStateRequest.Minimize).Success.Should().BeTrue();
            var minimized = await CallAsync(server, _output, "wait_for_window", Wait(processId, "minimized", all: true));

            // Assert
            Int(minimized["matchCount"]).Should().Be(2);
            minimized["candidates"]!.AsArray().Should().OnlyContain(c => (string?)c!["state"] == "minimized" && !c.AsObject().ContainsKey("visibleBounds"));

            // Act - both restored
            windows.SetWindowState(main, WindowStateRequest.Restore).Success.Should().BeTrue();
            windows.SetWindowState(document, WindowStateRequest.Restore).Success.Should().BeTrue();
            var restored = await CallAsync(server, _output, "wait_for_window", Wait(processId, "visible", all: true));

            // Assert
            Int(restored["matchCount"]).Should().Be(2);
            restored["candidates"]!.AsArray().Should().OnlyContain(c => (string?)c!["state"] != "minimized");

            // Act - the main window maximized: seen by polling, Windows sends no event for it
            windows.SetWindowState(main, WindowStateRequest.Maximize).Success.Should().BeTrue();
            var maximized = await CallAsync(server, _output, "wait_for_window", Wait(processId, "maximized"));

            // Assert
            ((long)maximized["window"]!["handle"]!).Should().Be((long)main);
            ((string?)maximized["window"]!["state"]).Should().Be("maximized");
        }
        finally
        {
            windows.SetWindowState(main, WindowStateRequest.Restore);
            windows.SetWindowState(document, WindowStateRequest.Restore);
        }
    }
}
