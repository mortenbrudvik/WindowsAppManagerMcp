using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>What wait_for_window waits for (roadmap M3.7).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "condition", NullMeans = "exists")]
public enum WindowWaitCondition
{
    /// <summary>A window the selector matches is open (the listing's rules: visible, minimized included).</summary>
    [JsonStringEnumMemberName("exists")]
    Exists,

    /// <summary>No window the selector matches is open any more (by handle: the handle is no window).</summary>
    [JsonStringEnumMemberName("gone")]
    Gone,

    /// <summary>A window the selector matches is the foreground window.</summary>
    [JsonStringEnumMemberName("foreground")]
    Foreground,

    /// <summary>A window the selector matches is on screen: visible, not minimized and not cloaked.</summary>
    [JsonStringEnumMemberName("visible")]
    Visible,

    /// <summary>The one window the selector names has a title other than the previous one.</summary>
    [JsonStringEnumMemberName("title_changed")]
    TitleChanged,

    /// <summary>
    /// The one window the selector matches is ready for input: its process reports input idle (WaitForInputIdle, best
    /// effort) and Windows does not report the window hung.
    /// </summary>
    [JsonStringEnumMemberName("input_idle")]
    InputIdle
}

/// <summary>A wait for a window condition (roadmap M3.7): which windows count, the condition and the timing.</summary>
public sealed record WindowConditionRequest
{
    /// <summary>The windows to wait for, resolved on every poll with the rules of IWindowService.ResolveWindow.</summary>
    public required WindowSelector Window { get; init; }

    public WindowWaitCondition Condition { get; init; } = WindowWaitCondition.Exists;

    /// <summary>
    /// Only windows that were not open (shown: visible, and not cloaked unless the selector takes cloaked windows) when the
    /// wait started count; a window hidden then and shown during the wait counts.
    /// </summary>
    public bool NewOnly { get; init; }

    /// <summary>Windows that never count (the ones the caller saw before it acted).</summary>
    public IReadOnlyCollection<nint> ExcludeHandles { get; init; } = [];

    /// <summary>For <see cref="WindowWaitCondition.TitleChanged"/>: the title to compare with; null is the title when the wait starts.</summary>
    public string? PreviousTitle { get; init; }

    /// <summary>How long to wait in all, clamped to 0-60000 ms.</summary>
    public int TimeoutMs { get; init; } = 10_000;

    /// <summary>How long the condition must hold before it counts (a splash screen, a dialog that closes and reopens).</summary>
    public int StableMs { get; init; } = 300;
}

/// <summary>How an input_idle wait knew that the window's process is ready for input.</summary>
public enum IdleCheck
{
    /// <summary>WaitForInputIdle reported the process idle, and the window is responding.</summary>
    InputIdle,

    /// <summary>
    /// Windows could not report input idle (a console program, a process without a message loop, or one the server cannot
    /// open), so only the window's responsiveness was checked.
    /// </summary>
    RespondingOnly
}

/// <summary>
/// How a window condition wait ended (roadmap M3.7). Satisfied, or not: timed out (<see cref="TimedOut"/>), or ended early
/// because the condition can no longer be met (<see cref="ErrorCode"/>: InvalidHandle, AmbiguousWindow, or InvalidArguments
/// for a selector that cannot be used).
/// </summary>
public sealed record WindowConditionResult
{
    public bool Satisfied { get; init; }

    public bool TimedOut { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>
    /// Satisfied: the one window that satisfies the condition (null when several do; see <see cref="Candidates"/>).
    /// Otherwise the window as last seen, when one is relevant: the window still open (gone), the matching window that is
    /// not in front, not on screen or not idle, or the window whose title did not change.
    /// </summary>
    public WindowInfo? Window { get; init; }

    /// <summary>The windows that satisfy the condition, or are in the way, when there are several (up to 5, front to back).</summary>
    public IReadOnlyList<WindowInfo> Candidates { get; init; } = [];

    /// <summary>How many windows <see cref="Candidates"/> stands for.</summary>
    public int MatchCount { get; init; }

    /// <summary>For title_changed: the title the window's title was compared with.</summary>
    public string? PreviousTitle { get; init; }

    /// <summary>For a satisfied input_idle wait: how readiness was checked.</summary>
    public IdleCheck? IdleCheck { get; init; }

    /// <summary>Null when satisfied; WaitTimedOut on timeout; otherwise why the wait ended early.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>What was last seen and what to do, when not satisfied.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Something to check although the wait was satisfied: a gone wait whose selector matched no window when it started
    /// (a misspelled selector is gone at once).
    /// </summary>
    public string? Warning { get; init; }
}
