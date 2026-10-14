using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: get_desktop_state and element_action's observe (roadmap M3.12) through the production tool and filter
/// registrations, with the real observation over mocked services (<see cref="ContractServices"/>). The typical observation
/// has every default section and no image; a screenshot comes as image content before the JSON, with a captureId the mouse
/// tools resolve; the text block round-trips into the payload type the tools declare; and a screenshot is refused before
/// anything is read or done when the user turned captures off.
/// </summary>
public class DesktopStateContractTests
{
    private static Task<InProcessMcpServer> StartAsync(ContractServices services, ToolsetSelection? toolsets = null) =>
        InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(toolsets), configureServices: services.Register);

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    [Fact]
    public async Task TheTypicalObservation_HasEveryDefaultSection_AndNoImage()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("get_desktop_state", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" }
        });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();
        var json = Json(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("windowHandle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        json.GetProperty("foreground").GetProperty("processName").GetString().Should().Be("notepad");
        json.GetProperty("cursor").GetProperty("x").GetInt32().Should().Be(640);
        json.GetProperty("input").GetProperty("paused").GetBoolean().Should().BeFalse();
        json.GetProperty("windows").GetArrayLength().Should().Be(TypicalDesktop.Windows.Count);
        json.GetProperty("windows")[0].GetProperty("process").GetString().Should().Be("notepad");
        json.GetProperty("windowCount").GetInt32().Should().Be(TypicalDesktop.Windows.Count);
        json.GetProperty("focus").GetProperty("controlType").GetString().Should().Be("Edit");
        json.GetProperty("focus").TryGetProperty("value", out _).Should().BeFalse("the focused element's value is never part of it");
        json.GetProperty("popups").GetArrayLength().Should().Be(TypicalDesktop.NotepadPopups.Count);
        json.GetProperty("elements").GetProperty("text").GetString().Should().StartWith("w4120035e");
        json.GetProperty("elements").GetProperty("scope").GetString().Should().Be("window");
        json.TryGetProperty("errors", out _).Should().BeFalse();
        var elements = result.Payload<DesktopState>().Elements!;
        elements.Count.Should().Be(80, "maxElements is 80 by default");
        elements.Truncated.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().NotContain("\\u0027", "the names' quotes stay readable")
            .And.Contain("MenuItem 'File'");
    }

    [Fact]
    public async Task AScreenshot_IsImageContentBeforeTheJson_WithACaptureIdTheMouseToolsTake()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("get_desktop_state", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle,
            ["include"] = new[] { "screenshot" },
            ["screenshotMaxLongEdge"] = 800
        });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content.Should().HaveCount(2);
        result.Content[0].Should().BeOfType<ImageContentBlock>().Which.DecodedData.ToArray().Should().NotBeEmpty();
        var state = result.Payload<DesktopState>();
        state.Screenshot!.Width.Should().Be(800, "the 1600 x 900 window is scaled to the long edge asked for");
        services.Captures.Find(state.Screenshot.CaptureId!).Should().NotBeNull("the capture is registered like capture_window's");
        state.Windows.Should().BeNull("only the screenshot was asked for");
    }

    [Fact]
    public async Task ScreenshotMarks_ComeOnTheCapture_WithTheElementLinesTheyMark()
    {
        // Arrange - roadmap M3.F32
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("get_desktop_state", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle,
            ["include"] = new[] { "elements", "screenshot" },
            ["screenshotMarks"] = true
        });

        // Assert - the capture went through capture_with_elements' path, and is registered as every capture is
        result.IsError.Should().NotBe(true);
        result.Content[0].Should().BeOfType<ImageContentBlock>();
        var state = result.Payload<DesktopState>();
        state.Elements!.Text.Should().StartWith("w4120035e");
        services.Captures.Find(state.Screenshot!.CaptureId!).Should().NotBeNull();
        services.UiElements.Verify(u => u.GetUIElementsAsync((nint)TypicalDesktop.NotepadHandle, It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Once, "the marks are the elements walk's own");
    }

    [Fact]
    public async Task AnUnknownIncludeValue_IsRefused_WithTheAllowedValues()
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        var result = await server.CallToolAsync("get_desktop_state", new Dictionary<string, object?> { ["include"] = new[] { "pixels" } });

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain(ToolErrorReporting.InvalidArgumentsErrorCode).And.Contain("screenshot");
    }

    [Fact]
    public async Task ElementAction_WithObserveScreenshot_ReturnsTheCaptureFirst_AndTheObservation()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var result = await server.CallToolAsync("element_action", new Dictionary<string, object?>
        {
            ["ref"] = "w4120035e17",
            ["action"] = "invoke",
            ["observe"] = "screenshot"
        });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content[0].Should().BeOfType<ImageContentBlock>();
        var json = Json(result);
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("method").GetString().Should().Be("Value", "the action's own result is unchanged");
        json.GetProperty("observed").GetProperty("windowHandle").GetInt64().Should().Be(TypicalDesktop.NotepadHandle);
        json.GetProperty("observed").GetProperty("screenshot").GetProperty("captureId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ElementAction_WithoutObserve_KeepsItsResult()
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        var result = await server.CallToolAsync("element_action", new Dictionary<string, object?>
        {
            ["ref"] = "w4120035e17", ["action"] = "set_value", ["value"] = "window layout presets"
        });

        result.Content.Should().ContainSingle();
        var payload = result.Payload<ElementActionResult>();
        payload.Should().BeEquivalentTo(TypicalDesktop.ElementActionDone(new ElementActionRequest("w4120035e17", ElementActionKind.SetValue)));
    }

    [Fact]
    public async Task TheIncludeValues_AreListedInTheInputSchema()
    {
        var tool = ToolSurface.Tool(await ToolSurface.ListToolsAsync(), "get_desktop_state");

        var items = tool["inputSchema"]!["properties"]!["include"]!["items"]!;
        items["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal("windows", "focus", "elements", "popups", "screenshot");
    }

    [Theory]
    [InlineData("get_desktop_state", "include")]
    [InlineData("element_action", "observe")]
    public async Task AScreenshot_IsRefusedBeforeAnythingIsReadOrDone_WhenTheUserTurnedCapturesOff(string tool, string argument)
    {
        // Arrange - WAM_TOOLSETS=-capture
        using var services = new ContractServices(ContractMode.Typical);
        var toolsets = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ToolsetsVariable ? "-capture" : null, Toolsets.Server);
        await using var server = await StartAsync(services, toolsets);
        var arguments = new Dictionary<string, object?>
        {
            [argument] = argument == "include" ? new[] { "screenshot" } : "screenshot"
        };
        if (tool == "element_action")
        {
            arguments["ref"] = "w4120035e17";
            arguments["action"] = "invoke";
        }

        // Act
        var result = await server.CallToolAsync(tool, arguments);

        // Assert
        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("capture_window");
        services.ElementActions.VerifyNoOtherCalls();
        services.UiElements.Verify(u => u.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
