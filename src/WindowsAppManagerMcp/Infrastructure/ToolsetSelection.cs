namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// The tools a server serves, as the user chose them in the server's environment (roadmap M2.14):
/// <list type="bullet">
/// <item><see cref="ToolsetsVariable"/>: the toolsets, separated by commas, semicolons or spaces. <c>default</c> stands
/// for the toolsets that are on by default and <c>all</c> for every toolset. <c>+name</c> adds a toolset and <c>-name</c>
/// removes one; with only such entries they change the default (<c>+ocr</c>, <c>-browser</c>). Not set: the default
/// toolsets. The always-on toolset is always served.</item>
/// <item><see cref="ExcludeToolsVariable"/>: tools to leave out, whatever their toolset.</item>
/// <item><see cref="ReadOnlyVariable"/>: <c>1</c> serves only the tools whose [McpServerTool] sets ReadOnly = true (their
/// tools/list annotation readOnlyHint).</item>
/// </list>
/// A name that is not a toolset or tool is ignored with a warning (logged at start, reported by get_server_info); when
/// <see cref="ToolsetsVariable"/> names no toolset at all, the default toolsets stay on, so a typo does not leave the
/// agent with nothing.
/// </summary>
public sealed class ToolsetSelection
{
    public const string ToolsetsVariable = "WAM_TOOLSETS";
    public const string ExcludeToolsVariable = "WAM_EXCLUDE_TOOLS";
    public const string ReadOnlyVariable = "WAM_READ_ONLY";

    /// <summary>The <see cref="ToolsetsVariable"/> entry for the toolsets that are on by default.</summary>
    public const string DefaultKeyword = "default";

    /// <summary>The <see cref="ToolsetsVariable"/> entry for every toolset.</summary>
    public const string AllKeyword = "all";

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];
    private static readonly string[] OnValues = ["1", "true", "yes", "on"];
    private static readonly string[] OffValues = ["0", "false", "no", "off"];

    private readonly HashSet<string> _enabled;
    private readonly HashSet<string> _excluded;

    private ToolsetSelection(
        ToolsetCatalog catalog,
        IEnumerable<string> enabled,
        string source,
        IReadOnlyList<string> excludedTools,
        bool readOnly,
        IReadOnlyList<string> warnings)
    {
        Catalog = catalog;
        _enabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase) { catalog.AlwaysOn };
        Enabled = catalog.Toolsets.Select(t => t.Name).Where(_enabled.Contains).ToList();
        Disabled = catalog.Toolsets.Select(t => t.Name).Where(name => !_enabled.Contains(name)).ToList();
        Source = source;
        ExcludedTools = excludedTools;
        _excluded = new HashSet<string>(excludedTools, StringComparer.OrdinalIgnoreCase);
        ReadOnly = readOnly;
        Warnings = warnings;
    }

    /// <summary>The toolsets the selection was made from.</summary>
    public ToolsetCatalog Catalog { get; }

    /// <summary>The toolsets that are served, in catalog order.</summary>
    public IReadOnlyList<string> Enabled { get; }

    /// <summary>The toolsets that are not served, in catalog order.</summary>
    public IReadOnlyList<string> Disabled { get; }

    /// <summary>"default" (<see cref="ToolsetsVariable"/> not set), <see cref="ToolsetsVariable"/>, or "all" (<see cref="All"/>).</summary>
    public string Source { get; }

    /// <summary>The tools <see cref="ExcludeToolsVariable"/> leaves out, as the catalog names them.</summary>
    public IReadOnlyList<string> ExcludedTools { get; }

    /// <summary>True when only the tools annotated readOnlyHint are served (<see cref="ReadOnlyVariable"/>).</summary>
    public bool ReadOnly { get; }

    /// <summary>The entries that were ignored or read in a way the user may not expect, one sentence each.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Every toolset, nothing excluded, not read-only: the whole tool surface. The registration uses it when it is given
    /// no selection, so the tests, the tools/list snapshot and the CI guard (WAM_TOOLSETS=all) see every tool.
    /// </summary>
    public static ToolsetSelection All(ToolsetCatalog? catalog = null)
    {
        var toolsets = catalog ?? Toolsets.Server;
        return new ToolsetSelection(toolsets, toolsets.Toolsets.Select(t => t.Name), AllKeyword, [], readOnly: false, []);
    }

    /// <summary>The selection the server's environment makes from <see cref="Toolsets.Server"/>.</summary>
    public static ToolsetSelection FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable, Toolsets.Server);

    /// <summary>The selection the variables <paramref name="environment"/> returns make from <paramref name="catalog"/>.</summary>
    public static ToolsetSelection FromEnvironment(Func<string, string?> environment, ToolsetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(catalog);

        var warnings = new List<string>();
        var (enabled, source) = ReadToolsets(environment(ToolsetsVariable), catalog, warnings);
        var excluded = ReadExcludedTools(environment(ExcludeToolsVariable), catalog, warnings);
        var readOnly = ReadReadOnly(environment(ReadOnlyVariable), warnings);
        return new ToolsetSelection(catalog, enabled, source, excluded, readOnly, warnings);
    }

    /// <summary>
    /// True when the tool is served: it is not excluded, it is annotated read-only when the selection is read-only, and
    /// its toolset is enabled. A tool that no toolset names is not the toolsets' to hide (the production catalog names
    /// every tool).
    /// </summary>
    public bool Serves(string toolName, bool readOnlyHint)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (_excluded.Contains(toolName) || (ReadOnly && !readOnlyHint))
        {
            return false;
        }

        return Catalog.ToolsetOf(toolName) is not { } toolset || _enabled.Contains(toolset.Name);
    }

    private static (IEnumerable<string> Enabled, string Source) ReadToolsets(string? value, ToolsetCatalog catalog, List<string> warnings)
    {
        var defaults = catalog.Toolsets.Where(t => t.OnByDefault).Select(t => t.Name).ToList();
        var entries = Split(value);
        if (entries.Count == 0)
        {
            return (defaults, DefaultKeyword);
        }

        var named = new List<string>();
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var entry in entries)
        {
            var name = entry.TrimStart('-', '+');
            IReadOnlyList<string>? toolsets = name.ToLowerInvariant() switch
            {
                DefaultKeyword => defaults,
                AllKeyword => catalog.Toolsets.Select(t => t.Name).ToList(),
                _ => catalog.Find(name) is { } toolset ? [toolset.Name] : null
            };

            if (toolsets == null)
            {
                warnings.Add($"{ToolsetsVariable}: '{entry}' is not a toolset and is ignored. The toolsets are " +
                             $"{string.Join(", ", catalog.Toolsets.Select(t => t.Name).Order(StringComparer.Ordinal))}, " +
                             $"and '{DefaultKeyword}' and '{AllKeyword}'.");
                continue;
            }

            (entry.StartsWith('-') ? removed : entry.StartsWith('+') ? added : named).AddRange(toolsets);
        }

        if (removed.Contains(catalog.AlwaysOn, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"{ToolsetsVariable}: the '{catalog.AlwaysOn}' toolset is always on and cannot be removed; " +
                         $"{ExcludeToolsVariable} can leave out its tools.");
        }

        if (named.Count == 0 && added.Count == 0 && removed.Count == 0)
        {
            warnings.Add($"{ToolsetsVariable} names no toolset, so the default toolsets are on.");
            return (defaults, DefaultKeyword);
        }

        // Plain names replace the default; +name and -name change what the plain names (or else the default) give
        var start = named.Count == 0 ? defaults : named;
        return (start.Concat(added).Except(removed, StringComparer.OrdinalIgnoreCase).ToList(), ToolsetsVariable);
    }

    private static List<string> ReadExcludedTools(string? value, ToolsetCatalog catalog, List<string> warnings)
    {
        var excluded = new List<string>();
        foreach (var entry in Split(value))
        {
            var tool = catalog.ToolNames.FirstOrDefault(name => string.Equals(name, entry, StringComparison.OrdinalIgnoreCase));
            if (tool == null)
            {
                warnings.Add($"{ExcludeToolsVariable}: '{entry}' is not a tool of this server and is ignored.");
            }
            else if (!excluded.Contains(tool, StringComparer.Ordinal))
            {
                excluded.Add(tool);
            }
        }

        return excluded;
    }

    private static bool ReadReadOnly(string? value, List<string> warnings)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || OffValues.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!OnValues.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"{ReadOnlyVariable}: '{text}' is not 1 or 0; read-only mode is on, to be safe.");
        }

        return true;
    }

    private static List<string> Split(string? value) =>
        (value ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
