using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// list_tabs, activate_tab, browser_navigate and wait_for_browser (roadmap M5.5): a browser's tabs through CDP only, for a
/// browser window (its own debug port, found as the reading tools find it, or cdpPort) or a debug port alone.
/// </summary>
/// <remarks>
/// Before anything is sent, the browser is checked as the reads check it: a recognized Chromium browser (NotABrowser;
/// Firefox has no CDP), allowed by the user's target policy (TargetDeniedByPolicy; with a port alone, the policy is
/// applied to the process that listens on it), and the port's listener is that browser's process (CdpPortOwnerMismatch,
/// also when it cannot be read). There is no UI Automation fallback: typing in the address bar would be keyboard input to a
/// window the agent did not name. The results carry their errorCode as a string, null on success.
/// </remarks>
public interface IBrowserTabService
{
    /// <summary>Lists the browser's tabs; with a window, which of them are in it and which one it shows when that can be told.</summary>
    Task<BrowserTabsResult> ListTabsAsync(BrowserTabTarget target, CancellationToken cancellationToken = default);

    /// <summary>Brings the tab <see cref="BrowserTabTarget.TargetId"/> to the front of its browser window.</summary>
    Task<BrowserTabResult> ActivateTabAsync(BrowserTabTarget target, CancellationToken cancellationToken = default);

    /// <summary>Navigates a tab (or a new one) to a URL and waits for it to load.</summary>
    Task<BrowserNavigateResult> NavigateAsync(BrowserTabTarget target, BrowserNavigateOptions options, CancellationToken cancellationToken = default);

    /// <summary>Waits until a tab's page meets <paramref name="condition"/>, for at most <paramref name="timeoutMs"/>.</summary>
    Task<BrowserWaitResult> WaitAsync(
        BrowserTabTarget target, BrowserWaitCondition condition, int timeoutMs, CancellationToken cancellationToken = default);
}
