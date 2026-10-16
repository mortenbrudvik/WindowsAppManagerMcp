using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the tools roadmap M6.1 took off the default surface (owner decisions D1 and D4, 2.2.0). The nine tools that
/// another tool replaced (click, right_click, double_click, move_window, resize_window, get_all_windows,
/// get_primary_monitor, list_screens, get_active_browser_url) are in the opt-in <c>deprecated</c> toolset until 3.0.0
/// removes them, each with a one-line description that names its replacement; the three geometry batches are in the
/// opt-in <c>windows-batch</c> toolset, not deprecated. A moved tool is served only while the toolset it came from is on
/// too, so no configuration serves more than it did before; a call to a tool that is not served is an unknown tool, never
/// forwarded to its replacement; excluding a moved tool that is not served is no error; and get_server_info says where a
/// moved tool went. In-process servers with the production registrations; no tool acts on the desktop.
/// </summary>
public class DeprecatedToolsContractTests
{
    /// <summary>Each replaced tool, the toolset it came from and the tool that replaces it.</summary>
    public static TheoryData<string, string, string> Replaced() => new()
    {
        { "click", "input", "mouse_click" },
        { "right_click", "input", "mouse_click" },
        { "double_click", "input", "mouse_click" },
        { "move_window", "windows", "set_window_bounds" },
        { "resize_window", "windows", "set_window_bounds" },
        { "get_all_windows", "windows", "find_windows" },
        { "get_primary_monitor", "monitors", "get_monitors" },
        { "list_screens", "monitors", "get_monitors" },
        { "get_active_browser_url", "browser", "get_browser_url" },
    };

    private static readonly string[] DeprecatedTools =
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
    public void TheDeprecatedToolset_HoldsTheNineReplacedTools_AndIsOffByDefault()
    {
        var toolset = Toolsets.Server.Find("deprecated")!;

        toolset.OnByDefault.Should().BeFalse();
        toolset.Tools.Should().BeEquivalentTo(DeprecatedTools);
        foreach (var row in Replaced())
        {
            var (tool, from, replacement) = ((string)row[0], (string)row[1], (string)row[2]);
            var moved = Toolsets.Server.MovedTools.Should().ContainSingle(m => m.Tool == tool).Subject;
            moved.FormerToolset.Should().Be(from);
            moved.Replacement.Should().StartWith(replacement);
        }
    }

    [Fact]
    public void TheWindowsBatchToolset_HoldsTheThreeGeometryBatches_OffByDefault_AndNothingReplacesThem()
    {
        var toolset = Toolsets.Server.Find("windows-batch")!;
        var byDefault = ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server);

        toolset.OnByDefault.Should().BeFalse();
        toolset.Tools.Should().BeEquivalentTo(WindowBatches);
        Toolsets.Server.MovedTools.Where(m => WindowBatches.Contains(m.Tool))
            .Should().HaveCount(3).And.OnlyContain(m => m.FormerToolset == "windows" && m.Replacement == null);

        // Closing and launching several at once stay in the default (owner decision D4)
        byDefault.Serves("close_windows_batch", readOnlyHint: false).Should().BeTrue();
        byDefault.Serves("launch_applications_batch", readOnlyHint: false).Should().BeTrue();
    }

    [Fact]
    public void EveryMovedTool_CameFromAToolsetThatIsOnByDefault_AndEveryReplacementIsServedByDefault()
    {
        var byDefault = ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server);

        Toolsets.Server.MovedTools.Select(m => m.Tool).Should().BeEquivalentTo([.. DeprecatedTools, .. WindowBatches]);
        foreach (var moved in Toolsets.Server.MovedTools)
        {
            Toolsets.Server.Find(moved.FormerToolset)!.OnByDefault.Should().BeTrue();
            byDefault.Serves(moved.Tool, readOnlyHint: true).Should().BeFalse("{0} is off by default since 2.2.0", moved.Tool);
            if (moved.Replacement is { } replacement)
            {
                byDefault.Serves(replacement.Split(' ')[0], readOnlyHint: false).Should().BeTrue(
                    "{0}'s replacement must be served wherever {0} was", moved.Tool);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Replaced))]
    public async Task EveryDeprecatedTool_HasAOneLineDescription_ThatNamesItsReplacement_AndTheRemoval(
        string tool, string from, string replacement)
    {
        // Roadmap M6.1, owner decision D1: the agent that still sees the tool (WAM_TOOLSETS=+deprecated or all) learns what
        // to call instead, and pays a line for it, not the old description
        var description = (string)ToolSurface.Tool(await ToolSurface.ListToolsAsync(), tool)["description"]!;
        var moved = Toolsets.Server.MovedTools.Single(m => m.Tool == tool);

        description.Should().StartWith("Deprecated, removed in 3.0.0: use ").And.Contain(moved.Replacement!)
            .And.Contain(replacement).And.NotContain("\n");
        description.Length.Should().BeLessThan(200, "it is one line");
        Toolsets.Server.ToolsetOf(tool)!.Name.Should().Be("deprecated");
        moved.FormerToolset.Should().Be(from);
    }

    [Fact]
    public async Task TheDefaultServer_ListsNoMovedTool_AndACallToOneIsAnUnknownTool_NotForwardedToItsReplacement()
    {
        // Arrange
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(ToolSurface.DefaultSelection));

        // Act
        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        var act = () => server.CallToolAsync("click", new Dictionary<string, object?> { ["x"] = 10, ["y"] = 10 });

        // Assert - a forwarded call would have returned mouse_click's result
        tools.Should().NotContain(DeprecatedTools).And.NotContain(WindowBatches)
            .And.Contain(["mouse_click", "set_window_bounds", "find_windows", "get_monitors", "get_browser_url",
                "close_windows_batch", "launch_applications_batch"]);
        (await act.Should().ThrowAsync<McpProtocolException>()).Which.Message.Should().Contain("click");
    }

    [Theory]
    [InlineData("+deprecated", true, false)]
    [InlineData("+windows-batch", false, true)]
    [InlineData("all", true, true)]
    [InlineData("default,+deprecated,+windows-batch", true, true)]
    public async Task TheMovedTools_AreServedWithTheirToolset(string toolsets, bool deprecated, bool batches)
    {
        var tools = await ListAsync(Select(toolsets));

        DeprecatedTools.Should().OnlyContain(tool => tools.Contains(tool) == deprecated);
        WindowBatches.Should().OnlyContain(tool => tools.Contains(tool) == batches);
    }

    [Theory]
    [InlineData("all,-input")]
    [InlineData("-input,+deprecated")]
    [InlineData("windows,monitors,deprecated")]
    public async Task ADeprecatedTool_IsNotServed_WhenTheToolsetItCameFromIsOff(string toolsets)
    {
        // A configuration that turned input off before 2.2.0 had no click; 'all' or '+deprecated' must not give it one now
        var tools = await ListAsync(Select(toolsets));

        tools.Should().NotContain(["click", "right_click", "double_click", "mouse_click"]);
        tools.Should().Contain(["move_window", "resize_window", "get_all_windows"], "windows is on");
    }

    [Fact]
    public async Task AllWithoutWindows_ServesNoWindowBatch()
    {
        var tools = await ListAsync(Select("all,-windows"));

        tools.Should().NotContain(WindowBatches).And.NotContain(["move_window", "resize_window", "get_all_windows"]);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("click,move_window,list_screens,set_windows_bounds_batch")]
    [InlineData("deprecated,windows-batch")]
    public async Task ExcludingAMovedToolThatIsNotServed_DoesNotFailClosed(string exclude)
    {
        // A WAM_EXCLUDE_TOOLS written for 2.1 names tools the default no longer serves; it must not cut the server down to
        // the server toolset (as an unknown name does)
        var selection = Select(null, exclude);

        var tools = await ListAsync(selection);

        selection.Source.Should().Be(ToolsetSelection.DefaultKeyword);
        selection.Warnings.Should().BeEmpty();
        selection.InstructionsNote.Should().BeNull("nothing the default serves was turned off");
        tools.Should().BeEquivalentTo(await ListAsync(ToolSurface.DefaultSelection));
    }

    [Fact]
    public async Task ExcludingTheInputToolset_AlsoExcludesTheDeprecatedClicks()
    {
        // WAM_EXCLUDE_TOOLS=input meant every input tool before 2.2.0, click, right_click and double_click included
        var tools = await ListAsync(Select("+deprecated", "input"));

        tools.Should().NotContain(["click", "right_click", "double_click", "mouse_click", "type_text"])
            .And.Contain(["move_window", "list_screens"]);
    }

    [Fact]
    public void TheDeprecatedTools_StayGatedAndAudited_WhileTheyAreServed()
    {
        // Their behaviour is unchanged (owner decision D1): the same input gate list, the same audit lines
        InputGateToolFilter.InputTools.Should().Contain(["click", "right_click", "double_click"]);
        InputGateToolFilter.SelfGatedTools.Should().Contain(["move_window", "resize_window", .. WindowBatches]);
        InputGateToolFilter.ReadOnlyTools.Should().Contain(["get_all_windows", "get_primary_monitor", "list_screens", "get_active_browser_url"]);
        AuditLogToolFilter.AuditedTools.Should().Contain(["click", "right_click", "double_click"]);
    }

    [Fact]
    public async Task GetServerInfo_SaysWhereTheMovedToolsWent_ForAListThatNamesTheirOldToolsets()
    {
        // A client configured with WAM_TOOLSETS=windows,input before 2.2.0 relied on click and move_window
        var toolsets = await ServerInfoToolsetsAsync(Select("windows,input"));

        var moved = toolsets.GetProperty("moved").EnumerateArray().Select(m => m.GetString()!).ToList();
        moved.Should().Contain(m => m.Contains("deprecated toolset") && m.Contains("click (use mouse_click)") &&
                                    m.Contains("move_window (use set_window_bounds") && m.Contains("WAM_TOOLSETS=+deprecated"));
        moved.Should().Contain(m => m.Contains("windows-batch toolset") && m.Contains("set_windows_bounds_batch"));
        moved.Should().NotContain(m => m.Contains("list_screens"), "monitors is off, so list_screens did not move out of reach");
    }

    [Fact]
    public async Task GetServerInfo_WarnsWhileDeprecatedToolsAreServed()
    {
        var info = await ServerInfoAsync(Select("+deprecated"));

        info.GetProperty("toolsets").TryGetProperty("moved", out var moved).Should().BeTrue();
        moved.EnumerateArray().Should().NotContain(m => m.GetString()!.Contains("deprecated toolset"));
        info.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!)
            .Should().Contain(w => w.Contains("click (use mouse_click)") && w.Contains("removed"));
    }

    private static async Task<JsonElement> ServerInfoToolsetsAsync(ToolsetSelection selection) =>
        (await ServerInfoAsync(selection)).GetProperty("toolsets");

    private static async Task<JsonElement> ServerInfoAsync(ToolsetSelection selection)
    {
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(selection), configureServices: services.Register);
        var result = await server.CallToolAsync("get_server_info");
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();
    }
}
