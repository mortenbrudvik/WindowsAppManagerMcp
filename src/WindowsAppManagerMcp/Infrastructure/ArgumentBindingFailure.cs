using System.Globalization;
using System.Reflection;
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
/// something at that path, or, for the argument as a whole (<c>$</c>), the only one that could have failed so: its schema
/// declares the JSON type the binder was reading, and its value is not one of that type the binder reads (a whole number out
/// of the parameter's range counts, since System.Text.Json reports it as it reports a wrong type). Otherwise the description
/// says "an argument", with the path.
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

            // Only an object lacks members: as a whole, it is the only object given to an object parameter
            var whole = path.Count == 0
                ? WholeArgument(given, declaredTypes, "object", (value, _) => value.ValueKind == JsonValueKind.Object)
                : null;
            return path.Count == 0
                ? $"{whole ?? "An argument"} lacks a required member: {members}."
                : Locate(given, path) is { } holder
                    ? $"{Display(holder, path)} lacks a required member: {members}."
                    : $"The object at {Display("$", path)} of an argument lacks a required member: {members}.";
        }

        if (NotConverted().Match(text) is { Success: true } converted && path != null)
        {
            if (path.Count == 0)
            {
                // The argument as a whole: the type named is then the parameter's own
                var type = converted.Groups["type"].Value;
                var kind = KindOf(type);
                var range = IntegerRangeOf(type);
                var argument = WholeArgument(given, declaredTypes, SchemaTypeOf(kind),
                    (value, types) => !Fits(value, types) || range is { } r && OutOf(r, value));
                if (range is { } bounds)
                {
                    // System.Text.Json says the same for 3000000000 as for "x" when it reads an int
                    var outOfRange = argument == null
                        ? given.Any(a => OutOf(bounds, a.Value))
                        : OutOf(bounds, given.First(a => a.Key == argument).Value);
                    return (argument, outOfRange) switch
                    {
                        (null, true) => "An argument is not an integer, or is out of range.",
                        (null, false) => "An argument is not an integer.",
                        (_, true) => $"{argument} is out of range: it takes a whole number from " +
                                     $"{bounds.Min.ToString(CultureInfo.InvariantCulture)} to {bounds.Max.ToString(CultureInfo.InvariantCulture)}.",
                        _ => $"{argument} is not an integer."
                    };
                }

                return kind != null
                    ? $"{argument ?? "An argument"} is not {kind}."
                    : $"{argument ?? "An argument"} does not have the type the input schema gives it.";
            }

            // Inside an argument, the place, and every value there (one when the argument is known)
            var where = Locate(given, path) is { } holder
                ? Display(holder, path)
                : $"The value at {Display("$", path)} of an argument";
            var values = given.Select(a => ValueAt(a.Value, path)).OfType<JsonElement>().ToList();
            switch (MemberIntegerRange(converted.Groups["type"].Value, path))
            {
                case { Known: true, Range: { } bounds } when values.Count > 0 && values.All(value => OutOf(bounds, value)):
                    return $"{where} is out of range: it takes a whole number from {bounds.Min.ToString(CultureInfo.InvariantCulture)} " +
                           $"to {bounds.Max.ToString(CultureInfo.InvariantCulture)}.";
                case { Known: false } when values.Any(IsWholeNumber):
                    // The member's type could not be told, so a whole number there may be out of its range rather than of a
                    // wrong type
                    return $"{where} does not have the type the input schema gives it, or is out of range.";
                default:
                    return $"{where} does not have the type the input schema gives it.";
            }
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
    /// The argument that failed as a whole: the only one, or the only one that could have failed so, whose schema declares
    /// <paramref name="schemaType"/> (the JSON type of what the binder was reading) and whose value <paramref name="failed"/>
    /// says the binder cannot read as that; null when that cannot be told, also when the binder's type has no JSON type.
    /// </summary>
    private static string? WholeArgument(
        List<KeyValuePair<string, JsonElement>> arguments, IReadOnlyDictionary<string, IReadOnlySet<string>>? declaredTypes,
        string? schemaType, Func<JsonElement, IReadOnlySet<string>, bool> failed)
    {
        if (arguments.Count == 1)
        {
            return arguments[0].Key;
        }

        if (declaredTypes == null || schemaType == null)
        {
            return null;
        }

        var candidates = arguments
            .Where(argument => declaredTypes.TryGetValue(argument.Key, out var types) && types.Contains(schemaType) &&
                               failed(argument.Value, types))
            .Select(argument => argument.Key)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>The JSON Schema type of what <see cref="KindOf"/> names; null for none.</summary>
    private static string? SchemaTypeOf(string? kind) => kind switch
    {
        "an integer" => "integer",
        "a number" => "number",
        "true or false" => "boolean",
        "a string" => "string",
        "an array" => "array",
        "an object" => "object",
        _ => null
    };

    /// <summary>The values of the integer CLR type System.Text.Json names; null for a type that is no integer.</summary>
    private static (decimal Min, decimal Max)? IntegerRangeOf(string clrType)
    {
        var type = clrType.Trim();
        if (NullableOf().Match(type) is { Success: true } nullable)
        {
            type = nullable.Groups["type"].Value;
        }

        return type switch
        {
            "System.Byte" => (byte.MinValue, byte.MaxValue),
            "System.SByte" => (sbyte.MinValue, sbyte.MaxValue),
            "System.Int16" => (short.MinValue, short.MaxValue),
            "System.UInt16" => (ushort.MinValue, ushort.MaxValue),
            "System.Int32" => (int.MinValue, int.MaxValue),
            "System.UInt32" => (uint.MinValue, uint.MaxValue),
            "System.Int64" or "System.IntPtr" => (long.MinValue, long.MaxValue),
            "System.UInt64" => (ulong.MinValue, ulong.MaxValue),
            _ => null
        };
    }

    /// <summary>
    /// The range of the integer member at <paramref name="path"/> that System.Text.Json could not read, from the type its
    /// message names: that type itself, or, as it names the object around the value for a constructor parameter (a record's
    /// member), the type of the member the path ends with. Known is false when the type cannot be found; Range is null for a
    /// member that is no integer.
    /// </summary>
    private static (bool Known, (decimal Min, decimal Max)? Range) MemberIntegerRange(string clrType, IReadOnlyList<Segment> path)
    {
        if (IntegerRangeOf(clrType) is { } own)
        {
            return (true, own);
        }

        var name = NullableOf().Match(clrType.Trim()) is { Success: true } nullable ? nullable.Groups["type"].Value : clrType.Trim();
        if (FindType(name) is not { } type)
        {
            return (false, null);
        }

        var member = path[^1].Name is { } memberName
            ? type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(property => string.Equals(property.Name, memberName, StringComparison.OrdinalIgnoreCase))
            : null;
        return (true, member == null ? null : IntegerRangeOf(member.PropertyType));
    }

    /// <summary>A type System.Text.Json names, in the server or another loaded assembly; null when none has it.</summary>
    private static Type? FindType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Prepend(typeof(ToolErrorReporting).Assembly))
        {
            try
            {
                if (assembly.GetType(name, throwOnError: false) is { } type)
                {
                    return type;
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or BadImageFormatException)
            {
                // Not a name this assembly can look up: the next one may
            }
        }

        return null;
    }

    /// <summary>The values of an integer type; null for a type that is no integer.</summary>
    private static (decimal Min, decimal Max)? IntegerRangeOf(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) is var t && t.IsPrimitive && t != typeof(bool) && t != typeof(char) &&
        t != typeof(float) && t != typeof(double)
            ? IntegerRangeOf(t.FullName ?? string.Empty)
            : null;

    /// <summary>
    /// Whether <paramref name="value"/> is a whole number outside <paramref name="range"/>: a JSON number written as one, or a
    /// string of its digits (which an integer parameter binds, <see cref="BoundInteger"/>).
    /// </summary>
    private static bool OutOf((decimal Min, decimal Max) range, JsonElement value) =>
        WholeNumber(value) is { } number ? number < range.Min || number > range.Max : IsWholeNumber(value);

    /// <summary>Whether <paramref name="value"/> is a whole number: a JSON number written as one, or a string of its digits.</summary>
    private static bool IsWholeNumber(JsonElement value) =>
        WholeNumber(value) != null || value.ValueKind == JsonValueKind.Number && WholeNumberLiteral().IsMatch(value.GetRawText());

    /// <summary>
    /// The whole number <paramref name="value"/> is, as a JSON number written as one or a string of its digits; null for
    /// anything else, and for a JSON number too large for a decimal (which <see cref="IsWholeNumber"/> still counts).
    /// </summary>
    private static decimal? WholeNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when WholeNumberLiteral().IsMatch(value.GetRawText()) && value.TryGetDecimal(out var number) => number,
        JsonValueKind.String when BoundInteger.Read(value) is { } bound => bound,
        _ => null
    };

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

    private static bool Resolves(JsonElement value, IReadOnlyList<Segment> path) => ValueAt(value, path) != null;

    /// <summary>The value at <paramref name="path"/> inside <paramref name="value"/>; null when there is none.</summary>
    private static JsonElement? ValueAt(JsonElement value, IReadOnlyList<Segment> path)
    {
        var current = value;
        foreach (var segment in path)
        {
            if (segment.Index is { } index)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || Member(current, segment.Name!) is not { } member)
            {
                return null;
            }
            else
            {
                current = member;
            }
        }

        return current;
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

    /// <summary>A JSON number written as a whole number: digits, with a minus sign, and no fraction or exponent.</summary>
    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex WholeNumberLiteral();
}
