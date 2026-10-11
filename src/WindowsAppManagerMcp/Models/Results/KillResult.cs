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
public sealed record KillTargetCheck(
    KillResult? Refusal,
    string? ProcessName,
    IReadOnlyList<(int ProcessId, string ImageName)> ChildProcesses)
{
    /// <summary>The kill would be refused with <paramref name="refusal"/>.</summary>
    public static KillTargetCheck Refused(KillResult refusal) => new(refusal, refusal.ProcessName, []);
}
