namespace WindowsAppManagerMcp.Models;

/// <summary>Whose a debug browser's profile folder is (roadmap M5.4, <see cref="Services.DebugBrowserProfiles"/>).</summary>
public enum DebugProfileKind
{
    /// <summary>A new folder the server made for one launch, under its temporary root; deleted when the browser is closed.</summary>
    Temporary,

    /// <summary>A named profile under the server's profile root, kept between launches (its sign-ins and cookies).</summary>
    Named,

    /// <summary>A userDataDir the agent passed: not the server's, so it is never deleted.</summary>
    Custom
}

/// <summary>
/// A debug browser close_debug_browser may close (roadmap M5.4), as <see cref="Services.Interfaces.IDebugBrowserLifecycle.CheckClose"/>
/// found it: its main process, its profile from its own command line, and why the server may close it.
/// </summary>
/// <param name="ProcessId">The browser's main process (no --type switch).</param>
/// <param name="ProcessName">Its image name without .exe, for example "msedge".</param>
/// <param name="StartTime">Its start time, so that a process that got the PID later is never touched; null when unreadable.</param>
/// <param name="UserDataDir">The --user-data-dir on its command line, as a full path.</param>
/// <param name="ProfileKind">Whose that folder is.</param>
/// <param name="StartedByServer">True when this server started it (and it still runs with that profile); false when only
/// its command line proves it: a debug port and a profile in the server's own folders.</param>
/// <param name="DeletesProfile">Whether the folder is deleted once the browser has exited.</param>
/// <param name="Helpers">The browser's own child processes (the same .exe) with their start times: what is left of them
/// after the browser exits is ended.</param>
/// <param name="Warning">A note for the result (for example that deleteProfile does not apply to a userDataDir).</param>
public sealed record DebugBrowserCloseTarget(
    int ProcessId,
    string ProcessName,
    DateTime? StartTime,
    string UserDataDir,
    DebugProfileKind ProfileKind,
    bool StartedByServer,
    bool DeletesProfile,
    IReadOnlyList<(int ProcessId, DateTime? StartTime)> Helpers,
    string? Warning = null);

/// <summary>What close_debug_browser checked: the browser to close, or why it is refused (nothing was done).</summary>
public sealed record DebugBrowserCloseCheck(DebugBrowserCloseTarget? Target, DebugBrowserCloseResult? Refusal)
{
    public static DebugBrowserCloseCheck Refused(DebugBrowserCloseResult refusal) => new(null, refusal);
}

/// <summary>
/// What close_debug_browser did (roadmap M5.4).
/// </summary>
/// <param name="Success">True once the browser has exited (its profile may still be there: see ProfileDeleted).</param>
/// <param name="ClosedGracefully">True when the browser accepted Browser.close over its own DevTools endpoint.</param>
/// <param name="Forced">True when it was still running after the wait and was ended.</param>
/// <param name="Exited">Whether the browser's process has exited.</param>
/// <param name="ProfileDeleted">True when its profile folder was deleted; false when it should have been but could not
/// be; null when it is kept (a named profile without deleteProfile, or a userDataDir).</param>
/// <param name="ProfileKind">temporary, named or userDataDir.</param>
public sealed record DebugBrowserCloseResult(
    bool Success,
    int? ProcessId,
    string? Error = null,
    string? ErrorCode = null,
    string? UserDataDir = null,
    string? ProfileKind = null,
    bool? ClosedGracefully = null,
    bool? Forced = null,
    bool? Exited = null,
    bool? ProfileDeleted = null,
    string? Warning = null);

/// <summary>The wire names of <see cref="DebugProfileKind"/>, as close_debug_browser reports them.</summary>
public static class DebugProfileKindNames
{
    public static string ToWireName(this DebugProfileKind kind) => kind switch
    {
        DebugProfileKind.Temporary => "temporary",
        DebugProfileKind.Named => "named",
        DebugProfileKind.Custom => "userDataDir",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
