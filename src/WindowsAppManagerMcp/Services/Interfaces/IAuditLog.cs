using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The append-only audit log of input and destructive actions (roadmap M2.17). The call filter
/// (<see cref="Infrastructure.AuditLogToolFilter"/>) starts a record before an audited tool runs and finishes it with
/// the result. Neither ever throws: a log that cannot be written never breaks a tool call; <see cref="LastError"/> and
/// a warning on stderr say so instead.
/// </summary>
public interface IAuditLog
{
    /// <summary>The folder that holds one <c>&lt;yyyy-MM-dd&gt;.jsonl</c> file per local day.</summary>
    string Folder { get; }

    /// <summary>Why the last line could not be written, or null when it was (or nothing was written yet).</summary>
    string? LastError { get; }

    /// <summary>
    /// Starts the record of a call: the time, and the windows and processes the arguments name, read now because a
    /// close or kill makes them unreadable.
    /// </summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="arguments">The call's arguments, as the client sent them.</param>
    /// <param name="requestId">The JSON-RPC request ID.</param>
    /// <param name="inputSchema">
    /// The tool's declared input schema: only the arguments it declares are summarized, and a number or boolean is kept
    /// only where it declares one (<see cref="Infrastructure.AuditRecord.SummarizeArguments"/>). Null when unknown.
    /// </param>
    AuditCall Begin(string tool, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, string? requestId, JsonElement? inputSchema = null);

    /// <summary>
    /// Finishes the record with the call's result (null when it threw) and outcome, and hands its line to the writer. The
    /// task completes once the line is written, or after a bounded wait (<see cref="AuditLog.MaxCallDelay"/>) when the
    /// writer is slow; it never faults, and the wait holds no thread.
    /// </summary>
    Task EndAsync(AuditCall call, CallToolResult? result, string outcome);
}

/// <summary>A call <see cref="IAuditLog.Begin"/> started: what is known before the tool runs.</summary>
/// <param name="Tool">The tool name.</param>
/// <param name="Arguments">The call's arguments; they are summarized only when the line is written (<see cref="Infrastructure.AuditRecord"/>).</param>
/// <param name="RequestId">The JSON-RPC request ID.</param>
/// <param name="Started">When the call started, in local time.</param>
/// <param name="StartTimestamp">The clock's timestamp at the start, for the duration.</param>
/// <param name="Windows">The windows the arguments name, by handle, as they were before the call.</param>
/// <param name="Processes">The names of the processes the arguments name, by ID, before the call (null when unknown).</param>
/// <param name="DeclaredTypes">
/// The JSON types the tool's input schema declares for each parameter (<see cref="Infrastructure.AuditRecord.DeclaredTypes"/>),
/// or null when the schema is unknown.
/// </param>
public sealed record AuditCall(
    string Tool,
    IReadOnlyDictionary<string, JsonElement> Arguments,
    string? RequestId,
    DateTimeOffset Started,
    long StartTimestamp,
    IReadOnlyDictionary<long, Models.AuditTarget> Windows,
    IReadOnlyDictionary<int, string?> Processes,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? DeclaredTypes = null)
{
    /// <summary>
    /// The windows the tool noted while it ran (<see cref="Infrastructure.AuditScope"/>), by handle: the window a selector
    /// resolved to, and the windows close_application listed, as they were before the tool acted on them. A window in
    /// <see cref="Windows"/> (read before the call) is taken from there first.
    /// </summary>
    public ConcurrentDictionary<long, Models.AuditTarget> ResolvedWindows { get; } = new();
}
