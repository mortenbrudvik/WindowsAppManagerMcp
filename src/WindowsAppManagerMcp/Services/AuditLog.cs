using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The append-only audit log of input and destructive actions (roadmap M2.17): one JSON line per call of an audited tool
/// (<see cref="AuditLogToolFilter.AuditedTools"/>) in <c>&lt;folder&gt;\&lt;yyyy-MM-dd&gt;.jsonl</c>, the local day
/// the call started. <see cref="AuditRecord"/> decides what a line holds; this class reads the targets before the call,
/// writes the line and deletes the days older than the retention.
/// </summary>
/// <remarks>
/// <para>
/// Nothing touches the disk until the first line: creating the log, or reading <see cref="Folder"/>, creates no folder.
/// The folder is created by the first line, and the first line of each day deletes the day files older than the
/// retention (only files named <c>yyyy-MM-dd.jsonl</c>; nothing else in the folder is touched).
/// </para>
/// <para>
/// Several servers (one per agent session) can write the same day's file. Each line is written in one call while the
/// file is open for writing by this writer alone (other writers wait, readers do not), so lines never interleave; a
/// writer that finds the file busy retries for up to about half a second.
/// </para>
/// <para>
/// A line that cannot be written is dropped: the tool call is never failed or delayed beyond those retries.
/// <see cref="LastError"/> keeps the reason, get_server_info reports it as a warning, and stderr gets one warning per
/// run of failures.
/// </para>
/// </remarks>
public sealed partial class AuditLog : IAuditLog
{
    private const int MaxWriteAttempts = 25;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AuditLogOptions _options;
    private readonly INativeWindowWrapper? _windows;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _writeLock = new();
    private DateOnly? _cleanedDay;
    private volatile string? _lastError;

    /// <param name="options">The folder and retention.</param>
    /// <param name="windows">Reads the target windows' titles and processes before a call; null reads nothing.</param>
    /// <param name="clock">The clock; defaults to the system clock.</param>
    /// <param name="logger">Gets one warning when lines start failing.</param>
    public AuditLog(AuditLogOptions options, INativeWindowWrapper? windows, TimeProvider? clock = null, ILogger<AuditLog>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _windows = windows;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string Folder => _options.Folder;

    public string? LastError => _lastError;

    public AuditCall Begin(string tool, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, string? requestId)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var started = _clock.GetLocalNow();
        var timestamp = _clock.GetTimestamp();
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var windows = new Dictionary<long, AuditTarget>();
        var processes = new Dictionary<int, string?>();
        try
        {
            foreach (var (name, value) in arguments ?? [])
            {
                copy[name] = value.Clone();
            }

            foreach (var handle in AuditRecord.WindowHandles(copy))
            {
                windows[handle] = ReadWindow(handle);
            }

            foreach (var processId in AuditRecord.ProcessIds(copy))
            {
                processes[processId] = ReadProcessName(processId);
            }
        }
        catch (Exception ex)
        {
            // Never fail the call: the line records what is known
            _logger.LogDebug(ex, "The audit log could not read the targets of a '{Tool}' call", tool);
        }

        return new AuditCall(tool, copy, requestId, started, timestamp, windows, processes);
    }

    public void End(AuditCall call, CallToolResult? result, string outcome)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            var durationMs = (long)_clock.GetElapsedTime(call.StartTimestamp).TotalMilliseconds;
            var entry = AuditRecord.Build(call, result, outcome, durationMs, Environment.ProcessId);
            Append(entry.Ts, AuditRecord.ToJsonLine(entry));
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    private AuditTarget ReadWindow(long handle)
    {
        try
        {
            if (_windows == null || !_windows.IsWindow((nint)handle))
            {
                return new AuditTarget(Handle: handle);
            }

            var title = _windows.GetWindowText((nint)handle);
            var processId = _windows.GetWindowThreadProcessId((nint)handle);
            return new AuditTarget(
                handle,
                title.Length > AuditRecord.MaxTitleLength ? title[..AuditRecord.MaxTitleLength] : title,
                processId == 0 ? null : _windows.GetProcessName(processId),
                processId == 0 ? null : (int)processId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The audit log could not read window {Handle}", handle);
            return new AuditTarget(Handle: handle);
        }
    }

    private string? ReadProcessName(int processId)
    {
        try
        {
            return processId > 0 ? _windows?.GetProcessName((uint)processId) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The audit log could not read process {ProcessId}", processId);
            return null;
        }
    }

    private void Append(DateTimeOffset when, string line)
    {
        var day = DateOnly.FromDateTime(when.DateTime);
        var path = Path.Combine(_options.Folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        var bytes = Utf8NoBom.GetBytes(line + "\n");

        lock (_writeLock)
        {
            Directory.CreateDirectory(_options.Folder);
            if (_cleanedDay != day)
            {
                _cleanedDay = day;
                DeleteExpiredDays(day);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Write access for this writer alone, so another server's line cannot land in the middle of this one;
                    // readers (and a delete by the retention of another server) are not blocked
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    stream.Write(bytes);
                    break;
                }
                catch (IOException ex) when (IsSharingViolation(ex) && attempt < MaxWriteAttempts)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        if (_lastError != null)
        {
            _lastError = null;
            _logger.LogInformation("The audit log in '{Folder}' is written again", _options.Folder);
        }
    }

    /// <summary>Deletes the day files whose day is <see cref="AuditLogOptions.Retention"/> or more before <paramref name="today"/>.</summary>
    private void DeleteExpiredDays(DateOnly today)
    {
        var oldestKept = today.AddDays(-(int)Math.Ceiling(_options.Retention.TotalDays) + 1);
        foreach (var file in Directory.EnumerateFiles(_options.Folder, "*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DayFileName().IsMatch(name) ||
                !DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDay) ||
                fileDay >= oldestKept)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another server may be writing it, or deleting it too: the next day's first line tries again
                _logger.LogDebug(ex, "The audit log could not delete the expired file '{File}'", file);
            }
        }
    }

    private void Failed(Exception ex)
    {
        var first = _lastError == null;
        _lastError = ex.Message;
        if (first)
        {
            _logger.LogWarning(
                "The audit log in '{Folder}' cannot be written: {Error} Input and destructive actions are not recorded until it can be written again.",
                _options.Folder, ex.Message);
        }
        else
        {
            _logger.LogDebug(ex, "The audit log still cannot be written");
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DayFileName();
}
