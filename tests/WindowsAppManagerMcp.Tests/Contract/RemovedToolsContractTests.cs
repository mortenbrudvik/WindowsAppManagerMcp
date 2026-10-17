using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the tools roadmap M6.1 took off the default surface. 3.0.0 removed the nine tools that another tool replaced
/// in 2.2.0 (owner decision D1): no selection serves them, a call to one fails with ToolRemoved and names the tool to call
/// instead, never forwarded to it, and a WAM_TOOLSETS or WAM_EXCLUDE_TOOLS value written for 2.x that names them or their
/// deprecated toolset still reads without failing closed. The three geometry batches only moved, to the opt-in
/// windows-batch toolset (owner decision D4): each is served only while the windows toolset is on too, and get_server_info
/// says where they went. In-process servers with the production registrations; no tool acts on the desktop.
/// </summary>
public class RemovedToolsContractTests
{
    /// <summary>Each removed tool and the tool its refusal names.</summary>
    public static TheoryData<string, string> Removed() => new()
    {
        { "click", "mouse_click" },
        { "right_click", "mouse_click" },
        { "double_click", "mouse_click" },
        { "move_window", "set_window_bounds" },
        { "resize_window", "set_window_bounds" },
        { "get_all_windows", "find_windows" },
        { "get_primary_monitor", "get_monitors" },
        { "list_screens", "get_monitors" },
        { "get_active_browser_url", "get_browser_url" },
    };

    private static readonly string[] RemovedNames =
    [
        "click", "right_click", "double_click", "move_window", "resize_window", "get_all_windows", "get_primary_monitor",
        "list_screens", "get_active_browser_url"
    ];

    private static readonly string[] WindowBatches = ["set_windows_bounds_batch", "snap_windows_batch", "set_windows_state_batch"];

    private static ToolsetSelection Select(string? toolsets, string? exclude = null) =>
        ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => toolsets,
                ToolsetSelection.ExcludeToolsVariable => exclude,
                _ => null
            },
            Toolsets.Server);

    private static async Task<List<string>> ListAsync(ToolsetSelection selection)
    {
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));
        return (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();
    }

    [Fact]
    public void TheRemovedTools_AreTheNine_AndNoToolsetNamesThemOrTheDeprecatedToolset()
    {
        RemovedTools.Replacements.Keys.Should().BeEquivalentTo(RemovedNames);
        Toolsets.Server.Find(RemovedTools.Toolset).Should().BeNull();
        RemovedNames.Should().OnlyContain(tool => Toolsets.Server.ToolsetOf(tool) == null);
        Toolsets.Server.MovedTools.Select(m => m.Tool).Should().BeEquivalentTo(WindowBatches);
        Toolsets.Server.RetiredNames.Should().BeEquivalentTo([RemovedTools.Toolset, .. RemovedNames]);
    }

    [Fact]
    public void NoInputGateOrAuditListNamesARemovedTool()
    {
        InputGateToolFilter.InputTools.Should().NotIntersectWith(RemovedNames);
        InputGateToolFilter.SelfGatedTools.Should().NotIntersectWith(RemovedNames);
        InputGateToolFilter.ReadOnlyTools.Should().NotIntersectWith(RemovedNames);
        AuditLogToolFilter.AuditedTools.Should().NotIntersectWith(RemovedNames);
    }

    [Fact]
    public void EveryReplacement_IsServedByDefault()
    {
        var byDefault = ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server);

        foreach (var row in Removed())
        {
            var (tool, replacement) = ((string)row[0], (string)row[1]);
            RemovedTools.Replacements[tool].Should().StartWith(replacement);
            byDefault.Serves(replacement, readOnlyHint: false).Should().BeTrue("{0} replaces {1}", replacement, tool);
        }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("default")]
    [InlineData("+deprecated")]
    public async Task NoSelection_ListsARemovedTool(string toolsets)
    {
        var tools = await ListAsync(Select(toolsets));

        tools.Should().NotIntersectWith(RemovedNames).And.Contain(["mouse_click", "set_window_bounds", "find_windows", "get_monitors",
            "get_browser_url"]);
    }

    [Theory]
    [MemberData(nameof(Removed))]
    public async Task ACallToARemovedTool_IsToolRemoved_NamingItsReplacement_AndIsNotForwarded(string tool, string replacement)
    {
        // Arrange - every tool served, as WAM_TOOLSETS=all
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);

        // Act
        var result = await server.CallToolAsync(tool, new Dictionary<string, object?> { ["x"] = 10, ["y"] = 10 });

        // Assert - a forwarded call would have returned the replacement's result
        result.IsError.Should().BeTrue();
        var payload = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
        payload.GetProperty("errorCode").GetString().Should().Be(RemovedTools.ErrorCode);
        payload.GetProperty("error").GetString().Should().Contain($"'{tool}' was removed in 3.0.0").And.Contain(replacement)
            .And.EndWith("Nothing was done.");
        services.Input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("click")]
    [InlineData("click,move_window,list_screens,set_windows_bounds_batch")]
    [InlineData("deprecated,windows-batch")]
    public async Task ExcludingARemovedOrUnservedTool_DoesNotFailClosed(string exclude)
    {
        // A WAM_EXCLUDE_TOOLS written for 2.x names tools 3.0.0 removed or the default does not serve; it must not cut the
        // server down to the server toolset (as an unknown name does)
        var selection = Select(null, exclude);

        var tools = await ListAsync(selection);

        selection.Source.Should().Be(ToolsetSelection.DefaultKeyword);
        selection.Warnings.Should().BeEmpty();
        selection.InstructionsNote.Should().BeNull("nothing the default serves was turned off");
        tools.Should().BeEquivalentTo(await ListAsync(ToolSurface.DefaultSelection));
    }

    [Theory]
    [InlineData("+deprecated")]
    [InlineData("all,-deprecated")]
    [InlineData("windows,monitors,deprecated")]
    public void TheDeprecatedToolset_InWamToolsets_IsIgnoredWithAWarning_AndDoesNotFailClosed(string toolsets)
    {
        var selection = Select(toolsets);

        selection.Warnings.Should().Contain(w => w.Contains("'deprecated' toolset was removed"));
        selection.Serves("set_window_bounds", readOnlyHint: false).Should().BeTrue();
        selection.Serves("get_monitors", readOnlyHint: true).Should().BeTrue();
    }

    [Fact]
    public void TheDeprecatedToolsetAlone_StillFailsClosed()
    {
        // The user meant to serve the deprecated tools only, which are gone: the default would serve more
        var selection = Select("deprecated");

        selection.Source.Should().Be(ToolsetSelection.ToolsetsVariable);
        selection.Serves("mouse_click", readOnlyHint: false).Should().BeFalse();
        selection.Serves("get_server_info", readOnlyHint: true).Should().BeTrue();
    }

    [Fact]
    public void TheWindowsBatchToolset_HoldsTheThreeGeometryBatches_OffByDefault_AndNothingReplacesThem()
    {
        var toolset = Toolsets.Server.Find("windows-batch")!;
        var byDefault = ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server);

        toolset.OnByDefault.Should().BeFalse();
        toolset.Tools.Should().BeEquivalentTo(WindowBatches);
        Toolsets.Server.MovedTools.Should().HaveCount(3).And.OnlyContain(m => m.FormerToolset == "windows" && m.Replacement == null);
        byDefault.Serves("set_windows_bounds_batch", readOnlyHint: false).Should().BeFalse();

        // Closing and launching several at once stay in the default (owner decision D4)
        byDefault.Serves("close_windows_batch", readOnlyHint: false).Should().BeTrue();
        byDefault.Serves("launch_applications_batch", readOnlyHint: false).Should().BeTrue();
    }

    [Theory]
    [InlineData("+windows-batch", true)]
    [InlineData("all", true)]
    [InlineData("all,-windows", false)]
    [InlineData("default", false)]
    public async Task TheWindowBatches_AreServedWithTheirToolset_AndOnlyWithWindows(string toolsets, bool batches)
    {
        var tools = await ListAsync(Select(toolsets));

        WindowBatches.Should().OnlyContain(tool => tools.Contains(tool) == batches);
    }

    [Fact]
    public async Task GetServerInfo_SaysWhereTheWindowBatchesWent_ForAListThatNamesWindows()
    {
        var info = await ServerInfoAsync(Select("windows,input"));

        var moved = info.GetProperty("toolsets").GetProperty("moved").EnumerateArray().Select(m => m.GetString()!).ToList();
        moved.Should().ContainSingle().Which.Should().Contain("windows-batch toolset").And.Contain("set_windows_bounds_batch")
            .And.NotContain("deprecated");
    }

    // ---- monitorIndex (owner decision D1): every tool and placement takes monitor alone since 3.0.0 ----

    [Theory]
    [InlineData("take_screenshot", """{ "monitorIndex": 0 }""")]
    [InlineData("snap_window", """{ "handle": 1706796, "position": "left_half", "monitorIndex": 1 }""")]
    [InlineData("move_window_to_monitor", """{ "handle": 1706796, "monitorIndex": 1 }""")]
    [InlineData("read_screen_text", """{ "monitorIndex": 0 }""")]
    [InlineData("wait_for_visual", """{ "monitorIndex": 0 }""")]
    public async Task MonitorIndex_IsRefused_AndTheErrorSendsTheAgentToMonitor(string tool, string arguments)
    {
        var (result, services) = await CallAsync(tool, arguments);

        using (services)
        {
            result.IsError.Should().BeTrue();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            payload.GetProperty("error").GetString().Should().Contain("'monitorIndex' (removed in 3.0.0: use 'monitor'");
            services.Windows.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Snap") || i.Method.Name.StartsWith("MoveWindow"));
            services.Screen.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Capture"));
        }
    }

    [Theory]
    [InlineData("launch_application", """{ "executable": "notepad", "placement": { "snap": "left_half", "monitorIndex": 1 } }""")]
    [InlineData("snap_windows_batch", """{ "placements": [ { "position": "left_half", "handle": 1706796, "monitorIndex": 1 } ] }""")]
    public async Task MonitorIndex_InAPlacement_IsRefused_NotIgnored(string tool, string arguments)
    {
        // An ignored member would launch or snap the window on another monitor than the agent meant
        var (result, services) = await CallAsync(tool, arguments);

        using (services)
        {
            result.IsError.Should().BeTrue();
            var payload = Payload(result);
            payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            payload.GetProperty("error").GetString().Should().Contain("monitorIndex");
            services.Processes.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Launch"));
            services.Windows.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("Snap"));
        }
    }

    [Fact]
    public async Task MoveWindowToMonitor_WithoutMonitor_IsInvalidArguments()
    {
        var (result, services) = await CallAsync("move_window_to_monitor", """{ "handle": 1706796 }""");

        using (services)
        {
            result.IsError.Should().BeTrue();
            Payload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
            services.Windows.Invocations.Should().NotContain(i => i.Method.Name.StartsWith("MoveWindow"));
        }
    }

    private static async Task<(CallToolResult Result, ContractServices Services)> CallAsync(string tool, string arguments)
    {
        var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);
        using var document = JsonDocument.Parse(arguments);
        var parsed = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        return (await server.CallToolAsync(tool, parsed), services);
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    private static async Task<JsonElement> ServerInfoAsync(ToolsetSelection selection)
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(selection), configureServices: services.Register);
        var result = await server.CallToolAsync("get_server_info");
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();
    }
}
