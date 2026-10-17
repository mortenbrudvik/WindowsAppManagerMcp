using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: toolsets, excluded tools and read-only mode (roadmap M2.14) decide what an MCP client sees. A tool that is
/// not served is missing from tools/list and a call to it is an unknown tool, not a refusal at run time. The mechanism is
/// checked with fixture tools and a fixture catalog that has an opt-in toolset; the production catalog is checked
/// against the tools the server serves. In-process servers; no production tool is called.
/// </summary>
public class ToolsetContractTests
{
    private static readonly ToolsetCatalog FixtureCatalog = new(
        "core",
        [
            new Toolset("core", OnByDefault: true, "The server's own state", ["info_tool"]),
            new Toolset("basic", OnByDefault: true, "Looking and acting", ["look_tool", "act_tool"]),
            new Toolset("extra", OnByDefault: false, "An opt-in group", ["extra_tool"]),
        ]);

    #region The mechanism, with fixture tools

    [Theory]
    [InlineData(null, null, null, new[] { "act_tool", "info_tool", "look_tool" })]
    [InlineData("all", null, null, new[] { "act_tool", "extra_tool", "info_tool", "look_tool" })]
    [InlineData("extra", null, null, new[] { "extra_tool", "info_tool" })]
    [InlineData("-basic", null, null, new[] { "info_tool" })]
    [InlineData(null, "act_tool", null, new[] { "info_tool", "look_tool" })]
    [InlineData(null, null, "1", new[] { "info_tool", "look_tool" })]
    [InlineData("all", null, "1", new[] { "extra_tool", "info_tool", "look_tool" })]
    [InlineData("all", "info_tool", "1", new[] { "extra_tool", "look_tool" })]
    public async Task ToolsList_ServesWhatTheSelectionChooses(string? toolsets, string? exclude, string? readOnly, string[] expected)
    {
        // Arrange
        await using var server = await StartFixtureServerAsync(Select(toolsets, exclude, readOnly));

        // Act
        var tools = await server.Client.ListToolsAsync();

        // Assert
        tools.Select(t => t.Name).Order(StringComparer.Ordinal).Should().Equal(expected);
    }

    [Fact]
    public async Task ACallToAToolThatIsNotServed_IsAnUnknownTool()
    {
        // The default leaves out the opt-in toolset; the call fails as a protocol error, so nothing runs
        await using var server = await StartFixtureServerAsync(Select(null, null, null));

        var act = () => server.CallToolAsync("extra_tool");

        (await act.Should().ThrowAsync<McpProtocolException>()).Which.Message.Should().Contain("extra_tool");
        FixtureTools.Calls.Should().NotContain("extra_tool");
    }

    [Fact]
    public async Task AServedTool_StillRuns()
    {
        await using var server = await StartFixtureServerAsync(Select("all", null, "1"));

        var result = await server.CallToolAsync("look_tool");

        result.IsError.Should().NotBe(true);
    }

    private static ToolsetSelection Select(string? toolsets, string? exclude, string? readOnly) =>
        ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => toolsets,
                ToolsetSelection.ExcludeToolsVariable => exclude,
                ToolsetSelection.ReadOnlyVariable => readOnly,
                _ => null
            },
            FixtureCatalog);

    private static Task<InProcessMcpServer> StartFixtureServerAsync(ToolsetSelection selection) =>
        InProcessMcpServer.StartAsync([], builder => builder.WithTools<FixtureTools>().WithToolsets(selection));

    [McpServerToolType]
    public sealed class FixtureTools
    {
        public static System.Collections.Concurrent.ConcurrentBag<string> Calls { get; } = [];

        [McpServerTool(Name = "info_tool", ReadOnly = true), Description("Reports the fixture's state.")]
        public static string Info() => Record("info_tool");

        [McpServerTool(Name = "look_tool", ReadOnly = true), Description("Looks.")]
        public static string Look() => Record("look_tool");

        [McpServerTool(Name = "act_tool"), Description("Acts.")]
        public static string Act() => Record("act_tool");

        [McpServerTool(Name = "extra_tool", ReadOnly = true), Description("Opt-in.")]
        public static string Extra() => Record("extra_tool");

        private static string Record(string tool)
        {
            Calls.Add(tool);
            return tool;
        }
    }

    #endregion

    #region The production toolsets

    [Fact]
    public void EveryServedTool_IsInOneToolset_AndTheToolsetsNameOnlyServedTools()
    {
        var served = ServerToolCatalog.ServedToolNames;

        served.Where(tool => Toolsets.Server.ToolsetOf(tool) == null).Should().BeEmpty(
            "a new tool needs a toolset in Toolsets.Server (and an entry in the toolsets table of docs/TOOLS.md, generated)");
        Toolsets.Server.ToolNames.Should().BeSubsetOf(served, "a toolset must not name a tool the server does not serve");
    }

    /// <summary>
    /// The toolsets that are off by default: browser-actions since roadmap M5.5 (and the DOM bridge of M5.8), browser-script
    /// since M5.8, windows-batch since M6.1 (owner decision D4; RemovedToolsContractTests), visual since M6.4 (owner
    /// decisions D2 and D10), ocr since M6.3 (owner decision D2), workspaces since M7.5.
    /// </summary>
    private static readonly string[] OptInToolsets = ["workspaces", "browser-actions", "browser-script", "visual", "windows-batch", "ocr"];

    /// <summary>The DOM bridge's tools (roadmap M5.8) that browser-actions serves: all but browser_evaluate.</summary>
    private static readonly string[] DomBridgeActionTools =
        ["browser_element_rect", "browser_click", "browser_type", "browser_handle_dialog", "browser_screenshot"];

    [Fact]
    public void TheDefault_ServesEveryToolset_ButTheOptInOnes()
    {
        // The niche or heavy toolsets M5-M6 add are opt-in (owner decision D2: browser actions and scripts, OCR, visual diffs,
        // the geometry batches); the first, browser-actions (activate_tab, browser_navigate, roadmap M5.5), changes what a
        // debug browser shows. The tools M6.1 replaced are gone since 3.0.0. Turning a toolset
        // off by default changes what every user sees: change this test, the docs and the release notes with it.
        var selection = ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server);

        selection.Disabled.Should().BeEquivalentTo(OptInToolsets);
        selection.Source.Should().Be(ToolsetSelection.DefaultKeyword);
        Toolsets.Server.Toolsets.Where(t => !t.OnByDefault).Select(t => t.Name).Should().BeEquivalentTo(OptInToolsets);
        ServerToolCatalog.ServedToolNames
            .Where(tool => !OptInToolsets.Contains(Toolsets.Server.ToolsetOf(tool)!.Name))
            .Should().OnlyContain(tool => selection.Serves(tool, false));
        Toolsets.Server.Find("browser-actions")!.Tools.Should().BeEquivalentTo(
                ["activate_tab", "browser_navigate", .. DomBridgeActionTools])
            .And.OnlyContain(tool => !selection.Serves(tool, false));

        // A script runs with the page's rights and leaves no trace on the screen: turning clicks and typing on does not turn
        // it on (roadmap M5.8)
        Toolsets.Server.Find("browser-script")!.Tools.Should().BeEquivalentTo(["browser_evaluate"])
            .And.OnlyContain(tool => !selection.Serves(tool, false));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("+ocr", true)]
    [InlineData("all", true)]
    public async Task TheOcrTools_AreServed_OnlyWithTheOcrToolset(string? toolsets, bool servesOcr)
    {
        // Roadmap M6.3 (owner decisions D2 and D10): two read-only tools in their own opt-in toolset, and nothing else in it
        var selection = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ToolsetsVariable ? toolsets : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        Toolsets.Server.Find("ocr")!.Tools.Should().BeEquivalentTo(["read_screen_text", "find_text_on_screen"]);
        tools.Contains("read_screen_text").Should().Be(servesOcr);
        tools.Contains("find_text_on_screen").Should().Be(servesOcr);
    }

    [Theory]
    [InlineData("+browser-actions", false)]
    [InlineData("+browser-script", true)]
    [InlineData("+browser-actions,+browser-script", true)]
    public async Task BrowserEvaluate_IsServed_OnlyWithBrowserScript(string toolsets, bool servesEvaluate)
    {
        // Turning on clicks and typing (browser-actions) does not turn on scripts, and turning on scripts does not turn on
        // clicks and typing (roadmap M5.8)
        var selection = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ToolsetsVariable ? toolsets : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        tools.Contains("browser_evaluate").Should().Be(servesEvaluate);
        var servesActions = toolsets.Contains("+browser-actions", StringComparison.Ordinal);
        DomBridgeActionTools.Should().OnlyContain(tool => tools.Contains(tool) == servesActions);
    }

    [Fact]
    public async Task AllToolsets_ServeTheOptInTools()
    {
        var selection = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ToolsetsVariable ? "all" : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        tools.Should().Contain(["activate_tab", "browser_navigate", "list_tabs", "wait_for_browser", "browser_click", "browser_evaluate"])
            .And.HaveCount(ServerToolCatalog.ServedToolNames.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("+browser-actions")]
    [InlineData("+browser-script")]
    [InlineData("+visual")]
    [InlineData("+ocr")]
    [InlineData("all")]
    public async Task ReadOnlyMode_ServesExactlyTheToolsAnnotatedReadOnly_OfTheToolsetsInForce(string? toolsets)
    {
        // Keyed on the [McpServerTool] ReadOnly property, as clients see it (annotations.readOnlyHint). An opt-in toolset's
        // read-only tool (browser_element_rect in browser-actions, roadmap M5.8; wait_for_visual in visual, M6.4) is served only
        // once its toolset is on;
        // browser_evaluate (browser-script) is destructive, so read-only mode never serves it
        var inForce = ToolsetSelection.FromEnvironment(name => name == ToolsetSelection.ToolsetsVariable ? toolsets : null, Toolsets.Server);
        var annotatedReadOnly = (await ToolSurface.ListToolsAsync()).OfType<JsonObject>()
            .Where(tool => (bool?)tool["annotations"]?["readOnlyHint"] == true)
            .Select(tool => (string)tool["name"]!)
            .Where(tool => inForce.Serves(tool, readOnlyHint: true))
            .Order(StringComparer.Ordinal)
            .ToList();
        var readOnly = ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ReadOnlyVariable => "1",
                ToolsetSelection.ToolsetsVariable => toolsets,
                _ => null
            }, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(readOnly));

        var tools = await server.Client.ListToolsAsync();

        tools.Select(t => t.Name).Order(StringComparer.Ordinal).Should().Equal(annotatedReadOnly);
        tools.Select(t => t.Name).Contains("browser_element_rect").Should().Be(toolsets is "all" or "+browser-actions");
        tools.Select(t => t.Name).Contains("wait_for_visual").Should().Be(toolsets is "all" or "+visual");
        tools.Select(t => t.Name).Should().NotContain("browser_evaluate");
    }

    /// <summary>
    /// The tools the input gate runs without the gate (InputGateToolFilter.ReadOnlyTools) that still change something:
    /// they write or delete files. "Read-only" for the gate means "needs no foreground", not "changes nothing".
    /// </summary>
    private static readonly string[] GateFreeToolsThatWrite =
    [
        "save_layout", "delete_layout",                                                    // replace or delete a preset file
        "take_screenshot", "capture_region", "capture_window", "capture_with_elements",    // write (and overwrite) image files
    ];

    [Fact]
    public async Task ToolsAnnotatedReadOnly_AreReadOnlyToolsOfTheInputGate_ThatWriteNothing()
    {
        // Read-only mode must never serve a tool that sends input, changes windows, starts or stops processes, or writes
        // or deletes files: every tool annotated read-only must be one the input gate runs as a read-only tool, and not
        // one of those that write
        var annotatedReadOnly = (await ToolSurface.ListToolsAsync()).OfType<JsonObject>()
            .Where(tool => (bool?)tool["annotations"]?["readOnlyHint"] == true)
            .Select(tool => (string)tool["name"]!)
            .ToList();

        annotatedReadOnly.Should().BeSubsetOf(InputGateToolFilter.ReadOnlyTools);
        annotatedReadOnly.Should().NotContain(GateFreeToolsThatWrite);
    }

    [Fact]
    public void TheGateFreeToolsThatWrite_AreToolsOfTheGatesReadOnlyList()
    {
        // The list above must follow the gate's list: a writing tool that leaves it is checked by the subset rule anyway,
        // and a renamed one must not silently drop out of the check
        InputGateToolFilter.ReadOnlyTools.Should().Contain(GateFreeToolsThatWrite);
    }

    [Fact]
    public async Task ReadOnlyMode_NeverServesAToolThatWrites_EvenIfItsToolsetIsNamed()
    {
        var readOnly = ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => "all",
                ToolsetSelection.ReadOnlyVariable => "1",
                _ => null
            },
            Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(readOnly));

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        tools.Should().NotContain(GateFreeToolsThatWrite).And.NotContain(InputGateToolFilter.InputTools)
            .And.NotContain(["launch_application", "launch_applications_batch", "launch_browser_with_debug", "kill_process",
                "close_window", "close_windows_batch", "apply_layout", "focus_window", "resume_input", "release_all_input"]);
        tools.Should().Contain(["get_server_info", "find_windows", "list_layouts"]);
    }

    [Fact]
    public async Task GetServerInfo_IsServed_EvenWhenExcluded()
    {
        // It is how the agent finds out which tools are off and why, so WAM_EXCLUDE_TOOLS cannot drop it
        var selection = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ExcludeToolsVariable ? "get_server_info,kill_process" : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

        var tools = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToList();

        tools.Should().Contain("get_server_info").And.NotContain("kill_process");
        selection.Warnings.Should().ContainSingle(w => w.Contains("'get_server_info'") && w.Contains("always served"));
    }

    [Fact]
    public async Task AToolsetValueWithNoValidEntry_ServesOnlyTheServerToolset()
    {
        // Fail closed: a typo in a variable meant to restrict the server must not serve every tool
        var selection = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ToolsetsVariable ? "windws,-proces" : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

        var tools = await server.Client.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo(Toolsets.Server.Find(Toolsets.ServerToolset)!.Tools);
    }

    [Fact]
    public async Task TheInstructions_TellTheAgentWhatTheUserTurnedOff()
    {
        // The fixed instructions name focus_window, mouse_click and the other tools of the loop; when the user turned some of
        // them off, a last line says so, so the agent skips those steps instead of looking for the tools
        var selection = ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => "-input,-process",
                ToolsetSelection.ExcludeToolsVariable => "close_window",
                _ => null
            },
            Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder =>
        {
            builder.Services.Configure<McpServerOptions>(ServerIdentity.Configure);
            builder.AddWindowsAppManagerTools(selection);
        });

        var instructions = server.Client.ServerInstructions;

        instructions.Should().StartWith(ServerIdentity.Instructions);
        var note = instructions![ServerIdentity.Instructions.Length..];
        note.Should().StartWith("\n").And.Contain("input").And.Contain("process").And.Contain("close_window")
            .And.Contain("missing from your tool list");
        note[1..].Should().NotContain("\n", "the note is one line");
    }

    [Fact]
    public async Task TheInstructions_AreUnchanged_WhenNothingIsTurnedOff()
    {
        await using var server = await InProcessMcpServer.StartAsync([], builder =>
        {
            builder.Services.Configure<McpServerOptions>(ServerIdentity.Configure);
            builder.AddWindowsAppManagerTools(ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server));
        });

        server.Client.ServerInstructions.Should().Be(ServerIdentity.Instructions);
    }

    [Fact]
    public async Task AnExcludedProductionTool_IsNotListed()
    {
        var selection = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ExcludeToolsVariable ? "kill_process" : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

        var tools = await server.Client.ListToolsAsync();

        // The default without kill_process: every tool but the opt-in toolsets' (browser-actions, browser-script, windows-batch and the others) and the one excluded
        var optIn = Toolsets.Server.Toolsets.Where(t => !t.OnByDefault).Sum(t => t.Tools.Count);
        tools.Select(t => t.Name).Should().NotContain("kill_process")
            .And.HaveCount(ServerToolCatalog.ServedToolNames.Count - optIn - 1);
    }

    [Fact]
    public void TheServer_ReadsTheToolsetsFromItsEnvironment_BeforeTheToolRegistrations()
    {
        var program = Snapshots.ReadNormalized(RepositoryFiles.PathOf("src", "WindowsAppManagerMcp", "Program.cs"))!;

        program.Should().Contain("var toolsets = ToolsetSelection.FromEnvironment();").And.Contain(".AddWindowsAppManagerTools(toolsets)");
        program.IndexOf("ToolsetSelection.FromEnvironment()", StringComparison.Ordinal)
            .Should().BeLessThan(program.IndexOf(".AddWindowsAppManagerTools(toolsets)", StringComparison.Ordinal));
    }

    #endregion
}
