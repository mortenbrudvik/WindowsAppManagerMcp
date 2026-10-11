namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// Result of kill_process. Success is true only when the process is confirmed to have exited.
/// </summary>
/// <param name="CloseRequested">A close request (WM_CLOSE) was posted to the main window (graceful mode).</param>
/// <param name="Forced">A forced termination of the process and its child processes was issued (forceKill=true).</param>
/// <param name="Exited">The process is confirmed to have exited.</param>
/// <param name="Warning">Additional information, for example child processes that could not be terminated.</param>
public record KillResult(
    bool Success,
    int ProcessId,
    string? ProcessName = null,
    string? ErrorMessage = null,
    string? ErrorCode = null,
    bool CloseRequested = false,
    bool Forced = false,
    bool Exited = false,
    string? Warning = null
);

/// <summary>
/// What kill_process would act on, checked without acting (IProcessService.CheckKillTarget): either the refusal the
/// kill would return (an invalid PID, a protected, denied, host or renamed process, an unreadable tree), or the
/// process's name and, for a forced kill, the child processes it would also end. kill_process asks the user about
/// this before it enters the input gate (roadmap M2.10).
/// </summary>
/// <param name="Refusal">The kill's refusal, or null when the kill could go ahead.</param>
/// <param name="ProcessName">The process's name; null when refused before it was read.</param>
/// <param name="ChildProcesses">forceKill: the descendants a forced kill also ends. Empty otherwise.</param>
/// <param name="StartTime">When the process started (UTC), which tells it from a later process that got its PID; null when it cannot be read.</param>
public sealed record KillTargetCheck(
    KillResult? Refusal,
    string? ProcessName,
    IReadOnlyList<(int ProcessId, string ImageName)> ChildProcesses,
    DateTime? StartTime = null)
{
    /// <summary>The kill would be refused with <paramref name="refusal"/>.</summary>
    public static KillTargetCheck Refused(KillResult refusal) => new(refusal, refusal.ProcessName, []);
}

/// <summary>
/// What a kill is held to beyond its arguments (IProcessService.KillProcessAsync, roadmap M2.10 review).
/// </summary>
/// <param name="Confirmed">
/// The check the user was asked about. The kill is refused when the PID now belongs to a process that started at another
/// time (ProcessNameMismatch: the confirmed one exited and its PID was reused, even by a process with the same name), and,
/// with forceKill, when the tree gained a process the user was not shown (ProcessTreeChanged).
/// </param>
/// <param name="BeforeWait">
/// Called once the close request or the termination was sent, before the wait for the process to exit: kill_process
/// releases the input gate there, so a graceful wait of up to 60 s does not hold up every input and window call.
/// </param>
public sealed record KillSafeguards(KillTargetCheck? Confirmed = null, Action? BeforeWait = null);
