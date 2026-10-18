using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A part of get_desktop_state's observation (roadmap M3.12) that the caller can leave out. The window in front, the cursor,
/// the held modifiers, Caps Lock and the pause are always read: they cost nothing.
/// </summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "include value")]
public enum DesktopStateSection
{
    /// <summary>The user-facing windows, front first, as compact rows.</summary>
    [JsonStringEnumMemberName("windows")]
    Windows,

    /// <summary>The element that has keyboard focus, with its ref.</summary>
    [JsonStringEnumMemberName("focus")]
    Focus,

    /// <summary>The target window's interactive elements (and its open popups'), one text line each with refs.</summary>
    [JsonStringEnumMemberName("elements")]
    Elements,

    /// <summary>The target window's open menus, dropdowns, flyouts and dialogs, with refs.</summary>
    [JsonStringEnumMemberName("popups")]
    Popups,

    /// <summary>A capture of the target window as image content, with a captureId. Off by default.</summary>
    [JsonStringEnumMemberName("screenshot")]
    Screenshot
}

/// <summary>
/// get_desktop_state's scope (roadmap M3.12): the window alone, or with its popups. A subset of
/// <see cref="ElementSearchScope"/>: the observation reads one window, so its schema does not offer find_ui_elements' desktop.
/// </summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode,
    "get_desktop_state scope (it reads one window and its popups; find_ui_elements' scope 'desktop' searches every window)")]
public enum DesktopStateScope
{
    /// <summary>Only the window.</summary>
    [JsonStringEnumMemberName("window")]
    Window,

    /// <summary>The window and its open menus, dropdowns, flyouts and dialogs.</summary>
    [JsonStringEnumMemberName("windowAndPopups")]
    WindowAndPopups
}

/// <summary>
/// What an action tool returns about the desktop after it acted (roadmap M3.12): nothing, get_desktop_state's observation, or
/// that observation with a capture of the window. element_action has it, and so do type_text,
/// send_keys and respond_to_dialog (roadmap M3.F32), and mouse_click, drag and press_keys (roadmap M4.3).
/// </summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "observe value", NullMeans = "none")]
public enum ObserveMode
{
    /// <summary>Only the action's own result.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Also get_desktop_state's default observation of the element's window, after the action (<c>observed</c>).</summary>
    [JsonStringEnumMemberName("state")]
    State,

    /// <summary>As state, plus a capture of the window as image content.</summary>
    [JsonStringEnumMemberName("screenshot")]
    Screenshot
}

/// <summary>What get_desktop_state reads (roadmap M3.12); the tool builds it from its arguments.</summary>
/// <param name="Window">The window to read the elements, popups and capture of; null for the window in front.</param>
/// <param name="Include">The sections to read besides the ones that are always read.</param>
/// <param name="MaxElements">The most element lines, the popups' and the window's together.</param>
/// <param name="InteractableOnly">Only elements that are enabled and focusable or have an action pattern.</param>
/// <param name="Scope">Whether the target's popups are walked too; null for find_ui_elements' default (windowAndPopups when the
/// window, a window it owns or another window of its process is in front, else window). Never desktop.</param>
/// <param name="MaxWindows">The most window rows.</param>
/// <param name="ScreenshotMaxLongEdge">The capture's long edge in pixels.</param>
/// <param name="Timeout">The one deadline of the whole observation: what is not read by then is reported as not read.</param>
/// <param name="ScreenshotMarks">Draw the window's element lines on the screenshot as Set-of-Marks marks, each tagged with its
/// line's ref (roadmap M3.F32), less the window part the refs share (the screenshot's markPrefix, roadmap M3.F38). The
/// capture then waits for the elements walk, whose elements it marks; it needs both sections.</param>
public sealed record DesktopStateRequest(
    nint? Window,
    IReadOnlySet<DesktopStateSection> Include,
    int MaxElements,
    bool InteractableOnly,
    ElementSearchScope? Scope,
    int MaxWindows,
    int ScreenshotMaxLongEdge,
    TimeSpan Timeout,
    bool ScreenshotMarks = false)
{
    /// <summary>The sections get_desktop_state reads when include is left out: all but the screenshot.</summary>
    public static IReadOnlySet<DesktopStateSection> DefaultInclude { get; } = new HashSet<DesktopStateSection>
    {
        DesktopStateSection.Windows, DesktopStateSection.Focus, DesktopStateSection.Elements, DesktopStateSection.Popups
    };
}

/// <summary>A window as get_desktop_state names it: the window in front.</summary>
/// <param name="Title">Its title, cut to <see cref="AppText.MaxLength"/> characters (roadmap M3.F14), as every
/// title of the observation is.</param>
/// <param name="IsAgentHost">True for the agent's own terminal, editor or chat window (or a window it owns); left out otherwise.</param>
public sealed record DesktopWindow(long Handle, string Title, string ProcessName, int ProcessId, bool? IsAgentHost = null);

/// <summary>One row of get_desktop_state's window list: a user-facing window, front first.</summary>
/// <param name="Title">Its title, cut as <see cref="DesktopWindow.Title"/> is.</param>
/// <param name="Process">The process name (for a UWP app, the app's own process).</param>
/// <param name="State">normal, minimized or maximized.</param>
/// <param name="Monitor">The index of the monitor the window is on (most of it), as get_monitors numbers them.</param>
/// <param name="IsAgentHost">True for the agent's own terminal, editor or chat window; left out otherwise.</param>
public sealed record DesktopWindowRow(long Handle, string Title, string Process, string State, int Monitor, bool? IsAgentHost = null);

/// <summary>The element that has keyboard focus, as get_desktop_state reports it (its value is never returned here).</summary>
/// <param name="Ref">The element's ref, for element_action and the other element tools; null when it has no runtime id.</param>
/// <param name="AutomationId">Cut to <see cref="AppText.MaxLength"/> characters (roadmap M3.F14).</param>
/// <param name="WindowHandle">The top-level window it is in.</param>
/// <param name="IsPassword">True for a password field; left out otherwise.</param>
public sealed record DesktopFocus(
    [property: JsonPropertyOrder(-1)] string? Ref,
    string ControlType,
    string? Name,
    string? AutomationId,
    BoundsDto? Bounds,
    long? WindowHandle,
    string? ProcessName,
    bool? IsPassword = null);

/// <summary>The input state get_desktop_state reports: the pause and the keys Windows reports, read only.</summary>
/// <param name="Paused">True while the server's input is paused (PausedBy says how): input tools return PausedByUser.</param>
/// <param name="PausedBy">How it was paused, while paused.</param>
/// <param name="ModifiersDown">The modifier keys Windows reports down (the user's, or stuck); left out when none is.</param>
/// <param name="CapsLock">True when Caps Lock is on; null when Windows did not say.</param>
public sealed record DesktopInputState(bool Paused, string? PausedBy = null, IReadOnlyList<string>? ModifiersDown = null, bool? CapsLock = null);

/// <summary>The target's interactive elements, as get_ui_elements' text format (roadmap M3.12).</summary>
/// <param name="Text">One line per element, ref first. When popups were walked too, each window's lines follow a header line
/// that starts with '#': <c># popup 2493112 Menu</c>, then <c># window 1706796 'notes.txt - Notepad'</c>.</param>
/// <param name="Count">How many element lines there are.</param>
/// <param name="Truncated">True when the windows hold more elements than were read (maxElements, the deadline, a part that
/// could not be read).</param>
/// <param name="TruncationReason">Why, for the window's own walk (MaxElements, Timeout, ElementsSkipped), or Timeout when the
/// deadline stopped the popups.</param>
/// <param name="Scope">window, or windowAndPopups when the target's open popups were walked too (front first).</param>
/// <param name="AccessibilityWarmup">Set when the window shows web content and the walk waited for it first (roadmap M3.3).</param>
/// <param name="Warnings">The window walk's warnings, as get_ui_elements gives them (roadmap M3.F16): why its lines may not
/// show what the window holds (WindowMinimized, TargetElevated). Null when there is nothing to say.</param>
public sealed record DesktopElements(
    string? Text,
    int Count,
    bool Truncated,
    UIElementTruncationReason? TruncationReason,
    ElementSearchScope Scope,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>A part of get_desktop_state's observation that was not read, and why; the rest of the observation is still returned.</summary>
/// <param name="Section">windows, focus, elements, popups, screenshot, or target (the window the call reads: its elements,
/// popups and capture were all left out).</param>
/// <param name="ErrorCode">The code the part's own tool would have returned (OperationTimedOut, NotResponding,
/// TargetIsAgentHost, TargetDeniedByPolicy, WindowMinimized, ...).</param>
public sealed record DesktopStateError(string Section, string ErrorCode, string Error);

/// <summary>
/// Result of get_desktop_state (roadmap M3.12), and the action tools' <c>observed</c>: one compact observation of the desktop.
/// <c>success</c> is false only when the window named is no window; a part that could not be read is listed in
/// <paramref name="Errors"/> and the rest is still returned.
/// </summary>
/// <param name="WindowHandle">The window the elements, popups and capture are of: the one named, or the window in front;
/// 0 when there is none.</param>
/// <param name="Foreground">The window in front; null when none is (windows switching, a UAC prompt, the lock screen).</param>
/// <param name="Cursor">The mouse pointer in physical screen pixels; null when Windows did not say.</param>
/// <param name="Windows">The user-facing windows (no cloaked, tool, untitled or shell windows), front first, at most maxWindows.</param>
/// <param name="WindowCount">How many user-facing windows there are; more than <paramref name="Windows"/> lists when it was cut.</param>
/// <param name="Popups">The target's open menus, dropdowns, flyouts and dialogs, front first, with refs.</param>
/// <param name="PopupsExcluded">The target's open popups left out of <paramref name="Popups"/> and <paramref name="Elements"/>
/// because they are windows of the agent's own host or of an app the target policy denies; left out when none was.</param>
/// <param name="Screenshot">The capture's metadata (captureId, imageToScreen, size); the image is the image content before the
/// JSON.</param>
public sealed record DesktopState(
    bool Success,
    long WindowHandle,
    string? WindowTitle,
    DesktopWindow? Foreground,
    DesktopFocus? Focus,
    ScreenPoint? Cursor,
    DesktopInputState Input,
    IReadOnlyList<DesktopWindowRow>? Windows,
    int? WindowCount,
    IReadOnlyList<ActivePopup>? Popups,
    DesktopElements? Elements,
    ScreenshotResult? Screenshot,
    IReadOnlyList<DesktopStateError>? Errors,
    double ElapsedMilliseconds,
    string? Error = null,
    string? ErrorCode = null,
    int? PopupsExcluded = null);

/// <summary>get_desktop_state's observation and, when a capture was asked for and made, its image.</summary>
public sealed record DesktopStateSnapshot(DesktopState State, EncodedImage? Image);
