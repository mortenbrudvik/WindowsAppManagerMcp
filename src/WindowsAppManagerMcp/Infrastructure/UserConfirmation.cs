using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>The user's answer to a confirmation question: confirmed, or why not.</summary>
/// <param name="Confirmed">True when the user accepted and ticked the confirmation.</param>
/// <param name="Reason">Why the action must not go ahead: declined, dismissed, not answered, or not asked; null when confirmed.</param>
public sealed record ConfirmationAnswer(bool Confirmed, string? Reason);

/// <summary>
/// Asks the user, not the model, to confirm an irreversible action (roadmap M2.10): the MCP client shows the question
/// (elicitation) and only a ticked "confirm" from the user lets the action go ahead. kill_process and
/// close_windows_batch use it when the client can ask; otherwise they fall back to their confirm argument.
/// </summary>
/// <remarks>
/// Never ask while holding the input gate: the user may take minutes to answer, and every input and window call would
/// wait for them (<see cref="InputGateToolFilter.SelfGatedTools"/>).
/// </remarks>
public static class UserConfirmation
{
    /// <summary>The name of the yes/no field in the question.</summary>
    public const string ConfirmField = "confirm";

    /// <summary>How long a tool waits for the user's answer by default.</summary>
    public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>True when the client can ask the user: it advertises form elicitation.</summary>
    public static bool CanAsk(McpServer? server) => server?.ClientCapabilities?.Elicitation?.Form != null;

    /// <summary>
    /// Shows <paramref name="message"/> to the user with a "confirm" checkbox labelled <paramref name="title"/>, and
    /// waits up to <paramref name="timeout"/> for the answer. Anything but an accepted, ticked answer is not a
    /// confirmation, including a client that fails to ask. Cancelling the call cancels the question.
    /// </summary>
    /// <exception cref="OperationCanceledException">The call was cancelled.</exception>
    public static async Task<ConfirmationAnswer> AskAsync(
        McpServer server, string message, string title, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var question = new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    [ConfirmField] = new ElicitRequestParams.BooleanSchema
                    {
                        Title = title,
                        Description = "Tick to let the AI agent do this; leave it unticked or decline to stop it",
                        Default = false
                    }
                },
                Required = [ConfirmField]
            }
        };

        ElicitResult answer;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(timeout);
            try
            {
                answer = await server.ElicitAsync(question, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ConfirmationAnswer(false, $"The user did not answer within {timeout.TotalMinutes:0.#} minutes.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or McpException)
            {
                return new ConfirmationAnswer(false, $"The user could not be asked ({ex.Message}).");
            }
        }

        if (answer.IsAccepted &&
            answer.Content != null &&
            answer.Content.TryGetValue(ConfirmField, out var value) &&
            (value.ValueKind == JsonValueKind.True ||
             (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase))))
        {
            return new ConfirmationAnswer(true, null);
        }

        return new ConfirmationAnswer(false, answer.IsAccepted
            ? "The user did not confirm."
            : $"The user {(answer.Action == "decline" ? "declined" : "dismissed")} the question.");
    }

    /// <summary>The longest text from another app, such as a window title, that a question shows (<see cref="Quote"/>).</summary>
    public const int MaxQuotedLength = 80;

    /// <summary>
    /// The single quote and the characters that look like it: left and right single quotation marks, the low-9 and
    /// reversed-9 ones, prime, modifier-letter apostrophe and turned comma, grave and acute accents, and the fullwidth
    /// apostrophe.
    /// </summary>
    private static readonly HashSet<char> QuoteLookAlikes =
        ['\'', '\u2018', '\u2019', '\u201A', '\u201B', '\u2032', '\u02BC', '\u02BB', '`', '\u00B4', '\uFF07'];

    /// <summary>
    /// Text another app chose, such as a window title, as a question shows it: in single quotes, on one line (each
    /// control character and line or paragraph separator, line breaks and tabs included, becomes a space), without
    /// format characters (the bidirectional overrides and isolates that reorder what follows, zero-width characters), with
    /// each single quote or look-alike inside it shown as a double quote (so the title cannot close the quotes and
    /// append a fake process and PID), and at most <see cref="MaxQuotedLength"/> characters, cut with an ellipsis. An
    /// app's title must not be able to rewrite the question the user answers.
    /// </summary>
    public static string Quote(string? text)
    {
        var line = new string((text ?? string.Empty)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            .Select(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? ' '
                : QuoteLookAlikes.Contains(c) ? '"' : c)
            .ToArray());
        if (line.Length > MaxQuotedLength)
        {
            line = line[..(MaxQuotedLength - 1)].TrimEnd() + "…";
        }

        return $"'{line}'";
    }
}
