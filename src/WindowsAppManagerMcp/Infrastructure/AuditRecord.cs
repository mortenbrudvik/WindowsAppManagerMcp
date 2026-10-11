using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Builds an audit log line (<see cref="AuditEntry"/>, roadmap M2.17) from a call's arguments, what was read before it
/// ran, and its result. Pure: it reads no window and writes no file.
/// </summary>
/// <remarks>
/// The arguments are summarized by default-deny against the tool's declared input schema, so a new tool or parameter,
/// or a value of the wrong type, cannot leak what it carries (<see cref="SummarizeArguments"/>): an argument the tool
/// does not declare is only counted; a number or boolean is kept only where the tool declares one; a string is kept
/// only when its name is in <see cref="NamedStringArguments"/> (process and preset names, fixed values), a program to
/// launch is kept as a path or as a URL's scheme and host (<see cref="RedactExecutable"/>), and any other value is
/// recorded as its length (<c>&lt;name&gt;Length</c>) or count (<c>&lt;name&gt;Count</c>). So typed text, keys,
/// clipboard text, URL paths and queries, working directories and command-line arguments never reach the file. Of the
/// result, only the numbers and booleans at its top level, its errorCode, the receiving window and a batch's items are
/// kept; never its text.
/// </remarks>
public static class AuditRecord
{
    /// <summary>A window title is cut to this many characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>At most this many windows of a batch are read before the call.</summary>
    public const int MaxWindowsRead = 50;

    /// <summary>
    /// The string arguments that are kept as they are: they name a process, a layout preset or a fixed value, never
    /// content. <c>executable</c> is kept too, as a path or as a URL's scheme and host only (<see cref="RedactExecutable"/>).
    /// </summary>
    public static IReadOnlySet<string> NamedStringArguments { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "processName", "expectedProcessName", "name", "matchBy", "browser", "executable", "state", "position"
    };

    /// <summary>The key under which the arguments a tool does not declare are counted.</summary>
    public const string UnknownArgumentsCount = "unknownArgumentsCount";

    private const string ExecutableArgument = "executable";

    /// <summary>Where a program name with no scheme is cut: the rest can be a URL's path, query or fragment.</summary>
    private static readonly char[] UrlDelimiters = ['/', '?', '#'];

    // One line per entry: no indentation, nulls left out, camelCase, and non-ASCII titles readable. The relaxed encoder
    // still escapes control characters and U+2028/U+2029, so a title cannot break the line.
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new TimestampConverter() }
    };

    /// <summary>The entry for <paramref name="call"/>, which ended with <paramref name="result"/> (null when it threw).</summary>
    public static AuditEntry Build(AuditCall call, CallToolResult? result, string outcome, long durationMs, int serverPid)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(outcome);

        var body = result == null ? null : ResultBody(result);
        var items = Items(body, call.Windows) ?? RequestedWindows(call);

        return new AuditEntry(
            call.Started,
            call.Tool,
            outcome,
            body == null ? null : Identifier(body, "errorCode"),
            durationMs,
            items == null ? Target(call, body) : null,
            body != null && body.TryGetPropertyValue("receivingWindow", out var receiving) && receiving is JsonObject window
                ? Window(window)
                : null,
            items,
            SummarizeArguments(call.Arguments, call.DeclaredTypes),
            body == null ? null : Scalars(body),
            serverPid,
            call.RequestId);
    }

    /// <summary>The entry as one line of JSON, without the line break.</summary>
    public static string ToJsonLine(AuditEntry entry) => JsonSerializer.Serialize(entry, LineOptions);

    /// <summary>
    /// The argument summary, default-deny:
    /// <list type="bullet">
    /// <item>An argument the tool does not declare (or, with no schema, whose name is no identifier) is only counted,
    /// under <see cref="UnknownArgumentsCount"/>: its name can be anything the client sent.</item>
    /// <item>A number or boolean is kept where the tool declares the parameter as one; where it does not (a PIN sent as
    /// a number for a text), or with no schema, it is recorded as the length of its JSON text.</item>
    /// <item>A string in <see cref="NamedStringArguments"/> is kept (<c>executable</c> redacted, with
    /// <c>executableLength</c> when something was cut); any other string is recorded as <c>&lt;name&gt;Length</c>.</item>
    /// <item>An array is recorded as <c>&lt;name&gt;Count</c>; objects and nulls are left out.</item>
    /// </list>
    /// Null when nothing is left.
    /// </summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <param name="declaredTypes">The JSON types the tool declares per parameter (<see cref="DeclaredTypes"/>); null when unknown.</param>
    public static JsonObject? SummarizeArguments(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? declaredTypes)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var summary = new JsonObject();
        var unknown = 0;
        foreach (var (name, value) in arguments)
        {
            IReadOnlySet<string>? types = null;
            if (declaredTypes == null ? !IsIdentifier(name) : !declaredTypes.TryGetValue(name, out types))
            {
                unknown++;
                continue;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Number when types != null && (types.Contains("integer") || types.Contains("number")):
                case JsonValueKind.True or JsonValueKind.False when types != null && types.Contains("boolean"):
                    summary[name] = JsonValue.Create(value.Clone());
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    summary[name + "Length"] = value.GetRawText().Length;
                    break;
                case JsonValueKind.String when name == ExecutableArgument:
                    var executable = value.GetString()!;
                    var redacted = RedactExecutable(executable);
                    summary[name] = redacted;
                    if (redacted != executable)
                    {
                        summary[name + "Length"] = executable.Length;
                    }

                    break;
                case JsonValueKind.String when NamedStringArguments.Contains(name):
                    summary[name] = Cut(value.GetString()!, MaxTitleLength);
                    break;
                case JsonValueKind.String:
                    summary[name + "Length"] = value.GetString()!.Length;
                    break;
                case JsonValueKind.Array:
                    summary[name + "Count"] = value.GetArrayLength();
                    break;
            }
        }

        if (unknown > 0)
        {
            summary[UnknownArgumentsCount] = unknown;
        }

        return summary.Count == 0 ? null : summary;
    }

    /// <summary>
    /// The JSON types an input schema declares for each of its <c>properties</c>: <c>"type": "integer"</c> gives
    /// {integer}, <c>"type": ["integer", "null"]</c> gives {integer, null}, a property without a type gives an empty set.
    /// Null when there is no schema; empty when it has no properties.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>>? DeclaredTypes(JsonElement? inputSchema)
    {
        if (inputSchema is not { ValueKind: JsonValueKind.Object } schema)
        {
            return null;
        }

        var declared = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return declared;
        }

        foreach (var property in properties.EnumerateObject())
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            if (property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("type", out var type))
            {
                if (type.ValueKind == JsonValueKind.String)
                {
                    types.Add(type.GetString()!);
                }
                else if (type.ValueKind == JsonValueKind.Array)
                {
                    types.UnionWith(type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!));
                }
            }

            declared[property.Name] = types;
        }

        return declared;
    }

    /// <summary>
    /// The windows the arguments name, to read before the call: <c>windowHandle</c>, <c>handle</c>, the <c>handle</c> of a
    /// <c>window</c> selector (roadmap M2.4), and the <c>handle</c> or <c>window.handle</c> of each item of <c>windows</c>
    /// (a close batch), at most <see cref="MaxWindowsRead"/>. A selector without a handle names no window until the tool
    /// resolves it; the result's <c>handle</c> then says which (<see cref="Build"/>).
    /// </summary>
    public static IReadOnlyList<long> WindowHandles(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var handles = new List<long>();
        foreach (var name in new[] { "windowHandle", "handle" })
        {
            if (arguments.TryGetValue(name, out var value) && HandleIn(value) is { } handle)
            {
                handles.Add(handle);
            }
        }

        if (arguments.TryGetValue("window", out var window) && SelectorHandle(window) is { } selected)
        {
            handles.Add(selected);
        }

        if (arguments.TryGetValue("windows", out var windows) && windows.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in windows.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("handle", out var value) && HandleIn(value) is { } handle)
                {
                    handles.Add(handle);
                }
                else if (item.TryGetProperty("window", out var itemWindow) && SelectorHandle(itemWindow) is { } itemSelected)
                {
                    handles.Add(itemSelected);
                }
            }
        }

        return handles.Distinct().Take(MaxWindowsRead).ToList();

        static long? HandleIn(JsonElement value) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var handle) ? handle : null;

        static long? SelectorHandle(JsonElement selector) =>
            selector.ValueKind == JsonValueKind.Object && selector.TryGetProperty("handle", out var value) ? HandleIn(value) : null;
    }

    /// <summary>The processes the arguments name, to read before the call: <c>processId</c>.</summary>
    public static IReadOnlyList<int> ProcessIds(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return arguments.TryGetValue("processId", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id)
            ? [id]
            : [];
    }

    /// <summary>
    /// What a launch was asked for, without what can hold a secret:
    /// <list type="bullet">
    /// <item>A URI that is no file path keeps only its scheme, host and port (<c>https://hooks.example.com</c>,
    /// <c>mailto:example.com</c>, <c>data:</c>): a path can be a bearer secret (webhook, password-reset, magic-login and
    /// share links), and an opaque URI's body (<c>data:</c>, <c>javascript:</c>) is content.</item>
    /// <item>A file path is kept; a <c>file:</c> URI as its local path, without query or fragment.</item>
    /// <item>Anything else (a program name such as <c>notepad</c>, a relative path, or a URL without a scheme such as
    /// <c>www.example.com/reset?t=...</c>, which the shell opens too) is kept up to its first <c>/ ? #</c>, without what
    /// comes before an <c>@</c> (user info).</item>
    /// </list>
    /// </summary>
    public static string RedactExecutable(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);

        if (Uri.TryCreate(executable, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile || uri.IsUnc)
            {
                var path = executable.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? uri.LocalPath : executable;
                return Cut(path, MaxTitleLength);
            }

            return Cut(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped), MaxTitleLength);
        }

        // A URL without a scheme: its host, without user info (before an @) and without path, query or fragment
        var end = executable.IndexOfAny(UrlDelimiters);
        var host = end < 0 ? executable : executable[..end];
        var at = host.LastIndexOf('@');
        return Cut(at < 0 ? host : host[(at + 1)..], MaxTitleLength);
    }

    private static AuditTarget? Target(AuditCall call, JsonObject? body)
    {
        // What the arguments aimed at, read before the call: a window, else a process, else a program to launch
        AuditTarget? target = null;
        var handles = WindowHandles(call.Arguments);
        if (handles.Count > 0)
        {
            target = call.Windows.TryGetValue(handles[0], out var window) ? window : new AuditTarget(Handle: handles[0]);
        }
        else if (ProcessIds(call.Arguments) is [var processId])
        {
            target = new AuditTarget(ProcessId: processId, Process: call.Processes.GetValueOrDefault(processId));
        }
        else if (call.Arguments.TryGetValue(ExecutableArgument, out var executable) && executable.ValueKind == JsonValueKind.String)
        {
            target = new AuditTarget(Executable: RedactExecutable(executable.GetString()!));
        }

        if (body == null)
        {
            return target;
        }

        // Completed from the result: the process a launch started and its window, a killed process's name, and the window a
        // window selector resolved to (roadmap M2.4: the window tools report it as handle)
        var resultHandle = Long(body, "windowHandle") ?? (handles.Count == 0 ? Long(body, "handle") : null);
        var resultProcessId = Int(body, "processId");
        var resultProcess = String(body, "processName");
        if (target == null && resultHandle == null && resultProcessId == null)
        {
            return null;
        }

        target ??= new AuditTarget();
        return target with
        {
            Handle = target.Handle ?? resultHandle,
            ProcessId = target.ProcessId ?? resultProcessId,
            Process = target.Process ?? resultProcess
        };
    }

    private static IReadOnlyList<AuditItem>? Items(JsonObject? body, IReadOnlyDictionary<long, AuditTarget> windows)
    {
        if (body == null || !body.TryGetPropertyValue("results", out var node) || node is not JsonArray results)
        {
            return null;
        }

        return results.OfType<JsonObject>()
            .Select(item =>
            {
                var handle = Long(item, "handle") ?? Long(item, "windowHandle");
                var before = handle is { } h ? windows.GetValueOrDefault(h) : null;
                var executable = String(item, ExecutableArgument);
                return new AuditItem(
                    handle,
                    before?.Title,
                    before?.Process,
                    before?.ProcessId ?? Int(item, "processId"),
                    executable == null ? null : RedactExecutable(executable),
                    item.TryGetPropertyValue("success", out var success) && success?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                        ? success.GetValue<bool>()
                        : null,
                    Identifier(item, "errorCode"));
            })
            .ToList();
    }

    /// <summary>
    /// A close batch that has no results (it was refused as a whole, or did not finish): the windows it asked for, as
    /// they were before the call, with no outcome of their own. Null for any other call.
    /// </summary>
    private static IReadOnlyList<AuditItem>? RequestedWindows(AuditCall call)
    {
        if (!call.Arguments.TryGetValue("windows", out var windows) || windows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return WindowHandles(new Dictionary<string, JsonElement> { ["windows"] = windows })
            .Select(handle => call.Windows.GetValueOrDefault(handle) is { } before
                ? new AuditItem(handle, before.Title, before.Process, before.ProcessId, null, null, null)
                : new AuditItem(handle, null, null, null, null, null, null))
            .ToList();
    }

    private static AuditTarget Window(JsonObject window) => new(
        Long(window, "handle"),
        String(window, "title") is { } title ? Cut(title, MaxTitleLength) : null,
        String(window, "processName"),
        Int(window, "processId"));

    /// <summary>The numbers and booleans at the top of the result, except success (the outcome says it). Null when none.</summary>
    private static JsonObject? Scalars(JsonObject body)
    {
        var scalars = new JsonObject();
        foreach (var (name, value) in body)
        {
            if (name != ToolOutcome.SuccessProperty && value is JsonValue &&
                value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                scalars[name] = value.DeepClone();
            }
        }

        return scalars.Count == 0 ? null : scalars;
    }

    /// <summary>The first text block of the result that is a JSON object, or null.</summary>
    private static JsonObject? ResultBody(CallToolResult result)
    {
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            if (block.Text is not { Length: > 0 } text || text[0] != '{')
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(text) is JsonObject body)
                {
                    return body;
                }
            }
            catch (JsonException)
            {
                // Not JSON: no outcome to read
            }
        }

        return null;
    }

    /// <summary>A short identifier such as an errorCode; anything else (a message, a value that is too long) is left out.</summary>
    private static string? Identifier(JsonObject node, string name) =>
        String(node, name) is { Length: > 0 and <= 64 } value && value.All(char.IsAsciiLetterOrDigit) ? value : null;

    private static string? String(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static long? Long(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is JsonValue number && number.GetValueKind() == JsonValueKind.Number &&
        number.TryGetValue<long>(out var result)
            ? result
            : null;

    private static int? Int(JsonObject node, string name) => Long(node, name) is { } value and >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>An argument name the log can hold: ASCII letters, digits and underscores, at most 64 characters.</summary>
    private static bool IsIdentifier(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Writes the time as 2026-10-11T14:03:22.123+02:00: milliseconds and the UTC offset, for every reader.</summary>
    private sealed class TimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
    }
}
