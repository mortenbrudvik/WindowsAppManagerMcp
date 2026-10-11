using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// The error contract of every tool (roadmap M2.1): a call whose result says <c>"success": false</c> goes out with
/// isError true, with the tool's own JSON body unchanged (its error and errorCode). The tools keep returning their
/// typed results; <see cref="ToolErrorReporting"/>, the outermost call filter, applies <see cref="WithIsError"/> to
/// every result, so a new tool cannot forget it.
/// </summary>
/// <remarks>
/// Only a top-level <c>success</c> property counts. A list, or a batch summary whose items failed, is data and keeps
/// isError unset: the call itself worked, and each item says what happened to it. A tool that sets isError itself
/// (the captures, through <see cref="ToolImages"/>) keeps its value.
/// </remarks>
public static class ToolOutcome
{
    /// <summary>The result property that carries a tool's outcome.</summary>
    public const string SuccessProperty = "success";

    /// <summary>
    /// True when a text block of <paramref name="result"/> is a JSON object whose top-level
    /// <see cref="SuccessProperty"/> is the boolean false.
    /// </summary>
    public static bool ReportsFailure(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            if (block.Text is not { Length: > 0 } text || text[0] != '{')
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty(SuccessProperty, out var success))
                {
                    return success.ValueKind == JsonValueKind.False;
                }
            }
            catch (JsonException)
            {
                // Not JSON: plain text reports no outcome
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="result"/> with isError set when it reports a failure (<see cref="ReportsFailure"/>). An isError
    /// the tool set itself is kept.
    /// </summary>
    public static CallToolResult WithIsError(CallToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsError != true && ReportsFailure(result))
        {
            result.IsError = true;
        }

        return result;
    }
}
