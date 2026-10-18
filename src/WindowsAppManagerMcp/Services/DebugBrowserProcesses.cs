using System.ComponentModel;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Ends a debug browser the server started, and only what it provably started (review F5): the browser process alone, never
/// its process tree, then what is left of its own child processes. A program the browser opened (a download's program, or
/// the user's own browser that a link started) is in its process tree too, and is never ended. One rule for every path that
/// ends a debug browser: close_debug_browser (<see cref="DebugBrowserLifecycle"/>), a launch abandoned during its placement
/// (<see cref="DebugBrowserLifecycle.AbandonAsync"/>), and a launch_browser_with_debug call cancelled during the launch
/// (<see cref="ProcessService.LaunchBrowserWithDebugAsync"/>).
/// </summary>
/// <param name="native">Reads the process tree, command lines and start times; opens and ends processes.</param>
/// <param name="hostGuard">The agent's host, whose processes are never ended; the server always passes it.</param>
internal sealed class DebugBrowserProcesses(INativeProcessWrapper native, IHostGuard? hostGuard)
{
    /// <summary>The switch that names a Chromium process's profile folder.</summary>
    public const string ProfileSwitch = "user-data-dir";

    /// <summary>The switch Chromium gives its child processes (renderer, GPU, utility), and never the browser itself.</summary>
    public const string TypeSwitch = "type";

    /// <summary>
    /// The browser's own child processes, with their start times, to end what is left of them once it exits: the same .exe
    /// with a --type and the browser's own --user-data-dir. Another program it opened is not one, nor is the user's own
    /// browser that a link started (the same .exe, a descendant, but without a --type or that profile), nor one whose
    /// command line cannot be read, nor the agent's host. Read before the browser is ended: its children are found through
    /// it.
    /// </summary>
    public List<(int ProcessId, DateTime? StartTime)> HelpersOf(int processId, string imageName, string profile) =>
        (native.GetDescendantsOf(processId) ?? [])
            .Where(d => string.Equals(d.ImageName, imageName, StringComparison.OrdinalIgnoreCase) && hostGuard?.IsHostProcess(d.ProcessId) != true &&
                        IsChildOf(d.ProcessId, profile))
            .Select(d => (d.ProcessId, native.GetProcessStartTime(d.ProcessId)))
            .ToList();

    /// <summary>
    /// <see cref="HelpersOf(int, string, string)"/> of a browser the server just started on <paramref name="userDataDir"/>;
    /// none when its image or profile is not known (a browser launch_browser_with_debug does not start), and then its
    /// helpers exit with it.
    /// </summary>
    public List<(int ProcessId, DateTime? StartTime)> HelpersOf(int processId, string? userDataDir)
    {
        if (userDataDir == null || native.GetProcessList() is not { } processes || !processes.TryGetValue(processId, out var entry) ||
            !DebugBrowserLocator.ExecutableNames.Contains(entry.ImageName))
        {
            return [];
        }

        return HelpersOf(processId, entry.ImageName, userDataDir);
    }

    /// <summary>
    /// Ends a debug browser the server started on <paramref name="userDataDir"/>, which the caller checked is still that
    /// browser (its handle pins the PID): the browser process alone, then, once it has exited, what is left of its own child
    /// processes. Best effort.
    /// </summary>
    /// <param name="process">The browser's open handle.</param>
    /// <param name="processId">The browser's PID.</param>
    /// <param name="userDataDir">The profile the launch started it with.</param>
    /// <param name="exitTimeout">How long the browser, and then its child processes, may take to exit once ended.</param>
    public async Task EndStartedBrowserAsync(IProcessHandle process, int processId, string? userDataDir, TimeSpan exitTimeout)
    {
        ArgumentNullException.ThrowIfNull(process);
        var helpers = HelpersOf(processId, userDataDir);
        if (End(process))
        {
            await process.WaitForExitAsync((int)exitTimeout.TotalMilliseconds, CancellationToken.None).ConfigureAwait(false);
            await EndHelpersAsync(helpers, exitTimeout, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ends what is left of the browser's own child processes: only those that still have the start time read with them,
    /// so a PID that went to another process meanwhile is not ended. How many were ended.
    /// </summary>
    public async Task<int> EndHelpersAsync(
        IReadOnlyList<(int ProcessId, DateTime? StartTime)> helpers, TimeSpan exitTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(helpers);
        var ended = new List<IProcessHandle>();
        try
        {
            foreach (var (processId, startTime) in helpers)
            {
                if (startTime == null || native.GetProcessStartTime(processId) != startTime)
                    continue;

                var helper = native.OpenProcess(processId);
                if (helper == null)
                    continue;

                if (HasExited(helper) || !End(helper))
                {
                    helper.Dispose();
                    continue;
                }

                ended.Add(helper);
            }

            await Task.WhenAll(ended.Select(h => h.WaitForExitAsync((int)exitTimeout.TotalMilliseconds, cancellationToken))).ConfigureAwait(false);
            return ended.Count;
        }
        finally
        {
            foreach (var helper in ended)
                helper.Dispose();
        }
    }

    /// <summary>Ends one process (not its tree); false when it could not be ended (it exited meanwhile, or access is denied).</summary>
    public static bool End(IProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether the process has exited; false when that cannot be read.</summary>
    public static bool HasExited(IProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The --user-data-dir of a command line as a full path, or null when it has none or a relative one.</summary>
    public static string? ProfileOf(IReadOnlyDictionary<string, string> switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        if (!switches.TryGetValue(ProfileSwitch, out var profile) || string.IsNullOrWhiteSpace(profile))
            return null;

        var trimmed = profile.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            return null;

        try
        {
            return DebugBrowserProfiles.Normalize(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Whether a descendant is a Chromium child process of the browser on <paramref name="profile"/>.</summary>
    private bool IsChildOf(int processId, string profile)
    {
        var switches = ChromiumCommandLine.Parse(native.GetProcessCommandLine(processId));
        return switches.ContainsKey(TypeSwitch) && ProfileOf(switches) is { } own && DebugBrowserProfiles.SamePath(own, profile);
    }
}
