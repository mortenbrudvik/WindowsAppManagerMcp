using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Decides whether a window belongs to a launch (roadmap M2.6), whichever process owns it. A window belongs to it when
/// its app process (for a UWP frame, the CoreWindow's process) is:
/// <list type="bullet">
/// <item>the started process, or a process it started (directly or further down), each started after the launch: a
/// launcher's child keeps its parent's PID even after the launcher exited, so the start times rule out reused PIDs;</item>
/// <item>a process with the started program's image path (QueryFullProcessImageNameW): a running instance it handed off to;</item>
/// <item>a process with the program's name, or a name of the handoff map (wt to WindowsTerminal, calc to CalculatorApp,
/// ms-settings: to SystemSettings, ...): the app the launcher or URI hands off to;</item>
/// <item>or a match of the caller's <see cref="LaunchWindowHint"/> (every field it sets).</item>
/// </list>
/// The process checks are made once per process and remembered for the launch; the hint's title is checked each time.
/// An instance may be used from several threads (the waits of a batch check each other's started processes).
/// </summary>
public sealed class LaunchWindowOwner
{
    /// <summary>Process start times are compared with this tolerance, for clock granularity.</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>How many parent links a descendant may be away from the started process.</summary>
    private const int MaxDescendantDepth = 16;

    /// <summary>The browsers a web address may open in (the user's default browser is one of them).</summary>
    private static readonly string[] Browsers = ["msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc", "iexplore"];

    /// <summary>
    /// Launchers that hand off to a process with another name: the program (file name without extension) or URI scheme,
    /// and the process names that show its window. Console programs get a Windows Terminal window when Terminal is the
    /// default console host; a web address opens in the default browser.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> HandoffMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["http"] = Browsers,
        ["https"] = Browsers,
        ["calc"] = ["CalculatorApp", "Calculator"],
        ["wt"] = ["WindowsTerminal"],
        ["code"] = ["Code"],
        ["code-insiders"] = ["Code - Insiders"],
        ["cmd"] = ["WindowsTerminal"],
        ["powershell"] = ["WindowsTerminal"],
        ["pwsh"] = ["WindowsTerminal"],
        ["wsl"] = ["WindowsTerminal"],
        ["ms-settings"] = ["SystemSettings"],
    };

    private readonly INativeProcessWrapper _processes;
    private readonly int? _launchedProcessId;
    private readonly DateTime _launchedAtUtc;
    private readonly string? _imagePath;
    private readonly HashSet<string> _processNames;
    private readonly LaunchWindowHint? _hint;
    private readonly Dictionary<int, bool> _processMatches = [];
    private readonly Dictionary<int, bool> _startedProcesses = [];
    private readonly Dictionary<int, string?> _processNamesById = [];
    private readonly object _sync = new();

    /// <param name="processes">Reads the process list, image paths and start times.</param>
    /// <param name="launchedProcessId">The started process; null when Windows started none (a handoff).</param>
    /// <param name="launchedAtUtc">When the start was requested (before Process.Start).</param>
    /// <param name="imagePath">The started program's full path when it is a native .exe or .com; else null.</param>
    /// <param name="processNames">Process names (without .exe) whose windows count: the program's own name and its handoff names.</param>
    /// <param name="hint">The caller's expectWindow hint, or null.</param>
    public LaunchWindowOwner(
        INativeProcessWrapper processes,
        int? launchedProcessId,
        DateTime launchedAtUtc,
        string? imagePath,
        IEnumerable<string> processNames,
        LaunchWindowHint? hint)
    {
        _processes = processes;
        _launchedProcessId = launchedProcessId;
        _launchedAtUtc = launchedAtUtc;
        _imagePath = imagePath;
        _processNames = new HashSet<string>(processNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(StripExe), StringComparer.OrdinalIgnoreCase);
        _hint = hint is { IsEmpty: false } ? hint : null;
    }

    /// <summary>
    /// The owner of a launch's windows: its process, the program's path, name and handoff names, and the hint.
    /// </summary>
    /// <param name="target">What was started: the resolved file name (or URI) and its kind.</param>
    public static LaunchWindowOwner For(
        INativeProcessWrapper processes, int? launchedProcessId, DateTime launchedAtUtc, ResolvedLaunchTarget target, LaunchWindowHint? hint)
    {
        var names = new List<string>();
        string? imagePath = null;
        var key = ProgramKey(target);
        if (key != null)
        {
            if (target.Kind != LaunchTargetKind.Other)
                names.Add(key);

            if (HandoffMap.TryGetValue(key, out var handoffNames))
                names.AddRange(handoffNames);
        }

        if (target.Kind == LaunchTargetKind.NativeExecutable && Path.IsPathFullyQualified(target.FileName))
            imagePath = target.FileName;

        return new LaunchWindowOwner(processes, launchedProcessId, launchedAtUtc, imagePath, names, hint);
    }

    /// <summary>
    /// Handoffs to a host, not to the same app: a console program gets a Windows Terminal window and a web address a
    /// browser window, and an open terminal or browser window is not an open copy of that program. reuseExisting (roadmap
    /// M3.13) leaves these names out.
    /// </summary>
    private static readonly HashSet<string> HostHandoffs = new(StringComparer.OrdinalIgnoreCase) { "cmd", "powershell", "pwsh", "wsl", "http", "https" };

    /// <summary>
    /// The process names (without .exe) whose open windows are copies of the launched program, for reuseExisting (roadmap
    /// M3.13): the program's own name and the names of the same app it hands off to (calc to CalculatorApp, code to Code,
    /// wt to WindowsTerminal, ms-settings: to SystemSettings), but not a host it hands off to (<see cref="HostHandoffs"/>).
    /// Empty for a document or URI whose app is not known.
    /// </summary>
    public static IReadOnlyList<string> SameAppProcessNames(ResolvedLaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var names = new List<string>();
        var key = ProgramKey(target);
        if (key == null)
            return names;

        if (target.Kind != LaunchTargetKind.Other)
            names.Add(key);

        if (!HostHandoffs.Contains(key) && HandoffMap.TryGetValue(key, out var handoffNames))
            names.AddRange(handoffNames);

        return names;
    }

    /// <summary>
    /// The program's name (a resolved file without its extension) or a URI's scheme, which keys <see cref="HandoffMap"/>;
    /// null for a document or anything else whose app is not known.
    /// </summary>
    private static string? ProgramKey(ResolvedLaunchTarget target)
    {
        if (target.Kind != LaunchTargetKind.Other)
            return Path.GetFileNameWithoutExtension(target.FileName);

        var colon = target.FileName.IndexOf(':');
        return colon > 1 && !target.FileName.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? target.FileName[..colon]
            : null;
    }

    /// <summary>Whether the window belongs to the launch.</summary>
    public bool Matches(WindowCandidate window)
    {
        if (window.ProcessId <= 0)
            return false;

        lock (_sync)
        {
            if (_hint != null && HintMatches(window))
                return true;

            if (!_processMatches.TryGetValue(window.ProcessId, out var matches))
            {
                matches = ProcessMatches(window.ProcessId);
                _processMatches[window.ProcessId] = matches;
            }

            return matches;
        }
    }

    /// <summary>
    /// Whether the process is the one this launch started, or a process it started (the strongest match, which no
    /// other launch of a batch can claim): never for a handoff, where Windows started no process.
    /// </summary>
    public bool StartedProcess(int processId)
    {
        if (processId <= 0 || _launchedProcessId == null)
            return false;

        if (processId == _launchedProcessId)
            return true;

        lock (_sync)
        {
            if (!_startedProcesses.TryGetValue(processId, out var started))
            {
                started = IsLaunchedDescendant(processId);
                _startedProcesses[processId] = started;
            }

            return started;
        }
    }

    private bool HintMatches(WindowCandidate window)
    {
        if (!string.IsNullOrWhiteSpace(_hint!.TitleContains) &&
            !window.Title.Contains(_hint.TitleContains.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(_hint.ProcessName))
            return true;

        var wanted = StripExe(_hint.ProcessName.Trim());
        return new[] { window.ProcessId, window.HostProcessId ?? 0 }
            .Where(id => id > 0)
            .Select(ProcessNameOf)
            .Any(name => name != null && name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private bool ProcessMatches(int processId)
    {
        if (processId == _launchedProcessId)
            return true;

        if (_processNames.Count > 0 && ProcessNameOf(processId) is { } name && _processNames.Contains(name))
            return true;

        if (_imagePath != null && _processes.GetProcessImagePath(processId) is { } path &&
            string.Equals(path, _imagePath, StringComparison.OrdinalIgnoreCase))
            return true;

        return StartedProcess(processId); // the lock is re-entrant
    }

    /// <summary>
    /// True when the parent links lead from <paramref name="processId"/> to the started process, and every process on
    /// the way (the started process itself aside) started after the launch: a parent PID can belong to a process that
    /// exited, and Windows reuses PIDs, so a process that is older than the launch cannot be its child.
    /// </summary>
    private bool IsLaunchedDescendant(int processId)
    {
        var processList = _processes.GetProcessList();
        if (processList == null)
            return false;

        var notBefore = _launchedAtUtc - StartTimeTolerance;
        var current = processId;
        for (var depth = 0; depth < MaxDescendantDepth; depth++)
        {
            if (!processList.TryGetValue(current, out var entry))
                return false;

            if (_processes.GetProcessStartTime(current) is not { } started || started < notBefore)
                return false;

            if (entry.ParentProcessId == _launchedProcessId)
                return true;

            if (entry.ParentProcessId <= 0 || entry.ParentProcessId == current)
                return false;

            current = entry.ParentProcessId;
        }

        return false;
    }

    /// <summary>A process's image name without .exe, from the process list; remembered per launch.</summary>
    private string? ProcessNameOf(int processId)
    {
        if (!_processNamesById.TryGetValue(processId, out var name))
        {
            name = _processes.GetProcessList() is { } list && list.TryGetValue(processId, out var entry) ? StripExe(entry.ImageName) : null;
            _processNamesById[processId] = name;
        }

        return name;
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
