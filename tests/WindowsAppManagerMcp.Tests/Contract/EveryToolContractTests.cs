using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: every tool the server serves, called the way an agent calls it, over MCP through the production tool
/// and filter registrations, with mocked services (<see cref="ContractRun"/>). A typical call returns non-empty
/// JSON content with success true and isError not set, and a capture returns an image block first. A failing call
/// (the services report invalid handles, errors and empty lists) returns non-empty content and isError = !success
/// (roadmap M2.1): no tool sends a failure with isError false, and none returns null, which a client receives as
/// empty content. The contract run serves every tool (<see cref="ToolsetSelection.All"/>), so the opt-in toolsets' tools are
/// called too: the deprecated ones (roadmap M6.1) with the arguments they always took, since their behaviour is unchanged.
/// </summary>
public class EveryToolContractTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Arguments =
        new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["find_windows"] = Args("""{ "processName": "notepad" }"""),
            ["get_all_windows"] = Args("{}"), // deprecated (roadmap M6.1): find_windows
            ["get_foreground_window"] = Args("{}"),
            // Deprecated (roadmap M6.1): set_window_bounds with x and y, or with width and height
            ["move_window"] = Args("""{ "handle": 1706796, "x": 100, "y": 80 }"""),
            ["resize_window"] = Args("""{ "handle": 1706796, "width": 1280, "height": 720 }"""),
            ["set_window_bounds"] = Args("""{ "handle": 1706796, "x": 0, "y": 0, "width": 1280, "height": 1392 }"""),
            ["set_window_state"] = Args("""{ "handle": 1706796, "state": "maximize" }"""),
            ["focus_window"] = Args("""{ "handle": 1706796 }"""),
            ["close_window"] = Args("""{ "handle": 1706796 }"""),
            ["close_application"] = Args("""{ "processName": "notepad" }"""),
            ["get_owned_windows"] = Args("""{ "handle": 1706796 }"""),
            ["get_window_info"] = Args("""{ "window": { "processName": "notepad" }, "detail": "full" }"""),
            ["wait_for_window"] = Args("""{ "window": { "titleEquals": "Save As" }, "excludeHandles": [1706796] }"""),
            ["snap_window"] = Args("""{ "handle": 1706796, "position": "left_half" }"""),
            ["move_window_to_monitor"] = Args("""{ "handle": 1706796, "monitor": "left" }"""),
            ["set_window_zorder"] = Args("""{ "handle": 1706796, "mode": "topmost" }"""),
            // The windows-batch toolset (roadmap M6.1), off by default
            ["set_windows_bounds_batch"] = Args("""
                { "placements": [
                    { "handle": 1706796, "x": 0, "y": 0, "width": 1280, "height": 1392 },
                    { "handle": 2821181, "x": 1280, "y": 0, "width": 1280, "height": 1392 } ] }
                """),
            ["snap_windows_batch"] = Args("""
                { "placements": [ { "handle": 1706796, "position": "left_half" }, { "handle": 2821181, "position": "right_half" } ] }
                """),
            ["set_windows_state_batch"] = Args("""
                { "changes": [ { "handle": 1706796, "state": "minimize" }, { "handle": 2821181, "state": "restore" } ] }
                """),
            ["close_windows_batch"] = Args("""{ "windows": [ { "handle": 1706796 }, { "handle": 2821181 } ] }"""),
            ["launch_applications_batch"] = Args("""
                { "applications": [ { "executable": "notepad" }, { "executable": "code", "arguments": ["."] } ], "waitForWindows": true }
                """),
            ["get_monitors"] = Args("{}"),
            ["get_primary_monitor"] = Args("{}"), // deprecated (roadmap M6.1): get_monitors
            ["list_screens"] = Args("{}"), // deprecated (roadmap M6.1): get_monitors
            ["take_screenshot"] = Args("{}"),
            ["capture_region"] = Args("""{ "x": 100, "y": 100, "width": 800, "height": 600 }"""),
            ["capture_window"] = Args("""{ "handle": 1706796 }"""),
            ["wait_for_visual"] = Args("""{ "since": "c2-3fa91c" }"""),
            ["capture_with_elements"] = Args("""{ "handle": 1706796 }"""),
            ["read_screen_text"] = Args("""{ "handle": 1706796 }"""),
            ["find_text_on_screen"] = Args("""{ "handle": 1706796, "text": "Canvas board" }"""),
            ["get_desktop_state"] = Args("""{ "handle": 1706796 }"""),
            ["get_ui_elements"] = Args("""{ "handle": 1706796 }"""),
            ["find_ui_elements"] = Args("""{ "handle": 1706796, "name": "Save", "controlType": "Button" }"""),
            ["read_element_text"] = Args("""{ "ref": "w4120035e57" }"""),
            ["read_table"] = Args("""{ "ref": "w4120035e61" }"""),
            ["element_action"] = Args("""{ "ref": "w4120035e17", "action": "set_value", "value": "window layout presets" }"""),
            ["respond_to_dialog"] = Args("""{ "handle": 1706796, "button": "no" }"""),
            ["file_dialog"] = Args($$"""{ "handle": 1706796, "path": {{JsonSerializer.Serialize(TypicalDesktop.SavePath)}}, "action": "save", "overwrite": true }"""),
            ["get_focused_element"] = Args("{}"),
            ["get_element_at_point"] = Args("""{ "x": 560, "y": 110 }"""),
            ["get_active_popups"] = Args("""{ "window": { "processName": "notepad" } }"""),
            ["wait_for_element"] = Args("""{ "window": { "processName": "notepad" }, "name": "Save", "condition": "enabled" }"""),
            ["get_browser_url"] = Args("""{ "handle": 2821181 }"""),
            ["get_active_browser_url"] = Args("{}"), // deprecated (roadmap M6.1): get_browser_url with a foreground selector
            ["get_browser_content"] = Args("""{ "handle": 2821181 }"""),
            ["launch_browser_with_debug"] = Args("""{ "url": "https://example.com/" }"""),
            ["close_debug_browser"] = Args("""{ "processId": 31337, "confirm": true }"""),
            ["list_tabs"] = Args("""{ "handle": 2821181 }"""),
            ["activate_tab"] = Args("""{ "handle": 2821181, "targetId": "2B7E4F1A9C3D5E6F7A8B9C0D1E2F3A4B" }"""),
            ["browser_navigate"] = Args("""{ "handle": 2821181, "url": "https://example.com/docs/?session=abc123" }"""),
            ["wait_for_browser"] = Args("""{ "handle": 2821181, "selector": "#results" }"""),
            ["browser_evaluate"] = Args("""{ "handle": 2821181, "expression": "({ title: document.title, links: document.links.length })" }"""),
            ["browser_handle_dialog"] = Args("""{ "handle": 2821181, "accept": false }"""),
            ["browser_element_rect"] = Args("""{ "handle": 2821181, "role": "button", "name": "Save draft" }"""),
            ["browser_click"] = Args("""{ "handle": 2821181, "text": "Save draft" }"""),
            ["browser_type"] = Args("""{ "handle": 2821181, "text": "window layout presets", "selector": "#search" }"""),
            ["browser_screenshot"] = Args("""{ "handle": 2821181 }"""),
            // Deprecated (roadmap M6.1): mouse_click
            ["click"] = Args("""{ "x": 560, "y": 110, "windowHandle": 1706796 }"""),
            ["right_click"] = Args("""{ "x": 560, "y": 110, "windowHandle": 1706796 }"""),
            ["double_click"] = Args("""{ "x": 560, "y": 110, "windowHandle": 1706796 }"""),
            ["mouse_click"] = Args("""{ "x": 560, "y": 110, "button": "secondary", "modifiers": ["shift"], "windowHandle": 1706796 }"""),
            ["drag"] = Args("""{ "fromX": 560, "fromY": 110, "toX": 760, "toY": 110, "windowHandle": 1706796 }"""),
            ["mouse_move"] = Args("""{ "x": 560, "y": 110, "windowHandle": 1706796 }"""),
            ["type_text"] = Args("""{ "text": "Meeting notes for Monday", "windowHandle": 1706796 }"""),
            ["send_keys"] = Args("""{ "keys": "^s", "windowHandle": 1706796 }"""),
            ["press_keys"] = Args("""{ "keys": "ctrl+shift+t", "windowHandle": 1706796 }"""),
            ["scroll"] = Args("""{ "windowHandle": 1706796, "deltaY": 3 }"""),
            // A form fill on Notepad's window (roadmap M6.2): two fields set through their refs, a typed line, a save, and the
            // Save As dialog waited for; the steps that name no window get the run's
            ["run_steps"] = Args("""
                {
                  "window": { "handle": 1706796 },
                  "steps": [
                    { "tool": "focus_window" },
                    { "tool": "element_action", "args": { "ref": "w4120035e17", "action": "set_value", "value": "window layout presets" } },
                    { "tool": "type_text", "args": { "text": "Meeting notes for Monday" } },
                    { "tool": "press_keys", "args": { "keys": "ctrl+s" } },
                    { "tool": "wait_for_window", "args": { "window": { "processName": "notepad", "titleContains": "Save As" } }, "id": "saveAs" }
                  ]
                }
                """),
            ["get_clipboard"] = Args("{}"),
            ["set_clipboard"] = Args("""{ "text": "Meeting notes for Monday" }"""),
            ["get_control_status"] = Args("{}"),
            ["resume_input"] = Args("{}"),
            ["release_all_input"] = Args("{}"),
            ["launch_application"] = Args("""{ "executable": "notepad", "waitForWindow": true }"""),
            ["list_processes"] = Args("{}"),
            ["kill_process"] = Args("""{ "processId": 10244, "confirm": true, "expectedProcessName": "notepad" }"""),
            ["list_layouts"] = Args("{}"),
            ["get_layout"] = Args("""{ "name": "layout-1" }"""),
            ["save_layout"] = Args("""{ "name": "layout-1", "description": "Editor left, browser right" }"""),
            ["apply_layout"] = Args("""{ "name": "layout-1" }"""),
            ["delete_layout"] = Args("""{ "name": "layout-1" }"""),
            ["get_server_info"] = Args("{}"),
        };

    /// <summary>The failing call of these tools fails differently than the mode's defaults.</summary>
    private static readonly IReadOnlyDictionary<string, Action<ContractServices>> FailingArrange =
        new Dictionary<string, Action<ContractServices>>(StringComparer.Ordinal)
        {
            // Paused, and the test client cannot ask the user (no elicitation): ResumeNeedsUser
            ["resume_input"] = services => services.Session.Pause(InputControlSources.Hotkey)
        };

    /// <summary>
    /// Tools that name their text block's payload ([ToolResultPayload]) but return an image only when the call asks for one
    /// (get_desktop_state's include screenshot, the action tools' observe screenshot; DesktopStateContractTests checks those).
    /// </summary>
    private static readonly HashSet<string> ImageOnRequest = new(StringComparer.Ordinal)
    {
        "get_desktop_state", "element_action", "click", "right_click", "double_click", "mouse_click", "drag", "type_text", "send_keys",
        "press_keys", "respond_to_dialog",
        // The images of its steps with return true, and of its last step (roadmap M6.2)
        "run_steps"
    };

    /// <summary>Tools with a success field that the mocked services cannot make fail.</summary>
    private static readonly HashSet<string> NoFailingCall = new(StringComparer.Ordinal)
    {
        "release_all_input" // releases what the server holds through the fake input wrapper, which never fails
    };

    public static TheoryData<string> ServedTools()
    {
        var data = new TheoryData<string>();
        foreach (var name in ServerToolCatalog.ServedToolNames.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>The typical (successful) call of a tool, shared with the payload budget tests.</summary>
    public static Task<CallToolResult> TypicalCallAsync(string tool) =>
        ContractRun.CallAsync(ContractMode.Typical, tool, ArgumentsFor(tool));

    [Fact]
    public void EveryServedTool_HasCallArguments()
    {
        Arguments.Keys.Should().BeEquivalentTo(ServerToolCatalog.ServedToolNames,
            "every tool is called in the contract run; a new tool needs typical arguments here");
    }

    [Theory]
    [MemberData(nameof(ServedTools))]
    public async Task TypicalCall_ReturnsJsonContent_WithSuccessAndNoIsError(string tool)
    {
        // Act
        var result = await TypicalCallAsync(tool);

        // Assert
        result.Content.Should().NotBeEmpty("'{0}' must return content; an empty result tells the agent nothing", tool);
        var text = result.Content.OfType<TextContentBlock>().ToList();
        text.Should().ContainSingle("'{0}' returns its result as one JSON text block", tool);
        result.IsError.Should().NotBe(true, "the typical call of '{0}' succeeds: {1}", tool, text[0].Text);

        using var json = JsonDocument.Parse(text[0].Text);
        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("success", out var success))
        {
            success.GetBoolean().Should().BeTrue("the typical call of '{0}' succeeds: {1}", tool, text[0].Text);
        }
    }

    [Theory]
    [MemberData(nameof(ServedTools))]
    public async Task TypicalCall_OfACaptureTool_ReturnsTheImageFirst(string tool)
    {
        var isCapture = ServerToolCatalog.Tools[tool].GetCustomAttribute<ToolResultPayloadAttribute>() != null && !ImageOnRequest.Contains(tool);

        var result = await TypicalCallAsync(tool);

        if (!isCapture)
        {
            result.Content.OfType<ImageContentBlock>().Should().BeEmpty("only the capture tools return images");
            return;
        }

        result.Content.Should().HaveCount(2);
        var image = result.Content[0].Should().BeOfType<ImageContentBlock>(
            "the model must see the capture as an image, before its metadata").Subject;
        image.MimeType.Should().StartWith("image/");
        image.DecodedData.ToArray().Should().NotBeEmpty();
        result.Content[1].Should().BeOfType<TextContentBlock>().Which.Text.Should().NotContain(
            System.Text.Encoding.ASCII.GetString(image.Data.Span[..64]),
            "the image is sent once, as image content");
    }

    [Theory]
    [MemberData(nameof(ServedTools))]
    public async Task FailingCall_ReportsIsErrorAsNotSuccess(string tool)
    {
        // Act
        var result = await ContractRun.CallAsync(ContractMode.Failing, tool, ArgumentsFor(tool), FailingArrange.GetValueOrDefault(tool));

        // Assert
        result.Content.Should().NotBeEmpty("'{0}' must say why it failed", tool);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty("a failed call has no image");

        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("success", out var successProperty))
        {
            // A list or a batch summary: an empty list or failed items are a result, not a failed call
            result.IsError.Should().NotBe(true, "'{0}' returned data: {1}", tool, text);
            return;
        }

        var success = successProperty.GetBoolean();
        if (!NoFailingCall.Contains(tool))
        {
            success.Should().BeFalse("the failing call of '{0}' fails: {1}", tool, text);
        }

        (result.IsError == true).Should().Be(!success, "isError must be !success for '{0}': {1}", tool, text);
    }

    [Fact]
    public async Task ParallelReadCalls_ThroughOneServer_AllSucceedWithTheSameResult()
    {
        // MCP clients run read-only tools in parallel; the SDK pipeline, the filters and the tool classes must keep
        // concurrent calls apart (the services are mocks here, so this covers the server, not the native reads)
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);
        string[] tools = ["find_windows", "get_monitors", "list_processes", "get_ui_elements", "get_focused_element"];

        var calls = Enumerable.Range(0, 50)
            .Select(i => tools[i % tools.Length])
            .Select(async tool => (Tool: tool, Result: await server.CallToolAsync(tool, ArgumentsFor(tool))))
            .ToList();
        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(r => r.Result.IsError != true);
        foreach (var group in results.GroupBy(r => r.Tool))
        {
            group.Select(r => r.Result.Content.OfType<TextContentBlock>().Single().Text).Distinct()
                .Should().ContainSingle("every parallel call of '{0}' returns the same result", group.Key);
        }
    }

    /// <summary>The arguments of a tool's typical call, shared with the audit log tests.</summary>
    public static IReadOnlyDictionary<string, object?> ArgumentsFor(string tool) =>
        Arguments.TryGetValue(tool, out var arguments)
            ? arguments
            : throw new KeyNotFoundException($"No contract-run arguments for '{tool}'. Add typical arguments to {nameof(EveryToolContractTests)}.{nameof(Arguments)}.");

    private static IReadOnlyDictionary<string, object?> Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }
}
