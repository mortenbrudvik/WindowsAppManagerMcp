using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// A tool argument that cannot be bound because its value is not allowed, such as an unknown window state. Thrown
/// while the SDK binds the call's arguments, before the tool runs; <see cref="ToolErrorReporting"/> reports it as an
/// error result (isError) with <see cref="ErrorCode"/> and the message, which lists the allowed values.
/// </summary>
/// <remarks>
/// It derives from <see cref="JsonException"/>, the exception System.Text.Json passes on from a converter without
/// wrapping it, and the message is the converter's own: System.Text.Json adds no path to it.
/// </remarks>
public sealed class ToolArgumentException : JsonException
{
    public ToolArgumentException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>The errorCode reported to the agent, for example InvalidState.</summary>
    public string ErrorCode { get; }
}

/// <summary>
/// Binds the <see cref="ToolEnumAttribute"/> enums of tool arguments from JSON strings (roadmap M2.2): a canonical
/// name, an alias or the member name, ignoring case, spaces, '_' and '-'. Anything else, numbers included, throws a
/// <see cref="ToolArgumentException"/> with the enum's errorCode and the allowed values. Writes the canonical name.
/// </summary>
public sealed class ToolEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => ToolEnumInfo.IsToolEnum(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    private sealed class Converter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly ToolEnumInfo _info = ToolEnumInfo.Get(typeof(T));

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (_info.TryParse(text, out var value))
                {
                    return (T)value!;
                }

                throw new ToolArgumentException(_info.ErrorCode, _info.InvalidMessage(text));
            }

            // A number or any other JSON value: never an index into the enum
            using var document = JsonDocument.ParseValue(ref reader);
            throw new ToolArgumentException(_info.ErrorCode, _info.InvalidMessage(document.RootElement.GetRawText()));
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(_info.Name(value));
    }
}
