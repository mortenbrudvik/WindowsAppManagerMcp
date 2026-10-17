using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Models;

/// <summary>What happened to a window, as the window event journal records it (roadmap M7.2).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "window event type")]
public enum WindowEventType
{
    /// <summary>A window appeared: a dialog, a popup, a menu or a new window (EVENT_OBJECT_SHOW).</summary>
    [JsonStringEnumMemberName("shown")]
    Shown,

    /// <summary>A window was hidden (EVENT_OBJECT_HIDE).</summary>
    [JsonStringEnumMemberName("hidden")]
    Hidden,

    /// <summary>A window the journal had seen was destroyed (closed).</summary>
    [JsonStringEnumMemberName("destroyed")]
    Destroyed,

    /// <summary>A window came to the front (EVENT_SYSTEM_FOREGROUND).</summary>
    [JsonStringEnumMemberName("foreground")]
    Foreground,

    /// <summary>A shown window's title changed (EVENT_OBJECT_NAMECHANGE).</summary>
    [JsonStringEnumMemberName("titleChanged")]
    TitleChanged,

    /// <summary>A window was minimized.</summary>
    [JsonStringEnumMemberName("minimized")]
    Minimized,

    /// <summary>A minimized window was restored.</summary>
    [JsonStringEnumMemberName("restored")]
    Restored
}

/// <summary>One window event in the journal (roadmap M7.2).</summary>
/// <param name="Seq">Its number: every event has a higher one than the events before it.</param>
/// <param name="At">When it happened.</param>
/// <param name="Type">What happened.</param>
/// <param name="Handle">The top-level window.</param>
/// <param name="Title">Its title when the event was recorded (for destroyed, as last seen); cut to 200 characters.</param>
/// <param name="ClassName">Its window class, such as "#32770" for a standard dialog.</param>
/// <param name="ProcessName">Its process's image name; null when it cannot be read.</param>
/// <param name="ProcessId">Its process.</param>
/// <param name="OwnerHandle">The window that owns it (a dialog's main window); left out for an unowned window.</param>
public sealed record WindowEventEntry(
    long Seq,
    DateTimeOffset At,
    WindowEventType Type,
    long Handle,
    string Title,
    string ClassName,
    string? ProcessName,
    int ProcessId,
    long? OwnerHandle = null);

/// <summary>The result of get_window_events (roadmap M7.2).</summary>
/// <param name="Success">True when the journal could be read.</param>
/// <param name="Events">The events after sinceSeq that pass the filters, oldest first.</param>
/// <param name="NextSeq">Pass it as sinceSeq next time, for the events after these.</param>
/// <param name="RecordingSince">When the journal started recording; nothing before it was recorded.</param>
/// <param name="StartedNow">True when this call started the recording: there is nothing earlier to report.</param>
/// <param name="Missed">
/// How many events after sinceSeq the journal no longer holds (it keeps the last 1,024), or that came while it was not
/// recording; left out when none were missed.
/// </param>
/// <param name="More">True when limit cut the events: call again with nextSeq for the rest.</param>
/// <param name="Error">Why the journal could not be read.</param>
/// <param name="ErrorCode">InvalidArguments; null on success.</param>
public sealed record WindowEventsResult(
    bool Success,
    IReadOnlyList<WindowEventEntry> Events,
    long NextSeq,
    DateTimeOffset? RecordingSince = null,
    bool? StartedNow = null,
    long? Missed = null,
    bool? More = null,
    string? Error = null,
    string? ErrorCode = null);
