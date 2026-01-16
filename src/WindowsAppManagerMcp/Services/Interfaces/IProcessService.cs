using WindowsAppManagerMcp.Models;
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

    /// <summary>
    /// Launches multiple applications in batch.
    /// </summary>
    /// <param name="applications">List of applications to launch with their arguments and working directories.</param>
    /// <param name="waitForWindows">Whether to wait for each application window to appear.</param>
    /// <param name="waitTimeoutMs">Maximum time in milliseconds to wait for each window.</param>
    /// <returns>Results for each launch attempt.</returns>
    IReadOnlyList<LaunchResult> LaunchApplicationsBatch(
        IReadOnlyList<(string Executable, string[]? Arguments, string? WorkingDirectory)> applications,
        bool waitForWindows = false,
        int waitTimeoutMs = 5000);

    /// <summary>
    /// Launches a browser with CDP (Chrome DevTools Protocol) debug port enabled.
    /// </summary>
    /// <param name="browser">Browser to launch: chrome, edge, or brave.</param>
    /// <param name="url">Optional URL to open.</param>
    /// <param name="port">CDP debug port (default: 9222).</param>
    /// <param name="userDataDir">Optional separate user data directory to avoid conflicts with existing sessions.</param>
    /// <param name="waitForWindow">Whether to wait for the browser window to appear.</param>
    /// <param name="waitTimeoutMs">Maximum time in milliseconds to wait for the window.</param>
    /// <returns>Result containing process ID, window handle, CDP port, and user data directory.</returns>
    DebugBrowserLaunchResult LaunchBrowserWithDebug(
        string browser = "chrome",
        string? url = null,
        int port = 9222,
        string? userDataDir = null,
        bool waitForWindow = true,
        int waitTimeoutMs = 10000);
}

public record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB
);
