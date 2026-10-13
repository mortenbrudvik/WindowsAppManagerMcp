using System.Collections.Concurrent;
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
/// The lines are written by one background writer on a thread of its own, in order; <see cref="EndAsync"/> waits for its
/// line at most <see cref="MaxCallDelay"/>, without holding a thread, so a folder on a slow or unreachable share, or a
/// stalled disk, delays a tool call by at most that and never blocks it or a thread of the pool. A line still waiting then
/// is written when the writer gets to it; one that would make more than <see cref="MaxPendingLines"/> wait is dropped.
/// </para>
/// <para>
/// A line that cannot be written is dropped: the tool call is never failed. <see cref="LastError"/> keeps the reason,
/// get_server_info reports it as a warning, and stderr gets one warning per run of failures.
/// </para>
/// </remarks>
public sealed partial class AuditLog : IAuditLog
{
    /// <summary>The longest a tool call waits for its line to be written (roadmap M2.17: the log never holds up a call).</summary>
    public static readonly TimeSpan MaxCallDelay = TimeSpan.FromSeconds(2);

    /// <summary>The most lines that wait for the writer; a line beyond them is dropped, with <see cref="LastError"/>.</summary>
    public const int MaxPendingLines = 1000;

    private const int MaxWriteAttempts = 25;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AuditLogOptions _options;
    private readonly INativeWindowWrapper? _windows;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _maxCallDelay;
    private readonly Action<int>? _writeAttempt;
    private readonly object _writeLock = new();
    private readonly ConcurrentQueue<PendingLine> _pending = new();
    private int _pendingCount;
    private int _writerRunning;
    private DateOnly? _cleanedDay;
    private volatile string? _lastError;

    /// <param name="options">The folder and retention.</param>
    /// <param name="windows">Reads the target windows' titles and processes before a call; null reads nothing.</param>
    /// <param name="clock">The clock; defaults to the system clock.</param>
    /// <param name="logger">Gets one warning when lines start failing.</param>
    /// <param name="maxCallDelay">How long <see cref="EndAsync"/> waits for its line; default <see cref="MaxCallDelay"/>.</param>
    /// <param name="writeAttempt">
    /// Called on the writer with the attempt's number (1 for the first) before each attempt to write a line; tests use it to
    /// stall the writer or to act when it retries. Null in the server.
    /// </param>
    public AuditLog(
        AuditLogOptions options,
        INativeWindowWrapper? windows,
        TimeProvider? clock = null,
        ILogger<AuditLog>? logger = null,
        TimeSpan? maxCallDelay = null,
        Action<int>? writeAttempt = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _windows = windows;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _maxCallDelay = maxCallDelay ?? MaxCallDelay;
        _writeAttempt = writeAttempt;
    }

    public string Folder => _options.Folder;

    public string? LastError => _lastError;

    public AuditCall Begin(string tool, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, string? requestId, JsonElement? inputSchema = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var started = _clock.GetLocalNow();
        var timestamp = _clock.GetTimestamp();
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var windows = new Dictionary<long, AuditTarget>();
        var processes = new Dictionary<int, string?>();
        IReadOnlyDictionary<string, IReadOnlySet<string>>? declaredTypes = null;
        try
        {
            declaredTypes = AuditRecord.DeclaredTypes(inputSchema);
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

        return new AuditCall(tool, copy, requestId, started, timestamp, windows, processes, declaredTypes);
    }

    public async Task EndAsync(AuditCall call, CallToolResult? result, string outcome)
    {
        ArgumentNullException.ThrowIfNull(call);

        Task? written;
        try
        {
            var durationMs = (long)_clock.GetElapsedTime(call.StartTimestamp).TotalMilliseconds;
            var entry = AuditRecord.Build(call, result, outcome, durationMs, Environment.ProcessId);
            written = Enqueue(entry.Ts, AuditRecord.ToJsonLine(entry));
        }
        catch (Exception ex)
        {
            Failed(ex);
            return;
        }

        if (written == null)
        {
            return;
        }

        try
        {
            // Without a thread: the call's thread goes back to the pool while the writer works
            await written.WaitAsync(_maxCallDelay).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The writer writes the line when it gets to it
        }
    }

    /// <summary>
    /// Waits at most <paramref name="timeout"/> until every line handed to the writer so far is written (or has failed).
    /// True when none is left waiting.
    /// </summary>
    public bool WaitForPendingLines(TimeSpan timeout)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _pendingCount) > 0)
        {
            if (waited.Elapsed >= timeout)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>
    /// Hands the line to the writer, which is started when it is not running. The task completes once the line is written
    /// or has failed; null when the line was dropped because too many wait.
    /// </summary>
    private Task? Enqueue(DateTimeOffset when, string line)
    {
        if (Interlocked.Increment(ref _pendingCount) > MaxPendingLines)
        {
            Interlocked.Decrement(ref _pendingCount);
            Failed(new IOException(
                $"{MaxPendingLines} lines are still waiting to be written to '{_options.Folder}' (the folder is slow or does not " +
                "answer), so this line was dropped."));
            return null;
        }

        var pending = new PendingLine(when, line);
        _pending.Enqueue(pending);
        if (Interlocked.CompareExchange(ref _writerRunning, 1, 0) == 0)
        {
            StartWriter();
        }

        return pending.Written.Task;
    }

    /// <summary>
    /// Starts the writer on a thread of its own, so that a pool busy with the server's calls, which wait for their lines,
    /// does not hold it back. Not on the call's flow (UnsafeStart): nothing of the call (its audit scope) goes with it.
    /// </summary>
    private void StartWriter()
    {
        try
        {
            new Thread(WritePending) { IsBackground = true, Name = "Audit log writer" }.UnsafeStart();
        }
        catch (Exception)
        {
            // No thread could be started (out of memory): the pool writes the lines instead
            ThreadPool.UnsafeQueueUserWorkItem(_ => WritePending(), null);
        }
    }

    /// <summary>The writer: writes the waiting lines in order until none is left.</summary>
    private void WritePending()
    {
        while (true)
        {
            while (_pending.TryDequeue(out var pending))
            {
                try
                {
                    Append(pending.When, pending.Line);
                }
                catch (Exception ex)
                {
                    Failed(ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingCount);
                    pending.Written.TrySetResult();
                }
            }

            Volatile.Write(ref _writerRunning, 0);

            // A line handed over after the queue was found empty, and before the writer stopped, is written by this writer
            if (_pending.IsEmpty || Interlocked.CompareExchange(ref _writerRunning, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private sealed record PendingLine(DateTimeOffset When, string Line)
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                _writeAttempt?.Invoke(attempt);
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
