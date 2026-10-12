using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Reads the JSON text block of a tool that returns <see cref="CallToolResult"/> (an image block may come first) back into its
/// payload type, as the server serializes it (<see cref="ToolJson.SerializerOptions"/>), so a unit test can check its fields.
/// </summary>
public static class ToolResultPayloads
{
    /// <summary>The payload of <paramref name="result"/>'s one text block.</summary>
    public static T Payload<T>(this CallToolResult result) =>
        JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().Single().Text, ToolJson.SerializerOptions)
        ?? throw new InvalidOperationException("The text block holds JSON null.");

    /// <summary>The payload of the result <paramref name="call"/> returns.</summary>
    public static async Task<T> Payload<T>(this Task<CallToolResult> call) => (await call.ConfigureAwait(false)).Payload<T>();
}
