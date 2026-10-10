using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of INativeProcessWrapper backed by System.Diagnostics.Process and
/// a Toolhelp32 process snapshot. This is the only type in the server that calls Process.Start,
/// Process.Kill or Process.CloseMainWindow (enforced by NativeCallGuardTests).
/// </summary>
public class NativeProcessWrapper : INativeProcessWrapper
{
    private const int WhereExeTimeoutMs = 5000;

    private readonly Func<IReadOnlyDictionary<int, (int ParentProcessId, string ImageName)>?> _snapshot;

    /// <param name="snapshot">
    /// Reads the process list that the ancestor and descendant walks of this instance use: every running process
    /// with its parent's ID and image name, or null when the list cannot be read. By default a Toolhelp32 snapshot
    /// (read with <see cref="ReadProcessList"/>); tests pass their own.
    /// </param>
    public NativeProcessWrapper(Func<IReadOnlyDictionary<int, (int ParentProcessId, string ImageName)>?>? snapshot = null)
    {
        _snapshot = snapshot ?? SnapshotProcesses;
    }

    public IProcessHandle? StartProcess(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        return process == null ? null : new ProcessHandle(process);
    }

    /// <summary>
    /// Runs where.exe for <paramref name="fileName"/> without a window and returns the first line it
    /// prints. Returns null when it finds nothing, cannot be run, or has not exited and closed its
    /// output within 5 seconds; in that case where.exe (and anything it started) is killed.
    /// </summary>
    public string? FindWithWhereExe(string fileName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = fileName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            // Read in the background, so the time limit also covers a where.exe that never prints
            // anything (for example while it searches a PATH entry on an unreachable network share).
            var output = process.StandardOutput.ReadToEndAsync();
            return ReadFirstLineWithin(output, process.WaitForExit, () => KillProcessTree(process), WhereExeTimeoutMs);
        }
        catch (Exception)
        {
            // where.exe missing or not runnable: treat as not found
            return null;
        }
    }

    /// <summary>
    /// Waits at most <paramref name="timeoutMs"/> in total for a process to exit and for its output
    /// to be read to the end, then returns the first line of the output (null when it is blank).
    /// When the process does not exit in time, its output is not complete in time, or reading it
    /// fails, <paramref name="kill"/> is called and null is returned.
    /// </summary>
    /// <param name="output">The process's standard output, being read to the end.</param>
    /// <param name="waitForExit">Waits up to the given milliseconds; returns true if the process exited.</param>
    /// <param name="kill">Kills the process (and its children); must not throw.</param>
    /// <param name="timeoutMs">Total time limit in milliseconds.</param>
    public static string? ReadFirstLineWithin(
        Task<string> output,
        Func<int, bool> waitForExit,
        Action kill,
        int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (waitForExit(timeoutMs))
            {
                var remainingMs = (int)Math.Max(0, timeoutMs - stopwatch.ElapsedMilliseconds);
                if (output.Wait(remainingMs))
                {
                    using var reader = new StringReader(output.Result);
                    var firstLine = reader.ReadLine();
                    return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
                }
            }
        }
        catch (Exception)
        {
            // Reading the output failed: treat as not found
        }

        kill();

        // The abandoned read fails once the process is disposed; observe that so it is not reported
        // as an unobserved task exception.
        output.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already exited, or cannot be killed: nothing more to do
        }
    }

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
        var parentByProcessId = ReadParentProcessIds();
        return parentByProcessId != null &&
               FindAncestors(Environment.ProcessId, parentByProcessId, TryGetStartTime).Contains(processId);
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

    public IReadOnlyList<(int ProcessId, DateTime? StartTime)> GetAncestorProcesses()
    {
        var parentByProcessId = ReadParentProcessIds();
        if (parentByProcessId == null)
        {
            return [];
        }

        // Read each start time once: the walk reads them to detect reused PIDs, the result reports them.
        var startTimes = new Dictionary<int, DateTime?>();
        DateTime? StartTime(int processId)
        {
            if (!startTimes.TryGetValue(processId, out var startTime))
            {
                startTime = TryGetStartTime(processId);
                startTimes[processId] = startTime;
            }

            return startTime;
        }

        return FindAncestors(Environment.ProcessId, parentByProcessId, StartTime)
            .Select(processId => (processId, StartTime(processId)))
            .ToList();
    }

    public IReadOnlyList<int> GetAncestorsOf(int processId)
    {
        var parentByProcessId = ReadParentProcessIds();
        return parentByProcessId == null || processId <= 0 || !parentByProcessId.ContainsKey(processId)
            ? []
            : FindAncestors(processId, parentByProcessId, TryGetStartTime);
    }

    public DateTime? GetProcessStartTime(int processId) => TryGetStartTime(processId);

    public nint GetConsoleWindow() => NativeMethods.Kernel32.GetConsoleWindow();

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

    public IReadOnlyList<(int ProcessId, string ImageName)>? GetDescendantsOf(int processId)
    {
        // The children are unknown, not absent, when the process list cannot be read or does not list the process
        // (it exited, or the snapshot missed it): the runtime's tree kill finds the children on its own, so a caller
        // that checks them must be able to fail closed
        var processes = _snapshot();
        if (processes == null || processId <= 0 || !processes.ContainsKey(processId))
        {
            return null;
        }

        var parentByProcessId = processes.ToDictionary(p => p.Key, p => p.Value.ParentProcessId);
        return FindDescendants(processId, parentByProcessId, TryGetStartTime)
            .Select(id => (id, processes[id].ImageName))
            .ToList();
    }

    /// <summary>
    /// Walks the children of <paramref name="processId"/> breadth-first, the way Kill(entireProcessTree)
    /// finds them. A child that started before its parent is skipped with its own children: Windows reuses
    /// PIDs, so its real parent exited and the process now holding that PID is unrelated.
    /// </summary>
    public static IReadOnlyList<int> FindDescendants(
        int processId,
        IReadOnlyDictionary<int, int> parentByProcessId,
        Func<int, DateTime?> getStartTime)
    {
        var childrenByParent = parentByProcessId
            .Where(p => p.Key != p.Value)
            .GroupBy(p => p.Value)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).OrderBy(id => id).ToList());

        var descendants = new List<int>();
        var visited = new HashSet<int> { processId };
        var pending = new Queue<int>();
        pending.Enqueue(processId);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                continue;
            }

            var parentStart = getStartTime(parent);
            foreach (var child in children)
            {
                if (!visited.Add(child))
                {
                    continue;
                }

                var childStart = getStartTime(child);
                if (parentStart.HasValue && childStart.HasValue && childStart.Value < parentStart.Value)
                {
                    continue;
                }

                descendants.Add(child);
                pending.Enqueue(child);
            }
        }

        return descendants;
    }

    private static Dictionary<int, int>? SnapshotParentProcessIds() =>
        SnapshotProcesses()?.ToDictionary(p => p.Key, p => p.Value.ParentProcessId);

    /// <summary>This instance's process list (see the constructor) as each process's parent ID; null when unreadable.</summary>
    private Dictionary<int, int>? ReadParentProcessIds() =>
        _snapshot()?.ToDictionary(p => p.Key, p => p.Value.ParentProcessId);

    /// <summary>
    /// The process list of a snapshot: every process with its parent's ID and its image name, read with
    /// <paramref name="read"/> (true for the first entry, as Process32FirstW reads it, then false, as Process32NextW
    /// does) until it returns null. Null when not even the first entry can be read: a snapshot always lists at least
    /// the idle process and the caller, so the process list is then unknown, and an empty list would read as "no
    /// processes", and to a forced kill as "no children".
    /// </summary>
    public static Dictionary<int, (int ParentProcessId, string ImageName)>? ReadProcessList(Func<bool, ProcessSnapshotEntry?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var processes = new Dictionary<int, (int ParentProcessId, string ImageName)>();
        for (var entry = read(true); entry is { } current; entry = read(false))
        {
            processes[current.ProcessId] = (current.ParentProcessId, current.ImageName);
        }

        return processes.Count == 0 ? null : processes;
    }

    /// <summary>The length of PROCESSENTRY32W.szExeFile.</summary>
    private const int MaxPath = 260;

    /// <summary>
    /// Every running process with its parent's ID and its image name, from one Toolhelp snapshot; null when the
    /// snapshot cannot be taken or read.
    /// </summary>
    private static Dictionary<int, (int ParentProcessId, string ImageName)>? SnapshotProcesses()
    {
        var snapshot = NativeMethods.Kernel32.CreateToolhelp32Snapshot(NativeMethods.Kernel32.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.Kernel32.INVALID_HANDLE_VALUE || snapshot == nint.Zero)
        {
            return null;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            return ReadProcessList(first => ReadSnapshotEntry(snapshot, ref entry, first));
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// Reads the first (Process32FirstW) or the next (Process32NextW) entry of a Toolhelp snapshot into
    /// <paramref name="entry"/>; null when there is none or it cannot be read.
    /// </summary>
    private static unsafe ProcessSnapshotEntry? ReadSnapshotEntry(nint snapshot, ref PROCESSENTRY32W entry, bool first)
    {
        var read = first
            ? NativeMethods.Kernel32.Process32FirstW(snapshot, ref entry)
            : NativeMethods.Kernel32.Process32NextW(snapshot, ref entry);
        if (!read)
        {
            return null;
        }

        fixed (ushort* exeFile = entry.szExeFile)
        {
            var name = (char*)exeFile;
            var length = 0;
            while (length < MaxPath && name[length] != '\0')
            {
                length++;
            }

            return new ProcessSnapshotEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, new string(name, 0, length));
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

        public nint RefreshMainWindowHandle()
        {
            process.Refresh();
            return process.MainWindowHandle;
        }

        public bool CloseMainWindow() => process.CloseMainWindow();

        public async Task<bool> WaitForExitAsync(int milliseconds, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(milliseconds);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The timeout passed first
                return process.HasExited;
            }
        }

        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

        public void Dispose() => process.Dispose();
    }
}

/// <summary>One process in a process snapshot: its ID, its parent's ID and its image name (such as "KeePass.exe").</summary>
public readonly record struct ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string ImageName);
