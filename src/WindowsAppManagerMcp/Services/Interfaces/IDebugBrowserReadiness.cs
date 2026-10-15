using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Checks that a debug browser's DevTools endpoint answers before launch_browser_with_debug reports success (roadmap
/// M5.1). <see cref="ProcessService"/> calls it once the browser has started, kept its window and reported its port; it
/// has no CDP dependency itself, so the check over /json/version is implemented next to the CDP transport (roadmap M5.2).
/// Without one, the launch is reported once the port is known.
/// </summary>
public interface IDebugBrowserReadiness
{
    /// <summary>
    /// Waits at most <paramref name="timeout"/> for the endpoint to answer. Not ready is reported, not thrown; a cancelled
    /// call throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<DebugBrowserReadiness> WaitUntilReadyAsync(DebugBrowserEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken);
}
