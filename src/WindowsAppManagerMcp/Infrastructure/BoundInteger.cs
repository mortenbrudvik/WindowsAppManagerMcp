using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// An integer argument read as the tools bind it, for the checks that read an argument before its tool does: run_steps'
/// one-window rule (<see cref="StepRunner"/>) and the audit log's targets (<see cref="AuditRecord"/>).
/// </summary>
/// <remarks>
/// The tools bind their arguments with <see cref="ToolJson.SerializerOptions"/>, a copy of the SDK's
/// McpJsonUtilities.DefaultOptions, whose NumberHandling is AllowReadingFromString: a tool given <c>"handle": "123"</c>
/// acts on window 123, as with <c>123</c>. A check that took only JSON numbers would not see the window such a call acts
/// on, so run_steps could act outside its window and the audit line would not name the window closed. The server keeps
/// the SDK's handling rather than refuse strings: clients that write large numbers as strings keep working. So the checks
/// read the value with the binder itself, never with a parser of their own, and see exactly what the tool will
/// (NestedArgumentContractTests pins the agreement).
/// </remarks>
public static class BoundInteger
{
    /// <summary>
    /// The number a tool's integer parameter binds <paramref name="value"/> to: a JSON number with a whole value in the
    /// range of a long, or a JSON string that holds one as the serializer reads it (digits, with an optional sign). Null
    /// for anything else (a fraction, an exponent, spaces, a boolean, an object), which such a parameter does not bind.
    /// </summary>
    public static long? Read(JsonElement value) =>
        value.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? Bind(() => value.Deserialize<long?>(ToolJson.SerializerOptions))
            : null;

    /// <summary>As <see cref="Read(JsonElement)"/>, for a value of a <see cref="JsonObject"/> (a run_steps step's arguments).</summary>
    public static long? Read(JsonNode? value) =>
        value is JsonValue && value.GetValueKind() is JsonValueKind.Number or JsonValueKind.String
            ? Bind(() => value.Deserialize<long?>(ToolJson.SerializerOptions))
            : null;

    private static long? Bind(Func<long?> bind)
    {
        try
        {
            return bind();
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            // Not a number the tool would bind: its call fails binding, and it names nothing
            return null;
        }
    }
}
