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
}

public record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB
);
