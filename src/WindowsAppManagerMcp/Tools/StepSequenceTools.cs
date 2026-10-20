using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// run_steps (roadmap M6.2, owner decision D7): several steps on one window in one call, under one hold of the input gate,
/// stopping at the first failure. A form fill or a keyboard sequence costs one round trip instead of one per step, and no
/// other call's input or window change lands between two of its steps. The steps are the server's own tools, called through
/// its own call filters (<see cref="McpStepDispatcher"/>); <see cref="StepRunner"/> checks and runs them.
/// </summary>
[McpServerToolType]
public class StepSequenceTools
{
    private readonly IWindowService _windows;
    private readonly StepRunner _runner;

    /// <param name="session">The user's pause, and the held input released at the end of a run.</param>
    /// <param name="windows">Resolves the run's window and reads what is in front.</param>
    /// <param name="elements">The windows of the element refs a step names.</param>
    /// <param name="keyGuard">The server's destructive chords, which a step's keys may not contain; null for the defaults.</param>
    /// <param name="takeover">Takeover detection (roadmap M7.4), which watches the run; the server always registers it.</param>
    /// <param name="keyboardLayout">
    /// The keyboard layout queries, to read a step's keys on the layout of the window they go to, as its tool maps them; the
    /// server always registers them.
    /// </param>
    public StepSequenceTools(IInputSession session, IWindowService windows, IElementRegistry elements, InputGuardOptions? keyGuard = null,
        ITakeoverMonitor? takeover = null, INativeKeyboardLayoutWrapper? keyboardLayout = null)
    {
        _windows = windows;
        _runner = new StepRunner(session, windows, elements, keyGuard, takeover: takeover, keyboardLayout: keyboardLayout);
    }

    [McpServerTool(Name = "run_steps", Title = "Run Steps", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Run up to 50 steps on one window in one call instead of one call each: a form fill (element_action " +
                 "set_value, toggle, select, invoke), a key sequence, a dialog to wait for and answer. A step is a tool " +
                 "of the list with that tool's own args, as a direct call takes them. Every step is checked first: a " +
                 "wrong one refuses the run (InvalidArguments) and nothing is done. A step that names no window gets " +
                 "window; a step may act only on window, its dialogs and its process's windows (get_focused_element and " +
                 "wait_for_window only read, anywhere). The run holds the input gate (no other call's input lands " +
                 "between steps) for at most maxDurationMs, and stops at the first failed step unless it is optional; " +
                 "the rest are skipped, and nothing is undone. It also stops on a pause (PausedByUser), when " +
                 "maxDurationMs runs out (OperationTimedOut), when another window takes the front after window or " +
                 "its dialog had it (TargetNotForeground: it never takes it back), and on the user's own input when " +
                 "takeover detection is set to stop (UserInterrupted; warn only warns, and a warning says when detection did " +
                 "not work). Check a state with wait_for_window " +
                 "(condition foreground) or wait_for_element, timeoutMs 0. Refused in a step: paste (type_text types, " +
                 "unicode or keys), file output, window_close, scope desktop, allowHostTarget, and keys that close, " +
                 "quit or lock (ctrl+w, ctrl+q); other keys act in window as in a direct call (ctrl+s saves). A whole " +
                 "value \"$steps.<id>.<path>\" takes an earlier step's ref, handle, captureId or clickPoint.x/y, e.g. " +
                 "\"$steps.find.elements[0].ref\". Returns success, ran, total, stoppedBy, and steps (status ok, failed " +
                 "or skipped, errorCode, elapsedMs; result for a failed step, the last step and return: true, warnings " +
                 "for the others), with at " +
                 "most 2 images. Each step is audited as its own call.")]
    [ToolResultPayload(typeof(RunStepsResult))]
    public async Task<CallToolResult> RunSteps(
        [Description("The run's window: a window selector, or {\"handle\": n}, resolved to exactly one window before the run; not {\"foreground\": true} alone")]
        WindowSelector window,
        [Description("The steps, 1-50, run in order")]
        RunStep[] steps,
        RequestContext<CallToolRequestParams> context,
        [Description("How long the run may hold the input gate, in milliseconds (default: 30000, min: 1000, max: 60000); a wait step's timeoutMs is cut to what is left")]
        int maxDurationMs = StepRunner.DefaultMaxDurationMs,
        [Description("The pause between two steps, in milliseconds (default: 50, min: 0, max: 2000)")]
        int delayBetweenStepsMs = StepRunner.DefaultDelayBetweenStepsMs,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maxDurationMs is < StepRunner.MinMaxDurationMs or > StepRunner.MaxMaxDurationMs)
        {
            throw Invalid($"maxDurationMs must be {StepRunner.MinMaxDurationMs}-{StepRunner.MaxMaxDurationMs}, but it is {maxDurationMs}.");
        }

        if (delayBetweenStepsMs is < 0 or > StepRunner.MaxDelayBetweenStepsMs)
        {
            throw Invalid($"delayBetweenStepsMs must be 0-{StepRunner.MaxDelayBetweenStepsMs}, but it is {delayBetweenStepsMs}.");
        }

        if (WindowTargeting.IsBareForeground(window))
        {
            throw Invalid("window {\"foreground\": true} is whatever window is in front, not a window you named. Name the run's " +
                          "window: its handle, or processName, a title or className beside foreground.");
        }

        var dispatcher = new McpStepDispatcher(context);
        var checkedSteps = _runner.Check(steps, dispatcher);

        var handle = WindowTargeting.Require(_windows, handle: null, window, "window");
        var target = _windows.GetWindowInfo(handle) ?? throw new ToolErrorException(nameof(WindowErrorCode.InvalidHandle),
            $"The run's window {handle} is no window (it closed). Nothing was done.");
        if (target.IsAgentHost)
        {
            throw new ToolErrorException(nameof(InputErrorCode.TargetIsAgentHost),
                $"The run's window {handle} ({target.ProcessName}) is your own terminal or chat window, or a window it owns." +
                $"{HostGuard.AncestorsUnknownNoteFor(target)} Nothing was done: name the window you mean to act on.");
        }

        var run = await _runner.RunAsync(handle, checkedSteps, dispatcher, TimeSpan.FromMilliseconds(maxDurationMs),
            TimeSpan.FromMilliseconds(delayBetweenStepsMs), ToolProgress.ForServices(progress), cancellationToken).ConfigureAwait(false);

        var result = ToolImages.Create(run.Result, image: null, isError: !run.Result.Success, ToolJson.SerializerOptions);
        if (run.Images.Count > 0)
        {
            result.Content = [.. run.Images, .. result.Content];
        }

        return result;
    }

    private static ToolErrorException Invalid(string message) =>
        new(ToolErrorReporting.InvalidArgumentsErrorCode, message + " Nothing was done.");
}
