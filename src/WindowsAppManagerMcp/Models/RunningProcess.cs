namespace WindowsAppManagerMcp.Models;

/// <summary>
/// One running process as the system's process list reports it, without opening the process
/// (<see cref="Services.Interfaces.INativeProcessWrapper.ListRunningProcesses"/>).
/// </summary>
/// <param name="ProcessId">The process ID.</param>
/// <param name="Name">The process name without the .exe extension, such as "notepad".</param>
/// <param name="WorkingSetBytes">The working set in bytes; null when it could not be read.</param>
/// <param name="SessionId">The Terminal Services session the process runs in; null when it could not be read.</param>
public sealed record RunningProcess(int ProcessId, string Name, long? WorkingSetBytes, int? SessionId);

/// <summary>
/// What one query handle to a process (PROCESS_QUERY_LIMITED_INFORMATION) tells about it
/// (<see cref="Services.Interfaces.INativeProcessWrapper.ReadProcessDetails"/>). Each field is null when its own read
/// failed.
/// </summary>
/// <param name="ImagePath">The full path of the process's executable (QueryFullProcessImageNameW).</param>
/// <param name="StartTime">When the process started, in UTC (GetProcessTimes).</param>
public sealed record ProcessDetails(string? ImagePath, DateTime? StartTime);
