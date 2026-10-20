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

    /// <summary>A window the selector matches is minimized (issue #14).</summary>
    [JsonStringEnumMemberName("minimized")]
    Minimized,

    /// <summary>A window the selector matches is maximized, and not minimized (issue #14).</summary>
    [JsonStringEnumMemberName("maximized")]
    Maximized,

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

    /// <summary>
    /// Every window that counts must hold the condition (visible, minimized or maximized), and at least one must count
    /// (issue #14). The windows that count must stay the same for <see cref="StableMs"/>.
    /// </summary>
    public bool All { get; init; }

    /// <summary>
    /// With exists: how many windows must count (issue #14). The windows that count must stay the same for
    /// <see cref="StableMs"/>; a count whose most is 0 waits as gone.
    /// </summary>
    public WindowCount? Count { get; init; }

    /// <summary>How long to wait in all, clamped to 0-60000 ms.</summary>
    public int TimeoutMs { get; init; } = 10_000;

    /// <summary>How long the condition must hold before it counts (a splash screen, a dialog that closes and reopens).</summary>
    public int StableMs { get; init; } = 300;
}

/// <summary>
/// wait_for_window's count (issue #14): how many windows must count, as exactly a number or as a range (at least, at most, or
/// both). The tool checks it (InvalidArguments otherwise): at least one member, none negative, exactly alone, atLeast not above
/// atMost, and not the range that always holds. An unknown member is refused (InvalidArguments); the range is a method, not
/// a property, so that a "min" or "max" is unknown rather than an ignored member.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WindowCount
{
    /// <summary>At least this many windows.</summary>
    public int? AtLeast { get; init; }

    /// <summary>Exactly this many windows; not with <see cref="AtLeast"/> or <see cref="AtMost"/>.</summary>
    public int? Exactly { get; init; }

    /// <summary>At most this many windows.</summary>
    public int? AtMost { get; init; }

    /// <summary>The fewest windows that satisfy it, and the most (null for no most).</summary>
    public (int Fewest, int? Most) Range() => (Exactly ?? AtLeast ?? 0, Exactly ?? AtMost);

    /// <summary>The count for messages: "exactly 3", "at least 2", "at least 2 and at most 4".</summary>
    public string Describe() =>
        Exactly is { } exactly ? $"exactly {exactly}"
        : AtLeast is { } atLeast && AtMost is { } atMost ? $"at least {atLeast} and at most {atMost}"
        : AtLeast is { } least ? $"at least {least}"
        : $"at most {AtMost}";
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

    /// <summary>
    /// The windows that satisfy the condition, or are in the way, when there are several, and with all or count the windows
    /// that count, front to back: up to <see cref="Services.WindowWaitService.MaxSetWindows"/>, or up to 5 for an early end
    /// (AmbiguousWindow and the other resolution errors).
    /// </summary>
    public IReadOnlyList<WindowInfo> Candidates { get; init; } = [];

    /// <summary>How many windows <see cref="Candidates"/> stands for.</summary>
    public int MatchCount { get; init; }

    /// <summary>
    /// A wait with all or count (issue #14): <see cref="Candidates"/> lists the windows that count and
    /// <see cref="MatchCount"/> is how many, even 0 or 1, and <see cref="Window"/> is null.
    /// </summary>
    public bool IsSet { get; init; }

    /// <summary>For title_changed: the title the window's title was compared with.</summary>
    public string? PreviousTitle { get; init; }

    /// <summary>For a satisfied input_idle wait: how readiness was checked.</summary>
    public IdleCheck? IdleCheck { get; init; }

    /// <summary>Null when satisfied; WaitTimedOut on timeout; otherwise why the wait ended early.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>What was last seen and what to do, when not satisfied.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Something to check although the wait was satisfied: a gone wait (or a count whose most is 0) whose selector matched no
    /// window when it started (a misspelled selector is gone at once).
    /// </summary>
    public string? Warning { get; init; }
}
