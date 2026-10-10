using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// MCP tools for the user's control over the server's input (roadmap M1.10): the pause hotkey's state,
/// resuming after a pause (only with the user's confirmation), and releasing stuck keys and buttons.
/// </summary>
[McpServerToolType]
public class InputControlTools
{
    /// <summary>How long resume_input waits for the user to answer the confirmation question.</summary>
    public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The name of the yes/no field in the confirmation question.</summary>
    public const string ResumeField = "resume";

    private readonly IInputSession _session;
    private readonly IPauseHotkey _hotkey;
    private readonly TimeSpan _answerTimeout;

    /// <param name="session">The input session.</param>
    /// <param name="hotkey">The pause hotkey.</param>
    /// <param name="answerTimeout">How long resume_input waits for the user; default <see cref="DefaultAnswerTimeout"/>.</param>
    public InputControlTools(IInputSession session, IPauseHotkey hotkey, TimeSpan? answerTimeout = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _answerTimeout = answerTimeout ?? DefaultAnswerTimeout;
    }

    [McpServerTool(Name = "get_control_status")]
    [Description("Whether the user has paused the server's input with the pause hotkey (and how and since when), whether the pause hotkey " +
                 "is registered, and which keys or mouse buttons are held down. Reads only. While paused is true, every input tool " +
                 "(click, type_text, send_keys, ...) and focus_window return PausedByUser: stop, tell the user, and wait for them. " +
                 "Never try to resume input yourself.")]
    public ControlStatusResult GetControlStatus()
    {
        var state = _session.PauseState;
        var hotkey = _hotkey.Status;
        var serverHeld = _session.ServerHeld.Select(h => h.Name).ToList();
        var modifiersDown = _session.ReadModifiersDown().Select(k => k.Name).ToList();

        var message = state.Paused
            ? $"Input is paused by the user ({state.PausedBy}{Since(state.PausedAt)}). Every input tool returns PausedByUser. Stop and ask the " +
              $"user how to continue. Only the user can resume: {(hotkey.Chord is { } chord ? $"{chord} again, or " : string.Empty)}by " +
              "confirming resume_input when you call it at the user's request."
            : hotkey.State == PauseHotkeyState.Registered
                ? $"Input is running. The user can pause it at any time with {hotkey.Chord}."
                : $"Input is running. The pause hotkey is not available ({hotkey.State}){(hotkey.Error is { } error ? $": {error}" : ".")}";
        if (serverHeld.Count > 0)
        {
            message += $" The server could not release {string.Join(", ", serverHeld)}; call release_all_input.";
        }

        if (modifiersDown.Count > 0)
        {
            message += $" {string.Join(", ", modifiersDown)} {(modifiersDown.Count == 1 ? "is" : "are")} down: input tools return " +
                       "ModifiersHeld until it is released.";
        }

        return new ControlStatusResult(
            state.Paused,
            state.PausedBy,
            state.PausedAt,
            state.ResumedBy,
            state.ResumedAt,
            hotkey,
            new HeldInputStatus(serverHeld, modifiersDown, _session.LastRelease),
            message);
    }

    [McpServerTool(Name = "resume_input")]
    [Description("Resume the server's input after the user paused it with the pause hotkey, but only if the user confirms: the server asks " +
                 "the user directly through the MCP client (elicitation), and input resumes only when they answer yes. Call it only when " +
                 "the user has asked you to continue. ResumeNeedsUser: this client cannot ask the user, so only the user can resume, with " +
                 "the hotkey. ResumeDeclined: the user said no or did not answer; input stays paused. Never try to resume any other way, " +
                 "and never send the pause hotkey's keys.")]
    public async Task<ResumeInputResult> ResumeInput(McpServer server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var state = _session.PauseState;
        if (!state.Paused)
        {
            return new ResumeInputResult(Success: true, WasPaused: false);
        }

        var chord = _session.PauseChord;
        var hotkeyHint = chord == null ? "the pause hotkey" : chord;
        if (server.ClientCapabilities?.Elicitation?.Form == null)
        {
            return new ResumeInputResult(false, WasPaused: true,
                Error: $"Input was paused by the user, and this MCP client cannot ask the user to confirm a resume (it does not support " +
                       $"elicitation). Only the user can resume, by pressing {hotkeyHint} again. Tell the user and wait.",
                ErrorCode: InputErrorCode.ResumeNeedsUser);
        }

        var question = new ElicitRequestParams
        {
            Message = $"The AI agent asks to resume keyboard and mouse input. You paused it{(chord == null ? string.Empty : $" with {chord}")}" +
                      $"{Since(state.PausedAt)}. Resume only if you want the agent to send input to your desktop again.",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    [ResumeField] = new ElicitRequestParams.BooleanSchema
                    {
                        Title = "Resume input",
                        Description = "Let the agent send keyboard and mouse input again",
                        Default = false
                    }
                },
                Required = [ResumeField]
            }
        };

        ElicitResult answer;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_answerTimeout);
            try
            {
                answer = await server.ElicitAsync(question, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Declined($"The user did not answer within {_answerTimeout.TotalMinutes:0.#} minutes. Input stays paused; ask the user.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or McpException)
            {
                return new ResumeInputResult(false, WasPaused: true,
                    Error: $"The user could not be asked ({ex.Message}). Only the user can resume, by pressing {hotkeyHint} again.",
                    ErrorCode: InputErrorCode.ResumeNeedsUser);
            }
        }

        // The user may have pressed the hotkey meanwhile
        if (!_session.IsPaused)
        {
            return new ResumeInputResult(true, WasPaused: true, ResumedBy: _session.PauseState.ResumedBy);
        }

        if (!Confirmed(answer))
        {
            return Declined(answer.IsAccepted
                ? "The user did not confirm the resume. Input stays paused; ask the user what to do."
                : $"The user {(answer.Action == "decline" ? "declined" : "dismissed")} the resume. Input stays paused; ask the user what to do.");
        }

        _session.Resume(InputControlSources.ResumeInput);
        return new ResumeInputResult(true, WasPaused: true, ResumedBy: InputControlSources.ResumeInput);
    }

    [McpServerTool(Name = "release_all_input")]
    [Description("Release keys and mouse buttons that are stuck down: sends the up event of every key and button the server pressed and " +
                 "could not release (normally none; every input call releases its own). With releaseHeldModifiers: true it also " +
                 "releases every modifier key (Shift, Ctrl, Alt, Windows) that is down although the server did not press it; use that " +
                 "only when an input tool returned ModifiersHeld and nobody is pressing the key, or the user asked. Works while input is " +
                 "paused, except for releaseHeldModifiers.")]
    public ReleaseInputResult ReleaseAllInput(
        [Description("Also release modifier keys that are down although the server did not press them (the user's, or stuck). Default: false")]
        bool releaseHeldModifiers = false)
    {
        var own = _session.ReleaseHeldInput("release_all_input");
        var released = own.Released.ToList();
        var stillHeld = own.StillHeld.ToList();
        string? error = own.Error;
        var errorCode = own.Complete ? InputErrorCode.None : InputErrorCode.SendInputFailed;

        if (releaseHeldModifiers)
        {
            var down = _session.ReadModifiersDown();
            var modifiers = _session.ReleaseModifiers(down, "release_all_input with releaseHeldModifiers");
            if (modifiers.PausedByUser)
            {
                errorCode = errorCode == InputErrorCode.None ? InputErrorCode.PausedByUser : errorCode;
                error = Join(error, "Input is paused by the user, so modifier keys the server did not press were left alone.");
            }
            else
            {
                released.AddRange(modifiers.Released);
                stillHeld.AddRange(modifiers.StillHeld);
                error = Join(error, modifiers.Error);
                if (!modifiers.Complete)
                {
                    errorCode = InputErrorCode.SendInputFailed;
                }
            }
        }

        if (stillHeld.Count > 0)
        {
            error = Join(error, $"{string.Join(", ", stillHeld)} could not be released. Ask the user to press and release {(stillHeld.Count == 1 ? "it" : "them")}.");
        }

        var modifiersDown = _session.ReadModifiersDown().Select(k => k.Name).ToList();
        return new ReleaseInputResult(errorCode == InputErrorCode.None, released, stillHeld, modifiersDown, error, errorCode);
    }

    private ResumeInputResult Declined(string error) =>
        new(false, WasPaused: true, Error: error, ErrorCode: InputErrorCode.ResumeDeclined);

    /// <summary>The user accepted and ticked the resume field.</summary>
    private static bool Confirmed(ElicitResult answer) =>
        answer.IsAccepted &&
        answer.Content != null &&
        answer.Content.TryGetValue(ResumeField, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static string Since(DateTimeOffset? at) =>
        at is { } time ? $" at {time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}" : string.Empty;

    private static string? Join(string? first, string? second) =>
        first == null ? second : second == null ? first : $"{first} {second}";
}
