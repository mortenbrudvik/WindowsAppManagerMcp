using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the user's target policy (docs/TOOLS.md, Target policy) holds for every tool that names a window or a process. Every
/// served tool whose input schema takes a window (handle, windowHandle, toWindowHandle, aboveHandle, a window selector, a batch's
/// windows, placements or changes) or a processId is called with a window or process the policy denies, through the production
/// pipeline and the real services over faked native seams (<see cref="NativeFakedServer"/>): it must answer TargetDeniedByPolicy,
/// and nothing may act (no window change, input, process start or stop, clipboard write, UI Automation action or CDP command),
/// read the window through UI Automation or capture its pixels. The tools that legitimately do not apply the policy are on
/// <see cref="Exempt"/>, each with the reason; a tool on neither list fails, so a new tool that takes a window must say which.
/// </summary>
/// <remarks>
/// The denied window is a Chrome window (<see cref="DeniedDesktop"/>, the policy's default deny list plus chrome), so the browser
/// tools reach their policy check rather than refusing a window that is not a browser. Nothing touches the desktop: every native
/// seam is a fake, and the harness refuses to start when a real one is reachable.
/// </remarks>
public sealed class DeniedTargetContractTests
{
    private const long Denied = DeniedDesktop.DeniedWindow;
    private const int DeniedPid = DeniedDesktop.DeniedProcessId;
    private const long DeniedDialog = DeniedDesktop.DeniedDialog;

    /// <summary>Where the refusal is in the result.</summary>
    public enum RefusalAt
    {
        /// <summary>The call's own errorCode.</summary>
        Result,

        /// <summary>A batch item's, a run step's or a placement's errorCode (the call reports each item).</summary>
        AnItem
    }

    /// <summary>A call that targets the denied window or process, and where its refusal is reported.</summary>
    /// <param name="Arguments">The call's arguments, as JSON.</param>
    /// <param name="At">Where the refusal is reported.</param>
    /// <param name="Before">A call made first that must succeed, such as saving the layout the call applies.</param>
    public sealed record DeniedCall(string Arguments, RefusalAt At = RefusalAt.Result, (string Tool, string Arguments)? Before = null);

    /// <summary>The tools that must refuse the denied window or process, with the call that names it.</summary>
    private static readonly IReadOnlyDictionary<string, DeniedCall> Checked = new Dictionary<string, DeniedCall>(StringComparer.Ordinal)
    {
        // Window Control and the closes
        ["set_window_bounds"] = new($$"""{ "handle": {{Denied}}, "x": 0, "y": 0 }"""),
        ["set_window_state"] = new($$"""{ "handle": {{Denied}}, "state": "minimize" }"""),
        ["focus_window"] = new($$"""{ "handle": {{Denied}} }"""),
        ["snap_window"] = new($$"""{ "handle": {{Denied}}, "position": "left_half" }"""),
        ["move_window_to_monitor"] = new($$"""{ "handle": {{Denied}}, "monitor": "primary" }"""),
        ["set_window_zorder"] = new($$"""{ "handle": {{Denied}}, "mode": "topmost" }"""),
        ["close_window"] = new($$"""{ "handle": {{Denied}}, "force": true, "confirm": true }"""),
        ["close_application"] = new($$"""{ "processId": {{DeniedPid}}, "confirm": true }"""),
        ["close_windows_batch"] = new($$"""{ "windows": [ { "handle": {{Denied}} } ], "confirm": true }""", RefusalAt.AnItem),
        ["set_windows_bounds_batch"] = new($$"""{ "placements": [ { "handle": {{Denied}}, "x": 0, "y": 0, "width": 800, "height": 600 } ] }""", RefusalAt.AnItem),
        ["snap_windows_batch"] = new($$"""{ "placements": [ { "handle": {{Denied}}, "position": "left_half" } ] }""", RefusalAt.AnItem),
        ["set_windows_state_batch"] = new($$"""{ "changes": [ { "handle": {{Denied}}, "state": "minimize" } ] }""", RefusalAt.AnItem),
        // Processes
        ["kill_process"] = new($$"""{ "processId": {{DeniedPid}}, "confirm": true, "forceKill": true }"""),
        ["close_debug_browser"] = new($$"""{ "processId": {{DeniedPid}}, "confirm": true }"""),
        // Input, by windowHandle
        ["mouse_click"] = new($$"""{ "x": 500, "y": 500, "windowHandle": {{Denied}} }"""),
        ["mouse_move"] = new($$"""{ "x": 500, "y": 500, "windowHandle": {{Denied}} }"""),
        ["drag"] = new($$"""{ "fromX": 500, "fromY": 500, "toX": 600, "toY": 500, "windowHandle": {{Denied}} }"""),
        ["scroll"] = new($$"""{ "windowHandle": {{Denied}}, "deltaY": 3 }"""),
        ["type_text"] = new($$"""{ "text": "1000", "windowHandle": {{Denied}} }"""),
        ["send_keys"] = new($$"""{ "keys": "{ENTER}", "windowHandle": {{Denied}} }"""),
        ["press_keys"] = new($$"""{ "keys": "enter", "windowHandle": {{Denied}} }"""),
        // Elements and dialogs
        ["element_action"] = new($$"""{ "handle": {{Denied}}, "name": "Transfer money", "action": "invoke" }"""),
        // The denied app's own dialog: a window that is no dialog is DialogNotFound before anything else
        ["respond_to_dialog"] = new($$"""{ "handle": {{DeniedDialog}}, "button": "save" }"""),
        ["file_dialog"] = new($$"""{ "handle": {{DeniedDialog}}, "path": "C:\\Temp\\statement.pdf", "action": "save" }"""),
        // The UI Automation reads
        ["get_ui_elements"] = new($$"""{ "handle": {{Denied}} }"""),
        ["find_ui_elements"] = new($$"""{ "handle": {{Denied}}, "name": "Transfer money" }"""),
        ["get_active_popups"] = new($$"""{ "handle": {{Denied}} }"""),
        ["read_element_text"] = new($$"""{ "window": { "handle": {{Denied}} }, "name": "Transfer money" }"""),
        ["wait_for_element"] = new($$"""{ "window": { "handle": {{Denied}} }, "name": "Transfer money", "timeoutMs": 200 }"""),
        // Succeeds with what it may read, and lists the window it left out in errors (documented)
        ["get_desktop_state"] = new($$"""{ "handle": {{Denied}} }""", RefusalAt.AnItem),
        // Captures and the screen's text and pixels
        ["capture_window"] = new($$"""{ "handle": {{Denied}} }"""),
        ["capture_with_elements"] = new($$"""{ "handle": {{Denied}} }"""),
        ["read_screen_text"] = new($$"""{ "handle": {{Denied}} }"""),
        ["find_text_on_screen"] = new($$"""{ "handle": {{Denied}}, "text": "Transfer" }"""),
        ["wait_for_visual"] = new($$"""{ "handle": {{Denied}}, "timeoutMs": 200 }"""),
        // The browser tools: reads, tabs and the DOM bridge
        ["get_browser_url"] = new($$"""{ "handle": {{Denied}} }"""),
        ["get_browser_content"] = new($$"""{ "handle": {{Denied}} }"""),
        ["list_tabs"] = new($$"""{ "handle": {{Denied}} }"""),
        ["activate_tab"] = new($$"""{ "handle": {{Denied}}, "targetId": "2B7E4F1A9C3D5E6F7A8B9C0D1E2F3A4B" }"""),
        ["browser_navigate"] = new($$"""{ "handle": {{Denied}}, "url": "https://example.com/" }"""),
        ["wait_for_browser"] = new($$"""{ "handle": {{Denied}}, "selector": "#balance", "timeoutMs": 200 }"""),
        ["browser_evaluate"] = new($$"""{ "handle": {{Denied}}, "expression": "document.title" }"""),
        ["browser_handle_dialog"] = new($$"""{ "handle": {{Denied}}, "accept": false }"""),
        ["browser_element_rect"] = new($$"""{ "handle": {{Denied}}, "role": "button", "name": "Transfer money" }"""),
        ["browser_click"] = new($$"""{ "handle": {{Denied}}, "text": "Transfer money" }"""),
        ["browser_type"] = new($$"""{ "handle": {{Denied}}, "text": "1000", "selector": "#amount" }"""),
        ["browser_screenshot"] = new($$"""{ "handle": {{Denied}} }"""),
        // A saved layout that places the denied app's window: its placement is refused, and the others would still be arranged
        ["apply_layout"] = new("""{ "name": "bank" }""", RefusalAt.AnItem,
            Before: ("save_layout", """{ "name": "bank", "placements": [ { "processName": "chrome", "snap": "left_half" } ] }""")),
        // A run of steps on the denied window: its first step is refused, and the run with it
        ["run_steps"] = new($$"""{ "window": { "handle": {{Denied}} }, "steps": [ { "tool": "focus_window" }, { "tool": "type_text", "args": { "text": "1000" } } ] }""",
            RefusalAt.AnItem),
    };

    /// <summary>The tools that take a window or a process but do not apply the policy to it, and why.</summary>
    private static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["find_windows"] = "a window list: it lists a denied app's windows with their titles, as documented (Target policy), so no tool acts on them by mistake",
        ["wait_for_window"] = "a window list's wait: it reports the window as find_windows lists it, as documented (Target policy)",
        ["get_window_info"] = "find_windows' row of one window, with its state and integrity; it reads no content of the window",
        ["get_owned_windows"] = "a window list: the windows a window owns, as find_windows lists them",
        ["get_window_events"] = "the window event journal records every app's titles, as find_windows lists them; the policy does not hide them (documented)",
        ["list_processes"] = "a process list; a denied process is listed, but its command line is left out and never read (ProcessService)",
        ["save_layout"] = "writes a layout file: its placements name windows by matchers and it acts on none; a window whose process cannot be named is saved without its title",
    };

    /// <summary>The window and process properties of a tool's input schema, found at any depth.</summary>
    private static readonly HashSet<string> TargetProperties = new(StringComparer.Ordinal)
    {
        "handle", "windowHandle", "toWindowHandle", "aboveHandle", "window", "windows", "placements", "changes", "processId"
    };

    public static TheoryData<string> CheckedTools()
    {
        var data = new TheoryData<string>();
        foreach (var tool in Checked.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(tool);
        }

        return data;
    }

    [Fact]
    public void EveryToolThatNamesAWindowOrAProcess_IsCheckedOrExempt()
    {
        // Arrange - the served tools' input schemas, as tools/list sends them
        var targeting = ServedSchemas()
            .Where(t => TargetPropertiesOf(t.Schema).Any())
            .Select(t => t.Name)
            .ToList();

        // Assert
        targeting.Should().Contain(["close_window", "type_text", "kill_process", "run_steps"], "the scan must see the schemas' window and process parameters");
        Checked.Keys.Intersect(Exempt.Keys).Should().BeEmpty("a tool is checked or exempt, not both");
        targeting.Except(Checked.Keys).Except(Exempt.Keys).Should().BeEmpty(
            "a tool that takes a window or a process must refuse one the user's target policy denies: add it to Checked with a call " +
            "that names the denied window, or to Exempt with the reason it legitimately does not apply the policy");
        Checked.Keys.Concat(Exempt.Keys).Except(targeting).Should().BeEmpty("an entry names a served tool that takes a window or a process");
    }

    [Theory]
    [MemberData(nameof(CheckedTools))]
    public async Task ACallThatNamesADeniedWindowOrProcess_IsTargetDeniedByPolicy_AndNothingIsDone(string tool)
    {
        // Arrange
        var call = Checked[tool];
        using var desktop = new NativeFakedServer(DeniedDesktop.Policy);
        await using var server = await desktop.StartAsync();

        if (call.Before is var (before, beforeArguments))
        {
            var prepared = await server.CallToolAsync(before, Arguments(beforeArguments));
            prepared.IsError.Should().NotBe(true, "{0} prepares the call: {1}", before, prepared.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        }

        // Act
        var result = await server.CallToolAsync(tool, Arguments(call.Arguments));

        // Assert
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        using var payload = JsonDocument.Parse(text);
        var codes = call.At == RefusalAt.Result
            ? payload.RootElement.TryGetProperty("errorCode", out var code) ? [code.GetString()] : []
            : ErrorCodes(payload.RootElement).ToList();
        codes.Should().Contain(nameof(InputErrorCode.TargetDeniedByPolicy), "'{0}' must refuse a window or process the policy denies: {1}", tool, text);
        if (call.At == RefusalAt.Result)
        {
            result.IsError.Should().BeTrue("a refusal is a tool error: {0}", text);
        }

        desktop.Actions().Should().BeEmpty("'{0}' must do nothing to a window or process the policy denies", tool);
        desktop.Desktop.WindowsReadThroughUiAutomation.Should().NotContain([(nint)Denied, (nint)DeniedDialog],
            "'{0}' must not read a denied window through UI Automation", tool);
        desktop.Mock<IScreenCaptureWrapper>().Invocations
            .Where(i => i.Method.Name.StartsWith("CaptureWindow", StringComparison.Ordinal) && i.Arguments.Contains((nint)Denied))
            .Should().BeEmpty("'{0}' must not capture a denied window's pixels", tool);
        desktop.Mock<IGraphicsCaptureWrapper>().Invocations.Where(i => i.Arguments.Contains((nint)Denied))
            .Should().BeEmpty("'{0}' must not capture a denied window's pixels", tool);
    }

    // ---- helpers ----

    /// <summary>Every errorCode in a result, at any depth (a batch's items, a run's steps, a layout's placements).</summary>
    private static IEnumerable<string?> ErrorCodes(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "errorCode" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString();
                    }

                    foreach (var code in ErrorCodes(property.Value))
                    {
                        yield return code;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var code in ErrorCodes(item))
                    {
                        yield return code;
                    }
                }

                break;
        }
    }

    private static IReadOnlyDictionary<string, object?> Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>The served tools and their input schemas, from the production registration (no service is created).</summary>
    private static IReadOnlyList<(string Name, JsonElement Schema)> ServedSchemas()
    {
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(() => throw new InvalidOperationException("The schemas need no layout storage."));
        services.AddMcpServer().AddWindowsAppManagerTools();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .Select(t => (t.ProtocolTool.Name, t.ProtocolTool.InputSchema.Clone()))
            .ToList();
    }

    /// <summary>The names of the window and process properties in a schema, at any depth (items of arrays, anyOf and the like).</summary>
    private static IEnumerable<string> TargetPropertiesOf(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (TargetProperties.Contains(property.Name))
                {
                    yield return property.Name;
                }

                foreach (var nested in TargetPropertiesOf(property.Value))
                {
                    yield return nested;
                }
            }
        }

        foreach (var key in new[] { "items", "anyOf", "oneOf", "allOf" })
        {
            if (!schema.TryGetProperty(key, out var value))
            {
                continue;
            }

            var children = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
            foreach (var nested in children.SelectMany(TargetPropertiesOf))
            {
                yield return nested;
            }
        }
    }
}
