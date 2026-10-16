namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What IProcessService.GetRunningProcesses lists (roadmap M6.6). Every filter that is set must match; the result is then
/// sorted by <see cref="Sort"/> and cut to <see cref="Limit"/>.
/// </summary>
public sealed record ProcessQuery
{
    /// <summary>Part of the process name, case-insensitive.</summary>
    public string? NameFilter { get; init; }

    /// <summary>The process ID.</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Part of the executable's full path, case-insensitive. A process whose path cannot be read (it runs under another
    /// account, or exited) does not match.
    /// </summary>
    public string? PathContains { get; init; }

    /// <summary>Also list processes without a visible, titled top-level window.</summary>
    public bool IncludeWindowless { get; init; }

    /// <summary>
    /// Also read each listed process's command line, redacted (<see cref="Infrastructure.CommandLineRedactor"/>). Never
    /// read for a process the user's target policy denies.
    /// </summary>
    public bool IncludeCommandLine { get; init; }

    /// <summary>The order of the result.</summary>
    public ProcessSort Sort { get; init; } = ProcessSort.Name;

    /// <summary>At most this many processes, the first in <see cref="Sort"/> order (at least 1); null for every one.</summary>
    public int? Limit { get; init; }
}
