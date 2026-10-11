using System.Text.Json.Nodes;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the tool surface an agent sees (every tool object of tools/list: name, title, description,
/// annotations, inputSchema and outputSchema) matches the checked-in snapshot, Contract/tools.snapshot.json.
/// A change to a tool's name, parameters, defaults, descriptions or annotations, or an SDK upgrade that changes
/// how they are sent, fails here until the snapshot is regenerated with UPDATE_SNAPSHOTS=1 and the diff is
/// reviewed. CI also compares the tools/list of the built exe with the same file (scripts/Test-ToolsSnapshot.ps1),
/// which catches an exe that is not built from these sources. Runs an in-process MCP server; no tool is called.
/// </summary>
public class ToolSurfaceSnapshotTests
{
    private const int MaxReportedPaths = 40;

    [Fact]
    public async Task ToolsList_MatchesTheCheckedInSnapshot()
    {
        // Arrange
        var live = await ToolSurface.ListToolsAsync();
        var expected = ToolSurface.ToSnapshotText(live);

        // Act
        var committed = Snapshots.ReadNormalized(ToolSurface.SnapshotPath);

        // Assert
        if (committed == expected)
        {
            return;
        }

        if (Snapshots.UpdateRequested)
        {
            Snapshots.WritePreservingFormat(ToolSurface.SnapshotPath, expected);
            return;
        }

        var differences = committed == null
            ? [$"{ToolSurface.SnapshotPath} does not exist."]
            : Differences(ToolSurface.ReadSnapshotTools()!, live);
        Assert.Fail(
            "tools/list differs from Contract/tools.snapshot.json. If the change is intended, regenerate the snapshot and the " +
            $"generated docs with {Snapshots.UpdateVariable}=1 (see TESTING.md) and review the diff.\n" +
            string.Join("\n", differences.DefaultIfEmpty("(only the formatting of the file differs)")));
    }

    [Fact]
    public async Task ToolsList_ServesExactlyTheRegisteredTools_EachOnceSortedByName()
    {
        // The snapshot, the generated docs and the per-tool contract tests run over these names
        var names = (await ToolSurface.ListToolsAsync()).Select(tool => (string?)tool!["name"]).ToList();

        names.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        names.Should().BeEquivalentTo(ServerToolCatalog.ServedToolNames);
        names.Should().BeEquivalentTo(ServerToolCatalog.Tools.Keys);
    }

    [Fact]
    public async Task EveryTool_HasADescriptionAndAnObjectInputSchema()
    {
        var tools = await ToolSurface.ListToolsAsync();

        var problems = new List<string>();
        foreach (var tool in tools.OfType<JsonObject>())
        {
            var name = (string?)tool["name"];
            if (string.IsNullOrWhiteSpace((string?)tool["description"]))
            {
                problems.Add($"{name}: no description");
            }

            if ((string?)tool["inputSchema"]?["type"] != "object")
            {
                problems.Add($"{name}: inputSchema is not an object schema");
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void BuildWorkflow_ComparesTheToolsListOfThePublishedAndTheCommittedExe_WithThisSnapshot()
    {
        // The CI guard that catches an exe whose tools differ from its sources (roadmap M2.12)
        var workflow = Snapshots.ReadNormalized(RepositoryFiles.PathOf(".github", "workflows", "build.yml"))!;
        var script = Snapshots.ReadNormalized(RepositoryFiles.PathOf("scripts", "Test-ToolsSnapshot.ps1"))!;

        workflow.Should().Contain("./scripts/Test-ToolsSnapshot.ps1 -ExePath ./publish/WindowsAppManagerMcp.exe\n")
            .And.Contain("./scripts/Test-ToolsSnapshot.ps1 -ExePath ./bin/WindowsAppManagerMcp.exe -OnlyIfCurrent");
        script.Should().Contain($"'{string.Join('/', ToolSurface.SnapshotRelativePath)}'",
            "the script's default snapshot is the file this test keeps current");
    }

    [Fact]
    public void TheGuardScript_ListsTheToolsOnTheNewestInitializeRevision()
    {
        // Claude Code and other stdio clients use the initialize handshake; the script asks for the newest revision of
        // it that the SDK speaks (ProtocolRevisionContractTests pins that revision), not one an upgrade left behind
        var script = Snapshots.ReadNormalized(RepositoryFiles.PathOf("scripts", "Test-ToolsSnapshot.ps1"))!;

        script.Should().Contain($"[string]$ProtocolVersion = '{InProcessMcpServer.NewestInitializeRevision}'")
            .And.Contain("protocolVersion = $ProtocolVersion");
    }

    [Fact]
    public void Differences_NamesAddedRemovedAndChangedTools()
    {
        // The failure message must point at what changed, not just say "differs"
        var committed = new JsonArray(
            new JsonObject { ["name"] = "a_tool", ["description"] = "old" },
            new JsonObject { ["name"] = "gone_tool" },
            new JsonObject
            {
                ["name"] = "same_tool",
                ["inputSchema"] = new JsonObject { ["properties"] = new JsonObject { ["x"] = new JsonObject { ["type"] = "integer" } } }
            });
        var live = new JsonArray(
            new JsonObject { ["name"] = "a_tool", ["description"] = "new" },
            new JsonObject { ["name"] = "new_tool" },
            new JsonObject
            {
                ["name"] = "same_tool",
                ["inputSchema"] = new JsonObject { ["properties"] = new JsonObject { ["x"] = new JsonObject { ["type"] = "string" } } }
            });

        var differences = Differences(committed, live);

        differences.Should().BeEquivalentTo(
            "added tool: new_tool",
            "removed tool: gone_tool",
            "changed: a_tool.description",
            "changed: same_tool.inputSchema.properties.x.type");
    }

    /// <summary>What differs between two tools/list arrays, one line per added, removed or changed tool or JSON path.</summary>
    internal static List<string> Differences(JsonArray committed, JsonArray live)
    {
        var before = ByName(committed);
        var after = ByName(live);
        var lines = new List<string>();

        lines.AddRange(after.Keys.Except(before.Keys).Order(StringComparer.Ordinal).Select(name => $"added tool: {name}"));
        lines.AddRange(before.Keys.Except(after.Keys).Order(StringComparer.Ordinal).Select(name => $"removed tool: {name}"));

        var paths = new List<string>();
        foreach (var name in before.Keys.Intersect(after.Keys).Order(StringComparer.Ordinal))
        {
            ChangedPaths(before[name], after[name], name, paths);
        }

        lines.AddRange(paths.Take(MaxReportedPaths).Select(path => $"changed: {path}"));
        if (paths.Count > MaxReportedPaths)
        {
            lines.Add($"... and {paths.Count - MaxReportedPaths} more changed paths");
        }

        return lines;
    }

    private static Dictionary<string, JsonNode> ByName(JsonArray tools) =>
        tools.OfType<JsonObject>().ToDictionary(tool => (string?)tool["name"] ?? "(no name)", tool => (JsonNode)tool);

    private static void ChangedPaths(JsonNode? before, JsonNode? after, string path, List<string> paths)
    {
        if (before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            var found = paths.Count;
            foreach (var key in beforeObject.Select(p => p.Key).Union(afterObject.Select(p => p.Key)))
            {
                ChangedPaths(beforeObject[key], afterObject[key], $"{path}.{key}", paths);
            }

            // Same members in another order: the order of parameters is part of what clients show
            if (paths.Count == found && !beforeObject.Select(p => p.Key).SequenceEqual(afterObject.Select(p => p.Key)))
            {
                paths.Add($"{path} (member order)");
            }

            return;
        }

        if (!JsonNode.DeepEquals(before, after))
        {
            paths.Add(path);
        }
    }
}
