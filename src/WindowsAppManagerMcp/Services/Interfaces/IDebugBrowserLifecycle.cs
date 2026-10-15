using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The debug browsers' lifecycle (roadmap M5.4): launch_browser_with_debug's named profiles and reuseIfRunning, the
/// browsers the server started, and close_debug_browser, which closes only a browser that is provably a debug browser
/// of the server's and never the user's own.
/// </summary>
public interface IDebugBrowserLifecycle
{
    /// <summary>
    /// Launches a debug browser through <see cref="IProcessService.LaunchBrowserWithDebugAsync"/>: a
    /// <see cref="DebugBrowserLaunchRequest.ProfileName"/> becomes its folder under the server's profile root; with
    /// <see cref="DebugBrowserLaunchRequest.ReuseIfRunning"/>, a debug browser already running with the profile (found by
    /// the --user-data-dir on its main process's command line and its own DevTools port, never by a window title) is
    /// returned as <see cref="LaunchStatus.AlreadyRunning"/> instead. A launch with a new temporary profile first deletes
    /// the temporary profiles earlier browsers left. The browser started is remembered for <see cref="CheckClose"/>.
    /// </summary>
    Task<DebugBrowserLaunchResult> LaunchAsync(DebugBrowserLaunchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the browser a successful <see cref="LaunchAsync"/> started when the launch_browser_with_debug call is cancelled
    /// after it (during the placement): the cancelled call sends no result, so nobody learns its PID or its unauthenticated
    /// port. Only a browser this launch started (never one it reused) whose PID still has the start time read at its start;
    /// its temporary profile is then deleted. Best effort; never throws.
    /// </summary>
    Task AbandonAsync(DebugBrowserLaunchResult launch);

    /// <summary>
    /// Finds the debug browser <paramref name="processId"/> or the listener of <paramref name="cdpPort"/> names (one or
    /// both; both must agree) and checks that close_debug_browser may close it: the main process of chrome, msedge or
    /// brave, whose command line has a DevTools port and a --user-data-dir that is either the one it was started with by
    /// this server (same PID and start time) or a folder of the server's own (<see cref="DebugBrowserProfiles"/>); not the
    /// agent's host; allowed by the target policy. Only reads; the refusal says why, and nothing is done.
    /// </summary>
    DebugBrowserCloseCheck CheckClose(int? processId, int? cdpPort, bool deleteProfile);

    /// <summary>
    /// Closes a browser <see cref="CheckClose"/> accepted, if it is still that process with that profile: Browser.close over
    /// its own DevTools endpoint, a wait of up to <paramref name="timeout"/> for it to exit, then an end of the browser
    /// process and of what is left of its own child processes; then its profile is deleted when the target says so.
    /// <paramref name="enterGate"/> takes the input gate around each step that can close windows (the Browser.close and the
    /// end), not around the waits.
    /// </summary>
    Task<DebugBrowserCloseResult> CloseAsync(
        DebugBrowserCloseTarget target,
        TimeSpan timeout,
        Func<CancellationToken, Task<IDisposable>> enterGate,
        CancellationToken cancellationToken = default);
}
