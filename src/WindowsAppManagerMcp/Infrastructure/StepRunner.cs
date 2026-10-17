using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// A step checked by <see cref="StepRunner.Check"/>: its number (1 for the first), its tool's name, the parameters its tool
/// declares, and the step as given.
/// </summary>
public sealed record CheckedStep(int Number, string Tool, IReadOnlySet<string> Parameters, RunStep Step);

/// <summary>What a run did: the result, and the images of the steps whose whole result it returns.</summary>
public sealed record StepRunOutcome(RunStepsResult Result, IReadOnlyList<ImageContentBlock> Images);

/// <summary>
/// Runs the steps of run_steps (roadmap M6.2, owner decision D7) on one window: checks them all before anything runs, holds
/// the input gate for the whole run through a lease its own steps pass, calls each step's tool through an
/// <see cref="IStepDispatcher"/>, and stops at the first failure.
/// </summary>
/// <remarks>
/// <para><b>Before the run</b> (<see cref="Check"/>), every step is checked and the whole run refused with InvalidArguments
/// when one is wrong, so nothing was done: 1 to <see cref="MaxSteps"/> steps; unique ids; a tool the server serves (the
/// toolsets, WAM_EXCLUDE_TOOLS and read-only mode apply to steps as to direct calls); arguments the tool declares; at most
/// <see cref="MaxTextLength"/> characters in all the steps' strings together; references that name an earlier step and an
/// identifier (<see cref="ReferencePrefix"/>); and none of the arguments a step may not use: allowHostTarget, type_text's
/// methods paste and auto (a step types: method unicode unless it says keys), element_action's mode paste and its
/// window_close, capture_window's file output, scope desktop (a search of every window), and send_keys' or press_keys'
/// keys with a chord that closes, quits or locks (the destructive chords, which a step's keyboard tool, always given a
/// window, would not refuse itself). No argument a step may use writes the clipboard or a file, closes a window or finds
/// one outside the run's window and its dialogs; the keys a step sends act inside the run's window as a direct call's
/// would (ctrl+s saves, ctrl+c copies).</para>
/// <para><b>The run's window.</b> A step that names no window (no handle, windowHandle, window, ref, withinRef or listRef)
/// is given the run's window, under the name its tool takes; wait_for_window never is (its window is what it waits for).
/// A step may act only on the run's window, a window it owns (its dialogs) or another window of its process (not for the
/// shell and the packaged apps' frame host, whose windows are unrelated): a step that names any other window, by handle,
/// selector or element ref, is not run and fails the run. A null argument counts as not given, an empty selector or
/// {"foreground": true} alone names no window, and a selector is resolved once and passed on as the handle it names, so
/// the tool acts on the window this check passed. get_focused_element and wait_for_window only read, and may read
/// outside the run's window. The run's window is read again once the gate is held: closed, reused by another process or
/// the agent's host, it refuses the run.</para>
/// <para><b>During the run</b>, before each step after the first: a pause stops it (PausedByUser), maxDurationMs running out
/// stops it (OperationTimedOut; a wait step's timeoutMs is cut to what is left, and a step still running then is
/// cancelled), and once the run's window (or its dialog) has been in front, a window outside it in front stops it
/// (TargetNotForeground): the user took the foreground, and the run does not take it back. A failed step stops the run,
/// unless it is optional; the steps after it are skipped. A step is never undone.</para>
/// </remarks>
public sealed partial class StepRunner
{
    /// <summary>The most steps one run takes.</summary>
    public const int MaxSteps = 50;

    /// <summary>The most characters all the strings of all the steps' arguments may hold together.</summary>
    public const int MaxTextLength = 20_000;

    /// <summary>
    /// The most images a run returns (of the steps with return true and the last step that ran); the last step's comes
    /// first when more qualify.
    /// </summary>
    public const int MaxImages = 2;

    /// <summary>
    /// The most characters of JSON the steps' own results take in a run's result together, about 6,000 tokens. The failed
    /// step's and the last step's come first, then the return true steps' in order; one that does not fit is left out, and
    /// its step says so (resultOmitted).
    /// </summary>
    public const int MaxResultTextLength = 24_000;

    /// <summary>The default, smallest and largest maxDurationMs.</summary>
    public const int DefaultMaxDurationMs = 30_000;
    public const int MinMaxDurationMs = 1_000;
    public const int MaxMaxDurationMs = 60_000;

    /// <summary>The default and largest pause between two steps.</summary>
    public const int DefaultDelayBetweenStepsMs = 50;
    public const int MaxDelayBetweenStepsMs = 2_000;

    /// <summary>A whole string argument that starts with this refers to an earlier step's result.</summary>
    public const string ReferencePrefix = "$steps.";

    /// <summary>A wait step's timeoutMs is cut to the time left minus this, so it ends on its own before the run is out of time.</summary>
    private const int WaitMarginMs = 250;

    /// <summary>The longest owner chain walked to tell whether a window is one of the run's window's.</summary>
    private const int MaxOwners = 32;

    /// <summary>The arguments that name the window a step acts on; a step with none of them gets the run's window.</summary>
    private static readonly string[] WindowArguments = ["windowHandle", "handle", "window", "ref", "withinRef", "listRef"];

    /// <summary>The arguments that name a window by handle.</summary>
    private static readonly string[] HandleArguments = ["windowHandle", "handle", "toWindowHandle"];

    /// <summary>The arguments that name an element, and with it its window.</summary>
    private static readonly string[] RefArguments = ["ref", "withinRef", "listRef"];

    /// <summary>The tools whose timeoutMs is the step's wait, cut to the run's time left.</summary>
    private static readonly HashSet<string> WaitTools = new(StringComparer.Ordinal) { "wait_for_window", "wait_for_element" };

    /// <summary>The tool whose window argument is the window it waits for, not a target.</summary>
    private const string WaitForWindowTool = "wait_for_window";

    /// <summary>The last segment a reference may end with: identifiers, never UI text.</summary>
    private static readonly HashSet<string> ReferenceEnds = new(StringComparer.Ordinal) { "ref", "handle", "captureId", "x", "y" };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z0-9]*)(?:\[(?<index>[0-9]{1,3})\])?$")]
    private static partial Regex PathSegment();

    private readonly IInputSession _session;
    private readonly IWindowService _windows;
    private readonly IElementRegistry _elements;
    private readonly InputGuardOptions _keyGuard;
    private readonly TimeProvider _time;

    /// <param name="keyGuard">
    /// The server's destructive chords (WAM_DESTRUCTIVE_CHORDS): a step's keys may contain none of them, nor any of the
    /// default ones, whatever the variable says. Null: the defaults.
    /// </param>
    public StepRunner(IInputSession session, IWindowService windows, IElementRegistry elements, InputGuardOptions? keyGuard = null,
        TimeProvider? time = null)
    {
        _session = session;
        _windows = windows;
        _elements = elements;
        _keyGuard = keyGuard ?? InputGuardOptions.Default;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The name a step tool has in tools/list.</summary>
    public static string ToolName(StepTool tool) => ToolEnumInfo.Get(typeof(StepTool)).Name(tool);

    /// <summary>
    /// Checks every step before anything runs (see the remarks). Returns the steps with their tools' names.
    /// </summary>
    /// <exception cref="ToolErrorException">A step is wrong (InvalidArguments); nothing was done.</exception>
    public IReadOnlyList<CheckedStep> Check(IReadOnlyList<RunStep>? steps, IStepDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (steps is not { Count: > 0 } || steps.Count > MaxSteps)
        {
            throw Invalid($"steps must hold 1-{MaxSteps} steps, but it holds {steps?.Count ?? 0}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var checkedSteps = new List<CheckedStep>(steps.Count);
        var text = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i] ?? throw Invalid($"Step {i + 1} is null.");
            var tool = ToolName(step.Tool);
            var label = Label(i + 1, tool, step.Id);

            if (step.Id != null && !IdPattern().IsMatch(step.Id))
            {
                throw Invalid($"Step {i + 1}'s id must be 1-32 letters, digits, '_' or '-'.");
            }

            if (dispatcher.InputSchemaOf(tool) is not { } schema)
            {
                throw Invalid($"{label}: the server does not serve {tool} (its toolset is off, WAM_EXCLUDE_TOOLS names it, or " +
                              "WAM_READ_ONLY is on); get_server_info says which tools are served. A step can call only a tool the " +
                              "server serves.");
            }

            var arguments = step.Args ?? [];
            var parameters = AuditRecord.DeclaredTypes(schema)?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
            if (UndeclaredArgumentToolFilter.Refusal(tool, arguments.Select(a => a.Key), parameters) is { } undeclared)
            {
                throw Invalid($"{label}: {undeclared.Replace(" Nothing was done: ", " Nothing was done (the whole run was refused): ", StringComparison.Ordinal)}",
                    addNothingWasDone: false);
            }

            if (Forbidden(tool, arguments) is { } forbidden)
            {
                throw Invalid($"{label}: {forbidden}");
            }

            if (ClosingChord(tool, arguments) is { } chord)
            {
                throw Invalid($"{label}: its keys contain {chord}, a chord that can close, quit or lock the window, and a step " +
                              "closes nothing (in a run it would go to the run's window). Close a window with close_window, by its " +
                              "handle, after the run.");
            }

            foreach (var reference in References(arguments))
            {
                if (CheckReference(reference, ids) is { } wrong)
                {
                    throw Invalid($"{label}: {wrong}");
                }
            }

            text += TextLength(arguments);
            if (step.Id != null && !ids.Add(step.Id))
            {
                throw Invalid($"Step {i + 1}'s id '{step.Id}' is already the id of an earlier step; ids are unique in a run.");
            }

            checkedSteps.Add(new CheckedStep(i + 1, tool, parameters, step));
        }

        if (text > MaxTextLength)
        {
            throw Invalid($"The steps' arguments hold {text} characters of strings together; a run takes at most {MaxTextLength}. " +
                          "Split the run, or type a long text with type_text on its own.");
        }

        return checkedSteps;
    }

    /// <summary>
    /// Runs <paramref name="steps"/> on <paramref name="window"/>: waits for the input gate (at most
    /// <see cref="InputGateToolFilter.GateWaitTimeout"/>), holds it for the run, and returns what every step did.
    /// </summary>
    /// <exception cref="ToolErrorException">Input is paused, or the gate is busy, before the first step; nothing was done.</exception>
    /// <exception cref="OperationCanceledException">The client cancelled the call; the steps that ran are not undone.</exception>
    public async Task<StepRunOutcome> RunAsync(
        nint window,
        IReadOnlyList<CheckedStep> steps,
        IStepDispatcher dispatcher,
        TimeSpan maxDuration,
        TimeSpan delayBetweenSteps,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(dispatcher);

        var started = _time.GetTimestamp();
        var before = _windows.GetWindowInfo(window);
        var ran = new List<Ran>(steps.Count);
        StepStopReason? stop = null;
        string? stopCode = null, stopError = null;

        using (var lease = await InputGateToolFilter.HoldAsync(_session, cancellationToken).ConfigureAwait(false))
        {
            // On this flow, so every step this method awaits from now on passes the gate the run holds
            lease.Bind();
            var target = Target(window, before);
            var held = _time.GetTimestamp();
            using var budget = new CancellationTokenSource(maxDuration, _time);
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
            var foregroundWasOurs = false;
            try
            {
                for (var i = 0; i < steps.Count && stop == null; i++)
                {
                    var step = steps[i];
                    if (i > 0 && delayBetweenSteps > TimeSpan.Zero)
                    {
                        await DelayAsync(delayBetweenSteps, run.Token).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var left = maxDuration - _time.GetElapsedTime(held);
                    if (_session.IsPaused)
                    {
                        (stop, stopCode, stopError) = (StepStopReason.Paused, nameof(WindowErrorCode.PausedByUser), Paused(step, during: false));
                        break;
                    }

                    if (left <= TimeSpan.Zero)
                    {
                        (stop, stopCode, stopError) = (StepStopReason.TimedOut, TimedOutCode, TimedOut(step, maxDuration, during: false));
                        break;
                    }

                    if (foregroundWasOurs && _windows.GetForegroundWindow() is { } front && !IsRunWindow(front.Handle, target))
                    {
                        (stop, stopCode, stopError) = (StepStopReason.ForegroundMoved, nameof(InputErrorCode.TargetNotForeground),
                            ForegroundMoved(step, front));
                        break;
                    }

                    var stepStarted = _time.GetTimestamp();
                    var (arguments, refusal, refusalCode, waitCut) = Prepare(step, target, ran, left);
                    if (refusal != null)
                    {
                        var body = new JsonObject { ["success"] = false, ["errorCode"] = refusalCode, ["error"] = refusal };
                        ran.Add(new Ran(step, false, refusalCode, body, [], Elapsed(stepStarted)));
                    }
                    else
                    {
                        try
                        {
                            var result = await dispatcher.InvokeAsync(step.Tool, arguments!, step.Number, run.Token).ConfigureAwait(false);
                            var body = Body(result);
                            var failed = result.IsError == true;
                            ran.Add(new Ran(step, !failed, failed ? ErrorCodeOf(body) ?? ToolErrorReporting.ToolErrorCode : null, body,
                                result.Content.OfType<ImageContentBlock>().ToList(), Elapsed(stepStarted)));
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
                        {
                            ran.Add(new Ran(step, false, TimedOutCode, null, [], Elapsed(stepStarted)));
                            (stop, stopCode, stopError) = (StepStopReason.TimedOut, TimedOutCode, TimedOut(step, maxDuration, during: true));
                        }
                    }

                    var last = ran[^1];
                    progress?.Report(new OperationProgress(i + 1, steps.Count,
                        $"step {i + 1} of {steps.Count}: {step.Tool} {(last.Ok ? "ok" : "failed")}"));

                    if (_windows.GetForegroundWindow() is { } now && IsRunWindow(now.Handle, target))
                    {
                        foregroundWasOurs = true;
                    }

                    if (stop != null || last.Ok)
                    {
                        continue;
                    }

                    if (last.ErrorCode == nameof(WindowErrorCode.PausedByUser) || _session.IsPaused)
                    {
                        (stop, stopCode, stopError) = (StepStopReason.Paused, nameof(WindowErrorCode.PausedByUser), Paused(step, during: true));
                    }
                    else if (budget.IsCancellationRequested || (waitCut && last.ErrorCode == nameof(WindowErrorCode.WaitTimedOut)))
                    {
                        // A wait the run cut to its time left ran out: the run's time, not the wait the step asked for
                        (stop, stopCode, stopError) = (StepStopReason.TimedOut, TimedOutCode, TimedOut(step, maxDuration, during: true));
                    }
                    else if (!step.Step.Optional)
                    {
                        (stop, stopCode, stopError) = (StepStopReason.StepFailed, last.ErrorCode, Failed(last, steps.Count));
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The wait between two steps: a pause, or the run's time ran out
                var next = steps[ran.Count];
                (stop, stopCode, stopError) = _session.IsPaused
                    ? (StepStopReason.Paused, nameof(WindowErrorCode.PausedByUser), Paused(next, during: false))
                    : (StepStopReason.TimedOut, TimedOutCode, TimedOut(next, maxDuration, during: false));
            }
            finally
            {
                // Every input call releases what it pressed itself; this only makes sure nothing outlives the run
                _session.ReleaseHeldInput("the end of run_steps");
            }
        }

        return Outcome(steps, ran, stop, stopCode, stopError, _time.GetElapsedTime(started));
    }

    /// <summary>The code of a run that ran out of time: the one the UI Automation and browser reads use for theirs.</summary>
    private const string TimedOutCode = nameof(UIElementErrorCode.OperationTimedOut);

    private async Task DelayAsync(TimeSpan delay, CancellationToken run)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(run, _session.PauseToken);
        await Task.Delay(delay, _time, waiting.Token).ConfigureAwait(false);
    }

    private long Elapsed(long since) => (long)_time.GetElapsedTime(since).TotalMilliseconds;

    /// <summary>A step that ran: whether it succeeded, its code, its result's JSON and images, and how long it took.</summary>
    private sealed record Ran(CheckedStep Step, bool Ok, string? ErrorCode, JsonNode? Body, IReadOnlyList<ImageContentBlock> Images, long ElapsedMs);

    // ---- Checks before the run ----

    /// <summary>Why a step may not use one of its arguments in a run, or null. Values are read as the tool binds them.</summary>
    private static string? Forbidden(string tool, JsonObject arguments)
    {
        if (IsTrue(arguments, "allowHostTarget"))
        {
            return "allowHostTarget is not allowed in a run: act on your own terminal or chat window with a direct call, after asking the user.";
        }

        if (ValueOf<ElementSearchScope>(arguments, "scope") is ElementSearchScope.Desktop)
        {
            // Criteria with scope desktop would find (and element_action act on) an element of any window, while the step
            // names the run's
            return "scope 'desktop' is not allowed in a run: it searches every window, and a run acts on one window and its " +
                   "dialogs. Use scope window or windowAndPopups, or search the desktop with a direct call.";
        }

        switch (tool)
        {
            case "type_text" when ValueOf<TextInputMethod>(arguments, "method") is TextInputMethod.Paste or TextInputMethod.Auto:
                return $"method '{StringOf(arguments, "method")}' is not allowed in a run: a step does not write the clipboard, and " +
                       "auto pastes a long text. A step types: method unicode (the default in a run) or keys; paste a long text " +
                       "with a direct type_text call.";
            case "element_action" when ValueOf<ElementValueMode>(arguments, "mode") is ElementValueMode.Paste:
                return "mode 'paste' is not allowed in a run: a step does not write the clipboard. Use mode value or type.";
            case "element_action" when ValueOf<ElementActionKind>(arguments, "action") is ElementActionKind.WindowClose:
                return "action 'window_close' is not allowed in a run: a step closes nothing. Press the dialog's own button " +
                       "(invoke), or close the window with close_window after the run.";
            case "capture_window" when arguments["outputPath"] is not null || IsTrue(arguments, "saveToFile") || IsTrue(arguments, "overwrite") ||
                                       ValueOf<CaptureOutputMode>(arguments, "output") is CaptureOutputMode.File or CaptureOutputMode.Both:
                return "a file output (outputPath, saveToFile, overwrite, output file or both) is not allowed in a run: a step " +
                       "writes no file. Capture as an image (with return true), or save it with a direct capture_window call.";
        }

        return null;
    }

    /// <summary>
    /// The closing, quitting or locking chord in a send_keys or press_keys step's keys (the default destructive chords and
    /// the server's own), or null. A step's keyboard tool always gets a window (the run's), so its own check, which
    /// refuses these chords only without one, would let them through. Keys that do not parse are left to the tool, which
    /// refuses them with the reason.
    /// </summary>
    private string? ClosingChord(string tool, JsonObject arguments)
    {
        IReadOnlyList<InputEvent> events;
        try
        {
            switch (tool)
            {
                case "send_keys" when StringOf(arguments, "keys") is { } keys:
                    events = SendKeysParser.Parse(keys);
                    break;
                case "press_keys" when arguments["keys"] is { } keys:
                    events = PressKeysParser.Parse(keys.Deserialize<KeyList>()!, 1, KeyboardLayout.Invariant).Events;
                    break;
                default:
                    return null;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        return (InputGuardOptions.Default.FindDestructiveChord(events) ?? _keyGuard.FindDestructiveChord(events))?.Spec;
    }

    /// <summary>A fixed-value argument as its tool binds it (any case, '_' and '-' ignored), or null when it is missing or not one.</summary>
    private static TEnum? ValueOf<TEnum>(JsonObject arguments, string name) where TEnum : struct, Enum =>
        StringOf(arguments, name) is { } text && ToolEnumInfo.Get(typeof(TEnum)).TryParse(text, out var value) ? (TEnum)value! : null;

    private static string? StringOf(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsTrue(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>The characters of every string value in <paramref name="node"/>, keys not counted.</summary>
    private static int TextLength(JsonNode? node) => node switch
    {
        JsonObject o => o.Sum(p => TextLength(p.Value)),
        JsonArray a => a.Sum(TextLength),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length,
        _ => 0
    };

    /// <summary>Every string value in <paramref name="node"/> that starts with <see cref="ReferencePrefix"/>.</summary>
    private static IEnumerable<string> References(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => References(p.Value)),
        JsonArray a => a.SelectMany(References),
        JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith(ReferencePrefix, StringComparison.Ordinal) => [s],
        _ => []
    };

    /// <summary>Why a reference is wrong, or null: it must name an earlier step's id and end with an identifier.</summary>
    private static string? CheckReference(string reference, IReadOnlySet<string> earlierIds)
    {
        var parts = reference[ReferencePrefix.Length..].Split('.');
        if (parts.Length < 2)
        {
            return $"'{reference}' is no reference: write \"$steps.<id>.<path>\", such as \"$steps.find.elements[0].ref\".";
        }

        if (!earlierIds.Contains(parts[0]))
        {
            return $"'{reference}' refers to step '{parts[0]}', which is not an earlier step of the run: a reference names the id of " +
                   "a step before it.";
        }

        var path = parts[1..];
        if (path.Any(segment => !PathSegment().IsMatch(segment)))
        {
            return $"'{reference}' has a path that is not property names with optional [n] indexes, such as elements[0].ref.";
        }

        var end = PathSegment().Match(path[^1]);
        var last = end.Groups["name"].Value;
        if (!ReferenceEnds.Contains(last) || end.Groups["index"].Success ||
            (last is "x" or "y" && (path.Length < 2 || PathSegment().Match(path[^2]).Groups["name"].Value != "clickPoint")))
        {
            return $"'{reference}' ends with '{path[^1]}', but a reference ends with an identifier only: ref, handle, captureId, or " +
                   "clickPoint.x and clickPoint.y. Text from a window (a name, value or title) is never passed on without you reading it.";
        }

        return null;
    }

    // ---- During the run ----

    /// <summary>The processes whose windows are unrelated to each other: the shell (the taskbar, every folder window) and the
    /// frame host of packaged apps. For a run window of theirs, only it and the windows it owns are the run's.</summary>
    private static readonly HashSet<string> SharedHostProcesses = new(StringComparer.OrdinalIgnoreCase) { "explorer", "ApplicationFrameHost" };

    /// <summary>The parameters through which a tool takes the window it acts on.</summary>
    private static readonly string[] TargetParameters = ["windowHandle", "handle", "window"];

    /// <summary>
    /// The run's window as the run holds it: its handle, its process, and whether the other windows of that process count
    /// as the run's (not for <see cref="SharedHostProcesses"/>).
    /// </summary>
    private sealed record RunTarget(nint Handle, int ProcessId, bool OwnsItsProcess);

    /// <summary>
    /// The run's window read again once the run holds the gate, which it may have waited 30 seconds for: it must still be
    /// the window it was (the same process), and not the agent's host.
    /// </summary>
    /// <exception cref="ToolErrorException">It closed, its handle names another process's window now, or it is the host.</exception>
    private RunTarget Target(nint window, WindowInfo? before)
    {
        var now = _windows.GetWindowInfo(window);
        if (now == null || (before != null && before.ProcessId != now.ProcessId))
        {
            throw new ToolErrorException(nameof(WindowErrorCode.InvalidHandle),
                $"The run's window {window} closed while the run waited for the input gate" +
                (now == null ? "" : $"; its handle now names a window of {now.ProcessName} ({now.ProcessId})") +
                ". Nothing was done: find the window again (find_windows).");
        }

        if (now.IsAgentHost)
        {
            throw new ToolErrorException(nameof(InputErrorCode.TargetIsAgentHost),
                $"The run's window {window} ({now.ProcessName}) is your own terminal or chat window, or a window it owns. " +
                "Nothing was done: name the window you mean to act on.");
        }

        return new RunTarget(window, now.ProcessId, !SharedHostProcesses.Contains(now.ProcessName));
    }

    /// <summary>
    /// The arguments the step's tool is called with: null arguments left out, references resolved, the run's window added
    /// when the step names none, a window selector replaced by the handle it names now, type_text's method unicode when it
    /// names none, and a wait's timeoutMs cut to the time left. Or why the step is not run, with the code: a reference to a
    /// step that failed, a selector that names no single window, or a window that is not the run's.
    /// </summary>
    private (JsonObject? Arguments, string? Refusal, string? RefusalCode, bool WaitCut) Prepare(
        CheckedStep step, RunTarget target, IReadOnlyList<Ran> ran, TimeSpan left)
    {
        var label = Label(step.Number, step.Tool, step.Step.Id);
        var arguments = (JsonObject)(step.Step.Args?.DeepClone() ?? new JsonObject());

        // A null argument is one the step did not give: it must neither count as the window it names (a keyboard tool
        // without a window sends to whatever is in front) nor bind to its tool's default
        foreach (var name in arguments.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            arguments.Remove(name);
        }

        if (Resolve(arguments, ran) is { } unresolved)
        {
            return (null, $"{label} was not run: {unresolved}", ToolErrorReporting.InvalidArgumentsErrorCode, false);
        }

        if (step.Tool != WaitForWindowTool)
        {
            // An empty selector, or {"foreground": true} alone, names no window: whatever is in front is not the run's
            if (arguments["window"] is JsonObject selector && !NamesAWindow(selector))
            {
                arguments.Remove("window");
            }

            if (!WindowArguments.Any(arguments.ContainsKey))
            {
                // The tools name their window differently: the input tools windowHandle, the others handle or only window
                var declared = step.Parameters;
                if (declared.Contains("windowHandle"))
                {
                    arguments["windowHandle"] = (long)target.Handle;
                }
                else if (declared.Contains("handle"))
                {
                    arguments["handle"] = (long)target.Handle;
                }
                else if (declared.Contains("window"))
                {
                    arguments["window"] = new JsonObject { ["handle"] = (long)target.Handle };
                }
            }

            if (!WindowArguments.Any(arguments.ContainsKey) && TargetParameters.Any(step.Parameters.Contains))
            {
                return (null, $"{label} was not run: it names no window, and its tool would act on the window in front.",
                    ToolErrorReporting.InvalidArgumentsErrorCode, false);
            }
        }

        // Missing or null: a null would bind to type_text's default, auto, which pastes a long text
        if (step.Tool == "type_text" && arguments["method"] is null)
        {
            arguments["method"] = "unicode";
        }

        var waitCut = false;
        if (WaitTools.Contains(step.Tool))
        {
            var leftMs = Math.Max(0, (long)left.TotalMilliseconds - WaitMarginMs);
            var asked = arguments["timeoutMs"] is JsonValue given && given.TryGetValue<long>(out var value) ? value : long.MaxValue;
            if (asked > leftMs)
            {
                arguments["timeoutMs"] = leftMs;
                waitCut = true;
            }
        }

        if (CheckWindows(step, arguments, target) is { } refused)
        {
            return (null, $"{label} was not run: {refused.Error}", refused.Code, false);
        }

        return (arguments, null, null, waitCut);
    }

    /// <summary>True when a selector names a window: some filter besides foreground.</summary>
    private static bool NamesAWindow(JsonObject selector) =>
        selector.Any(p => p.Value is not null && !string.Equals(p.Key, "foreground", StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces each reference with the value it names; why one cannot be, or null.</summary>
    private static string? Resolve(JsonNode node, IReadOnlyList<Ran> ran)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in o.Select(p => p.Key).ToList())
                {
                    if (o[name] is { } child && Replace(child, ran, value => o[name] = value) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var index = i;
                    if (a[i] is { } child && Replace(child, ran, value => a[index] = value) is { } error)
                    {
                        return error;
                    }
                }

                return null;
            default:
                return null;
        }

        static string? Replace(JsonNode child, IReadOnlyList<Ran> ran, Action<JsonNode> set)
        {
            if (child is not JsonValue value || !value.TryGetValue<string>(out var text) || !text.StartsWith(ReferencePrefix, StringComparison.Ordinal))
            {
                return Resolve(child, ran);
            }

            var parts = text[ReferencePrefix.Length..].Split('.');
            var source = ran.LastOrDefault(r => r.Step.Step.Id == parts[0]);
            if (source is not { Ok: true, Body: { } body })
            {
                return $"'{text}' refers to step '{parts[0]}', which failed, so it has no result to take the value from.";
            }

            JsonNode? current = body;
            foreach (var segment in parts[1..])
            {
                var match = PathSegment().Match(segment);
                current = current is JsonObject o ? o[match.Groups["name"].Value] : null;
                if (match.Groups["index"].Success)
                {
                    var index = int.Parse(match.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    current = current is JsonArray a && index < a.Count ? a[index] : null;
                }
            }

            var end = parts[^1];
            var resolved = current is JsonValue found &&
                           (end is "ref" or "captureId" ? found.TryGetValue<string>(out _) : found.TryGetValue<long>(out _));
            if (!resolved)
            {
                return $"'{text}' names nothing in step '{parts[0]}''s result (or not {(end is "ref" or "captureId" ? "a string" : "a whole number")}).";
            }

            set(current!.DeepClone());
            return null;
        }
    }

    /// <summary>
    /// Why a window the step names is not one of the run's, with the code, or null. A selector (not wait_for_window's,
    /// which names what it waits for) is resolved here and replaced by the handle it names, so the tool acts on the window
    /// this check passed rather than resolving the selector again; one that names no single window is not run.
    /// </summary>
    private (string Error, string Code)? CheckWindows(CheckedStep step, JsonObject arguments, RunTarget target)
    {
        var named = new List<nint>();
        foreach (var name in HandleArguments)
        {
            if (arguments[name] is JsonValue value && value.TryGetValue<long>(out var handle))
            {
                named.Add((nint)handle);
            }
        }

        if (step.Tool != WaitForWindowTool && arguments["window"] is JsonObject selector)
        {
            var (selected, error, code) = SelectorWindow(selector);
            if (selected is not { } handle)
            {
                return ($"its window selector names no single window: {error} Name the window by its handle (find_windows).", code);
            }

            arguments["window"] = new JsonObject { ["handle"] = (long)handle };
            named.Add(handle);
        }

        foreach (var name in RefArguments)
        {
            if (StringOf(arguments, name) is { } elementRef && _elements.Lookup(elementRef) is { ErrorCode: UIElementErrorCode.None } found &&
                found.WindowHandle != nint.Zero)
            {
                named.Add(found.WindowHandle);
            }
        }

        foreach (var handle in named)
        {
            if (!IsRunWindow(handle, target))
            {
                var info = _windows.GetWindowInfo(handle);
                var described = info == null
                    ? $"window {handle} (no window)"
                    : $"window {handle} of process {info.ProcessName} ({info.ProcessId})";
                return ($"it names {described}, not the run's window, a window it owns or another window of its process. A run " +
                        "acts on one window and its dialogs; call the tool directly for another window.",
                    ToolErrorReporting.InvalidArgumentsErrorCode);
            }
        }

        return null;
    }

    /// <summary>The one window a step's selector names now; or why there is none, and the code.</summary>
    private (nint? Handle, string? Error, string Code) SelectorWindow(JsonObject selector)
    {
        WindowSelector? parsed;
        try
        {
            parsed = selector.Deserialize<WindowSelector>(ToolJson.SerializerOptions);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            parsed = null;
        }

        if (parsed == null)
        {
            return (null, "it is not a window selector.", ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        if (parsed.IsHandleOnly)
        {
            return ((nint)parsed.Handle!.Value, null, ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        var resolution = _windows.ResolveWindow(parsed);
        return resolution.Success
            ? (resolution.Handle, null, ToolErrorReporting.InvalidArgumentsErrorCode)
            : (null, resolution.Error ?? "no window matches it.", resolution.ErrorCode ?? ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    /// <summary>
    /// True when <paramref name="handle"/> is the run's window, a window it owns (directly or through other owned windows) or
    /// another window of its process (not for a process that hosts unrelated windows, <see cref="SharedHostProcesses"/>).
    /// </summary>
    private bool IsRunWindow(nint handle, RunTarget target)
    {
        if (handle == target.Handle)
        {
            return true;
        }

        var info = _windows.GetWindowInfo(handle);
        if (info == null)
        {
            return false;
        }

        if (target.OwnsItsProcess && target.ProcessId != 0 && info.ProcessId == target.ProcessId)
        {
            return true;
        }

        var owner = info.OwnerHandle;
        for (var i = 0; i < MaxOwners && owner is { } current && current != nint.Zero; i++)
        {
            if (current == target.Handle)
            {
                return true;
            }

            owner = _windows.GetWindowInfo(current)?.OwnerHandle;
        }

        return false;
    }

    // ---- The result ----

    private static StepRunOutcome Outcome(
        IReadOnlyList<CheckedStep> steps, IReadOnlyList<Ran> ran, StepStopReason? stop, string? stopCode, string? stopError, TimeSpan elapsed)
    {
        // The steps whose whole result is returned: a failed one, the last that ran, and the return true ones
        var lastRan = ran.Count - 1;
        var full = Enumerable.Range(0, ran.Count).Where(i => !ran[i].Ok || ran[i].Step.Step.Return || i == lastRan).ToList();

        // Within MaxResultTextLength: the failed and the last first, then in step order
        var returned = new HashSet<int>();
        var omitted = new HashSet<int>();
        var room = MaxResultTextLength;
        foreach (var i in full.OrderBy(i => i == lastRan || !ran[i].Ok ? 0 : 1).ThenBy(i => i))
        {
            if (ran[i].Body is not { } body)
            {
                continue;
            }

            var length = body.ToJsonString().Length;
            if (length <= room)
            {
                returned.Add(i);
                room -= length;
            }
            else
            {
                omitted.Add(i);
            }
        }

        var outcomes = new List<StepOutcome>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (i >= ran.Count)
            {
                outcomes.Add(new StepOutcome(step.Tool, StepStatus.Skipped, step.Step.Id));
                continue;
            }

            var r = ran[i];
            outcomes.Add(new StepOutcome(step.Tool, r.Ok ? StepStatus.Ok : StepStatus.Failed, step.Step.Id, r.ElapsedMs, r.ErrorCode,
                returned.Contains(i) ? r.Body : null, omitted.Contains(i) ? true : null));
        }

        // At most MaxImages, of the ok steps whose result is returned: the last step's first, then in step order
        var images = full.Where(i => ran[i].Ok)
            .SelectMany(i => ran[i].Images.Select(image => (Step: i, Image: image)))
            .OrderBy(x => x.Step == lastRan ? 0 : 1).ThenBy(x => x.Step)
            .Take(MaxImages)
            .OrderBy(x => x.Step)
            .Select(x => x.Image)
            .ToList();

        var success = stop == null && ran.All(r => r.Ok || r.Step.Step.Optional);
        return new StepRunOutcome(
            new RunStepsResult(success, ran.Count, steps.Count, (long)elapsed.TotalMilliseconds, outcomes, stop, stopCode, stopError),
            images);
    }

    /// <summary>The first text block's JSON, or null.</summary>
    private static JsonNode? Body(CallToolResult result)
    {
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            if (block.Text is { Length: > 0 } text && text[0] is '{' or '[')
            {
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    // Not JSON: the next block, or none
                }
            }
        }

        return null;
    }

    private static string? ErrorCodeOf(JsonNode? body) =>
        body is JsonObject o && o["errorCode"] is JsonValue code && code.TryGetValue<string>(out var text) ? text : null;

    private static string Label(int number, string tool, string? id, bool capital = true) =>
        $"{(capital ? 'S' : 's')}tep {number} ({tool}{(id == null ? null : $", '{id}'")})";

    private static string Label(CheckedStep step, bool capital = true) => Label(step.Number, step.Tool, step.Step.Id, capital);

    private static string SkippedAfter(CheckedStep step, int total) =>
        step.Number == total ? "It was the last step"
        : step.Number + 1 == total ? $"Step {total} was skipped"
        : $"Steps {step.Number + 1}-{total} were skipped";

    private static string Failed(Ran failed, int total)
    {
        var error = failed.Body is JsonObject o && o["error"] is JsonValue e && e.TryGetValue<string>(out var text) ? text : "(no error message)";
        return $"{Label(failed.Step.Number, failed.Step.Tool, failed.Step.Step.Id)} failed with {failed.ErrorCode}: {error} " +
               $"{SkippedAfter(failed.Step, total)}; the steps before it ran and are not undone.";
    }

    private static string Paused(CheckedStep step, bool during) =>
        $"The user paused input with the pause hotkey {(during ? "during" : "before")} {Label(step, capital: false)}; " +
        "the run stopped and the steps after it were skipped. Stop and ask the user how to continue; never resume input yourself.";

    private static string TimedOut(CheckedStep step, TimeSpan maxDuration, bool during) =>
        $"maxDurationMs ({maxDuration.TotalMilliseconds:0}) ran out {(during ? "during" : "before")} " +
        $"{Label(step, capital: false)}; the steps after it were skipped. A run holds the input " +
        "gate, so it is bounded: wait for slow windows in a run of their own, or raise maxDurationMs (at most 60000).";

    private static string ForegroundMoved(CheckedStep step, WindowInfo front) =>
        $"Before {Label(step, capital: false)}, window {front.Handle} of process {front.ProcessName} " +
        $"({front.ProcessId}) was in front instead of the run's window: the user, or another program, took the foreground. The run " +
        "stopped rather than take it back, and that step and the ones after it were skipped. Look at the screen (get_desktop_state) " +
        "and ask the user before you go on.";

    private static ToolErrorException Invalid(string message, bool addNothingWasDone = true) =>
        new(ToolErrorReporting.InvalidArgumentsErrorCode,
            addNothingWasDone ? message + " Nothing was done: the whole run was refused." : message);
}
