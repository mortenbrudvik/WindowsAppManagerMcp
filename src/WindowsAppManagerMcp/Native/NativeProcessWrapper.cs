using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of INativeProcessWrapper backed by System.Diagnostics.Process and
/// a Toolhelp32 process snapshot. This is the only type in the server that calls Process.Start,
/// Process.Kill or Process.CloseMainWindow (enforced by NativeCallGuardTests).
/// </summary>
public class NativeProcessWrapper : INativeProcessWrapper
{
    /// <summary>SYNCHRONIZE: the right to wait for the process, which the pin asks for with the query right.</summary>
    private const uint Synchronize = 0x00100000;

    private readonly Func<IReadOnlyDictionary<int, (int ParentProcessId, string ImageName)>?> _snapshot;
    private readonly Func<int, IDisposable?> _openPin;

    /// <param name="snapshot">
    /// Reads the process list that the ancestor and descendant walks of this instance use: every running process
    /// with its parent's ID and image name, or null when the list cannot be read. By default a Toolhelp32 snapshot
    /// (read with <see cref="ReadProcessList"/>); tests pass their own.
    /// </param>
    /// <param name="openPin">
    /// Opens the handle that <see cref="OpenProcess"/> holds to pin a process's PID (see there), or returns null when none
    /// can be opened. By default a <see cref="SafeProcessHandle"/> with query and wait access; tests pass their own.
    /// </param>
    public NativeProcessWrapper(
        Func<IReadOnlyDictionary<int, (int ParentProcessId, string ImageName)>?>? snapshot = null,
        Func<int, IDisposable?>? openPin = null)
    {
        _snapshot = snapshot ?? SnapshotProcesses;
        _openPin = openPin ?? OpenPin;
    }

    public IProcessHandle? StartProcess(ProcessStartInfo startInfo)
    {
        // A started Process holds the handle CreateProcess or ShellExecuteEx returned until it is disposed, so its PID is
        // pinned like an opened one's
        var process = Process.Start(startInfo);
        return process == null ? null : new ProcessHandle(process, pin: null);
    }

    /// <summary>
    /// Opens a running process by ID, holding a handle to it (PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE) until the
    /// returned object is disposed. Review F4: Windows does not give a PID to another process while a handle to the process
    /// object is open, even after it exited, so every later read, wait, close request or kill through the returned object
    /// (which reopen the process by its ID) reaches this process, never one that got its PID after it exited. The handle is
    /// opened before the process is read, so the process read is the one pinned. When no handle can be opened (a protected
    /// process may refuse even that access), the process is opened without one, as before, and its PID is not pinned: the
    /// callers' checks of the start time are then what tells a reused PID.
    /// </summary>
    public IProcessHandle? OpenProcess(int processId)
    {
        var pin = processId > 0 ? _openPin(processId) : null;
        try
        {
            return new ProcessHandle(Process.GetProcessById(processId), pin);
        }
        catch (ArgumentException)
        {
            // No process with this ID is running (it may have exited, its object kept only by the pin)
            pin?.Dispose();
            return null;
        }
        catch
        {
            pin?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A handle to the process with query and wait access, or with either alone, or null when none can be opened. Any
    /// handle keeps the process object, and with it its PID, until it is closed.
    /// </summary>
    private static SafeProcessHandle? OpenPin(int processId)
    {
        foreach (var access in new[] { NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize, NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION, Synchronize })
        {
            var handle = NativeMethods.Kernel32.OpenProcess(access, false, (uint)processId);
            if (handle != nint.Zero)
            {
                return new SafeProcessHandle(handle, ownsHandle: true);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the process is an ancestor of this server; null when the process list cannot be read (review F3): the
    /// answer is then unknown, not no, and a caller that protects the agent's host must refuse.
    /// </summary>
    public bool? IsAncestorOfCurrentProcess(int processId)
    {
        var parentByProcessId = ReadParentProcessIds();
        return parentByProcessId == null
            ? null
            : FindAncestors(Environment.ProcessId, parentByProcessId, TryGetStartTime).Contains(processId);
    }

    /// <summary>
    /// Returns the current process's ancestors, nearest first (parent, grandparent, ...), or null when the process
    /// snapshot cannot be taken: the ancestors are then unknown, not absent.
    /// </summary>
    public static IReadOnlyList<int>? GetAncestorProcessIds()
    {
        var parentByProcessId = SnapshotParentProcessIds();
        return parentByProcessId == null
            ? null
            : FindAncestors(Environment.ProcessId, parentByProcessId, TryGetStartTime);
    }

    public IReadOnlyList<(int ProcessId, DateTime? StartTime)>? GetAncestorProcesses()
    {
        // Unknown, not "no ancestors" (review F3): the host guard records that and warns
        var parentByProcessId = ReadParentProcessIds();
        if (parentByProcessId == null)
        {
            return null;
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

    public IReadOnlyDictionary<int, (int ParentProcessId, string ImageName)>? GetProcessList() => _snapshot();

    public string? GetProcessImagePath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var handle = NativeMethods.Kernel32.OpenProcess(NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            return ReadImagePath(handle);
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(handle);
        }
    }

    public IReadOnlyList<RunningProcess>? ListRunningProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The system's process list could not be read
            return null;
        }

        var result = new List<RunningProcess>(processes.Length);
        foreach (var process in processes)
        {
            try
            {
                // All of these come from the list itself; none opens the process
                result.Add(new RunningProcess(
                    process.Id,
                    process.ProcessName,
                    ReadOrNull(() => process.WorkingSet64),
                    ReadOrNull(() => process.SessionId)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Exited between the list and the read
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;

        static T? ReadOrNull<T>(Func<T> read) where T : struct
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return null;
            }
        }
    }

    public ProcessDetails? ReadProcessDetails(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var handle = NativeMethods.Kernel32.OpenProcess(NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == nint.Zero)
        {
            // Not running, or access denied
            return null;
        }

        try
        {
            var startTime = NativeMethods.Kernel32.GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0
                ? DateTime.FromFileTimeUtc(creation)
                : (DateTime?)null;
            return new ProcessDetails(ReadImagePath(handle), startTime);
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>The image path of the process behind <paramref name="handle"/>, or null when it cannot be read.</summary>
    private static string? ReadImagePath(nint handle)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return NativeMethods.Kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
            ? new string(buffer, 0, (int)size)
            : null;
    }

    public string? GetProcessCommandLine(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var handle = NativeMethods.Kernel32.OpenProcess(NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            // A UNICODE_STRING followed by its text; asked once for the size, then read (a command line is at most
            // 32767 characters)
            NativeMethods.Ntdll.NtQueryInformationProcess(
                handle, NativeMethods.Ntdll.ProcessCommandLineInformation, nint.Zero, 0, out var size);
            if (size <= 0 || size > 128 * 1024)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NativeMethods.Ntdll.NtQueryInformationProcess(
                    handle, NativeMethods.Ntdll.ProcessCommandLineInformation, buffer, size, out _);
                if (status != 0)
                {
                    return null;
                }

                var length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return text == nint.Zero ? string.Empty : Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(handle);
        }
    }

    public bool? WaitForInputIdle(int processId, int milliseconds)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForInputIdle(Math.Max(0, milliseconds));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not running, no message loop (a console program), or access denied
            return null;
        }
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
        Func<int, DateTime?> getStartTime) =>
        WalkAncestors(processId, parentByProcessId, getStartTime).ToList();

    /// <summary>
    /// The parent of <paramref name="processId"/>, for reporting it: the parent in <paramref name="parentByProcessId"/>
    /// when it is still running, is not the process itself, and both start times are known and show it started no later
    /// than its child; null otherwise. Stricter than <see cref="FindAncestors"/>, which trusts a parent whose start time
    /// (or whose child's) is unknown: a reported parent must never be a later process that reused the real parent's PID,
    /// and an unknown start time cannot rule that out. Reads only the start times of the process and its parent.
    /// </summary>
    public static int? FindParent(
        int processId,
        IReadOnlyDictionary<int, int> parentByProcessId,
        Func<int, DateTime?> getStartTime)
    {
        if (!parentByProcessId.TryGetValue(processId, out var parent) ||
            parent <= 0 ||
            parent == processId ||
            !parentByProcessId.ContainsKey(parent))
        {
            return null;
        }

        if (getStartTime(processId) is not { } childStart)
        {
            return null;
        }

        return getStartTime(parent) is { } parentStart && parentStart <= childStart ? parent : null;
    }

    /// <summary>The walk of <see cref="FindAncestors"/>, lazily: each step reads only the start time it needs.</summary>
    private static IEnumerable<int> WalkAncestors(
        int processId,
        IReadOnlyDictionary<int, int> parentByProcessId,
        Func<int, DateTime?> getStartTime)
    {
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

            yield return parent;
            visited.Add(parent);
            current = parent;

            // Every further ancestor must have started before this one. If this start time is
            // unknown, the child's start time is still a valid upper bound.
            startTimeUpperBound = parentStart ?? startTimeUpperBound;
        }
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

    /// <summary>
    /// A process opened or started by this wrapper, with the handle that pins its PID (<see cref="OpenProcess"/>; null for
    /// a started process, whose <see cref="Process"/> holds its own, or when none could be opened).
    /// </summary>
    private sealed class ProcessHandle(Process process, IDisposable? pin) : IProcessHandle
    {
        public int Id => process.Id;

        public string ProcessName => process.ProcessName;

        public bool HasExited => process.HasExited;

        public int? ExitCode
        {
            get
            {
                try
                {
                    return process.HasExited ? process.ExitCode : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // No handle to read it from, or the process cannot be queried
                    return null;
                }
            }
        }

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

        public void Dispose()
        {
            // The pin last, so the PID stays this process's while the Process object lets go of it
            process.Dispose();
            pin?.Dispose();
        }
    }
}

/// <summary>One process in a process snapshot: its ID, its parent's ID and its image name (such as "KeePass.exe").</summary>
public readonly record struct ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string ImageName);
