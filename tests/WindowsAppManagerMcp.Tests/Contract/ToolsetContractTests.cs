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

    [Fact]
    public void TheDefault_ServesEveryToolsetToday()
    {
        // The toolsets M3-M6 add for OCR, visual diffs, browser actions, layouts v2 and workspaces are opt-in; until
        // then the default is the whole surface. Turning a toolset off by default changes what every user sees: change
        // this test, the docs and the release notes with it.
        var selection = ToolsetSelection.FromEnvironment(_ => null, Toolsets.Server);

        selection.Disabled.Should().BeEmpty();
        selection.Source.Should().Be(ToolsetSelection.DefaultKeyword);
        ServerToolCatalog.ServedToolNames.Should().OnlyContain(tool => selection.Serves(tool, false));
    }

    [Fact]
    public async Task ReadOnlyMode_ServesExactlyTheToolsAnnotatedReadOnly()
    {
        // Keyed on the [McpServerTool] ReadOnly property, as clients see it (annotations.readOnlyHint)
        var annotatedReadOnly = (await ToolSurface.ListToolsAsync()).OfType<JsonObject>()
            .Where(tool => (bool?)tool["annotations"]?["readOnlyHint"] == true)
            .Select(tool => (string)tool["name"]!)
            .Order(StringComparer.Ordinal)
            .ToList();
        var readOnly = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ReadOnlyVariable ? "1" : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(readOnly));

        var tools = await server.Client.ListToolsAsync();

        tools.Select(t => t.Name).Order(StringComparer.Ordinal).Should().Equal(annotatedReadOnly);
    }

    [Fact]
    public async Task ToolsAnnotatedReadOnly_AreReadOnlyToolsOfTheInputGate()
    {
        // Read-only mode must never serve a tool that sends input, changes windows or starts or stops processes: every
        // tool annotated read-only must be one the input gate runs as a read-only tool
        var annotatedReadOnly = (await ToolSurface.ListToolsAsync()).OfType<JsonObject>()
            .Where(tool => (bool?)tool["annotations"]?["readOnlyHint"] == true)
            .Select(tool => (string)tool["name"]!);

        annotatedReadOnly.Should().BeSubsetOf(InputGateToolFilter.ReadOnlyTools);
    }

    [Fact]
    public async Task AnExcludedProductionTool_IsNotListed()
    {
        var selection = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ExcludeToolsVariable ? "kill_process" : null, Toolsets.Server);
        await using var server = await InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

        var tools = await server.Client.ListToolsAsync();

        tools.Select(t => t.Name).Should().NotContain("kill_process")
            .And.HaveCount(ServerToolCatalog.ServedToolNames.Count - 1);
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
