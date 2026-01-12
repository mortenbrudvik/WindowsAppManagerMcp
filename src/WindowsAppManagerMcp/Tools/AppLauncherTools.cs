using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class AppLauncherTools
{
    private readonly IProcessService _processService;

    public AppLauncherTools(IProcessService processService)
    {
        _processService = processService;
    }

    [McpServerTool(Name = "launch_application")]
    [Description("Launch an application by executable path, name, or protocol URI. Returns process ID and optionally the window handle if waitForWindow is true.")]
    public LaunchResultDto LaunchApplication(
        [Description("Path to executable, application name, or protocol URI (e.g., 'notepad', 'C:\\Program Files\\App\\app.exe', 'https://example.com')")]
        string executable,
        [Description("Command line arguments to pass to the application")]
        string[]? arguments = null,
        [Description("Working directory for the application")]
        string? workingDirectory = null,
        [Description("Whether to wait for the application window to appear before returning (default: false)")]
        bool waitForWindow = false,
        [Description("Maximum time in milliseconds to wait for the window (default: 5000)")]
        int waitTimeoutMs = 5000)
    {
        var result = _processService.LaunchApplication(
            executable,
            arguments,
            workingDirectory,
            waitForWindow,
            waitTimeoutMs);

        return new LaunchResultDto(
            Success: result.Success,
            ProcessId: result.ProcessId,
            WindowHandle: result.WindowHandle?.ToInt64(),
            Error: result.ErrorMessage
        );
    }

    [McpServerTool(Name = "list_processes")]
    [Description("List currently running processes with optional filtering by name. Returns process info including ID, name, window count, and memory usage.")]
    public IReadOnlyList<ProcessInfoDto> ListProcesses(
        [Description("Filter processes by name (partial match, case-insensitive)")]
        string? nameFilter = null,
        [Description("Include processes without visible windows (default: false)")]
        bool includeWindowless = false)
    {
        var processes = _processService.GetRunningProcesses(nameFilter, includeWindowless);

        return processes.Select(p => new ProcessInfoDto(
            ProcessId: p.ProcessId,
            Name: p.Name,
            ExecutablePath: p.ExecutablePath,
            WindowCount: p.WindowCount,
            MemoryUsageMB: p.MemoryUsageMB.HasValue ? Math.Round(p.MemoryUsageMB.Value, 1) : null
        )).ToList();
    }
}

public record LaunchResultDto(
    bool Success,
    int? ProcessId = null,
    long? WindowHandle = null,
    string? Error = null
);

public record ProcessInfoDto(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int WindowCount,
    double? MemoryUsageMB
);
