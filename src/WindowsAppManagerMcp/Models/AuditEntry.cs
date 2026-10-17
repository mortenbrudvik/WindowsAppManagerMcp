using System.Text.Json.Nodes;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// One line of the audit log (roadmap M2.17): a call of an audited tool, what it was aimed at, where its input went,
/// and how it ended. It never holds typed text, keys, clipboard text, URL paths or queries, command-line arguments or
/// result text: a string argument is recorded as its length unless it names a process, a preset, a fixed value or a
/// program to launch (<see cref="Infrastructure.AuditRecord"/>).
/// </summary>
/// <param name="Ts">When the call started, in local time with its UTC offset.</param>
/// <param name="Tool">The tool name.</param>
/// <param name="Outcome">
/// <c>ok</c>, <c>error</c> (the result went out with isError; <paramref name="ErrorCode"/> says why), <c>cancelled</c>
/// (the client cancelled the call; some input may have been sent), or <c>protocolError</c> (nothing ran).
/// </param>
/// <param name="ErrorCode">The result's errorCode, when it has one.</param>
/// <param name="DurationMs">How long the call took, including the wait for the input gate and for the user's answer.</param>
/// <param name="Target">
/// What the arguments aimed at, read before the call (a window's title and process cannot be read once it is closed),
/// completed from the result (the process a launch started).
/// </param>
/// <param name="ReceivingWindow">The window the input went to, or would have gone to when it was refused.</param>
/// <param name="Items">The items of a batch, in request order, with the windows' titles read before the call.</param>
/// <param name="Args">
/// The arguments the tool declares: numbers and booleans where it declares them, named strings, and every other value as
/// its length or count; undeclared arguments only as unknownArgumentsCount.
/// </param>
/// <param name="Result">The numbers and booleans of the result, such as charactersTyped or windowsArranged, and get_clipboard's origin.</param>
/// <param name="ServerPid">The server's process ID: several servers can write the same day's file.</param>
/// <param name="RequestId">The JSON-RPC request ID, as the server's stderr call log names it.</param>
/// <param name="DropWindow">drag (roadmap M4.3): the window at the drop point, where the drop went or would have gone.</param>
public sealed record AuditEntry(
    DateTimeOffset Ts,
    string Tool,
    string Outcome,
    string? ErrorCode,
    long DurationMs,
    AuditTarget? Target,
    AuditTarget? ReceivingWindow,
    IReadOnlyList<AuditItem>? Items,
    JsonObject? Args,
    JsonObject? Result,
    int ServerPid,
    string? RequestId,
    AuditTarget? DropWindow = null);

/// <summary>A window, process or program in an <see cref="AuditEntry"/>; what is unknown is left out.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Title">The window title, cut to <see cref="Infrastructure.AuditRecord.MaxTitleLength"/> characters.</param>
/// <param name="Process">The process name.</param>
/// <param name="ProcessId">The process ID.</param>
/// <param name="Executable">The program a launch was asked for: a program name or file path, or only a URL's scheme and host.</param>
/// <param name="Placement">A launch's placement (roadmap M3.13): what was asked for and what it did.</param>
public sealed record AuditTarget(
    long? Handle = null,
    string? Title = null,
    string? Process = null,
    int? ProcessId = null,
    string? Executable = null,
    AuditPlacement? Placement = null);

/// <summary>
/// A launch's placement in an <see cref="AuditEntry"/> (roadmap M3.13): what the arguments asked for (snap and state only
/// as valid canonical names; a `monitor` as MonitorIndex when it is an index, and as Monitor only when it is a monitor
/// keyword, an id or a device name, roadmap M6.7) and what the result reports; what is unknown is left out.
/// </summary>
public sealed record AuditPlacement(
    string? Snap = null,
    int? MonitorIndex = null,
    BoundsDto? Bounds = null,
    string? State = null,
    bool? Focus = null,
    BoundsDto? FinalBounds = null,
    string? FinalState = null,
    bool? Focused = null,
    bool? Reapplied = null,
    string? ErrorCode = null,
    string? Monitor = null);

/// <summary>
/// One item in an <see cref="AuditEntry"/>: a window or launch of a batch, a window close_application sent a close to,
/// or a placement of apply_layout.
/// </summary>
/// <param name="Handle">The window handle.</param>
/// <param name="Title">The window title, read before the call (or, for close_application and apply_layout, as the result names it).</param>
/// <param name="Process">The window's process name, read before the call.</param>
/// <param name="ProcessId">The process ID: the window's, or the process a launch started.</param>
/// <param name="Executable">
/// The program a launch was asked for (a launch batch item, or the launchCommand an apply_layout placement ran), as in
/// <see cref="AuditTarget.Executable"/>.
/// </param>
/// <param name="Success">Whether the item succeeded, when the result says so.</param>
/// <param name="ErrorCode">Why it failed.</param>
/// <param name="Status">The item's status in the result, such as closed, promptShown, notSent, applied or notFound.</param>
/// <param name="Launched">True for an apply_layout placement whose window was launched (launchMissing).</param>
/// <param name="Placement">A launch batch item's placement (roadmap M3.13).</param>
public sealed record AuditItem(
    long? Handle,
    string? Title,
    string? Process,
    int? ProcessId,
    string? Executable,
    bool? Success,
    string? ErrorCode,
    string? Status = null,
    bool? Launched = null,
    AuditPlacement? Placement = null,
    string? Elevation = null);

/// <summary>The outcomes an <see cref="AuditEntry"/> records.</summary>
public static class AuditOutcome
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string ProtocolError = "protocolError";
}
