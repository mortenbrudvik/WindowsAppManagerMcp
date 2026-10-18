using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// What went wrong when the SDK bound a call's arguments to a tool's parameters, said in the agent's terms: the argument and
/// the place inside it (<c>steps[1]</c>, <c>placement.bounds</c>) and what is wrong there, never a .NET type. System.Text.Json's
/// own text names CLR types ("could not be mapped to any .NET member contained in type WindowsAppManagerMcp.Models.RunStep",
/// "could not be converted to System.Nullable`1[System.Int32]"), which mean nothing to an agent that only sees the input
/// schema, and the type it names is sometimes the object around the value rather than the value's own.
/// <see cref="ToolErrorReporting"/> puts the description into its InvalidArguments error.
/// </summary>
/// <remarks>
/// The SDK binds each argument on its own, so System.Text.Json's path (<see cref="JsonException.Path"/>) starts at the argument
/// (<c>$[1].arguments</c>) and does not name it. The argument is named only when it is certain: the only argument that has
/// something at that path, or, for the argument as a whole (<c>$</c>), the only one whose value does not fit the types its
/// input schema declares. Otherwise the description says "an argument", with the path.
/// </remarks>
internal static partial class ArgumentBindingFailure
{
    /// <summary>
    /// The description of <paramref name="exception"/>, an <see cref="ArgumentException"/> or <see cref="JsonException"/> that
    /// <see cref="ToolErrorReporting.IsArgumentBindingFailure"/> attributed to the binder, as one or more sentences.
    /// </summary>
    /// <param name="exception">The binder's exception.</param>
    /// <param name="arguments">The call's arguments, as the client sent them; null when there were none.</param>
    /// <param name="declaredTypes">
    /// The JSON types the tool's input schema declares for each parameter (<see cref="AuditRecord.DeclaredTypes"/>); null when
    /// the schema is not known.
    /// </param>
    public static string Describe(
        Exception exception,
        IEnumerable<KeyValuePair<string, JsonElement>>? arguments,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? declaredTypes)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var given = arguments?.ToList() ?? [];

        if (exception is not JsonException json)
        {
            // AIFunctionFactory: "The arguments dictionary is missing a value for the required parameter 'text'."
            var message = WithoutParameterSuffix(exception);
            return MissingParameter().Match(message) is { Success: true } missing
                ? $"The required argument '{missing.Groups["name"].Value}' is missing."
                : WithoutTypeNames(message, "An argument");
        }

        var path = Parse(json.Path);
        var text = WithoutPathSuffix(json.Message);

        if (UnmappedMember().Match(text) is { Success: true } unmapped && path is { Count: > 0 })
        {
            var name = unmapped.Groups["name"].Value;
            var owner = path.Take(path.Count - 1).ToList();
            return Locate(given, path) is { } argument
                ? $"{Display(argument, owner)} has a member '{name}' that it does not take."
                : owner.Count == 0
                    ? $"An argument has a member '{name}' that it does not take."
                    : $"The object at {Display("$", owner)} of an argument has a member '{name}' that it does not take.";
        }

        if (MissingMembers().Match(text) is { Success: true } lacking && path != null)
        {
            var members = lacking.Groups["names"].Value;
            return path.Count == 0
                ? $"{WholeArgument(given, declaredTypes) ?? "An argument"} lacks a required member: {members}."
                : Locate(given, path) is { } holder
                    ? $"{Display(holder, path)} lacks a required member: {members}."
                    : $"The object at {Display("$", path)} of an argument lacks a required member: {members}.";
        }

        if (NotConverted().Match(text) is { Success: true } converted && path != null)
        {
            if (path.Count == 0)
            {
                // The argument as a whole: the type named is then the parameter's own
                var kind = KindOf(converted.Groups["type"].Value);
                var argument = WholeArgument(given, declaredTypes);
                return kind != null
                    ? $"{argument ?? "An argument"} is not {kind}."
                    : $"{argument ?? "An argument"} does not have the type the input schema gives it.";
            }

            return Locate(given, path) is { } holder
                ? $"{Display(holder, path)} does not have the type the input schema gives it."
                : $"The value at {Display("$", path)} of an argument does not have the type the input schema gives it.";
        }

        // Another message, such as a converter's own ("keys takes a string ..."): passed on with its place, unless it names a
        // CLR type
        var place = path is { Count: > 0 } ? Locate(given, path) is { } at ? Display(at, path) : Display("$", path) : null;
        return place == null ? WithoutTypeNames(text, "An argument")
            : TypeName().IsMatch(text) ? $"The value at {place} does not have the type the input schema gives it."
            : $"{text} (at {place})";
    }

    /// <summary>
    /// The one argument that has something at <paramref name="path"/> (members matched as the binder matches them: exactly,
    /// else in any letter case), or null when none or several do.
    /// </summary>
    private static string? Locate(List<KeyValuePair<string, JsonElement>> arguments, IReadOnlyList<Segment> path)
    {
        var found = arguments.Where(argument => Resolves(argument.Value, path)).Select(argument => argument.Key).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>
    /// The argument that failed as a whole: the only one, or the only one whose value does not fit the JSON types its schema
    /// declares; null when that cannot be told.
    /// </summary>
    private static string? WholeArgument(
        List<KeyValuePair<string, JsonElement>> arguments, IReadOnlyDictionary<string, IReadOnlySet<string>>? declaredTypes)
    {
        if (arguments.Count == 1)
        {
            return arguments[0].Key;
        }

        if (declaredTypes == null)
        {
            return null;
        }

        var unfit = arguments
            .Where(argument => declaredTypes.TryGetValue(argument.Key, out var types) && types.Count > 0 && !Fits(argument.Value, types))
            .Select(argument => argument.Key)
            .Take(2)
            .ToList();
        return unfit.Count == 1 ? unfit[0] : null;
    }

    /// <summary>Whether a value's JSON kind is one of <paramref name="types"/>, as JSON Schema types name them.</summary>
    private static bool Fits(JsonElement value, IReadOnlySet<string> types) => value.ValueKind switch
    {
        JsonValueKind.Object => types.Contains("object"),
        JsonValueKind.Array => types.Contains("array"),
        JsonValueKind.String => types.Contains("string") ||
                                (types.Contains("integer") || types.Contains("number")) && BoundInteger.Read(value) != null,
        JsonValueKind.Number => types.Contains("number") || types.Contains("integer") && value.TryGetInt64(out _),
        JsonValueKind.True or JsonValueKind.False => types.Contains("boolean"),
        JsonValueKind.Null => types.Contains("null"),
        _ => true
    };

    private static bool Resolves(JsonElement value, IReadOnlyList<Segment> path)
    {
        var current = value;
        foreach (var segment in path)
        {
            if (segment.Index is { } index)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || Member(current, segment.Name!) is not { } member)
            {
                return false;
            }
            else
            {
                current = member;
            }
        }

        return true;
    }

    private static JsonElement? Member(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>The place an argument's path names, as the agent writes it: <c>steps[1].args</c>, <c>placement.bounds</c>.</summary>
    private static string Display(string argument, IEnumerable<Segment> path)
    {
        var text = new StringBuilder(argument);
        foreach (var segment in path)
        {
            if (segment.Index is { } index)
            {
                text.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else if (IsIdentifier(segment.Name!))
            {
                text.Append('.').Append(segment.Name);
            }
            else
            {
                text.Append("['").Append(segment.Name).Append("']");
            }
        }

        return text.ToString();
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(name[0]);

    /// <summary>
    /// A System.Text.Json path (<c>$</c>, <c>$.bounds.unit</c>, <c>$[1].arguments</c>, <c>$[0]['weird name']</c>) as its
    /// members and indexes; an empty list for <c>$</c>, and null when it is not such a path.
    /// </summary>
    private static List<Segment>? Parse(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '$')
        {
            return null;
        }

        var segments = new List<Segment>();
        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] != '.' && path[end] != '[')
                {
                    end++;
                }

                segments.Add(new Segment(path[(i + 1)..end], null));
                i = end;
            }
            else if (path.AsSpan(i).StartsWith("['", StringComparison.Ordinal))
            {
                var end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }

                segments.Add(new Segment(path[(i + 2)..end], null));
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1);
                if (end < 0 || !int.TryParse(path.AsSpan(i + 1, end - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    return null;
                }

                segments.Add(new Segment(null, index));
                i = end + 1;
            }
            else
            {
                return null;
            }
        }

        return segments;
    }

    /// <summary>
    /// What a value of the CLR type System.Text.Json names is, in JSON terms ("an integer", "an array"); null for a type that
    /// has no plain JSON name (an enum, which has listed values; an unknown type).
    /// </summary>
    private static string? KindOf(string clrType)
    {
        var type = clrType.Trim();
        if (NullableOf().Match(type) is { Success: true } nullable)
        {
            type = nullable.Groups["type"].Value;
        }

        if (type.EndsWith("[]", StringComparison.Ordinal) || CollectionOf().IsMatch(type))
        {
            return "an array";
        }

        return type switch
        {
            "System.Int16" or "System.Int32" or "System.Int64" or "System.UInt16" or "System.UInt32" or "System.UInt64" or
                "System.Byte" or "System.SByte" or "System.IntPtr" => "an integer",
            "System.Single" or "System.Double" or "System.Decimal" => "a number",
            "System.Boolean" => "true or false",
            "System.String" or "System.Char" => "a string",
            "System.Text.Json.Nodes.JsonObject" => "an object",
            _ => typeof(ToolErrorReporting).Assembly.GetType(type) is { IsEnum: false, IsClass: true } ? "an object" : null
        };
    }

    private static string WithoutParameterSuffix(Exception exception)
    {
        // "... (Parameter 'arguments')" names the binder's own parameter, not the tool's
        var message = exception.Message;
        if (exception is ArgumentException { ParamName: { } paramName })
        {
            var suffix = $" (Parameter '{paramName}')";
            if (message.EndsWith(suffix, StringComparison.Ordinal))
            {
                message = message[..^suffix.Length];
            }
        }

        return message;
    }

    /// <summary>The message without System.Text.Json's " Path: $.x | LineNumber: 0 | BytePositionInLine: 5." (the path is kept apart).</summary>
    private static string WithoutPathSuffix(string message) => PathSuffix().Replace(message, string.Empty).Trim();

    /// <summary>
    /// <paramref name="message"/> as it is, or, when it names a CLR type, that <paramref name="subject"/> does not have the type
    /// the input schema gives it: a .NET type name means nothing to the agent.
    /// </summary>
    private static string WithoutTypeNames(string message, string subject) =>
        TypeName().IsMatch(message) ? $"{subject} does not have the type the input schema gives it." : message;

    private sealed record Segment(string? Name, int? Index);

    [GeneratedRegex(@"missing a value for the required parameter '(?<name>[^']+)'")]
    private static partial Regex MissingParameter();

    [GeneratedRegex(@"^The JSON property '(?<name>.*)' could not be mapped to any \.NET member contained in type '[^']*'\.$", RegexOptions.Singleline)]
    private static partial Regex UnmappedMember();

    [GeneratedRegex(@"was missing required properties,? including: (?<names>.+?)\.?$", RegexOptions.Singleline)]
    private static partial Regex MissingMembers();

    [GeneratedRegex(@"^The JSON value could not be converted to (?<type>.+?)\.$", RegexOptions.Singleline)]
    private static partial Regex NotConverted();

    [GeneratedRegex(@"\s*Path: \S* \| LineNumber: \d+ \| BytePositionInLine: \d+\.?\s*$")]
    private static partial Regex PathSuffix();

    [GeneratedRegex(@"^System\.Nullable`1\[(?<type>.+)\]$")]
    private static partial Regex NullableOf();

    [GeneratedRegex(@"^System\.Collections\.(Generic|ObjectModel)\.\w+`1\[.+\]$")]
    private static partial Regex CollectionOf();

    [GeneratedRegex(@"\b(?:System|Microsoft|WindowsAppManagerMcp|ModelContextProtocol)(?:\.[A-Za-z_][\w`+]*)+")]
    private static partial Regex TypeName();
}
