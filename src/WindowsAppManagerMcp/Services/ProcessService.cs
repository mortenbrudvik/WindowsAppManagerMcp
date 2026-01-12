using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class ProcessService : IProcessService
{
    private readonly IWindowService _windowService;
    private readonly IInputValidationService _validationService;

    public ProcessService(IWindowService windowService, IInputValidationService validationService)
    {
        _windowService = windowService;
        _validationService = validationService;
    }

    public LaunchResult LaunchApplication(
        string executable,
        string[]? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = false,
        int waitTimeoutMs = 5000)
    {
        // Validate executable path
        var executableValidation = _validationService.ValidateExecutable(executable);
        if (!executableValidation.IsValid)
        {
            return new LaunchResult(false, ErrorMessage: executableValidation.Error, ErrorCode: executableValidation.ErrorCode);
        }

        // Validate working directory
        var workingDirValidation = _validationService.ValidateWorkingDirectory(workingDirectory);
        if (!workingDirValidation.IsValid)
        {
            return new LaunchResult(false, ErrorMessage: workingDirValidation.Error, ErrorCode: workingDirValidation.ErrorCode);
        }

        // Combine warnings
        var warning = CombineWarnings(executableValidation.Warning, workingDirValidation.Warning);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executableValidation.SanitizedExecutable ?? executable,
                UseShellExecute = true,
                WorkingDirectory = workingDirValidation.SanitizedPath ?? workingDirectory ?? Environment.CurrentDirectory
            };

            if (arguments?.Length > 0)
            {
                startInfo.Arguments = string.Join(" ", arguments.Select(a =>
                    a.Contains(' ') ? $"\"{a}\"" : a));
            }

            var process = Process.Start(startInfo);
            if (process == null)
            {
                return new LaunchResult(false, ErrorMessage: "Failed to start process", ErrorCode: nameof(LaunchErrorCode.ProcessStartFailed), Warning: warning);
            }

            nint? windowHandle = null;

            if (waitForWindow)
            {
                windowHandle = WaitForMainWindow(process, waitTimeoutMs);
            }

            return new LaunchResult(
                Success: true,
                ProcessId: process.Id,
                WindowHandle: windowHandle,
                Warning: warning
            );
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, ErrorMessage: ex.Message, ErrorCode: nameof(LaunchErrorCode.LaunchException), Warning: warning);
        }
    }

    private static string? CombineWarnings(string? warning1, string? warning2)
    {
        if (string.IsNullOrEmpty(warning1)) return warning2;
        if (string.IsNullOrEmpty(warning2)) return warning1;
        return $"{warning1}; {warning2}";
    }

    public IReadOnlyList<ProcessInfo> GetRunningProcesses(string? nameFilter = null, bool includeWindowless = false)
    {
        var result = new List<ProcessInfo>();
        var processes = Process.GetProcesses();

        // Count windows per process
        var windowCounts = new Dictionary<int, int>();
        if (!includeWindowless)
        {
            var windows = _windowService.GetAllWindows(includeMinimized: true);
            foreach (var window in windows)
            {
                if (windowCounts.TryGetValue(window.ProcessId, out var count))
                    windowCounts[window.ProcessId] = count + 1;
                else
                    windowCounts[window.ProcessId] = 1;
            }
        }

        foreach (var process in processes)
        {
            try
            {
                // Apply name filter
                if (!string.IsNullOrEmpty(nameFilter))
                {
                    if (!process.ProcessName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Check window count
                var windowCount = windowCounts.GetValueOrDefault(process.Id, 0);
                if (!includeWindowless && windowCount == 0)
                    continue;

                // Get executable path
                var execPath = GetProcessPath(process.Id);

                // Get memory usage
                double? memoryMB = null;
                try
                {
                    memoryMB = process.WorkingSet64 / (1024.0 * 1024.0);
                }
                catch
                {
                    // Access denied for some processes
                }

                result.Add(new ProcessInfo(
                    ProcessId: process.Id,
                    Name: process.ProcessName,
                    ExecutablePath: execPath,
                    WindowCount: windowCount,
                    MemoryUsageMB: memoryMB
                ));
            }
            catch
            {
                // Skip processes we can't access
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    public string? GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public string? GetProcessPath(int processId)
    {
        try
        {
            var hProcess = NativeMethods.Kernel32.OpenProcess(
                NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                (uint)processId);

            if (hProcess == nint.Zero)
                return null;

            try
            {
                var buffer = new char[1024];
                uint size = (uint)buffer.Length;

                if (NativeMethods.Kernel32.QueryFullProcessImageNameW(hProcess, 0, buffer, ref size))
                {
                    return new string(buffer, 0, (int)size);
                }
            }
            finally
            {
                NativeMethods.Kernel32.CloseHandle(hProcess);
            }
        }
        catch
        {
            // Access denied or process terminated
        }

        return null;
    }

    // Protected system processes that should not be terminated
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "smss", "csrss", "wininit", "services", "lsass", "lsm",
        "svchost", "winlogon", "dwm", "explorer", "Registry", "Memory Compression"
    };

    public KillResult KillProcess(int processId, bool confirm, bool forceKill = false)
    {
        // Safety check: require explicit confirmation
        if (!confirm)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Confirmation required. Set confirm=true to terminate the process.",
                ErrorCode: nameof(ProcessErrorCode.ConfirmationRequired));
        }

        // Validate process ID
        if (processId <= 0)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Invalid process ID. Must be a positive integer.",
                ErrorCode: nameof(ProcessErrorCode.InvalidProcessId));
        }

        // Cannot kill self
        if (processId == Environment.ProcessId)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Cannot terminate the current process.",
                ErrorCode: nameof(ProcessErrorCode.CannotTerminateSelf));
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;

            // Check if it's a protected system process
            if (ProtectedProcesses.Contains(processName))
            {
                return new KillResult(
                    Success: false,
                    ProcessId: processId,
                    ProcessName: processName,
                    ErrorMessage: $"Cannot terminate protected system process '{processName}'.",
                    ErrorCode: nameof(ProcessErrorCode.ProtectedProcess));
            }

            // Attempt graceful close first if not forcing
            if (!forceKill)
            {
                try
                {
                    if (process.CloseMainWindow())
                    {
                        // Wait briefly for graceful exit
                        if (process.WaitForExit(3000))
                        {
                            return new KillResult(
                                Success: true,
                                ProcessId: processId,
                                ProcessName: processName);
                        }
                    }
                }
                catch
                {
                    // Graceful close failed, fall through to force kill
                }
            }

            // Force kill
            process.Kill(entireProcessTree: true);
            process.WaitForExit(1000);

            return new KillResult(
                Success: true,
                ProcessId: processId,
                ProcessName: processName);
        }
        catch (ArgumentException)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Process not found. It may have already exited.",
                ErrorCode: nameof(ProcessErrorCode.ProcessNotFound));
        }
        catch (UnauthorizedAccessException)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: "Access denied. Insufficient permissions to terminate this process.",
                ErrorCode: nameof(ProcessErrorCode.AccessDenied));
        }
        catch (Exception ex)
        {
            return new KillResult(
                Success: false,
                ProcessId: processId,
                ErrorMessage: ex.Message,
                ErrorCode: nameof(ProcessErrorCode.TerminationException));
        }
    }

    private nint? WaitForMainWindow(Process process, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                process.Refresh();

                if (process.MainWindowHandle != nint.Zero)
                {
                    return process.MainWindowHandle;
                }

                // Also try to find windows belonging to this process
                var windows = _windowService.FindWindows(processId: process.Id);
                if (windows.Count > 0)
                {
                    return windows[0].Handle;
                }

                Thread.Sleep(100);
            }
            catch
            {
                break;
            }
        }

        return null;
    }
}
