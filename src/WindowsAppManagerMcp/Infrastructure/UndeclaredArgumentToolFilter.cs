using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Call-tool filter that refuses an argument an <see cref="ActingTools">acting tool</see> does not declare (M3 review), before
/// the tool runs, with InvalidArguments naming it and, when one is within <see cref="MaxSuggestionDistance"/> edits, the
/// declared name it most likely stands for. The SDK binds each parameter by name and ignores every other key, so a
/// misspelled or borrowed name (get_ui_elements' controlTypes for element_action's controlType, find_ui_elements'
/// withinRef) would be dropped without a word, and the call would act on a wider match than the agent asked for. Read
/// tools still ignore such an argument: it can only widen what they read.
/// </summary>
/// <remarks>
/// The declared names are the <c>properties</c> of the input schema the server serves for the tool, as the audit log reads
/// them (<see cref="AuditLogToolFilter"/>). The filter sits inside the audit log and the error filter, so the refusal is
/// audited and reported as a tool error, and outside the input gate filter, so it never waits for the gate or a pause.
/// </remarks>
public static class UndeclaredArgumentToolFilter
{
    /// <summary>
    /// The tools that refuse an undeclared argument: the input tools, element_action, respond_to_dialog and file_dialog,
    /// and the close and kill tools. Written out rather than taken from the tool annotations: the captures, launches,
    /// window tools, layouts and the clipboard are not read-only either, and they keep ignoring such an argument.
    /// </summary>
    public static IReadOnlySet<string> ActingTools { get; } = new HashSet<string>(
        InputGateToolFilter.InputTools.Concat(
        [
            "element_action", "respond_to_dialog", "file_dialog",
            "close_window", "close_windows_batch", "close_application", "kill_process"
        ]),
        StringComparer.Ordinal);

    /// <summary>The largest edit distance at which a declared name is offered as the one meant.</summary>
    private const int MaxSuggestionDistance = 2;

    /// <summary>What to do instead, for a name a tool does not declare because it takes it another way.</summary>
    private static readonly IReadOnlyDictionary<(string Tool, string Argument), string> Hints =
        new Dictionary<(string Tool, string Argument), string>
        {
            [("element_action", "withinRef")] =
                "to act on an element inside another one, find it with find_ui_elements withinRef and pass its ref"
        };

    /// <summary>Registers <see cref="Filter"/> in the call-tool pipeline of the MCP server.</summary>
    public static IMcpServerBuilder AddUndeclaredArgumentRefusal(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));
    }

    /// <summary>
    /// Call-tool filter: for an <see cref="ActingTools">acting tool</see>, throws an InvalidArguments
    /// <see cref="ToolErrorException"/> when an argument's name is not in the tool's input schema; every other call, and a
    /// tool whose schema the filter cannot see, passes straight through.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
        {
            var tool = request.Params?.Name;
            if (tool != null && ActingTools.Contains(tool) && request.Params?.Arguments is { Count: > 0 } arguments &&
                AuditRecord.DeclaredTypes(AuditLogToolFilter.InputSchemaOf(request, tool)) is { } declared &&
                Refusal(tool, arguments.Keys, declared.Keys) is { } refusal)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, refusal);
            }

            return next(request, cancellationToken);
        };

    /// <summary>
    /// The InvalidArguments message for the names in <paramref name="given"/> that <paramref name="tool"/> does not declare,
    /// or null when it declares them all.
    /// </summary>
    public static string? Refusal(string tool, IEnumerable<string> given, IEnumerable<string> declaredNames)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var declared = declaredNames.ToList();
        var unknown = given.Where(name => !declared.Contains(name, StringComparer.Ordinal)).ToList();
        if (unknown.Count == 0)
        {
            return null;
        }

        // A name that is no identifier can be anything the client sent (it is logged with the error): it is not repeated
        var named = unknown.Select(name =>
            !AuditRecord.IsIdentifier(name) ? "an argument whose name is not a parameter name"
            : Hints.TryGetValue((tool, name), out var hint) ? $"'{name}' ({hint})"
            : Nearest(name, declared) is { } meant ? $"'{name}' (did you mean '{meant}'?)"
            : $"'{name}'");
        var list = unknown.Count == 1 ? named.Single() : string.Join(", ", named.SkipLast(1)) + " or " + named.Last();
        return $"{tool} does not take {list}. Nothing was done: an argument the tool does not declare would be ignored, so the " +
               "call could act on something other than what was asked. Use the names of its input schema.";
    }

    /// <summary>
    /// The declared name <paramref name="name"/> most likely stands for: the same name in another letter case, else the one at
    /// the smallest edit distance, at most <see cref="MaxSuggestionDistance"/>; null when none is that close.
    /// </summary>
    private static string? Nearest(string name, IReadOnlyList<string> declared)
    {
        if (declared.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)) is { } sameLetters)
        {
            return sameLetters;
        }

        return declared
            .Select(d => (Name: d, Distance: EditDistance(name.ToLowerInvariant(), d.ToLowerInvariant())))
            .Where(d => d.Distance <= MaxSuggestionDistance)
            .OrderBy(d => d.Distance)
            .Select(d => d.Name)
            .FirstOrDefault();
    }

    /// <summary>The Levenshtein distance: the fewest single-character insertions, deletions or substitutions from one to the other.</summary>
    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
