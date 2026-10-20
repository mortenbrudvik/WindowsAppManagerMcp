using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The tools a run_steps step may call (roadmap M6.2, owner decision D7): the window, element, wait, input and capture tools
/// of a form fill or a keyboard sequence, by their own names, after roadmap M6.1 (mouse_click, not the click trio 3.0.0
/// removed). No tool that asks the user, closes, kills, launches, or writes the clipboard, a file or a layout; no browser tool,
/// and not run_steps itself. Some arguments of the tools on the list are refused in a step too (StepRunner.Check): pasting,
/// file output, closing a window through a pattern or a closing chord, a search of every window (scope desktop), and
/// allowHostTarget. The keys a step sends otherwise act in the run's window as a direct call's do (ctrl+s saves).
/// </summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "step tool")]
public enum StepTool
{
    [JsonStringEnumMemberName("focus_window")]
    FocusWindow,

    [JsonStringEnumMemberName("find_ui_elements")]
    FindUiElements,

    [JsonStringEnumMemberName("element_action")]
    ElementAction,

    [JsonStringEnumMemberName("type_text")]
    TypeText,

    [JsonStringEnumMemberName("send_keys")]
    SendKeys,

    [JsonStringEnumMemberName("press_keys")]
    PressKeys,

    [JsonStringEnumMemberName("mouse_click")]
    MouseClick,

    [JsonStringEnumMemberName("drag")]
    Drag,

    [JsonStringEnumMemberName("mouse_move")]
    MouseMove,

    [JsonStringEnumMemberName("scroll")]
    Scroll,

    [JsonStringEnumMemberName("wait_for_window")]
    WaitForWindow,

    [JsonStringEnumMemberName("wait_for_element")]
    WaitForElement,

    [JsonStringEnumMemberName("get_focused_element")]
    GetFocusedElement,

    [JsonStringEnumMemberName("capture_window")]
    CaptureWindow
}

/// <summary>One step of a run_steps call: a tool of <see cref="StepTool"/> with that tool's own arguments.</summary>
/// <remarks>
/// An unknown member is refused (InvalidArguments), so it is never ignored: a step written with "arguments" for args would
/// otherwise run without them, and be given the run's window instead of the one it named. <see cref="Tool"/> is nullable
/// so that a step without one is refused (StepRunner.Check) instead of binding the enum's first value, focus_window; the
/// input schema lists it as a required string of the tools, without null (<see cref="ToolRequiredAttribute"/>), since a
/// null is refused too.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record RunStep(
    [property: Description("The tool to call")]
    [property: ToolRequired]
    StepTool? Tool,
    [property: Description("The tool's own arguments, as a direct call takes them. A string \"$steps.<id>.<path>\" is an earlier step's ref, handle, captureId or clickPoint x/y")]
    JsonObject? Args = null,
    [property: Description("A name for the step ([A-Za-z0-9_-], at most 32), unique in the run: later steps refer to its result by it")]
    string? Id = null,
    [property: Description("True: the run goes on when this step fails (a dialog that may not appear). Default false: a failure stops the run")]
    bool Optional = false,
    [property: Description("True: the step's whole result (and its image) is returned, not only its status")]
    bool Return = false
);

/// <summary>How a step of a run ended.</summary>
public enum StepStatus
{
    /// <summary>The step ran and succeeded.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>The step failed: its tool reported an error, or the run refused to call it (its errorCode says why).</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The run stopped before the step.</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped
}

/// <summary>Why a run stopped before its last step.</summary>
public enum StepStopReason
{
    /// <summary>A step that is not optional failed.</summary>
    [JsonStringEnumMemberName("stepFailed")]
    StepFailed,

    /// <summary>Input was paused (PausedByUser): by the user, or by the server itself when it could not read the shared pause.</summary>
    [JsonStringEnumMemberName("paused")]
    Paused,

    /// <summary>maxDurationMs ran out (OperationTimedOut).</summary>
    [JsonStringEnumMemberName("timedOut")]
    TimedOut,

    /// <summary>A window outside the run's window took the foreground after the run's window had it (TargetNotForeground).</summary>
    [JsonStringEnumMemberName("foregroundMoved")]
    ForegroundMoved,

    /// <summary>With WAM_TAKEOVER_DETECTION=stop: the user used the keyboard or mouse during the run (UserInterrupted).</summary>
    [JsonStringEnumMemberName("userInterrupted")]
    UserInterrupted
}

/// <summary>What one step of a run did.</summary>
/// <param name="Tool">The step's tool.</param>
/// <param name="Status">ok, failed or skipped.</param>
/// <param name="Id">The step's id, when it has one.</param>
/// <param name="ElapsedMs">How long the step took; null when it was skipped.</param>
/// <param name="ErrorCode">A failed step's errorCode.</param>
/// <param name="Result">
/// The step's own result (its JSON): for a failed step, the last step that ran, and a step with return true, within the
/// run's bound on them all (StepRunner.MaxResultTextLength); null otherwise.
/// </param>
/// <param name="ResultOmitted">
/// True when the step's result, or the warnings of a step whose result is not returned, would have been returned but did not
/// fit the run's bound.
/// </param>
/// <param name="Warnings">
/// The warnings of a step that ran and whose result is not returned (an ok step in the middle of the run): its result's
/// warnings and warning, as its tool gives them (issues #9 and #10: an ok press_keys step whose chord Windows may take, or
/// whose character went out with no key, says so). Within the run's bound, after the results; null when it has none, or its
/// result is returned, which holds them.
/// </param>
public record StepOutcome(
    string Tool,
    StepStatus Status,
    string? Id = null,
    long? ElapsedMs = null,
    string? ErrorCode = null,
    JsonNode? Result = null,
    bool? ResultOmitted = null,
    IReadOnlyList<string>? Warnings = null
);

/// <summary>The result of run_steps (roadmap M6.2).</summary>
/// <param name="Success">True when every step that is not optional ran and succeeded.</param>
/// <param name="Ran">How many steps ran (ok or failed).</param>
/// <param name="Total">How many steps the run had.</param>
/// <param name="ElapsedMs">How long the run took, the wait for the input gate included.</param>
/// <param name="Steps">Every step, in order.</param>
/// <param name="StoppedBy">Why the run stopped before its last step; null when it ran them all.</param>
/// <param name="ErrorCode">The code of what stopped the run (the failed step's own code).</param>
/// <param name="Error">What stopped the run, and what that means for the steps after it.</param>
/// <param name="Warning">
/// Takeover detection (roadmap M7.4, TakeoverMessages.For): with WAM_TAKEOVER_DETECTION=warn, the user used the keyboard or
/// mouse during the run; with warn or stop, detection did not work for all or part of the run, so the user's input was not
/// watched then. Null otherwise.
/// </param>
public record RunStepsResult(
    bool Success,
    int Ran,
    int Total,
    long ElapsedMs,
    IReadOnlyList<StepOutcome> Steps,
    StepStopReason? StoppedBy = null,
    string? ErrorCode = null,
    string? Error = null,
    string? Warning = null
);
