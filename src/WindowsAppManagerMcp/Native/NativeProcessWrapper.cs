using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of INativeProcessWrapper backed by System.Diagnostics.Process and
/// a Toolhelp32 process snapshot.
/// </summary>
public class NativeProcessWrapper : INativeProcessWrapper
{
    public IProcessHandle? OpenProcess(int processId)
    {
        try
        {
            return new ProcessHandle(Process.GetProcessById(processId));
        }
        catch (ArgumentException)
        {
            // No process with this ID is running
            return null;
        }
    }

    public bool IsAncestorOfCurrentProcess(int processId)
    {
        return GetAncestorProcessIds().Contains(processId);
    }

    /// <summary>
    /// Returns the current process's ancestors, nearest first (parent, grandparent, ...).
    /// Returns an empty list if the process snapshot cannot be taken.
    /// </summary>
    public static IReadOnlyList<int> GetAncestorProcessIds()
    {
        var parentByProcessId = SnapshotParentProcessIds();
        return parentByProcessId == null
            ? []
            : FindAncestors(Environment.ProcessId, parentByProcessId, TryGetStartTime);
    }

    /// <summary>
    /// Walks the parent chain of <paramref name="processId"/>, nearest ancestor first.
    /// Stops at a parent that is no longer running, at a cycle, or at a parent that started after its
    /// child: Windows reuses PIDs, so such a "parent" is an unrelated process that took the PID of
    /// the real parent after it exited.
    /// </summary>
    public static IReadOnlyList<int> FindAncestors(
        int processId,
        IReadOnlyDictionary<int, int> parentByProcessId,
        Func<int, DateTime?> getStartTime)
    {
        var ancestors = new List<int>();
        var visited = new HashSet<int> { processId };
        var current = processId;
        var startTimeUpperBound = getStartTime(current);

        while (parentByProcessId.TryGetValue(current, out var parent) &&
               parent > 0 &&
               !visited.Contains(parent) &&
               parentByProcessId.ContainsKey(parent))
        {
            var parentStart = getStartTime(parent);
            if (startTimeUpperBound.HasValue && parentStart.HasValue && parentStart.Value > startTimeUpperBound.Value)
            {
                break;
            }

            ancestors.Add(parent);
            visited.Add(parent);
            current = parent;

            // Every further ancestor must have started before this one. If this start time is
            // unknown, the child's start time is still a valid upper bound.
            startTimeUpperBound = parentStart ?? startTimeUpperBound;
        }

        return ancestors;
    }

    private static Dictionary<int, int>? SnapshotParentProcessIds()
    {
        var snapshot = NativeMethods.Kernel32.CreateToolhelp32Snapshot(NativeMethods.Kernel32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.Kernel32.INVALID_HANDLE_VALUE || snapshot == nint.Zero)
        {
            return null;
        }

        try
        {
            var parentByProcessId = new Dictionary<int, int>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };

            if (!NativeMethods.Kernel32.Process32FirstW(snapshot, ref entry))
            {
                return parentByProcessId;
            }

            do
            {
                parentByProcessId[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (NativeMethods.Kernel32.Process32NextW(snapshot, ref entry));

            return parentByProcessId;
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(snapshot);
        }
    }

    private static DateTime? TryGetStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // Exited, access denied, or a system process without a start time
            return null;
        }
    }

    private sealed class ProcessHandle(Process process) : IProcessHandle
    {
        public int Id => process.Id;

        public string ProcessName => process.ProcessName;

        public bool HasExited => process.HasExited;

        public bool CloseMainWindow() => process.CloseMainWindow();

        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);

        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

        public void Dispose() => process.Dispose();
    }
}
