using System.Text.Json.Nodes;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the tool tables of README.md, docs/TOOLS.md and the plugin skill (SKILL.md) are generated from the
/// server's tools/list and <see cref="ToolDocCatalog"/>, into marked regions (<see cref="ToolDocGenerator"/>), and
/// are up to date. Agents copy these tables, so drift makes them call tools that do not exist or with wrong
/// arguments. A stale region fails here; the tests rewrite it with UPDATE_SNAPSHOTS=1 (see TESTING.md).
/// Generated: the tool counts, every tool listed once per table under its category, and the parameter tables'
/// names, order, types, required flags and defaults. Checked: SKILL.md's key parameters are real parameter names,
/// and docs/TOOLS.md has one section per tool, under its category, in catalog order.
/// These tests read files in the repository and list the tools of an in-process server; nothing is called.
/// </summary>
public class GeneratedToolDocsTests
{
    public static TheoryData<string> Docs()
    {
        var data = new TheoryData<string>();
        foreach (var doc in ToolDocGenerator.All)
        {
            data.Add(doc.Name);
        }

        return data;
    }

    [Fact]
    public void Catalog_ListsExactlyTheToolsTheServerServes()
    {
        // The reflection catalog (ServerToolCatalog) backs the description and serialization tests, so it must be
        // exactly what the production registration serves: not empty, no unregistered class, no class without
        // [McpServerToolType].
        ServerToolCatalog.ServedToolNames.Should().OnlyHaveUniqueItems().And.Contain("launch_browser_with_debug");
        ServerToolCatalog.Tools.Keys.Should().BeEquivalentTo(ServerToolCatalog.ServedToolNames,
            "tools/list serves the registered tools, and the tests check the catalog");
    }

    [Fact]
    public async Task DocCatalog_ListsEveryServedToolOnce()
    {
        var served = (await ToolSurface.ListToolsAsync()).Select(t => (string?)t!["name"]).ToList();
        var documented = ToolDocCatalog.AllTools.Select(t => t.Name).ToList();

        documented.Should().OnlyHaveUniqueItems("each tool belongs to one category");
        documented.Should().BeEquivalentTo(served,
            "every served tool needs a category, a summary and its key parameters in ToolDocCatalog");
    }

    [Fact]
    public void DocCatalog_CategoriesHaveUniqueNamesAndTools()
    {
        ToolDocCatalog.Categories.Select(c => c.Name).Should().OnlyHaveUniqueItems();
        ToolDocCatalog.Categories.Should().OnlyContain(c => c.Tools.Count > 0);
        ToolDocCatalog.AllTools.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Summary) && !t.Summary.Contains('|'));
    }

    [Fact]
    public async Task DocCatalog_KeyParameters_AreRealParameterNames()
    {
        // SKILL.md once listed 'path', 'pid' and 'maximize', which no tool takes
        var tools = await ToolSurface.ListToolsAsync();

        var problems = new List<string>();
        foreach (var doc in ToolDocCatalog.AllTools)
        {
            var properties = ToolSurface.Tool(tools, doc.Name)["inputSchema"]?["properties"] as JsonObject;
            foreach (var name in doc.KeyParameters.Select(ToolDocCatalog.ParameterName))
            {
                if (properties == null || !properties.ContainsKey(name))
                {
                    problems.Add($"{doc.Name}: '{name}' is not a parameter");
                }
            }

            if (doc.KeyParameters.Select(ToolDocCatalog.ParameterName).Distinct().Count() != doc.KeyParameters.Length)
            {
                problems.Add($"{doc.Name}: a key parameter is listed twice");
            }
        }

        problems.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Docs))]
    public async Task Doc_HasExactlyTheGeneratedRegions(string docName)
    {
        var doc = ToolDocGenerator.All.Single(d => d.Name == docName);
        var tools = await ToolSurface.ListToolsAsync();
        var text = Snapshots.ReadNormalized(doc.FullPath)!;

        var found = MarkdownRegions.Find(text).Select(r => r.Id).ToList();
        var required = ToolDocGenerator.RequiredRegionIds(doc, tools);

        required.Except(found).Should().BeEmpty(
            "{0} needs a <!-- tools:begin ID --> / <!-- tools:end ID --> pair for each generated table (an empty pair is " +
            "filled by a run with UPDATE_SNAPSHOTS=1); for a new tool, add its '### name' section to docs/TOOLS.md with a " +
            "params:name pair", doc);
        found.Except(required).Should().BeEmpty("{0} has generated regions nothing generates (a removed tool?)", doc);
        found.Should().Equal(required, "the regions of {0} are in catalog order", doc);
    }

    [Theory]
    [MemberData(nameof(Docs))]
    public async Task Doc_GeneratedRegions_AreUpToDate(string docName)
    {
        // Arrange
        var doc = ToolDocGenerator.All.Single(d => d.Name == docName);
        var tools = await ToolSurface.ListToolsAsync();
        var text = Snapshots.ReadNormalized(doc.FullPath)!;

        // Act
        var regenerated = ToolDocGenerator.Regenerate(doc, text, tools);

        // Assert
        if (regenerated == text)
        {
            return;
        }

        if (Snapshots.UpdateRequested)
        {
            Snapshots.WritePreservingFormat(doc.FullPath, regenerated);
            return;
        }

        var stale = MarkdownRegions.Find(text)
            .Where(region => MarkdownRegions.Content(text, region) != ToolDocGenerator.Generate(doc, region.Id, MarkdownRegions.Content(text, region), tools))
            .Select(region => $"{region.Id} (line {region.BeginLine + 1})");
        Assert.Fail(
            $"Generated tool tables in {doc} are out of date: {string.Join(", ", stale)}. They are generated from tools/list " +
            $"and ToolDocCatalog: edit those (or the hand-written Description cells), then run the tests with " +
            $"{Snapshots.UpdateVariable}=1 (see TESTING.md).");
    }

    [Fact]
    public void ToolsDoc_HasOneSectionPerTool_UnderItsCategory_InCatalogOrder()
    {
        // The '### tool' sections hold the examples and the params:tool region; a tool documented twice drifts apart
        var lines = Snapshots.ReadNormalized(ToolDocGenerator.ToolsReference.FullPath)!.Split('\n');
        var toolNames = ToolDocCatalog.AllTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var categoryOf = ToolDocCatalog.Categories
            .SelectMany(c => c.Tools.Select(t => (t.Name, Category: c.Name)))
            .ToDictionary(x => x.Name, x => x.Category);

        var sections = new List<string>();
        var problems = new List<string>();
        string? category = null;
        string? section = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                category = line[3..].Trim();
                section = null;
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                section = line[4..].Trim();
                if (!toolNames.Contains(section))
                {
                    continue;
                }

                sections.Add(section);
                if (category != categoryOf[section])
                {
                    problems.Add($"'### {section}' is under '## {category}', not '## {categoryOf[section]}'");
                }
            }
            else if (line.StartsWith("<!-- tools:begin params:", StringComparison.Ordinal))
            {
                var tool = line["<!-- tools:begin params:".Length..].Split(' ')[0];
                if (tool != section)
                {
                    problems.Add($"line {i + 1}: the params:{tool} region is in the section of '{section}'");
                }
            }
        }

        problems.Should().BeEmpty();
        sections.Should().OnlyHaveUniqueItems("a tool documented twice drifts apart");
        sections.Should().Equal(ToolDocCatalog.AllTools.Select(t => t.Name), "docs/TOOLS.md documents the tools in catalog order");
    }
}
