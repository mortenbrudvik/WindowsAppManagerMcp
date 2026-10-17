using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Call-tool filter that refuses an argument a tool does not declare (M3 review, every tool since roadmap M3.F42), before
/// the tool runs, with InvalidArguments naming it, listing the tool's parameters and, for a near miss, the declared name it
/// most likely stands for. The SDK binds each parameter by its exact name and ignores every other key, so a misspelled or
/// borrowed name would be dropped without a word: take_screenshot with <c>handle</c> (it takes none) captured the whole
/// primary monitor, and element_action with get_ui_elements' <c>controlTypes</c> would act on a wider match than asked for.
/// </summary>
/// <remarks>
/// The declared names are the <c>properties</c> of the input schema the server serves for the tool, as the audit log reads
/// them (<see cref="AuditLogToolFilter"/>), so the check follows the tools as they change. Only the call's
/// <c>arguments</c> are checked: the request's <c>_meta</c> sits beside them, not among them. The filter sits inside the
/// audit log and the error filter, so the refusal is audited and reported as a tool error, and outside the input gate
/// filter, so it never waits for the gate or a pause.
/// </remarks>
public static class UndeclaredArgumentToolFilter
{
    /// <summary>The largest edit distance at which a declared name is offered as the one meant.</summary>
    private const int MaxSuggestionDistance = 2;

    /// <summary>What to do instead, for a name a tool does not declare because it takes it another way.</summary>
    private static readonly IReadOnlyDictionary<(string Tool, string Argument), string> Hints =
        new Dictionary<(string Tool, string Argument), string>
        {
            [("element_action", "withinRef")] =
                "to act on an element inside another one, find it with find_ui_elements withinRef and pass its ref",
            [("take_screenshot", "handle")] = CaptureWindowHint,
            [("take_screenshot", "windowHandle")] = CaptureWindowHint,
            [("take_screenshot", "window")] = CaptureWindowHint
        };

    private const string CaptureWindowHint = "take_screenshot captures a whole monitor; to capture a window, call capture_window";

    /// <summary>Registers <see cref="Filter"/> in the call-tool pipeline of the MCP server.</summary>
    public static IMcpServerBuilder AddUndeclaredArgumentRefusal(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));
    }

    /// <summary>
    /// Call-tool filter: throws an InvalidArguments <see cref="ToolErrorException"/> when an argument's name is not in the
    /// tool's input schema; every other call, and a tool whose schema the filter cannot see (an unknown tool, which the SDK
    /// reports itself), passes straight through.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
        {
            var tool = request.Params?.Name;
            if (tool != null && request.Params?.Arguments is { Count: > 0 } arguments &&
                AuditRecord.DeclaredTypes(AuditLogToolFilter.InputSchemaOf(request, tool)) is { } declared &&
                Refusal(tool, arguments.Keys, declared.Keys) is { } refusal)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, refusal);
            }

            return next(request, cancellationToken);
        };

    /// <summary>
    /// The InvalidArguments message for the names in <paramref name="given"/> that <paramref name="tool"/> does not declare,
    /// or null when it declares them all. Names are compared exactly, letter case included, as the SDK binds them.
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
            : name == "monitorIndex" && declared.Contains("monitor") ? $"'{name}' (removed in 3.0.0: use 'monitor', which takes the index too)"
            : Nearest(name, declared) is { } meant ? $"'{name}' (did you mean '{meant}'?)"
            : $"'{name}'");
        var list = unknown.Count == 1 ? named.Single() : string.Join(", ", named.SkipLast(1)) + " or " + named.Last();
        var parameters = declared.Count == 0 ? "It takes no arguments." : $"Its parameters are {string.Join(", ", declared)}.";
        return $"Invalid arguments for tool '{tool}'. It does not take {list}. {parameters} Nothing was done: an argument a " +
               "tool does not declare would be ignored, so the call could act on, capture or read something other than " +
               "what was meant.";
    }

    /// <summary>
    /// The declared name <paramref name="name"/> most likely stands for: the same name in another letter case, else the one at
    /// the smallest edit distance, at most <see cref="MaxSuggestionDistance"/>, else the only one that ends with the other's
    /// last words or whose last words it is (<c>handle</c> for <c>windowHandle</c>, and the reverse); null when none is that
    /// close.
    /// </summary>
    private static string? Nearest(string name, IReadOnlyList<string> declared)
    {
        if (declared.FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)) is { } sameLetters)
        {
            return sameLetters;
        }

        if (declared
                .Select(d => (Name: d, Distance: EditDistance(name.ToLowerInvariant(), d.ToLowerInvariant())))
                .Where(d => d.Distance <= MaxSuggestionDistance)
                .OrderBy(d => d.Distance)
                .Select(d => d.Name)
                .FirstOrDefault() is { } close)
        {
            return close;
        }

        var endings = declared.Where(d => EndsWithWords(d, name) || EndsWithWords(name, d)).Take(2).ToList();
        return endings.Count == 1 ? endings[0] : null;
    }

    /// <summary>
    /// Whether the camelCase name <paramref name="longer"/> ends with the words of <paramref name="shorter"/>, ignoring
    /// letter case: <c>windowHandle</c> ends with <c>handle</c>, <c>maxWidth</c> does not end with <c>dth</c>.
    /// </summary>
    private static bool EndsWithWords(string longer, string shorter)
    {
        var start = longer.Length - shorter.Length;
        return start > 0 && char.IsUpper(longer[start]) && longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase);
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
