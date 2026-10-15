using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the fixed-value parameters (roadmap M2.2) and the batch items (roadmap M2.1), as an MCP client sees them
/// through the production tool and filter registrations. The input schema lists every allowed value; a value outside
/// them fails the call with the parameter's own errorCode and the allowed values, and nothing is done, instead of
/// falling back to another action; documented aliases and other casings still work. A bad batch item fails alone.
/// </summary>
public sealed class ToolArgumentContractTests : IDisposable
{
    private readonly ContractServices _services = new(ContractMode.Typical);

    public void Dispose() => _services.Dispose();

    private async Task<CallToolResult> CallAsync(string tool, string arguments)
    {
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: _services.Register);
        return await server.CallToolAsync(tool, Args(arguments));
    }

    private static IReadOnlyDictionary<string, object?> Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    private static void ShouldBeRefused(CallToolResult result, string errorCode, string allowed)
    {
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(errorCode);
        payload.GetProperty("error").GetString().Should().Contain("Use one of: " + allowed).And.Contain("Nothing was done");
    }

    #region Unknown values are refused, with the allowed values

    [Theory]
    [InlineData("""{ "handle": 1706796, "state": "hide" }""")]
    [InlineData("""{ "handle": 1706796, "state": "minimise" }""")]
    [InlineData("""{ "handle": 1706796, "state": 2 }""")]
    public async Task SetWindowState_UnknownState_IsInvalidState_AndChangesNothing(string arguments)
    {
        // Act
        var result = await CallAsync("set_window_state", arguments);

        // Assert - it used to restore the window and report success
        ShouldBeRefused(result, nameof(WindowErrorCode.InvalidState), "minimize, maximize, restore (also accepted: minimized, maximized, normal)");
        _services.Windows.Verify(w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowStateRequest>()), Times.Never);
    }

    [Fact]
    public async Task SnapWindow_UnknownPosition_IsInvalidSnapPosition()
    {
        var result = await CallAsync("snap_window", """{ "handle": 1706796, "position": "middle" }""");

        ShouldBeRefused(result, nameof(WindowErrorCode.InvalidSnapPosition), "left_half, right_half");
        _services.Windows.Verify(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task MoveWindowToMonitor_UnknownPositioning_IsInvalidPositioning()
    {
        // It used to center the window and report success
        var result = await CallAsync("move_window_to_monitor", """{ "handle": 1706796, "monitorIndex": 1, "positioning": "left" }""");

        ShouldBeRefused(result, nameof(WindowErrorCode.InvalidPositioning), "center, topleft, topright, bottomleft, bottomright, maximize, restore");
        _services.Windows.Verify(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ApplyLayout_UnknownMatchBy_IsInvalidMatchBy_AndArrangesNothing()
    {
        // It used to match by process and title
        var result = await CallAsync("apply_layout", """{ "name": "layout-1", "matchBy": "process" }""");

        ShouldBeRefused(result, nameof(LayoutErrorCode.InvalidMatchBy), "process_and_title, process_only, title_only");
        _services.Layouts.Verify(l => l.ApplyPresetAsync(It.IsAny<string>(), It.IsAny<LayoutMatchBy>(), It.IsAny<bool>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("""{ "format": "bmp" }""", nameof(ScreenshotErrorCode.InvalidFormat), "auto, png, jpeg (also accepted: jpg)")]
    [InlineData("""{ "output": "files" }""", nameof(ScreenshotErrorCode.InvalidOutputMode), "image, file, both, metadata")]
    public async Task Captures_UnknownFormatOrOutput_KeepTheirErrorCodes(string arguments, string errorCode, string allowed)
    {
        var result = await CallAsync("take_screenshot", arguments);

        ShouldBeRefused(result, errorCode, allowed);
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_UnknownBrowser_IsUnsupportedBrowser_AndLaunchesNothing()
    {
        var result = await CallAsync("launch_browser_with_debug", """{ "browser": "firefox" }""");

        ShouldBeRefused(result, nameof(BrowserErrorCode.UnsupportedBrowser), "chrome, edge, brave");
        _services.Processes.Verify(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Documented aliases and other casings still work

    [Theory]
    [InlineData("minimized", WindowStateRequest.Minimize)]
    [InlineData("MAXIMIZE", WindowStateRequest.Maximize)]
    [InlineData("normal", WindowStateRequest.Restore)]
    public async Task SetWindowState_AliasesAndCasings_ReachTheServiceAsTheirState(string state, WindowStateRequest expected)
    {
        var result = await CallAsync("set_window_state", $$"""{ "handle": 1706796, "state": "{{state}}" }""");

        result.IsError.Should().NotBe(true);
        _services.Windows.Verify(w => w.SetWindowState((nint)1706796, expected), Times.Once);
    }

    [Fact]
    public async Task SnapAndMoveToMonitor_AcceptTheirOtherSpellings()
    {
        (await CallAsync("snap_window", """{ "handle": 1706796, "position": "Left-Half" }""")).IsError.Should().NotBe(true);
        (await CallAsync("move_window_to_monitor", """{ "handle": 1706796, "monitorIndex": 1, "positioning": "top_left" }""")).IsError.Should().NotBe(true);

        _services.Windows.Verify(w => w.SnapWindow((nint)1706796, SnapPosition.LeftHalf, null, true), Times.Once);
        _services.Windows.Verify(w => w.MoveWindowToMonitor((nint)1706796, 1, MonitorPositioning.TopLeft, true), Times.Once);
    }

    [Fact]
    public async Task Captures_TakeANullFormatAndOutputAsTheDefaults_AsTheStringParametersDid()
    {
        var result = await CallAsync("take_screenshot", """{ "format": null, "output": null }""");

        result.IsError.Should().NotBe(true);
        result.Content.OfType<ImageContentBlock>().Should().ContainSingle("output null is the default, image");
    }

    [Fact]
    public async Task Captures_TakeJpgForJpeg()
    {
        var result = await CallAsync("take_screenshot", """{ "format": "jpg" }""");

        result.IsError.Should().NotBe(true);
        result.Content.OfType<ImageContentBlock>().Single().MimeType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task GetUIElements_UnknownFormat_IsInvalidArguments_AndReadsNothing()
    {
        var result = await CallAsync("get_ui_elements", """{ "handle": 1706796, "format": "yaml" }""");

        ShouldBeRefused(result, ToolErrorReporting.InvalidArgumentsErrorCode, "tree, flat, text");
        _services.UiElements.Verify(u => u.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region The input schema lists the allowed values

    [Fact]
    public async Task InputSchemas_ListTheAllowedValues_OfEveryFixedValueParameter()
    {
        // Arrange
        var tools = await ToolSurface.ListToolsAsync();
        JsonNode Property(string tool, params string[] path)
        {
            JsonNode node = ToolSurface.Tool(tools, tool)["inputSchema"]!;
            foreach (var step in path)
            {
                node = step == "[]" ? node["items"]! : node["properties"]![step]!;
            }

            return node;
        }

        static string[] Values(JsonNode property) => property["enum"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();

        // Assert
        Values(Property("set_window_state", "state")).Should().Equal("minimize", "maximize", "restore", "minimized", "maximized", "normal");
        Values(Property("set_windows_state_batch", "changes", "[]", "state")).Should().Contain("minimize").And.Contain("normal");
        Values(Property("snap_window", "position")).Should().HaveCount(14).And.Contain("left_two_thirds");
        Values(Property("snap_windows_batch", "placements", "[]", "position")).Should().HaveCount(14);
        Property("move_window_to_monitor", "positioning")["default"]!.GetValue<string>().Should().Be("center");
        Values(Property("apply_layout", "matchBy")).Should().Equal("process_and_title", "process_only", "title_only");
        Values(Property("capture_window", "format")).Should().Equal("auto", "png", "jpeg", "jpg");
        Values(Property("capture_with_elements", "output")).Should().Equal("image", "file", "both", "metadata");
        Values(Property("launch_browser_with_debug", "browser")).Should().Equal("chrome", "edge", "brave");
        Values(Property("get_ui_elements", "format")).Should().Equal("tree", "flat", "text");
        Property("take_screenshot", "format")["type"]!.GetValue<string>().Should().Be("string");
    }

    #endregion

    #region Ranges

    [Theory]
    [InlineData("resize_window", """{ "handle": 1706796, "width": 0, "height": 600 }""")]
    [InlineData("set_window_bounds", """{ "handle": 1706796, "x": 0, "y": 0, "width": 800, "height": -1 }""")]
    public async Task WindowSizes_ThatAreNotPositive_AreInvalidArguments_AndNothingIsChanged(string tool, string arguments)
    {
        var result = await CallAsync(tool, arguments);

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _services.Windows.Verify(w => w.ResizeWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _services.Windows.Verify(w => w.SetWindowBounds(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData("get_browser_content", """{ "handle": 2821181, "maxLength": 0 }""", "maxLength must be at least 1")]
    [InlineData("get_browser_content", """{ "handle": 2821181, "offset": -1 }""", "offset must be 0 or more")]
    [InlineData("get_browser_content", """{ "handle": 2821181, "selector": "main" }""", "selector is read through CDP only, so it needs cdpPort")]
    [InlineData("launch_browser_with_debug", """{ "port": 80 }""", "port must be 0 (the browser picks a free port) or a TCP port from 1024 to 65535")]
    [InlineData("launch_browser_with_debug", """{ "port": 70000 }""", "port must be 0 (the browser picks a free port) or a TCP port from 1024 to 65535")]
    public async Task OutOfRangeValues_AreInvalidArguments(string tool, string arguments, string message)
    {
        var result = await CallAsync(tool, arguments);

        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain(message);
    }

    [Theory]
    [InlineData("get_browser_url", """{ "handle": 2821181, "targetId": "8C1F0A6E2B7D4F3A" }""", "targetId needs cdpPort")]
    [InlineData("get_browser_content", """{ "handle": 2821181, "targetId": "8C1F0A6E2B7D4F3A" }""", "targetId needs cdpPort")]
    [InlineData("get_browser_url", """{ "handle": 2821181, "cdpPort": 9222, "targetId": "../browser/1" }""", "CDP tab id")]
    [InlineData("get_browser_content", """{ "handle": 2821181, "cdpPort": 9222, "targetId": "TAB1%2F" }""", "CDP tab id")]
    public async Task TargetId_WithoutCdpPortOrMalformed_IsInvalidArguments_AndNothingIsRead(string tool, string arguments, string message)
    {
        // Roadmap M5.2: targetId names a tab of the browser on cdpPort, and is never sent anywhere unchecked
        var result = await CallAsync(tool, arguments);

        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain(message);
        _services.Browser.Verify(b => b.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
        _services.Browser.Verify(b => b.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("get_browser_url")]
    [InlineData("get_browser_content")]
    public async Task TargetId_WithCdpPort_IsPassedToTheRead(string tool)
    {
        var result = await CallAsync(tool, """{ "handle": 2821181, "cdpPort": 9222, "targetId": "8C1F0A6E2B7D4F3A" }""");

        result.IsError.Should().NotBe(true, Payload(result).ToString());
        _services.Browser.Invocations.SelectMany(i => i.Arguments).OfType<CdpOptions>()
            .Should().ContainSingle().Which.TargetId.Should().Be("8C1F0A6E2B7D4F3A");
    }

    [Theory]
    [InlineData("""{ "url": "--remote-allow-origins=*" }""", "url")]
    [InlineData("""{ "url": "  --utility-cmd-prefix=calc.exe" }""", "url")]
    [InlineData("""{ "url": "/gpu-launcher=calc.exe" }""", "url")]
    [InlineData("""{ "url": "-incognito" }""", "url")]
    [InlineData("""{ "userDataDir": "C:/Users/someone/AppData/Local/Google/Chrome/User Data" }""", "User Data")]
    [InlineData("""{ "userDataDir": "C:/Users/someone/AppData/Local/Microsoft/Edge/User Data/Default" }""", "User Data")]
    [InlineData("""{ "userDataDir": "C:/Users/someone/AppData/Local/BraveSoftware/Brave-Browser/user data/" }""", "User Data")]
    [InlineData("""{ "userDataDir": "wam-profile" }""", "userDataDir must be a full path")]
    [InlineData("""{ "executablePath": "chrome.exe" }""", "executablePath must be the full path of chrome.exe")]
    [InlineData("""{ "browser": "edge", "executablePath": "C:/Windows/System32/cmd.exe" }""", "executablePath must name msedge.exe")]
    public async Task LaunchBrowserWithDebug_RefusesASwitchAsUrl_ARealProfile_AndAnotherProgram_AndLaunchesNothing(string arguments, string message)
    {
        // Review MCP-S-1: a url that Chromium reads as a switch is a command-line switch of the agent's choosing, and the
        // open debug port on the user's real profile would hand its signed-in sessions to any local program; an
        // executablePath that is not the browser's own .exe would start another program with the debug switches (M5.1)
        var result = await CallAsync("launch_browser_with_debug", arguments);

        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain(message).And.Contain("Nothing was launched");
        _services.Processes.Verify(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("""{ "url": "https://example.com/?q=--flag" }""")]
    [InlineData("""{ "url": "example.com" }""")]
    [InlineData("""{ "userDataDir": "C:/Temp/debug-profile" }""")]
    [InlineData("""{ "port": 0 }""")]
    [InlineData("""{ "port": 1024 }""")]
    [InlineData("""{ "browser": "brave", "executablePath": "D:/Portable/Brave/brave.exe" }""")]
    public async Task LaunchBrowserWithDebug_TakesUrlsAndASeparateProfile(string arguments)
    {
        var result = await CallAsync("launch_browser_with_debug", arguments);

        result.IsError.Should().NotBe(true, Payload(result).ToString());
    }

    [Fact]
    public async Task SnapWindow_OnAMonitorThatDoesNotExist_ReportsInvalidMonitorIndex()
    {
        // Arrange - the service checks the index against the monitors (WindowServiceOutcomeTests)
        _services.Windows.Setup(w => w.SnapWindow((nint)1706796, SnapPosition.LeftHalf, 5, It.IsAny<bool>()))
            .Returns(WindowChangeResult.Failed(1706796, WindowErrorCode.InvalidMonitorIndex, WindowService.MonitorIndexMessage(5, 2)));

        // Act
        var result = await CallAsync("snap_window", """{ "handle": 1706796, "position": "left_half", "monitorIndex": 5 }""");

        // Assert
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.InvalidMonitorIndex));
        payload.GetProperty("error").GetString().Should().Contain("valid indexes are 0 to 1");
    }

    #endregion

    #region Batch items are checked one by one

    [Fact]
    public async Task SnapWindowsBatch_ABadItem_FailsAlone_AndTheOthersAreSnapped()
    {
        // Act
        var result = await CallAsync("snap_windows_batch", """
            { "placements": [
                { "handle": 1706796, "position": "left_half" },
                { "handle": 2821181 },
                null,
                { "handle": 3001, "position": "sideways" },
                { "handle": 2821181, "position": "right-half" } ], "delayBetweenMs": 0 }
            """);

        // Assert - a batch summary is data: isError stays unset, and each item says what happened to it
        result.IsError.Should().NotBe(true);
        var items = Payload(result).GetProperty("results").EnumerateArray().ToList();
        items.Should().HaveCount(5);
        items.Select(i => i.GetProperty("success").GetBoolean()).Should().Equal(true, false, false, false, true);
        items[1].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.InvalidSnapPosition));
        items[1].GetProperty("error").GetString().Should().Contain("Item 1: No snap position was given");
        items[2].GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        items[3].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.InvalidSnapPosition));
        items[3].GetProperty("error").GetString().Should().Contain("'sideways' is not a valid snap position");
        _services.Windows.Verify(w => w.SnapWindowsBatchAsync(
            It.Is<IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)>>(p =>
                p.Count == 2 && p[0].Position == SnapPosition.LeftHalf && p[1].Position == SnapPosition.RightHalf),
            0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetWindowsStateBatch_ABadState_FailsAlone_WithInvalidState()
    {
        var result = await CallAsync("set_windows_state_batch", """
            { "changes": [ { "handle": 1706796, "state": "hide" }, { "handle": 2821181, "state": "minimized" } ] }
            """);

        var items = Payload(result).GetProperty("results").EnumerateArray().ToList();
        items[0].GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.InvalidState));
        items[1].GetProperty("success").GetBoolean().Should().BeTrue();
        items[1].GetProperty("state").GetString().Should().NotBeNullOrEmpty("every item reports the state its window is in");
        _services.Windows.Verify(w => w.SetWindowStateBatchAsync(
            It.Is<IReadOnlyList<(nint Handle, WindowStateRequest State)>>(c => c.Count == 1 && c[0].State == WindowStateRequest.Minimize),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetWindowsBoundsBatch_ANullItemOrABadSize_FailsAlone()
    {
        var result = await CallAsync("set_windows_bounds_batch", """
            { "placements": [ null, { "handle": 1706796, "x": 0, "y": 0, "width": 0, "height": 600 },
                              { "handle": 2821181, "x": 0, "y": 0, "width": 800, "height": 600 } ] }
            """);

        var items = Payload(result).GetProperty("results").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("success").GetBoolean()).Should().Equal(false, false, true);
        items.Take(2).Select(i => i.GetProperty("errorCode").GetString()).Should().AllBe(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Theory]
    [InlineData("close_windows_batch", """{ "windows": null }""")]
    [InlineData("set_windows_bounds_batch", """{ "placements": null }""")]
    [InlineData("launch_applications_batch", """{ "applications": null }""")]
    public async Task ABatchWhoseArrayIsNull_IsInvalidArguments(string tool, string arguments)
    {
        var result = await CallAsync(tool, arguments);

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _services.Processes.Verify(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(),
            It.IsAny<int>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LaunchApplicationsBatch_ANullItem_FailsAlone_AndTheOthersAreLaunched()
    {
        // Act
        var result = await CallAsync("launch_applications_batch", """
            { "applications": [ { "executable": "notepad" }, null, { "executable": "calc", "arguments": ["-x"] } ] }
            """);

        // Assert - it used to fail the whole call with a NullReferenceException
        result.IsError.Should().NotBe(true);
        var payload = Payload(result);
        payload.GetProperty("totalRequested").GetInt32().Should().Be(3);
        payload.GetProperty("succeeded").GetInt32().Should().Be(2);
        payload.GetProperty("failed").GetInt32().Should().Be(1);
        var items = payload.GetProperty("results").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("success").GetBoolean()).Should().Equal(true, false, true);
        items.Select(i => i.GetProperty("executable").GetString()).Should().Equal("notepad", "", "calc");
        items[1].GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        items[1].GetProperty("error").GetString().Should().Contain("Item 1 of applications is null");
        items[2].GetProperty("processId").GetInt32().Should().Be(20_001, "the service's results stay with their own items");
        _services.Processes.Verify(p => p.LaunchApplicationsBatchAsync(
            It.Is<IReadOnlyList<LaunchRequest>>(a =>
                a.Count == 2 && a[0].Executable == "notepad" && a[1].Executable == "calc"),
            false, 5000, It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ABatchFailureFromTheService_CarriesItsErrorCode()
    {
        // Arrange - Windows refused one window (it runs as administrator)
        _services.Windows.Setup(w => w.SetWindowBoundsBatchAsync(It.IsAny<IReadOnlyList<(nint, int, int, int, int, bool)>>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([WindowChangeResult.Failed(1706796, WindowErrorCode.AccessDenied, "Windows blocked the change")]);

        // Act
        var result = await CallAsync("set_windows_bounds_batch", """
            { "placements": [ { "handle": 1706796, "x": 0, "y": 0, "width": 800, "height": 600 } ] }
            """);

        // Assert
        var item = Payload(result).GetProperty("results").EnumerateArray().Single();
        item.GetProperty("errorCode").GetString().Should().Be(nameof(WindowErrorCode.AccessDenied));
        item.TryGetProperty("bounds", out _).Should().BeTrue("a window that is still there reports where it is");
    }

    #endregion
}
