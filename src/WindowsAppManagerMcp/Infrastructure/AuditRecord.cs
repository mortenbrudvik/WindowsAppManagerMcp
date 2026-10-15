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
/// only when its name is in <see cref="NamedStringArguments"/> (process and preset names, fixed values) and it is no
/// content for that tool (<see cref="ContentArguments"/>: element_action's name criterion), a program to
/// launch is kept as a path or as a URL's scheme and host (<see cref="RedactExecutable"/>), and any other value is
/// recorded as its length (<c>&lt;name&gt;Length</c>) or count (<c>&lt;name&gt;Count</c>). So typed text, keys,
/// clipboard text, URL paths and queries, working directories and command-line arguments never reach the file. Of the
/// result, only the numbers and booleans at its top level, its errorCode, get_clipboard's origin, the receiving window and its items (a batch's
/// results, close_application's windows, apply_layout's placements: handle, title, process, a launch's program redacted,
/// success, status and errorCode) are kept; never its text. A window a tool learns of only while it runs (a selector's,
/// close_application's, or the dialog respond_to_dialog and file_dialog answered, which is their target) is named by the
/// title and process the tool noted before acting on it (<see cref="AuditScope"/>).
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
    /// <c>name</c> is a layout preset's name (apply_layout, save_layout, delete_layout); element_action's <c>name</c> is an
    /// element's name, which is content, and is recorded as nameLength (<see cref="ContentArguments"/>).
    /// </summary>
    public static IReadOnlySet<string> NamedStringArguments { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "processName", "expectedProcessName", "name", "matchBy", "browser", "executable", "state", "position", "format", "space",
        // element_action (roadmap M3.6): the element's ref and fixed values; its value is recorded as valueLength
        "ref", "action", "mode", "direction", "amount", "fallback", "observe",
        // respond_to_dialog (roadmap M3.5): the button pressed, a standard name, id or caption. file_dialog's path is
        // recorded as pathLength: a file name can say what the user works on
        "button",
        // type_text (roadmap M4.1, M4.4): fixed values; its text is recorded as textLength
        "controlChars", "method",
        // activate_tab and browser_navigate (roadmap M5.5): a CDP tab id and a fixed value; browser_navigate's url is kept
        // as its scheme and host (NavigatedUrlTool)
        "targetId", "waitUntil",
        // browser_click, browser_type and browser_navigate (roadmap M5.8, dialogs): accept or dismiss; promptText is recorded
        // as promptTextLength
        "dialog"
    };

    /// <summary>
    /// The tool whose <c>url</c> argument is kept as a web page's scheme, host and port, and otherwise as its scheme alone
    /// (<see cref="RedactNavigatedUrl"/>): the page's path and query can hold a secret (a reset or share link).
    /// </summary>
    private const string NavigatedUrlTool = "browser_navigate";

    /// <summary>
    /// The arguments of <see cref="NamedStringArguments"/> that are content for one tool, and are recorded as their length
    /// there: element_action's <c>name</c> criterion is an element's name (a file in Explorer, an email's subject, a
    /// contact), which can say what the user works on, as file_dialog's path can.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ContentArguments { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ElementActionTool] = new HashSet<string>(StringComparer.Ordinal) { "name" },
            // browser_click's name is an element's accessible name on a web page (roadmap M5.8): content, as element_action's
            [BrowserClickTool] = new HashSet<string>(StringComparer.Ordinal) { "name" }
        };

    private const string BrowserClickTool = "browser_click";

    /// <summary>
    /// The tool whose result's <c>value</c> is never recorded, even as a number or a boolean (roadmap M5.8): browser_evaluate's
    /// value is whatever the page's script returned (a code, a balance, a flag); only its valueLength is.
    /// </summary>
    private const string ScriptValueTool = "browser_evaluate";

    private const string ScriptValueProperty = "value";

    /// <summary>The result property, kept as an identifier, that says whose clipboard content get_clipboard returned.</summary>
    private const string ClipboardOriginProperty = "origin";

    /// <summary>The result property, kept as an identifier, that says what a launch led to.</summary>
    private const string LaunchStatusProperty = "launchStatus";

    private const string ElementActionTool = "element_action";

    /// <summary>The tools that answer a dialog (roadmap M3.5, M3.14): their line's target is the dialog, <see cref="DialogHandleProperty"/>.</summary>
    private static readonly HashSet<string> DialogTools = new(StringComparer.Ordinal) { "respond_to_dialog", "file_dialog" };

    /// <summary>The result property that names the dialog respond_to_dialog or file_dialog found.</summary>
    private const string DialogHandleProperty = "dialogHandle";

    /// <summary>element_action's result properties kept as identifiers: its method and status.</summary>
    private static readonly string[] ElementActionIdentifiers = ["method", "status"];

    private const string TypeTextTool = "type_text";

    /// <summary>type_text's result property kept as an identifier: the method it used (roadmap M4.4).</summary>
    private const string TypeTextMethodProperty = "method";

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
        var items = Items(body, call) ?? RequestedWindows(call);

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
            SummarizeArguments(call.Arguments, call.DeclaredTypes, call.Tool),
            body == null ? null : Scalars(body, call.Tool),
            serverPid,
            call.RequestId,
            body != null && body.TryGetPropertyValue("dropWindow", out var drop) && drop is JsonObject dropWindow ? Window(dropWindow) : null);
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
    /// <item>A string in <see cref="NamedStringArguments"/> is kept (<c>executable</c>, and browser_navigate's <c>url</c>,
    /// redacted, with <c>executableLength</c> when something was cut, and always with <c>urlLength</c>), unless it is content for <paramref name="tool"/>
    /// (<see cref="ContentArguments"/>); any other string is recorded as <c>&lt;name&gt;Length</c>.</item>
    /// <item>An array is recorded as <c>&lt;name&gt;Count</c>; objects and nulls are left out.</item>
    /// </list>
    /// Null when nothing is left.
    /// </summary>
    /// <param name="arguments">The call's arguments.</param>
    /// <param name="declaredTypes">The JSON types the tool declares per parameter (<see cref="DeclaredTypes"/>); null when unknown.</param>
    /// <param name="tool">The tool called, for <see cref="ContentArguments"/>; null keeps every name of <see cref="NamedStringArguments"/>.</param>
    public static JsonObject? SummarizeArguments(
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? declaredTypes,
        string? tool = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var content = tool != null && ContentArguments.TryGetValue(tool, out var names) ? names : null;
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
                case JsonValueKind.String when name == "url" && tool == NavigatedUrlTool:
                    var url = value.GetString()!;
                    summary[name] = RedactNavigatedUrl(url);
                    summary[name + "Length"] = url.Length;
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
                case JsonValueKind.String when NamedStringArguments.Contains(name) && content?.Contains(name) != true:
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

    /// <summary>
    /// browser_navigate's url as it is recorded (roadmap M5.5): a web page's scheme, host and port
    /// (<c>https://accounts.example.com</c>), about:blank as it is, and any other URL as its scheme alone (<c>file:</c>,
    /// <c>javascript:</c>), or <c>invalid</c> without one. The tool refuses those, but the call is recorded all the same, and
    /// unlike a launched program's path, a local file's path or name can say what the user works on.
    /// </summary>
    public static string RedactNavigatedUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase))
            return "about:blank";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" && !uri.IsFile && !uri.IsUnc
                ? Cut(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped), MaxTitleLength)
                : Cut(uri.Scheme.ToLowerInvariant() + ":", MaxTitleLength);
        }

        var colon = url.IndexOf(':');
        var scheme = colon > 0 ? url[..colon] : "";
        return scheme.Length > 0 && char.IsAsciiLetter(scheme[0]) && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            ? Cut(scheme.ToLowerInvariant() + ":", MaxTitleLength)
            : "invalid";
    }

    private static AuditTarget? Target(AuditCall call, JsonObject? body)
    {
        // The dialog respond_to_dialog or file_dialog found (their results name it dialogHandle; 0 when none was found) is the
        // target, also when the arguments named its owner, a selector or no window at all: the dialog services note it
        // before they act on it (AuditScope), so its title and process are known
        if (body != null && DialogTools.Contains(call.Tool) && Long(body, DialogHandleProperty) is { } dialog and not 0)
        {
            return Before(call, dialog) ?? new AuditTarget(Handle: dialog);
        }

        // What the arguments aimed at, read before the call: a window, else a process, else a program to launch
        AuditTarget? target = null;
        var handles = WindowHandles(call.Arguments);
        if (handles.Count > 0)
        {
            target = Before(call, handles[0]) ?? new AuditTarget(Handle: handles[0]);
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
        // window selector resolved to (roadmap M2.4: the window tools report it as handle), with the title and process the
        // tool noted when it resolved it (AuditScope): a closed window cannot be read afterwards
        var resultHandle = Long(body, "windowHandle") ?? (handles.Count == 0 ? Long(body, "handle") : null);
        var resultProcessId = Int(body, "processId");
        var resultProcess = String(body, "processName");
        if (target == null && resultHandle == null && resultProcessId == null)
        {
            return null;
        }

        target ??= resultHandle is { } resolved && Before(call, resolved) is { } noted ? noted : new AuditTarget();
        return target with
        {
            Handle = target.Handle ?? resultHandle,
            ProcessId = target.ProcessId ?? resultProcessId,
            Process = target.Process ?? resultProcess,
            Placement = Placement(call.Arguments.TryGetValue(PlacementProperty, out var asked) ? asked : null, body)
        };
    }

    /// <summary>The placement argument of a launch tool and of each launch batch item, and its result (roadmap M3.13).</summary>
    private const string PlacementProperty = "placement";

    /// <summary>
    /// A launch's placement (roadmap M3.13), default-deny: of the arguments only snap and state that are valid values (as
    /// their canonical names), monitorIndex, the bounds' numbers and focus; of the result (<paramref name="owner"/>'s
    /// placement) the final bounds, a valid final state, focused, reapplied and the errorCode, never the error text. Null
    /// when neither has one.
    /// </summary>
    private static AuditPlacement? Placement(JsonElement? asked, JsonObject? owner)
    {
        var done = owner != null && owner.TryGetPropertyValue(PlacementProperty, out var node) ? node as JsonObject : null;
        var request = asked is { ValueKind: JsonValueKind.Object } a ? a : (JsonElement?)null;
        if (request == null && done == null)
        {
            return null;
        }

        string? snap = null;
        string? state = null;
        int? monitorIndex = null;
        BoundsDto? bounds = null;
        bool? focus = null;
        if (request is { } r)
        {
            snap = r.TryGetProperty("snap", out var s) && s.ValueKind == JsonValueKind.String &&
                   ToolEnum<SnapPosition>.TryParse(s.GetString(), out var position, out _) ? ToolEnum<SnapPosition>.Name(position) : null;
            state = r.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String &&
                    ToolEnum<LaunchWindowState>.TryParse(st.GetString(), out var parsed, out _) ? ToolEnum<LaunchWindowState>.Name(parsed) : null;
            monitorIndex = r.TryGetProperty("monitorIndex", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var index)
                ? index
                : null;
            bounds = r.TryGetProperty("bounds", out var b) ? BoundsIn(b) : null;
            focus = r.TryGetProperty("focus", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False ? f.GetBoolean() : null;
        }

        var finalBounds = done != null && done.TryGetPropertyValue("finalBounds", out var fb) && fb is JsonObject box &&
                          Int(box, "x") is { } x && Int(box, "y") is { } y && Int(box, "width") is { } w && Int(box, "height") is { } h
            ? new BoundsDto(x, y, w, h)
            : null;
        var finalState = done == null ? null : Identifier(done, "finalState") is { } fs && Enum.TryParse<WindowState>(fs, true, out _) ? fs : null;
        return new AuditPlacement(snap, monitorIndex, bounds, state, focus, finalBounds, finalState,
            done == null ? null : Bool(done, "focused"), done == null ? null : Bool(done, "reapplied"),
            done == null ? null : Identifier(done, "errorCode"));

        static BoundsDto? BoundsIn(JsonElement value) =>
            value.ValueKind == JsonValueKind.Object && Number(value, "x") is { } x && Number(value, "y") is { } y &&
            Number(value, "width") is { } w && Number(value, "height") is { } h
                ? new BoundsDto(x, y, w, h)
                : null;

        static int? Number(JsonElement value, string name) =>
            value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var result)
                ? result
                : null;
    }

    /// <summary>
    /// The per-item arrays of the results: a batch's <c>results</c>, close_application's <c>windows</c> and apply_layout's
    /// <c>placements</c>, in that order of preference.
    /// </summary>
    private static readonly string[] ItemArrays = ["results", "windows", "placements"];

    /// <summary>
    /// The items of the result's per-item array (<see cref="ItemArrays"/>): each window or launch with its outcome, and
    /// the window's title and process as they were before the call. Null when the result has no such array, or an empty
    /// one (a call refused before it listed anything: its target is recorded instead).
    /// </summary>
    private static IReadOnlyList<AuditItem>? Items(JsonObject? body, AuditCall call)
    {
        var results = body == null
            ? null
            : ItemArrays.Select(name => body.TryGetPropertyValue(name, out var node) ? node as JsonArray : null).FirstOrDefault(a => a != null);
        if (results is not { Count: > 0 })
        {
            return null;
        }

        // A launch batch's items, in request order like its results, each with its placement argument (roadmap M3.13)
        var requested = call.Arguments.TryGetValue("applications", out var applications) && applications.ValueKind == JsonValueKind.Array
            ? applications.EnumerateArray().ToList()
            : [];

        return results
            .Select((node, index) => (Item: node as JsonObject, Index: index))
            .Where(t => t.Item != null)
            .Select(t =>
            {
                var item = t.Item!;
                JsonElement? asked = t.Index < requested.Count && requested[t.Index] is { ValueKind: JsonValueKind.Object } request &&
                                     request.TryGetProperty(PlacementProperty, out var argument)
                    ? argument
                    : null;
                var placement = Placement(asked, item);
                // A launch batch item and an apply_layout placement name their window in a window object
                var window = item.TryGetPropertyValue("window", out var node) ? node as JsonObject : null;
                var handle = Long(item, "handle") ?? Long(item, "windowHandle") ?? (window == null ? null : Long(window, "handle"));
                var before = handle is { } h ? Before(call, h) : null;
                var title = before?.Title ?? String(item, "title") ?? (window == null ? null : String(window, "title"));
                var executable = String(item, ExecutableArgument) ?? String(item, "launchCommand");
                return new AuditItem(
                    handle,
                    title == null ? null : Cut(title, MaxTitleLength),
                    before?.Process ?? String(item, "processName") ?? (window == null ? null : String(window, "processName")),
                    before?.ProcessId ?? Int(item, "processId") ?? (window == null ? null : Int(window, "processId")),
                    executable == null ? null : RedactExecutable(executable),
                    Bool(item, "success"),
                    Identifier(item, "errorCode"),
                    Identifier(item, "status") ?? Identifier(item, LaunchStatusProperty),
                    Bool(item, "launched"),
                    placement);
            })
            .ToList();
    }

    /// <summary>
    /// The window <paramref name="handle"/> as it was before the tool acted on it: as Begin read it, else as the tool noted
    /// it (<see cref="AuditScope"/>), else the handle Begin could not read. Null when neither knows it.
    /// </summary>
    private static AuditTarget? Before(AuditCall call, long handle)
    {
        call.Windows.TryGetValue(handle, out var read);
        if (read is { Title: not null } or { Process: not null })
        {
            return read;
        }

        return call.ResolvedWindows.TryGetValue(handle, out var noted) ? noted : read;
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
            .Select(handle => Before(call, handle) is { } before
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
    private static JsonObject? Scalars(JsonObject body, string tool)
    {
        var scalars = new JsonObject();

        // element_action (roadmap M3.6): how it acted (a pattern, or the mouse or keyboard path) and whether it left a dialog
        // pending; fixed identifiers, never content
        if (tool == ElementActionTool)
        {
            foreach (var property in ElementActionIdentifiers)
            {
                if (Identifier(body, property) is { } identifier)
                {
                    scalars[property] = identifier;
                }
            }
        }

        // type_text (roadmap M4.4): whether it typed or pasted, a fixed identifier
        if (tool == TypeTextTool && Identifier(body, TypeTextMethodProperty) is { } method)
        {
            scalars[TypeTextMethodProperty] = method;
        }

        // get_clipboard's origin (user, setClipboard, agentInput): whose clipboard content the agent read, never the content
        if (Identifier(body, ClipboardOriginProperty) is { } origin)
        {
            scalars[ClipboardOriginProperty] = origin;
        }

        // A launch's launchStatus (roadmap M3.13): whether it started something or reused an open window (alreadyOpen)
        if (Identifier(body, LaunchStatusProperty) is { } launchStatus)
        {
            scalars[LaunchStatusProperty] = launchStatus;
        }

        foreach (var (name, value) in body)
        {
            if (tool == ScriptValueTool && name == ScriptValueProperty)
            {
                continue;
            }

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

    private static bool? Bool(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : null;

    private static int? Int(JsonObject node, string name) => Long(node, name) is { } value and >= int.MinValue and <= int.MaxValue ? (int)value : null;

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>An argument name the log can hold: ASCII letters, digits and underscores, at most 64 characters.</summary>
    /// <summary>Whether <paramref name="name"/> looks like a parameter name: 1-64 ASCII letters, digits and underscores.</summary>
    internal static bool IsIdentifier(string name) =>
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
