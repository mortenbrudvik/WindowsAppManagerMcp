using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: docs/TOOLS.md, README.md and the plugin skill (SKILL.md) list exactly the
/// [McpServerTool] methods the server exposes, with their real parameter names, and TOOLS.md
/// gives each parameter's type, whether it is required and its default. Agents copy these
/// tables, so drift makes them call tools with wrong arguments.
/// These tests only read files in the repository and reflect over the server assembly.
/// </summary>
public class ToolDocumentationTests
{
    private static IReadOnlyDictionary<string, MethodInfo> Tools => ServerToolCatalog.Tools;

    public static TheoryData<string> ToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Tools.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Catalog_FindsTheServerTools()
    {
        // Guards against the checks below silently running over an empty set.
        Tools.Should().ContainKey("launch_browser_with_debug");
        Tools.Count.Should().BeGreaterThan(30);
    }

    #region docs/TOOLS.md

    [Fact]
    public void ToolsDoc_StatesTheToolCount()
    {
        var text = File.ReadAllText(RepositoryFiles.PathOf("docs", "TOOLS.md"));

        var match = Regex.Match(text, @"all (\d+) MCP tools");

        match.Success.Should().BeTrue("the TOOLS.md introduction states the number of tools");
        int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture).Should().Be(Tools.Count);
    }

    [Fact]
    public void ToolsDoc_QuickReference_ListsEveryToolOnceWithCorrectCounts()
    {
        var lines = ReadLines("docs", "TOOLS.md");
        var rows = TableRowsAfter(lines, Array.IndexOf(lines, "## Quick Reference"))
            .Skip(2) // header and separator
            .ToList();

        var listed = new List<string>();
        var problems = new List<string>();
        foreach (var cells in rows)
        {
            var names = cells[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            listed.AddRange(names);
            if (!int.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != names.Length)
            {
                problems.Add($"row '{cells[0]}' says {cells[2]} but lists {names.Length} tools");
            }
        }

        problems.Should().BeEmpty();
        listed.Should().OnlyHaveUniqueItems("each tool belongs to one category");
        listed.Should().BeEquivalentTo(Tools.Keys);
    }

    [Fact]
    public void ToolsDoc_HasExactlyOneSectionPerTool()
    {
        var headings = ReadLines("docs", "TOOLS.md")
            .Where(line => line.StartsWith("### ", StringComparison.Ordinal))
            .Select(line => line[4..].Trim())
            .Where(Tools.ContainsKey)
            .ToList();

        headings.Should().OnlyHaveUniqueItems("a tool documented twice drifts apart");
        headings.Should().BeEquivalentTo(Tools.Keys);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void ToolsDoc_ParameterTable_MatchesToolSignature(string toolName)
    {
        // Arrange
        var expected = ServerToolCatalog.ClientParameters(Tools[toolName]).ToList();
        var documented = DocumentedParameters(ReadLines("docs", "TOOLS.md"), toolName);

        // Assert - same names in the same order as the signature
        documented.Select(p => p.Name).Should().Equal(expected.Select(p => p.Name),
            "the '{0}' parameter table in docs/TOOLS.md must list the tool's parameters in signature order", toolName);

        var problems = new List<string>();
        foreach (var (doc, param) in documented.Zip(expected))
        {
            var expectedType = DocType(param.ParameterType);
            if (doc.Type != expectedType)
            {
                problems.Add($"{param.Name}: type '{doc.Type}', expected '{expectedType}'");
            }

            var expectedRequired = param.HasDefaultValue ? "No" : "Yes";
            if (doc.Required != expectedRequired)
            {
                problems.Add($"{param.Name}: required '{doc.Required}', expected '{expectedRequired}'");
            }

            if (DefaultProblem(param, doc.Default) is { } defaultProblem)
            {
                problems.Add($"{param.Name}: {defaultProblem}");
            }
        }

        problems.Should().BeEmpty("docs/TOOLS.md must describe '{0}' as the server defines it", toolName);
    }

    #endregion

    #region README.md

    [Fact]
    public void Readme_ToolTable_ListsEveryToolOnceAndStatesTheCount()
    {
        var lines = ReadLines("README.md");

        var header = Array.FindIndex(lines, line => line.StartsWith("## Available Tools", StringComparison.Ordinal));
        header.Should().BeGreaterThan(-1);
        lines[header].Should().Be($"## Available Tools ({Tools.Count})");

        var listed = TableRowsAfter(lines, header)
            .Skip(2)
            .SelectMany(cells => BacktickedNames(cells[1]))
            .ToList();

        listed.Should().OnlyHaveUniqueItems();
        listed.Should().BeEquivalentTo(Tools.Keys);
    }

    #endregion

    #region SKILL.md

    [Fact]
    public void Skill_StatesTheToolCount()
    {
        var text = File.ReadAllText(SkillPath());
        var count = Tools.Count.ToString(CultureInfo.InvariantCulture);

        Regex.Match(text, @"provides (\d+) MCP tools").Groups[1].Value.Should().Be(count);
        Regex.Match(text, @"## Available Tools \((\d+)\)").Groups[1].Value.Should().Be(count);
    }

    [Fact]
    public void Skill_ToolTables_ListEveryToolOnceWithRealParameterNames()
    {
        var lines = File.ReadAllLines(SkillPath());
        var start = Array.FindIndex(lines, line => line.StartsWith("## Available Tools", StringComparison.Ordinal));
        start.Should().BeGreaterThan(-1);
        var end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
        var section = lines[start..(end < 0 ? lines.Length : end)];

        var listed = new List<string>();
        var problems = new List<string>();
        for (var i = 0; i < section.Length; i++)
        {
            var heading = Regex.Match(section[i], @"^### .+ \((\d+) tools?\)$");
            if (!heading.Success)
            {
                continue;
            }

            var rows = TableRowsAfter(section, i).Skip(2).ToList();
            if (rows.Count != int.Parse(heading.Groups[1].Value, CultureInfo.InvariantCulture))
            {
                problems.Add($"'{section[i]}' has {rows.Count} rows");
            }

            foreach (var cells in rows)
            {
                var tool = BacktickedNames(cells[0]).Single();
                listed.Add(tool);
                if (!Tools.TryGetValue(tool, out var method))
                {
                    problems.Add($"unknown tool '{tool}'");
                    continue;
                }

                var parameterNames = ServerToolCatalog.ClientParameters(method)
                    .Select(p => p.Name!)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var name in KeyParameterNames(cells[2]).Where(n => !parameterNames.Contains(n)))
                {
                    problems.Add($"{tool}: '{name}' is not a parameter");
                }
            }
        }

        problems.Should().BeEmpty();
        listed.Should().OnlyHaveUniqueItems();
        listed.Should().BeEquivalentTo(Tools.Keys);
    }

    #endregion

    #region Helpers

    private sealed record DocumentedParameter(string Name, string Type, string Required, string Default);

    private static string DocType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(int)) return "int";
        if (underlying == typeof(long)) return "long";
        if (underlying == typeof(bool)) return "bool";
        if (underlying == typeof(string)) return "string";
        if (underlying == typeof(string[])) return "string[]";
        return underlying.IsArray ? "array" : underlying.Name;
    }

    private static string? DefaultProblem(ParameterInfo parameter, string documented)
    {
        if (!parameter.HasDefaultValue)
        {
            return documented == "-" ? null : $"required, so the default should be '-', not '{documented}'";
        }

        var expected = parameter.DefaultValue switch
        {
            null => null,
            bool b => b ? "true" : "false",
            string s => $"\"{s}\"",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString(),
        };

        if (expected == null)
        {
            // No default value: the docs say what happens instead ("primary", "temp file", "-", ...),
            // which must not look like a literal value.
            return Regex.IsMatch(documented, @"^(true|false|-?\d+|"".*"")$")
                ? $"default is null in code, but the docs give the literal {documented}"
                : null;
        }

        return documented == expected ? null : $"default '{documented}', expected {expected}";
    }

    private static List<DocumentedParameter> DocumentedParameters(string[] lines, string toolName)
    {
        var heading = Array.IndexOf(lines, $"### {toolName}");
        heading.Should().BeGreaterThan(-1, "docs/TOOLS.md needs a '### {0}' section", toolName);

        for (var i = heading + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith("**Parameters:**", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("None", StringComparison.Ordinal))
            {
                return [];
            }

            var rows = TableRowsAfter(lines, i).ToList();
            rows[0].Should().Equal(new[] { "Parameter", "Type", "Required", "Default", "Description" });
            return rows.Skip(2)
                .Select(cells => new DocumentedParameter(
                    cells[0].Trim('`'),
                    cells[1].Trim('`'),
                    cells[2],
                    cells[3].Trim('`')))
                .ToList();
        }

        throw new InvalidOperationException($"The '### {toolName}' section in docs/TOOLS.md has no **Parameters:** line.");
    }

    /// <summary>The cells of the first Markdown table after line <paramref name="anchor"/>.</summary>
    private static IEnumerable<string[]> TableRowsAfter(string[] lines, int anchor)
    {
        anchor.Should().BeGreaterThan(-1);
        var i = anchor + 1;
        while (i < lines.Length && !lines[i].StartsWith('|'))
        {
            i++;
        }

        for (; i < lines.Length && lines[i].StartsWith('|'); i++)
        {
            yield return lines[i].Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
        }
    }

    private static IEnumerable<string> BacktickedNames(string cell) =>
        Regex.Matches(cell, @"`([a-z_]+)`").Select(m => m.Groups[1].Value);

    private static IEnumerable<string> KeyParameterNames(string cell)
    {
        // "keys (e.g., `^c`, `{ENTER}`)" -> "keys"; "(none)" -> nothing
        var withoutNotes = Regex.Replace(cell, @"\([^)]*\)", string.Empty);
        return withoutNotes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Trim('`'));
    }

    private static string[] ReadLines(params string[] relativeParts) =>
        File.ReadAllLines(RepositoryFiles.PathOf(relativeParts));

    private static string SkillPath() => RepositoryFiles.PathOf("skills", "using-windows-app-manager", "SKILL.md");

    #endregion
}
