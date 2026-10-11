using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Marks an enum as a tool argument type (roadmap M2.2): the tools' input schemas list its values, and an unknown value
/// is rejected with <see cref="ErrorCode"/> and a message that lists the allowed values, instead of falling back to a
/// default. Each member's canonical name is its <see cref="JsonStringEnumMemberNameAttribute"/>; members can add
/// aliases with <see cref="ToolValueAliasAttribute"/>. Parsing ignores case, spaces, '_' and '-', so "Left-Half" is
/// left_half, and never accepts numbers.
/// </summary>
/// <param name="errorCode">The errorCode of an unknown value, for example nameof(WindowErrorCode.InvalidState).</param>
/// <param name="noun">What a value names, for the error message: "window state" gives "'hide' is not a valid window state".</param>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class ToolEnumAttribute(string errorCode, string noun) : Attribute
{
    /// <summary>The errorCode of an unknown value.</summary>
    public string ErrorCode { get; } = errorCode;

    /// <summary>What a value names, for the error message.</summary>
    public string Noun { get; } = noun;

    /// <summary>
    /// The canonical name of the member a JSON null binds to, for an optional parameter whose earlier string version took
    /// null as "use the default" (format = null meant auto). Null (the default): a JSON null is refused like an unknown value.
    /// </summary>
    public string? NullMeans { get; init; }
}

/// <summary>
/// Another name a <see cref="ToolEnumAttribute"/> enum member is accepted under, such as 'minimized' for minimize.
/// A listed alias appears in the input schema next to the canonical names (documented aliases, so a client that checks
/// arguments against the schema accepts them too); an unlisted one is only accepted.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class ToolValueAliasAttribute(string alias) : Attribute
{
    /// <summary>The alias.</summary>
    public string Alias { get; } = alias;

    /// <summary>Whether the input schema lists the alias. Default: true.</summary>
    public bool Listed { get; init; } = true;
}

/// <summary>
/// A string property or parameter that takes the values of a <see cref="ToolEnumAttribute"/> enum but is checked by
/// the tool itself, such as a batch item's field: one bad item must fail alone, not the whole call (roadmap M2.1). The
/// input schema lists the enum's values, as for a parameter of the enum type.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ToolValuesAttribute(Type enumType) : Attribute
{
    /// <summary>The <see cref="ToolEnumAttribute"/> enum whose values the string takes.</summary>
    public Type EnumType { get; } = enumType;
}

/// <summary>
/// The names, aliases and error of a <see cref="ToolEnumAttribute"/> enum, read once per type. Used by the JSON
/// converter that binds tool arguments, by the input schema, and by tools that check a string themselves.
/// </summary>
public sealed class ToolEnumInfo
{
    private static readonly ConcurrentDictionary<Type, ToolEnumInfo> Cache = new();

    private readonly Dictionary<string, object> _byKey;
    private readonly Dictionary<object, string> _names;

    private ToolEnumInfo(Type enumType)
    {
        var attribute = enumType.GetCustomAttribute<ToolEnumAttribute>()
            ?? throw new ArgumentException($"{enumType.Name} has no [{nameof(ToolEnumAttribute)}].", nameof(enumType));

        EnumType = enumType;
        ErrorCode = attribute.ErrorCode;
        Noun = attribute.Noun;
        _byKey = new Dictionary<string, object>(StringComparer.Ordinal);
        _names = [];

        var canonical = new List<string>();
        var listedAliases = new List<string>();
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = field.GetValue(null)!;
            var name = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                ?? throw new ArgumentException($"{enumType.Name}.{field.Name} has no [JsonStringEnumMemberName].", nameof(enumType));
            _names.Add(value, name);
            canonical.Add(name);
            Add(name, value);
            Add(field.Name, value);

            foreach (var alias in field.GetCustomAttributes<ToolValueAliasAttribute>())
            {
                Add(alias.Alias, value);
                if (alias.Listed)
                {
                    listedAliases.Add(alias.Alias);
                }
            }
        }

        if (attribute.NullMeans is { } nullMeans)
        {
            NullValue = _byKey.TryGetValue(Key(nullMeans), out var nullValue)
                ? nullValue
                : throw new ArgumentException($"{enumType.Name}: NullMeans '{nullMeans}' names no member.", nameof(enumType));
        }

        Names = canonical;
        Aliases = listedAliases;
        SchemaValues = [.. canonical, .. listedAliases];
        AllowedValues = string.Join(", ", canonical) +
                        (listedAliases.Count > 0 ? $" (also accepted: {string.Join(", ", listedAliases)})" : string.Empty);

        void Add(string text, object value)
        {
            var key = Key(text);
            if (_byKey.TryGetValue(key, out var existing) && !existing.Equals(value))
            {
                throw new ArgumentException($"{enumType.Name}: '{text}' names both {existing} and {value}.", nameof(enumType));
            }

            _byKey[key] = value;
        }
    }

    /// <summary>The enum type.</summary>
    public Type EnumType { get; }

    /// <summary>The errorCode of an unknown value.</summary>
    public string ErrorCode { get; }

    /// <summary>What a value names, for the error message.</summary>
    public string Noun { get; }

    /// <summary>The canonical names, in declaration order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The listed aliases, in declaration order.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>The values the input schema lists: the canonical names, then the listed aliases.</summary>
    public IReadOnlyList<string> SchemaValues { get; }

    /// <summary>The allowed values as the error message lists them: "a, b, c (also accepted: d)".</summary>
    public string AllowedValues { get; }

    /// <summary>The member a JSON null binds to (<see cref="ToolEnumAttribute.NullMeans"/>), or null when null is refused.</summary>
    public object? NullValue { get; }

    /// <summary>True when <paramref name="type"/> is a <see cref="ToolEnumAttribute"/> enum.</summary>
    public static bool IsToolEnum(Type type) => type.IsEnum && type.IsDefined(typeof(ToolEnumAttribute), inherit: false);

    /// <summary>The info of a <see cref="ToolEnumAttribute"/> enum.</summary>
    /// <exception cref="ArgumentException">The type is not such an enum, or two of its members share a name.</exception>
    public static ToolEnumInfo Get(Type enumType) => Cache.GetOrAdd(enumType, static type => new ToolEnumInfo(type));

    /// <summary>The canonical name of <paramref name="value"/>.</summary>
    public string Name(object value) =>
        _names.TryGetValue(value, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a {EnumType.Name} member.");

    /// <summary>
    /// Finds the member named by <paramref name="text"/>: a canonical name, an alias or the member's own name, ignoring
    /// case, spaces, '_' and '-'. Numbers are never accepted.
    /// </summary>
    public bool TryParse(string? text, out object? value)
    {
        value = null;
        return !string.IsNullOrWhiteSpace(text) && _byKey.TryGetValue(Key(text), out value);
    }

    /// <summary>The error message for <paramref name="text"/>, which is not a valid value (null: the value is missing).</summary>
    public string InvalidMessage(string? text) =>
        (text == null ? $"No {Noun} was given." : $"'{text}' is not a valid {Noun}.") + $" Use one of: {AllowedValues}.";

    private static string Key(string text) =>
        string.Concat(text.Where(c => c is not ('_' or '-') && !char.IsWhiteSpace(c))).ToLowerInvariant();
}

/// <summary>Typed access to <see cref="ToolEnumInfo"/>.</summary>
public static class ToolEnum<T> where T : struct, Enum
{
    private static readonly ToolEnumInfo Info = ToolEnumInfo.Get(typeof(T));

    /// <summary>The errorCode of an unknown value.</summary>
    public static string ErrorCode => Info.ErrorCode;

    /// <summary>The canonical name of <paramref name="value"/>, as the input schema and results show it.</summary>
    public static string Name(T value) => Info.Name(value);

    /// <summary>
    /// Parses <paramref name="text"/> (see <see cref="ToolEnumInfo.TryParse"/>). On failure <paramref name="error"/>
    /// says why and lists the allowed values.
    /// </summary>
    public static bool TryParse(string? text, out T value, out string? error)
    {
        if (Info.TryParse(text, out var parsed))
        {
            value = (T)parsed!;
            error = null;
            return true;
        }

        value = default;
        error = Info.InvalidMessage(text);
        return false;
    }
}
