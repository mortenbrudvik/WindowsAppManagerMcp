using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Services.Interfaces;

public interface IProcessService
{
    LaunchResult LaunchApplication(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000);

    IReadOnlyList<ProcessInfo> GetRunningProcesses(string? nameFilter = null, bool includeWindowless = false);

    string? GetProcessName(int processId);

    string? GetProcessPath(int processId);

    /// <summary>
    /// Terminates a process by its ID.
    /// </summary>
    /// <param name="processId">The process ID to terminate.</param>
    /// <param name="confirm">Must be true to confirm termination (safety check).</param>
    /// <param name="forceKill">If true, kills immediately. If false, attempts graceful close first.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    KillResult KillProcess(int processId, bool confirm, bool forceKill = false);
}

public record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB
);
