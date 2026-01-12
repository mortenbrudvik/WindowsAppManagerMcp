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
