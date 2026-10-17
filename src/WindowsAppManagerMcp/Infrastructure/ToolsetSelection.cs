namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// The tools a server serves, as the user chose them in the server's environment (roadmap M2.14):
/// <list type="bullet">
/// <item><see cref="ToolsetsVariable"/>: the toolsets, separated by commas, semicolons or spaces. <c>default</c> stands
/// for the toolsets that are on by default and <c>all</c> for every toolset. <c>+name</c> adds a toolset and <c>-name</c>
/// removes one; with only such entries they change the default (<c>-browser,-process</c>). Not set: the default
/// toolsets. The always-on toolset is always served.</item>
/// <item><see cref="ExcludeToolsVariable"/>: tools to leave out, whatever their toolset, by tool name or by toolset name
/// (every tool of that toolset, and the tools that moved out of it); the catalog's always-served tool (get_server_info)
/// stays. A tool that the toolsets in force do not serve can be excluded too (a name written before the tool moved to an
/// opt-in toolset, roadmap M6.1): it is no error.</item>
/// <item><see cref="ReadOnlyVariable"/>: <c>1</c> serves only the tools whose [McpServerTool] sets ReadOnly = true (their
/// tools/list annotation readOnlyHint).</item>
/// </list>
/// A tool that moved out of a toolset (<see cref="ToolsetCatalog.MovedTools"/>, roadmap M6.1) is served only while the
/// toolset it came from is on too, so <c>all</c> never serves a configuration a tool it did not serve before the move;
/// get_server_info says where each moved tool went while its toolset is off (<see cref="Moved"/>). A name a major release
/// removed (<see cref="ToolsetCatalog.RetiredNames"/>) is a known name that serves nothing: excluding it, or adding or
/// removing it as a toolset, changes nothing and does not fail closed.
/// A name that is not a toolset or tool is ignored with a warning (logged at start, reported by get_server_info), as long
/// as ignoring it serves fewer tools, not more. The variables exist to restrict the server, so a value that cannot be
/// read fails closed: when a <c>-name</c> of <see cref="ToolsetsVariable"/> names no toolset (the user meant to remove
/// one), or a plain name names none and no other plain name is valid (the user meant to replace the default), or an entry
/// of <see cref="ExcludeToolsVariable"/> names no tool or toolset (the user meant to remove something), only the
/// always-on toolset is served. An unknown <c>+name</c> is ignored: it only asked for more. The agent can still call get_server_info, whose
/// warnings say what was wrong, and tell the user; a server that refused to start would only show the client a failed
/// connection, with the reason on a stderr the user may never see.
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
        Moved = MovedNotes();
    }

    /// <summary>The toolsets the selection was made from.</summary>
    public ToolsetCatalog Catalog { get; }

    /// <summary>The toolsets that are served, in catalog order.</summary>
    public IReadOnlyList<string> Enabled { get; }

    /// <summary>The toolsets that are not served, in catalog order.</summary>
    public IReadOnlyList<string> Disabled { get; }

    /// <summary>
    /// "default" (<see cref="ToolsetsVariable"/> not set), <see cref="ToolsetsVariable"/>, <see cref="ExcludeToolsVariable"/>
    /// (an entry it could not read left only the always-on toolset on), or "all" (<see cref="All"/>).
    /// </summary>
    public string Source { get; }

    /// <summary>The tools <see cref="ExcludeToolsVariable"/> leaves out, as the catalog names them.</summary>
    public IReadOnlyList<string> ExcludedTools { get; }

    /// <summary>True when only the tools annotated readOnlyHint are served (<see cref="ReadOnlyVariable"/>).</summary>
    public bool ReadOnly { get; }

    /// <summary>The entries that were ignored or read in a way the user may not expect, one sentence each.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Where the tools that moved out of a toolset in force went (roadmap M6.1), one sentence per toolset that is off and
    /// holds such tools: the toolset, how to turn it on, and each tool with its replacement, if any. A tool the user
    /// excluded, or whose former toolset is off too, is left out. Null when there is none: get_server_info reports it, so an
    /// agent that looks for set_windows_bounds_batch learns that the windows-batch toolset serves it.
    /// </summary>
    public IReadOnlyList<string>? Moved { get; }

    /// <summary>
    /// One line for the end of the server instructions when the user turned off tools that the default serves (a
    /// toolset that is on by default, read-only mode, excluded tools): the instructions name tools of the loop, such as
    /// focus_window and mouse_click, and the agent must skip the steps whose tools are missing instead of looking for them or
    /// working around them. Null when nothing the default serves is off (an opt-in toolset that is off is not mentioned).
    /// </summary>
    public string? InstructionsNote
    {
        get
        {
            var parts = new List<string>();
            var off = Disabled.Where(name => Catalog.Find(name)!.OnByDefault).ToList();
            if (off.Count > 0)
            {
                parts.Add($"the toolset{(off.Count == 1 ? "" : "s")} {string.Join(", ", off)}");
            }

            if (ReadOnly)
            {
                parts.Add("every tool that changes something (read-only mode)");
            }

            // A tool the toolsets in force do not serve anyway (an opt-in toolset's) was not turned off by excluding it
            var excluded = ExcludedTools.Where(ServedByToolsets).ToList();
            if (excluded.Count > 0)
            {
                parts.Add($"the tool{(excluded.Count == 1 ? "" : "s")} {string.Join(", ", excluded)}");
            }

            return parts.Count == 0
                ? null
                : $"The user turned off {string.Join("; ", parts)}. Skip the steps above whose tools are missing from your tool " +
                  "list, never work around a missing tool (for example with key chords), and tell the user when the task needs one.";
        }
    }

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
        var (excluded, unreadable) = ReadExcludedTools(environment(ExcludeToolsVariable), catalog, warnings);
        if (unreadable.Count > 0)
        {
            // Fail closed, as for an unknown -name of WAM_TOOLSETS: the entry was meant to remove a tool or toolset
            warnings.Add($"{ExcludeToolsVariable}: ignoring {string.Join(", ", unreadable.Select(e => $"'{e}'"))} could serve tools " +
                         $"the user meant to turn off, so only the '{catalog.AlwaysOn}' toolset is on, to be safe. Correct the " +
                         "value to serve more tools.");
            (enabled, source) = (new[] { catalog.AlwaysOn }, ExcludeToolsVariable);
        }

        var readOnly = ReadReadOnly(environment(ReadOnlyVariable), warnings);
        var selection = new ToolsetSelection(catalog, enabled, source, excluded, readOnly, warnings);
        warnings.AddRange(selection.MovedWarnings());
        return selection;
    }

    /// <summary>
    /// True when the tool is served: it is the catalog's always-served tool, or it is not excluded, it is annotated
    /// read-only when the selection is read-only, and its toolset is enabled. A tool that no toolset names is not the toolsets' to hide (the production catalog names
    /// every tool).
    /// </summary>
    public bool Serves(string toolName, bool readOnlyHint)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (string.Equals(toolName, Catalog.AlwaysServedTool, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_excluded.Contains(toolName) || (ReadOnly && !readOnlyHint))
        {
            return false;
        }

        return ServedByToolsets(toolName);
    }

    /// <summary>
    /// True when the toolsets in force serve the tool, whatever WAM_EXCLUDE_TOOLS and WAM_READ_ONLY say: its toolset is on,
    /// and so is the toolset it moved out of, if it moved (roadmap M6.1). A tool that no toolset names is served.
    /// </summary>
    private bool ServedByToolsets(string toolName)
    {
        if (Catalog.ToolsetOf(toolName) is not { } toolset)
        {
            return true;
        }

        return _enabled.Contains(toolset.Name) && (Catalog.MoveOf(toolName) is not { } move || _enabled.Contains(move.FormerToolset));
    }

    /// <summary>
    /// One sentence per toolset that is off and holds tools that moved out of a toolset in force, unless the user excluded
    /// them (<see cref="Moved"/>).
    /// </summary>
    private List<string>? MovedNotes()
    {
        var notes = Catalog.Toolsets
            .Where(toolset => !_enabled.Contains(toolset.Name))
            .Select(toolset => (Toolset: toolset, Tools: Catalog.MovedTools
                .Where(move => Catalog.ToolsetOf(move.Tool) == toolset && _enabled.Contains(move.FormerToolset) && !_excluded.Contains(move.Tool))
                .ToList()))
            .Where(group => group.Tools.Count > 0)
            .Select(group => $"In the {group.Toolset.Name} toolset, which is off ({ToolsetsVariable}=+{group.Toolset.Name} serves it): " +
                             $"{string.Join(", ", group.Tools.Select(Describe))}.")
            .ToList();
        return notes.Count == 0 ? null : notes;
    }

    /// <summary>
    /// The warnings about moved tools (roadmap M6.1): a toolset that is on serves some of its tools only with the toolset they
    /// moved out of, which is off; and tools that something replaced are turned on, which a later release removes.
    /// </summary>
    private IEnumerable<string> MovedWarnings()
    {
        var notServed = Catalog.MovedTools
            .Where(move => _enabled.Contains(Catalog.ToolsetOf(move.Tool)!.Name) && !_enabled.Contains(move.FormerToolset) &&
                           !_excluded.Contains(move.Tool))
            .GroupBy(move => (Toolset: Catalog.ToolsetOf(move.Tool)!.Name, move.FormerToolset));
        foreach (var group in notServed)
        {
            yield return $"{ToolsetsVariable}: {string.Join(", ", group.Select(move => move.Tool))} of the '{group.Key.Toolset}' " +
                         $"toolset {(group.Count() == 1 ? "is" : "are")} served only together with the '{group.Key.FormerToolset}' " +
                         $"toolset {(group.Count() == 1 ? "it" : "they")} moved out of, which is off.";
        }

        var replaced = Catalog.MovedTools.Where(move => move.Replacement != null && Serves(move.Tool, readOnlyHint: true)).ToList();
        if (replaced.Count > 0)
        {
            yield return $"{ToolsetsVariable} turns on tools that were replaced and will be removed in the next major release: " +
                         $"{string.Join(", ", replaced.Select(Describe))}. Use the replacements.";
        }
    }

    private static string Describe(MovedTool move) =>
        move.Replacement is { } replacement ? $"{move.Tool} (use {replacement})" : move.Tool;

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
        var unknown = new List<string>();
        foreach (var entry in entries)
        {
            var name = entry.TrimStart('-', '+');
            IReadOnlyList<string>? toolsets = name.ToLowerInvariant() switch
            {
                DefaultKeyword => defaults,
                AllKeyword => catalog.Toolsets.Select(t => t.Name).ToList(),
                _ => catalog.Find(name) is { } toolset ? [toolset.Name] : null
            };

            if (toolsets == null && catalog.IsRetired(name))
            {
                // A toolset a major release removed (3.0.0: deprecated): +name or -name changes nothing now, so it is
                // ignored; a plain name still counts as unknown, so a list of nothing else fails closed as before
                warnings.Add($"{ToolsetsVariable}: the '{name}' toolset was removed in a major release, so '{entry}' is ignored.");
                if (!entry.StartsWith('-') && !entry.StartsWith('+'))
                {
                    unknown.Add(entry);
                }

                continue;
            }

            if (toolsets == null)
            {
                warnings.Add($"{ToolsetsVariable}: '{entry}' is not a toolset and is ignored. The toolsets are " +
                             $"{string.Join(", ", catalog.Toolsets.Select(t => t.Name).Order(StringComparer.Ordinal))}, " +
                             $"and '{DefaultKeyword}' and '{AllKeyword}'.");
                unknown.Add(entry);
                continue;
            }

            (entry.StartsWith('-') ? removed : entry.StartsWith('+') ? added : named).AddRange(toolsets);
        }

        if (removed.Contains(catalog.AlwaysOn, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"{ToolsetsVariable}: the '{catalog.AlwaysOn}' toolset is always on and cannot be removed; " +
                         $"{ExcludeToolsVariable} can leave out its tools.");
        }

        // Fail closed: the variable is set to restrict the server, so ignoring an entry must never serve more than the
        // user asked for. An unknown -name was meant to remove a toolset, and an unknown plain name, with no valid one
        // beside it, was meant to replace the default, which the server would otherwise start from. An unknown +name
        // only asked for more, so ignoring it is safe.
        var unreadable = unknown.Where(e => e.StartsWith('-') || (!e.StartsWith('+') && named.Count == 0)).ToList();
        if (unreadable.Count > 0)
        {
            warnings.Add($"{ToolsetsVariable}: ignoring {string.Join(", ", unreadable.Select(e => $"'{e}'"))} could serve tools " +
                         $"the user meant to turn off, so only the '{catalog.AlwaysOn}' toolset is on, to be safe. Correct the " +
                         "value to serve more tools.");
            return ([catalog.AlwaysOn], ToolsetsVariable);
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

    /// <summary>
    /// The tools <see cref="ExcludeToolsVariable"/> leaves out: each entry is a tool name, or a toolset name that stands for
    /// every tool of that toolset and every tool that moved out of it. A tool the toolsets in force do not serve can be named
    /// too, so a value written before a tool moved to an opt-in toolset still reads. Also the entries that name neither
    /// (<c>Unreadable</c>), which make the selection fail closed.
    /// </summary>
    private static (List<string> Excluded, List<string> Unreadable) ReadExcludedTools(string? value, ToolsetCatalog catalog, List<string> warnings)
    {
        var excluded = new List<string>();
        var unreadable = new List<string>();
        foreach (var entry in Split(value))
        {
            var tool = catalog.ToolNames.FirstOrDefault(name => string.Equals(name, entry, StringComparison.OrdinalIgnoreCase));
            // A toolset's name stands for its tools and the tools that moved out of it (roadmap M6.1): WAM_EXCLUDE_TOOLS=input
            // written before 2.2.0 meant click too
            IReadOnlyList<string>? tools = tool != null
                ? [tool]
                : catalog.Find(entry) is { } toolset
                    ? [.. toolset.Tools, .. catalog.MovedTools.Where(move => move.FormerToolset == toolset.Name).Select(move => move.Tool)]
                    : null;
            if (tools == null && catalog.IsRetired(entry))
            {
                // A tool or toolset a major release removed (3.0.0: click, deprecated): it is excluded already
                continue;
            }

            if (tools == null)
            {
                warnings.Add($"{ExcludeToolsVariable}: '{entry}' is not a tool or toolset of this server. The toolsets are " +
                             $"{string.Join(", ", catalog.Toolsets.Select(t => t.Name).Order(StringComparer.Ordinal))}.");
                unreadable.Add(entry);
                continue;
            }

            foreach (var name in tools)
            {
                if (string.Equals(name, catalog.AlwaysServedTool, StringComparison.Ordinal))
                {
                    warnings.Add($"{ExcludeToolsVariable}: '{name}' is always served (it tells the agent which tools are off) and is not excluded.");
                }
                else if (!excluded.Contains(name, StringComparer.Ordinal))
                {
                    excluded.Add(name);
                }
            }
        }

        return (excluded, unreadable);
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
