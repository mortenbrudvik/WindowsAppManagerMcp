using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// press_keys' keys (roadmap M4.3), as the call gave them: a string ('ctrl+shift+t', 'tab*3', 'ctrl+a delete') or an array
/// of strings (['ctrl','shift','t'] for one chord, ['ctrl+a','delete'] for a sequence). The input schema offers both
/// (ToolJson writes <c>"type": ["string", "array"]</c> for it); anything else fails to bind.
/// </summary>
/// <param name="Items">The string, or the array's items.</param>
/// <param name="IsArray">True when the call passed an array.</param>
[JsonConverter(typeof(KeyListConverter))]
public sealed record KeyList(IReadOnlyList<string> Items, bool IsArray)
{
    public static KeyList Of(string keys) => new([keys], IsArray: false);

    public static KeyList Of(IReadOnlyList<string> keys) => new(keys, IsArray: true);

    /// <summary>
    /// True when the array is one chord: every item but the last names a modifier, and no item is itself a chord, a
    /// repeat or a sequence (no '+', '*', ',' or space). ['ctrl','shift','t'] is one; ['tab','tab'] and ['ctrl+a','delete']
    /// are sequences.
    /// </summary>
    public bool IsOneChord => IsArray && Items.Count > 0 &&
        Items.All(item => item.Length > 0 && item.IndexOfAny(['+', '*', ',', ' ']) < 0) &&
        Items.Take(Items.Count - 1).All(item => Services.PressKeysParser.IsModifierName(item));

    /// <summary>The keys as one string, for the result: the chord joined with '+', a sequence with spaces.</summary>
    public string Text => !IsArray ? Items[0] : string.Join(IsOneChord ? "+" : " ", Items);

    public bool Equals(KeyList? other) => other != null && IsArray == other.IsArray && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(IsArray, Items.Count);
}

/// <summary>Reads a <see cref="KeyList"/> from a JSON string or an array of strings, and writes it back as given.</summary>
public sealed class KeyListConverter : JsonConverter<KeyList>
{
    public override KeyList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return KeyList.Of(reader.GetString()!);

            case JsonTokenType.StartArray:
                var items = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException("keys takes a string, such as 'ctrl+shift+t', or an array of strings, such as ['ctrl','shift','t'].");
                    }

                    items.Add(reader.GetString()!);
                }

                return KeyList.Of(items);

            default:
                throw new JsonException("keys takes a string, such as 'ctrl+shift+t', or an array of strings, such as ['ctrl','shift','t'].");
        }
    }

    public override void Write(Utf8JsonWriter writer, KeyList value, JsonSerializerOptions options)
    {
        if (!value.IsArray)
        {
            writer.WriteStringValue(value.Items[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value.Items)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
