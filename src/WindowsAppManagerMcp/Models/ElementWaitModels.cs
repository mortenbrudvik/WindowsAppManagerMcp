using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>What wait_for_element waits for (roadmap M3.8).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "condition", NullMeans = "exists")]
public enum ElementWaitCondition
{
    /// <summary>An element the criteria match is in the window's tree (offscreen or disabled included); for a ref, it resolves.</summary>
    [JsonStringEnumMemberName("exists")]
    Exists,

    /// <summary>No element the criteria match is left (a complete search found none); for a ref, its element is gone.</summary>
    [JsonStringEnumMemberName("gone")]
    Gone,

    /// <summary>The element is enabled.</summary>
    [JsonStringEnumMemberName("enabled")]
    Enabled,

    /// <summary>The element is on screen: not reported offscreen, and with a size.</summary>
    [JsonStringEnumMemberName("visible")]
    Visible,

    /// <summary>The element has keyboard focus.</summary>
    [JsonStringEnumMemberName("focused")]
    Focused,

    /// <summary>The element's name is exactly the expected text.</summary>
    [JsonStringEnumMemberName("name_equals")]
    NameEquals,

    /// <summary>The element's Value pattern holds exactly the expected text (never checked for a password field).</summary>
    [JsonStringEnumMemberName("value_equals")]
    ValueEquals,

    /// <summary>The element's Toggle pattern is in the expected state: on, off or indeterminate.</summary>
    [JsonStringEnumMemberName("toggle_state")]
    ToggleState
}

/// <summary>
/// A wait for an element condition (roadmap M3.8): the element named by <see cref="Ref"/>, or the elements
/// <see cref="Criteria"/> match in <see cref="Window"/> (or below <see cref="ElementQuery.WithinRef"/>'s element), and the
/// condition with its expected value and timing.
/// </summary>
public sealed record ElementWaitRequest
{
    /// <summary>
    /// The window to search, waited for first (wait_for_window's exists, so a window that is still opening counts once it
    /// is there). Null with <see cref="Ref"/> or with the criteria's withinRef, which name their window.
    /// </summary>
    public WindowSelector? Window { get; init; }

    /// <summary>The element to wait on, as a ref from an earlier read. Null with <see cref="Criteria"/>.</summary>
    public string? Ref { get; init; }

    /// <summary>
    /// find_ui_elements' request model: the elements to wait on. Its window handle is set by the wait once the window is
    /// found; visibleOnly, enabledOnly, maxResults, detail and the timeout are set by the wait too (the condition decides
    /// what counts). Null with <see cref="Ref"/>.
    /// </summary>
    public ElementQuery? Criteria { get; init; }

    public ElementWaitCondition Condition { get; init; } = ElementWaitCondition.Exists;

    /// <summary>
    /// For name_equals and value_equals the exact text (case-sensitive); for toggle_state "on", "off" or "indeterminate".
    /// Null for the other conditions.
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>How long to wait in all, clamped to 0-60000 ms; 0 checks once.</summary>
    public int TimeoutMs { get; init; } = 10_000;
}

/// <summary>
/// wait_for_element's result (roadmap M3.8). Success is Satisfied: a wait that was not satisfied goes out with isError
/// (ToolOutcome) and its errorCode, as wait_for_window's does: WaitTimedOut after timeoutMs (TimedOut true), or why the
/// wait ended early (the window closed, a stale ref, an argument that cannot be checked).
/// </summary>
/// <param name="TimedOut">True only when timeoutMs passed; an element that is simply not there is not a timeout until
/// then, and a window that closed or a stale ref ends the wait with its own code.</param>
/// <param name="ElapsedMs">How long the wait took, the wait for the window included.</param>
/// <param name="Polls">How often the element was checked.</param>
/// <param name="WindowHandle">The window searched, once it was found.</param>
/// <param name="Element">Satisfied: the element that satisfies the condition, with its ref (none for gone). Otherwise the
/// element as last seen (the first match, or the ref's element), when there was one.</param>
/// <param name="MatchCount">For criteria: how many elements matched at the last check.</param>
/// <param name="LastObservedState">Not satisfied: what the last check saw, such as "Button 'OK' (w4120035e9) is disabled".
/// A password field's value is never read, so it never appears here.</param>
/// <param name="AccessibilityWarmup">Set when the window shows web content and the first check waited for its
/// accessibility tree (roadmap M3.3).</param>
/// <param name="Candidates">AmbiguousWindow: the windows the selector matched.</param>
/// <param name="Warning">Something to check although the wait was satisfied: a gone wait whose element or window was
/// already gone when it started, or a window that closed.</param>
public sealed record WaitForElementResult(
    bool Success,
    bool Satisfied,
    bool TimedOut,
    long ElapsedMs,
    int Polls,
    long? WindowHandle = null,
    FoundElement? Element = null,
    int? MatchCount = null,
    string? LastObservedState = null,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    IReadOnlyList<WindowCandidateDto>? Candidates = null,
    string? Warning = null,
    string? Error = null,
    string? ErrorCode = null);
