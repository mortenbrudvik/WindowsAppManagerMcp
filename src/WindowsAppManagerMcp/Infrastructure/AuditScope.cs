using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// The audit record of the audited call that is running on this flow (roadmap M2.17), for the windows a tool learns of
/// only while it runs: the window a selector resolves to, and the windows close_application lists. Their titles and
/// processes are noted before the tool acts on them, so the line can name a window that is closed by the time the call
/// ends. <see cref="AuditLogToolFilter"/> enters the scope around the rest of the pipeline; outside an audited call
/// (a tool that is not audited, a unit test that calls a tool directly) noting does nothing.
/// </summary>
/// <remarks>
/// The scope is an <see cref="AsyncLocal{T}"/>: it flows into the tool's awaits and into the tasks it starts, and never
/// into another call. Noting reads nothing: it keeps the title (cut to <see cref="AuditRecord.MaxTitleLength"/>), process
/// and process ID of a window the tool already read, so it adds no window call to the tool's path.
/// </remarks>
public static class AuditScope
{
    private static readonly AsyncLocal<AuditCall?> Current = new();

    /// <summary>
    /// Makes <paramref name="call"/> the audited call of this flow until the result is disposed, which restores the one
    /// before (none).
    /// </summary>
    public static IDisposable Enter(AuditCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        var previous = Current.Value;
        Current.Value = call;
        return new Exit(previous);
    }

    /// <summary>Notes <paramref name="window"/> as it is now, for the line of the audited call on this flow, if any.</summary>
    public static void NoteWindow(WindowInfo? window)
    {
        if (window != null && Current.Value is { } call)
        {
            Note(call, window);
        }
    }

    /// <summary>Notes every window of <paramref name="windows"/>, as <see cref="NoteWindow"/> does.</summary>
    public static void NoteWindows(IEnumerable<WindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        if (Current.Value is not { } call)
        {
            return;
        }

        foreach (var window in windows)
        {
            Note(call, window);
        }
    }

    private static void Note(AuditCall call, WindowInfo window)
    {
        var title = window.Title ?? string.Empty;
        call.ResolvedWindows.TryAdd(window.Handle.ToInt64(), new AuditTarget(
            window.Handle.ToInt64(),
            title.Length > AuditRecord.MaxTitleLength ? title[..AuditRecord.MaxTitleLength] : title,
            string.IsNullOrEmpty(window.ProcessName) ? null : window.ProcessName,
            window.ProcessId > 0 ? window.ProcessId : null));
    }

    private sealed class Exit(AuditCall? previous) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Current.Value = previous;
            }
        }
    }
}
