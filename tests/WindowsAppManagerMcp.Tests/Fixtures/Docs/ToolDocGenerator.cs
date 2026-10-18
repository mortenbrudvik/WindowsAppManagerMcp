using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Prompts;
using WindowsAppManagerMcp.Resources;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>A Markdown file with generated tool tables.</summary>
/// <param name="Name">How messages name the file.</param>
/// <param name="RelativePath">The path below the repository root.</param>
public sealed record GeneratedDoc(string Name, string[] RelativePath)
{
    public string FullPath => RepositoryFiles.PathOf(RelativePath);

    public override string ToString() => Name;
}

/// <summary>
/// Generates the tool tables of README.md, docs/TOOLS.md and the plugin skill (its SKILL.md and tools.md) from the
/// server's tools/list (<see cref="ToolSurface"/>) and <see cref="ToolDocCatalog"/>, into the files' marked regions
/// (<see cref="MarkdownRegions"/>). GeneratedToolDocsTests fails when a region is out of date, and rewrites it
/// with UPDATE_SNAPSHOTS=1.
/// </summary>
/// <remarks>
/// Region ids: README.md has <c>tool-table</c> (heading with the tool count, and the category table); docs/TOOLS.md
/// has <c>summary</c> (introduction with the count, and the quick reference), <c>annotations</c> (the tools each of the
/// four hints marks, from the tools' [McpServerTool] annotations), <c>toolsets</c> (the toolsets of
/// <see cref="Toolsets.Server"/>, roadmap M2.14), <c>read-only</c> (the tools annotated readOnlyHint, which read-only
/// mode serves), <c>params:TOOL</c> in each tool's section and <c>resources</c> (the resources and prompts of
/// <see cref="ResourcesAndPrompts"/>, roadmap M6.9); the skill's SKILL.md has <c>count</c> (the sentence with the tool
/// count) and its tools.md <c>tool-tables</c> (the whole "Available Tools" section). The tables are in tools.md, not in
/// SKILL.md, because a client loads SKILL.md whole whenever the skill triggers, and the tables (about 3,600 tokens) are
/// reference an agent reads only when its task needs them (issue #11).
/// <para>
/// A <c>params:TOOL</c> region is the tool's parameter table, and a field table for each parameter that is an
/// array of objects. Names, order, types, required flags and defaults come from the tool's inputSchema. The
/// Description cells, and a Default cell that explains a null default in words ("primary", "temp file"), are
/// written by hand and kept: a regeneration keeps the cell of a parameter that is still there, drops removed
/// parameters, and fills a new parameter's description from its schema description, ready to be edited.
/// </para>
/// </remarks>
public static partial class ToolDocGenerator
{
    public static readonly GeneratedDoc Readme = new("README.md", ["README.md"]);
    public static readonly GeneratedDoc ToolsReference = new("docs/TOOLS.md", ["docs", "TOOLS.md"]);
    public static readonly GeneratedDoc Skill = new("SKILL.md", ["skills", "using-windows-app-manager", "SKILL.md"]);
    public static readonly GeneratedDoc SkillTools = new(SkillToolsName, ["skills", "using-windows-app-manager", "tools.md"]);

    public static IReadOnlyList<GeneratedDoc> All { get; } = [Readme, ToolsReference, Skill, SkillTools];

    private const string SkillToolsName = "skills/using-windows-app-manager/tools.md";

    private const string ParamsPrefix = "params:";

    [GeneratedRegex(@"^(true|false|null|-?\d+(\.\d+)?|"".*"")$")]
    private static partial Regex LiteralValue();

    [GeneratedRegex(@"`(?<name>[A-Za-z_][A-Za-z0-9_]*)`")]
    private static partial Regex BacktickedName();

    /// <summary>The region id of a tool's parameter tables in docs/TOOLS.md.</summary>
    public static string ParamsRegionId(string toolName) => ParamsPrefix + toolName;

    /// <summary>The regions <paramref name="doc"/> must have, in file order.</summary>
    public static IReadOnlyList<string> RequiredRegionIds(GeneratedDoc doc, JsonArray tools)
    {
        if (doc == Readme)
        {
            return ["tool-table"];
        }

        if (doc == Skill)
        {
            return ["count"];
        }

        if (doc == SkillTools)
        {
            return ["tool-tables"];
        }

        if (doc == ToolsReference)
        {
            return ["summary", "annotations", "toolsets", "read-only", .. ToolDocCatalog.AllTools.Select(t => ParamsRegionId(t.Name)), "resources"];
        }

        throw new ArgumentException($"Unknown generated doc {doc}.", nameof(doc));
    }

    /// <summary><paramref name="text"/> (LF line ends) with every region regenerated.</summary>
    public static string Regenerate(GeneratedDoc doc, string text, JsonArray tools) =>
        MarkdownRegions.Rewrite(text, (id, current) => Generate(doc, id, current, tools));

    /// <summary>The content of one region.</summary>
    /// <exception cref="ArgumentException">The file has no region with that id.</exception>
    public static string Generate(GeneratedDoc doc, string id, string current, JsonArray tools)
    {
        var count = tools.Count;
        return (doc.Name, id) switch
        {
            ("README.md", "tool-table") => ReadmeToolTable(count),
            ("docs/TOOLS.md", "summary") => ToolsSummary(count),
            ("docs/TOOLS.md", "annotations") => AnnotationsTable(tools),
            ("docs/TOOLS.md", "toolsets") => ToolsetsTable(Toolsets.Server, tools),
            ("docs/TOOLS.md", "read-only") => ReadOnlyTools(tools),
            ("docs/TOOLS.md", "resources") => ResourcesAndPromptsTables(),
            ("docs/TOOLS.md", _) when id.StartsWith(ParamsPrefix, StringComparison.Ordinal) =>
                ParameterTables(ToolSurface.Tool(tools, id[ParamsPrefix.Length..]), current),
            ("SKILL.md", "count") =>
                $"Windows App Manager provides {count} MCP tools for managing Windows applications, windows, and desktop layouts.",
            (SkillToolsName, "tool-tables") => SkillToolTables(count),
            _ => throw new ArgumentException($"{doc} has an unknown generated region '{id}'.", nameof(id))
        };
    }

    private static string ReadmeToolTable(int count)
    {
        var lines = new List<string>
        {
            $"## Available Tools ({count})",
            "",
            "| Category | Tools |",
            "|----------|-------|"
        };
        lines.AddRange(ToolDocCatalog.Categories.Select(c =>
            $"| {c.Name} | {string.Join(", ", c.Tools.Select(t => $"`{t.Name}`"))} |"));
        return string.Join('\n', lines);
    }

    private static string ToolsSummary(int count)
    {
        var lines = new List<string>
        {
            $"A comprehensive guide to all {count} MCP tools for managing Windows applications, windows, and layouts.",
            "",
            "## Quick Reference",
            "",
            "| Category | Tools | Count |",
            "|----------|-------|-------|"
        };
        lines.AddRange(ToolDocCatalog.Categories.Select(c =>
            $"| [{c.Name}](#{c.Anchor}) | {string.Join(", ", c.Tools.Select(t => $"[{t.Name}](#{t.Name})"))} | {c.Tools.Count} |"));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The toolsets of <paramref name="catalog"/>: whether each is on by default, what its tools cost in tools/list (roadmap
    /// M6.1, <see cref="ToolSurface.ToolsetTokens"/>, rounded to the nearest 100 so that a small description change leaves the
    /// table alone), what it covers and its tools.
    /// </summary>
    public static string ToolsetsTable(ToolsetCatalog catalog, JsonArray tools)
    {
        var lines = new List<string>
        {
            "| Toolset | Default | Tokens | What it covers | Tools |",
            "|---------|---------|--------|----------------|-------|"
        };
        lines.AddRange(catalog.Toolsets.Select(t =>
            $"| `{t.Name}` | {(t.Name == catalog.AlwaysOn ? "always on" : t.OnByDefault ? "on" : "off")} | " +
            $"{RoundedTokens(ToolSurface.ToolsetTokens(tools, t))} | {Cell(t.Description)} | " +
            $"{string.Join(", ", t.Tools.Select(tool => $"[{tool}](#{tool})"))} |"));
        lines.Add("");
        lines.Add(
            $"The default surface (the toolsets that are on) is {RoundedTokens(SurfaceTokens(tools, catalog.Toolsets.Where(t => t.OnByDefault)))} " +
            $"tokens, and every tool together {RoundedTokens(SurfaceTokens(tools, catalog.Toolsets))}.");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The estimated tokens of a tools/list that serves the tools of <paramref name="toolsets"/>, measured as the budgets
    /// measure a whole list (one JSON array, as the SDK writes it), so the totals beside the Tokens column are generated too.
    /// </summary>
    private static int SurfaceTokens(JsonArray tools, IEnumerable<Toolset> toolsets) =>
        ToolSurface.EstimatedTokens(ToolSurface.WireBytes(new JsonArray(
            toolsets.SelectMany(t => t.Tools).Select(name => ToolSurface.Tool(tools, name).DeepClone()).ToArray())));

    /// <summary>A token estimate for the docs, to the nearest 100: "about 13,700", and at least "about 100".</summary>
    public static string RoundedTokens(int tokens) =>
        "about " + Math.Max(100, (int)Math.Round(tokens / 100.0, MidpointRounding.AwayFromZero) * 100)
            .ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The four hints of tools/list's annotations, in the order the annotations table lists them, each with what it tells a
    /// client. Which tools each hint marks is generated from the annotations (<see cref="AnnotationsTable"/>); why a tool
    /// has a hint is not generated: the hand-written paragraph under the table says it for some of the tools.
    /// </summary>
    public static IReadOnlyList<(string Hint, string Says)> AnnotationHints { get; } =
    [
        ("readOnlyHint", "They change nothing, write no file and run without the input gate (get_clipboard may ask the user, " +
                         "which changes nothing); [read-only mode](#toolsets) serves exactly these"),
        ("destructiveHint", "What they do may not be undone: input that confirms, deletes or sends, a script run in a page, a " +
                            "window, process or browser closed, a file, preset, clipboard or page replaced or deleted, a program started"),
        ("idempotentHint", "A second call with the same arguments ends in the same place"),
        ("openWorldHint", "They reach beyond the windows on the desktop: they start programs, open URIs, folders and web pages, " +
                          "or act on or read what a website wrote"),
    ];

    /// <summary>
    /// The annotations table: for each hint of <see cref="AnnotationHints"/>, what it says and the tools whose
    /// [McpServerTool] sets it, linked, in catalog order and grouped as the read-only list groups them (<see cref="ToolLinks"/>).
    /// A hint that marks every read-only tool names them as "the read-only tools" and links only the others.
    /// </summary>
    public static string AnnotationsTable(JsonArray tools)
    {
        var lines = new List<string>
        {
            "| Hint | What it says | Tools |",
            "|------|--------------|-------|"
        };
        var readOnly = ToolsWith(tools, "readOnlyHint");
        foreach (var (hint, says) in AnnotationHints)
        {
            var marked = ToolsWith(tools, hint);
            var cell = hint != "readOnlyHint" && readOnly.Count > 0 && readOnly.All(marked.Contains)
                ? "The read-only tools, and " + ToolLinks(marked.Except(readOnly).ToList())
                : ToolLinks(marked);
            lines.Add($"| `{hint}: true` | {says} | {cell} |");
        }

        return string.Join('\n', lines);
    }

    /// <summary>The tools whose tools/list annotation <paramref name="hint"/> is true, in catalog order.</summary>
    public static List<string> ToolsWith(JsonArray tools, string hint) =>
        ToolDocCatalog.AllTools
            .Select(t => t.Name)
            .Where(name => ToolSurface.Tool(tools, name)["annotations"]?[hint]?.GetValue<bool>() == true)
            .ToList();

    /// <summary>
    /// Links to <paramref name="names"/>: the tools of the toolsets that are on by default first, then those of an opt-in
    /// toolset with its name, as the read-only list names them.
    /// </summary>
    private static string ToolLinks(IReadOnlyList<string> names)
    {
        var optIn = names.Where(name => Toolsets.Server.ToolsetOf(name) is { OnByDefault: false }).ToList();
        var byDefault = string.Join(", ", names.Except(optIn).Select(name => $"[{name}](#{name})"));
        if (optIn.Count == 0)
        {
            return byDefault;
        }

        var opt = string.Join(", ", optIn.Select(name => $"[{name}](#{name}) (`{Toolsets.Server.ToolsetOf(name)!.Name}`)"));
        return byDefault.Length == 0 ? $"in toolsets that are off by default: {opt}" : $"{byDefault}; in toolsets that are off by default: {opt}";
    }

    /// <summary>
    /// The tools annotated readOnlyHint, which read-only mode serves, in catalog order: the Observer profile is exactly
    /// this list, so it cannot drift from the annotations. Those of a toolset that is off by default are named apart: read-only
    /// mode alone does not serve them.
    /// </summary>
    private static string ReadOnlyTools(JsonArray tools)
    {
        var readOnly = ToolsWith(tools, "readOnlyHint");
        var optIn = readOnly.Where(name => Toolsets.Server.ToolsetOf(name) is { OnByDefault: false }).ToList();
        var byDefault = readOnly.Except(optIn).ToList();
        var sentence = $"Read-only mode serves these {byDefault.Count} tools: {string.Join(", ", byDefault.Select(name => $"[{name}](#{name})"))}.";
        return optIn.Count == 0
            ? sentence
            : sentence + " With their opt-in toolset turned on it also serves " +
              string.Join(", ", optIn.Select(name => $"[{name}](#{name}) (`{Toolsets.Server.ToolsetOf(name)!.Name}`)")) + ".";
    }

    /// <summary>
    /// The resources and prompts (roadmap M6.9), in catalog order, from their attributes: each resource's URI, the tool whose
    /// JSON it returns and its description; each prompt's name, arguments, the tools it needs and its description.
    /// </summary>
    private static string ResourcesAndPromptsTables()
    {
        var resources = typeof(DesktopResources).GetMethods()
            .Select(m => (Attribute: m.GetCustomAttribute<McpServerResourceAttribute>(), Description: m.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Where(r => r.Attribute != null)
            .ToDictionary(r => r.Attribute!.UriTemplate!, r => r.Description ?? string.Empty);
        var prompts = typeof(WorkflowPrompts).GetMethods()
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerPromptAttribute>()))
            .Where(p => p.Attribute != null)
            .ToDictionary(p => p.Attribute!.Name!, p => p.Method);

        var lines = new List<string>
        {
            "| Resource | The JSON of | What it holds |",
            "|----------|-------------|---------------|"
        };
        lines.AddRange(ResourcesAndPrompts.Resources.Select(r =>
            $"| `{r.UriTemplate}` | [{r.Mirrors}](#{r.Mirrors}) | {Cell(resources[r.UriTemplate])} |"));
        lines.Add("");
        lines.Add("| Prompt | Arguments | Served while these tools are | What it does |");
        lines.Add("|--------|-----------|------------------------------|--------------|");
        lines.AddRange(ResourcesAndPrompts.Prompts.Select(p =>
        {
            var method = prompts[p.Name];
            var arguments = method.GetParameters().Where(a => a.ParameterType == typeof(string)).Select(a => $"`{a.Name}`");
            return $"| `{p.Name}` | {string.Join(", ", arguments)} | {string.Join(", ", p.Needs.Select(t => $"[{t}](#{t})"))} | " +
                   $"{Cell(method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty)} |";
        }));
        return string.Join('\n', lines);
    }

    private static string SkillToolTables(int count)
    {
        var lines = new List<string> { $"## Available Tools ({count})" };
        foreach (var category in ToolDocCatalog.Categories)
        {
            lines.Add("");
            lines.Add($"### {category.Name} ({category.Tools.Count} {(category.Tools.Count == 1 ? "tool" : "tools")})");
            lines.Add("");
            lines.Add("| Tool | Description | Key Parameters |");
            lines.Add("|------|-------------|----------------|");
            lines.AddRange(category.Tools.Select(t =>
                $"| `{t.Name}` | {t.Summary} | {(t.KeyParameters.Length == 0 ? "(none)" : string.Join(", ", t.KeyParameters))} |"));
            if (category.SkillNote != null)
            {
                lines.Add("");
                lines.Add(category.SkillNote);
            }
        }

        return string.Join('\n', lines);
    }

    #region Parameter tables

    /// <summary>A hand-written cell of a parameter table, kept when the table is regenerated.</summary>
    private sealed record WrittenCells(string? Default, string Description);

    /// <summary>The parameter table of <paramref name="tool"/>, keeping the hand-written cells of <paramref name="current"/>.</summary>
    public static string ParameterTables(JsonObject tool, string current)
    {
        var schema = tool["inputSchema"] as JsonObject;
        var properties = schema?["properties"] as JsonObject;
        if (properties == null || properties.Count == 0)
        {
            return "**Parameters:** None";
        }

        var objectArrays = properties
            .Where(p => ObjectItems(p.Value) != null)
            .Select(p => p.Key)
            .ToList();
        var written = ParseWrittenCells(current, objectArrays);
        var required = RequiredNames(schema);

        var lines = new List<string>
        {
            "**Parameters:**",
            "| Parameter | Type | Required | Default | Description |",
            "|-----------|------|----------|---------|-------------|"
        };
        lines.AddRange(properties.Select(p => Row(p.Key, p.Value, required.Contains(p.Key), written.GetValueOrDefault(p.Key))));

        foreach (var name in objectArrays)
        {
            var items = ObjectItems(properties[name])!;
            var itemRequired = RequiredNames(items);
            lines.Add("");
            lines.Add($"Each item of `{name}` contains:");
            lines.Add("| Field | Type | Required | Default | Description |");
            lines.Add("|-------|------|----------|---------|-------------|");
            lines.AddRange(((JsonObject)items["properties"]!).Select(f =>
                Row(f.Key, f.Value, itemRequired.Contains(f.Key), written.GetValueOrDefault($"{name}[].{f.Key}"))));
        }

        return string.Join('\n', lines);
    }

    private static string Row(string name, JsonNode? property, bool isRequired, WrittenCells? written)
    {
        var description = string.IsNullOrWhiteSpace(written?.Description)
            ? Cell((string?)property?["description"] ?? "-")
            : written.Description;
        return $"| {name} | {TypeName(property)} | {(isRequired ? "Yes" : "No")} | {DefaultCell(property, isRequired, written?.Default)} | {description} |";
    }

    /// <summary>The JSON type of a schema: integer, string, boolean, number, object, or T[] for an array.</summary>
    public static string TypeName(JsonNode? property)
    {
        // A string or an array of strings (press_keys' keys)
        if (property?["type"] is JsonArray { Count: 2 } either && either.Select(t => (string?)t).SequenceEqual(["string", "array"]))
        {
            return $"string or {TypeName(property["items"])}[]";
        }

        // An index or a name (a `monitor`, roadmap M6.7)
        if (property?["type"] is JsonArray types && types.Select(t => (string?)t).Where(t => t != "null").SequenceEqual(["integer", "string"]))
        {
            return "integer or string";
        }

        var type = PrimaryType(property) ?? "any";

        if (type == "array")
        {
            return $"{TypeName(property?["items"])}[]";
        }

        if (property?["enum"] is JsonArray values)
        {
            return $"{type} (one of: {string.Join(", ", values.Where(v => v != null).Select(v => $"`{Literal(v!)}`"))})";
        }

        return type;
    }

    private static string DefaultCell(JsonNode? property, bool isRequired, string? written)
    {
        if (isRequired)
        {
            return "-";
        }

        if (property is JsonObject schema && schema.TryGetPropertyValue("default", out var value) && value != null)
        {
            return Literal(value);
        }

        // No default value: keep words that say what happens instead ("primary", "temp file"), never a literal
        return written is { Length: > 0 } words && words != "-" && !LiteralValue().IsMatch(words) ? words : "-";
    }

    private static string Literal(JsonNode value) => value.GetValueKind() switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => $"\"{(string?)value}\"",
        _ => value.ToJsonString()
    };

    /// <summary>
    /// The JSON type of a schema, without "null": the SDK describes a nullable parameter (string?, string[]?) as the
    /// union ["string", "null"], which the tables show as its non-null type. Null when the schema names no type.
    /// </summary>
    private static string? PrimaryType(JsonNode? property) => property?["type"] switch
    {
        JsonValue value => (string?)value,
        JsonArray union => union.Select(t => (string?)t).FirstOrDefault(t => t != "null"),
        _ => null
    };

    private static JsonObject? ObjectItems(JsonNode? property) =>
        property?["items"] is JsonObject items && PrimaryType(items) == "object" && items["properties"] is JsonObject
            ? items
            : null;

    private static HashSet<string> RequiredNames(JsonObject? schema) =>
        (schema?["required"] as JsonArray)?.Select(n => (string?)n).OfType<string>().ToHashSet(StringComparer.Ordinal)
        ?? new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Text from the schema as a table cell: one line, pipes escaped.</summary>
    private static string Cell(string text) =>
        text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>
    /// The hand-written Default and Description cells of the tables in a region, by parameter name, and by
    /// "param[].field" for a field table. A field table belongs to the parameter its introduction names in
    /// backticks; an introduction without one ("Each placement object contains:", the hand-written format) takes
    /// the next array-of-objects parameter in schema order.
    /// </summary>
    private static Dictionary<string, WrittenCells> ParseWrittenCells(string current, IReadOnlyList<string> objectArrays)
    {
        var cells = new Dictionary<string, WrittenCells>(StringComparer.Ordinal);
        var lines = current.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var nextObjectArray = 0;
        string? prefix = null;
        var columns = Array.Empty<string>();
        var lastText = string.Empty;

        foreach (var line in lines)
        {
            if (!line.StartsWith('|'))
            {
                prefix = null;
                if (line.Trim().Length > 0)
                {
                    lastText = line;
                }

                continue;
            }

            var row = SplitRow(line);
            if (prefix == null)
            {
                // Header row of a new table
                columns = row;
                if (row.FirstOrDefault() == "Parameter")
                {
                    prefix = string.Empty;
                }
                else if (row.FirstOrDefault() == "Field")
                {
                    var named = BacktickedName().Match(lastText);
                    var owner = named.Success
                        ? named.Groups["name"].Value
                        : nextObjectArray < objectArrays.Count ? objectArrays[nextObjectArray] : "(unknown)";
                    nextObjectArray++;
                    prefix = owner + "[].";
                }
                else
                {
                    prefix = "(other table)";
                }

                continue;
            }

            if (row.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':')) || prefix == "(other table)")
            {
                continue; // separator row, or a table this generator does not own
            }

            var name = row[0].Trim('`');
            var defaultColumn = Array.IndexOf(columns, "Default");
            var descriptionColumn = Array.IndexOf(columns, "Description");
            cells[prefix + name] = new WrittenCells(
                defaultColumn >= 0 && defaultColumn < row.Length ? row[defaultColumn].Trim('`') : null,
                descriptionColumn >= 0 && descriptionColumn < row.Length ? row[descriptionColumn] : string.Empty);
        }

        return cells;
    }

    /// <summary>The cells of a Markdown table row; an escaped pipe (\|) stays in its cell.</summary>
    internal static string[] SplitRow(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var text = line.Trim();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                cell.Append("\\|");
                i++;
            }
            else if (text[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(text[i]);
            }
        }

        cells.Add(cell.ToString().Trim());

        // "| a | b |" splits into "", "a", "b", ""
        return cells.Skip(1).Take(Math.Max(0, cells.Count - 2)).ToArray();
    }

    #endregion
}
