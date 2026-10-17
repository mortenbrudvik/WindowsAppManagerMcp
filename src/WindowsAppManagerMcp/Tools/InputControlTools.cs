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
    private readonly ITakeoverMonitor? _takeover;

    /// <param name="session">The input session.</param>
    /// <param name="hotkey">The pause hotkey.</param>
    /// <param name="answerTimeout">How long resume_input waits for the user; default <see cref="DefaultAnswerTimeout"/>.</param>
    /// <param name="takeover">
    /// Takeover detection (roadmap M7.4), whose mode and state get_control_status reports; the server always registers it.
    /// </param>
    public InputControlTools(IInputSession session, IPauseHotkey hotkey, TimeSpan? answerTimeout = null, ITakeoverMonitor? takeover = null)
    {
        _takeover = takeover;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _answerTimeout = answerTimeout ?? DefaultAnswerTimeout;
    }

    [McpServerTool(Name = "get_control_status", Title = "Get Input Control Status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Whether the user has paused the server's input with the pause hotkey (and how and since when), whether the pause hotkey " +
                 "is registered, which keys or mouse buttons are held down, and takeover detection's mode (off, warn or stop: " +
                 "whether type_text, drag and run_steps stop when the user uses the keyboard or mouse). Reads only. While paused is true, every input tool " +
                 "(mouse_click, type_text, send_keys, ...) and every tool that changes windows or starts or stops processes return " +
                 "PausedByUser: stop, tell the user, and wait for them. Never try to resume input yourself.")]
    public ControlStatusResult GetControlStatus()
    {
        var state = _session.PauseState;
        var hotkey = _hotkey.Status;
        var shared = _session.SharesDesktopPause;
        var serverHeld = _session.ServerHeld.Select(h => h.Name).ToList();
        var modifiersDown = _session.ReadModifiersDown().Select(k => k.Name).ToList();

        var message = state.Paused
            ? $"Input is paused by the user ({state.PausedBy}{Since(state.PausedAt)}). Every input tool and every tool that changes windows " +
              "or starts or stops processes returns PausedByUser. Stop and ask the user how to continue. Only the user can resume: " +
              $"{(hotkey.Chord is { } chord ? $"{chord} again, or " : string.Empty)}by confirming resume_input when you call it at the user's request."
            : hotkey.State switch
            {
                PauseHotkeyState.Registered => $"Input is running. The user can pause it at any time with {hotkey.Chord}.",
                PauseHotkeyState.OtherServer when shared =>
                    $"Input is running. The user can pause it at any time with {hotkey.Chord}, which another Windows App Manager server on this " +
                    "desktop has registered; it pauses every server.",
                PauseHotkeyState.OtherServer =>
                    $"Input is running. Another Windows App Manager server has registered {hotkey.Chord}, and this server could not share its " +
                    "pause, so the hotkey does not pause this server. Tell the user.",
                _ => $"Input is running. The pause hotkey is not available ({hotkey.State}){(hotkey.Error is { } error ? $": {error}" : ".")}"
            };
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
            shared,
            new HeldInputStatus(serverHeld, modifiersDown, _session.LastRelease),
            message,
            _takeover?.Status);
    }

    [McpServerTool(Name = "resume_input", Title = "Resume Input (Asks the User)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Resume the server's input after the user paused it with the pause hotkey, but only if the user confirms: the server asks " +
                 "the user directly through the MCP client (elicitation), and input resumes only when they answer yes. Call it only when " +
                 "the user has asked you to continue, and only once at a time. ResumeNeedsUser: this client cannot ask the user, so only " +
                 "the user can resume, with the hotkey. ResumeDeclined: the user said no or did not answer, paused again meanwhile, or " +
                 "another resume_input is already waiting for the answer; input stays paused. Never try to resume any other way, and " +
                 "never send the pause hotkey's keys.")]
    public async Task<ResumeInputResult> ResumeInput(McpServer server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var state = _session.PauseState;
        if (!state.Paused)
        {
            return new ResumeInputResult(Success: true, WasPaused: false);
        }

        // One question at a time: several open questions would give a careless "yes" several chances. The guard is the
        // session's, not this instance's: the SDK builds a new InputControlTools for every call.
        using var question = _session.TryOpenResumeQuestion();
        if (question == null)
        {
            return Declined("Another resume_input call is already waiting for the user's answer. Input stays paused; wait for that " +
                            "answer instead of asking again.");
        }

        return await AskToResumeAsync(server, state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResumeInputResult> AskToResumeAsync(McpServer server, InputPauseState state, CancellationToken cancellationToken)
    {
        var chord = _session.PauseChord;
        var hotkeyHint = chord == null ? "the pause hotkey" : chord;
        if (server.ClientCapabilities?.Elicitation?.Form == null)
        {
            return new ResumeInputResult(false, WasPaused: true,
                Error: $"Input was paused by the user, and this MCP client cannot ask the user to confirm a resume (it does not support " +
                       $"elicitation). Only the user can resume, by pressing {hotkeyHint} again. Tell the user and wait.",
                ErrorCode: InputErrorCode.ResumeNeedsUser);
        }

        var everyServer = _session.SharesDesktopPause
            ? " The pause is shared: resuming lets every Windows App Manager server on this desktop send input again."
            : string.Empty;
        var question = new ElicitRequestParams
        {
            Message = $"The AI agent asks to resume keyboard and mouse input. You paused it{(chord == null ? string.Empty : $" with {chord}")}" +
                      $"{Since(state.PausedAt)}.{everyServer} Resume only if you want the agent to send input to your desktop again.",
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

        // The user may have pressed the hotkey meanwhile: to resume, or to resume and pause again
        var now = _session.PauseState;
        if (!now.Paused)
        {
            return new ResumeInputResult(true, WasPaused: true, ResumedBy: now.ResumedBy);
        }

        if (now.PauseNumber != state.PauseNumber)
        {
            return Declined("The user paused input again while the question was open, so the answer does not apply to this pause. Input " +
                            "stays paused; ask the user what to do.");
        }

        if (!Confirmed(answer))
        {
            return Declined(answer.IsAccepted
                ? "The user did not confirm the resume. Input stays paused; ask the user what to do."
                : $"The user {(answer.Action == "decline" ? "declined" : "dismissed")} the resume. Input stays paused; ask the user what to do.");
        }

        // Only the pause the user was asked about; a newer one stays
        if (!_session.Resume(InputControlSources.ResumeInput, state.PauseNumber))
        {
            var after = _session.PauseState;
            return after.Paused
                ? Declined("The user paused input again while the question was answered. Input stays paused; ask the user what to do.")
                : new ResumeInputResult(true, WasPaused: true, ResumedBy: after.ResumedBy);
        }

        return new ResumeInputResult(true, WasPaused: true, ResumedBy: InputControlSources.ResumeInput);
    }

    [McpServerTool(Name = "release_all_input", Title = "Release Stuck Keys and Buttons", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Release keys and mouse buttons that are stuck down: sends the up event of every key and button the server pressed and " +
                 "could not release (normally none; every input call releases its own). With releaseHeldModifiers: true it also " +
                 "releases every modifier key (Shift, Ctrl, Alt, Windows) that is down although the server did not press it; use that " +
                 "only when an input tool returned ModifiersHeld and nobody is pressing the key, or the user asked. Works while input is " +
                 "paused, except for releaseHeldModifiers. While any modifier that is down belongs to the pause hotkey's chord, no " +
                 "modifier the server did not press is released (ModifiersHeld): the user may be pressing the hotkey, so ask the user to " +
                 "press and release the key; the keys and buttons the server pressed are still released.")]
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
            var modifiers = _session.IsPauseChordHeld(down)
                ? null
                : _session.ReleaseModifiers(down, "release_all_input with releaseHeldModifiers");
            if (modifiers == null)
            {
                // One modifier of the chord is enough: the user may be pressing the hotkey one key after another
                var one = down.Count == 1;
                errorCode = errorCode == InputErrorCode.None ? InputErrorCode.ModifiersHeld : errorCode;
                error = Join(error, $"{string.Join(", ", down.Select(k => k.Name))} {(one ? "is" : "are")} down, and the pause hotkey " +
                                    $"({_session.PauseChord}) uses {(one ? "it" : "at least one of them")}. The user may be pressing the " +
                                    $"hotkey, so no modifier the server did not press was released. Ask the user to press and release " +
                                    $"{(one ? "it" : "them")} if {(one ? "it stays" : "they stay")} down.");
            }
            else if (modifiers.PausedByUser)
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
