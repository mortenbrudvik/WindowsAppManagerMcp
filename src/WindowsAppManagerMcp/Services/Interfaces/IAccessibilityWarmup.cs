using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Turns on the web accessibility tree of a window that shows web content before it is read (roadmap M3.3). Chromium, and
/// the Electron, CEF and WebView2 apps built on it, build the accessibility tree of a page only after a UI Automation
/// client asks for it, so the first read of such a window sees an empty document and the agent wrongly concludes that the
/// control is absent.
/// </summary>
public interface IAccessibilityWarmup
{
    /// <summary>
    /// When <paramref name="window"/> (or a window inside it) shows web content and has not been warmed up yet, asks for its
    /// documents and waits, up to <paramref name="budget"/>, for them to fill. Call it on the UI Automation worker thread,
    /// inside the read it prepares (see <see cref="IUiaWorker"/>), so the read's deadline bounds it.
    /// </summary>
    /// <param name="window">The window about to be read.</param>
    /// <param name="budget">The longest wait; the wait ends sooner when <paramref name="cancellationToken"/> is cancelled.</param>
    /// <param name="cancellationToken">The read's deadline or the caller's cancellation: ends the wait with what it saw.</param>
    /// <returns>What the warm-up did, or null when nothing was done: the window shows no web content, or an earlier call
    /// already warmed it up.</returns>
    /// <remarks>Never throws for a provider's failure: the warm-up is a courtesy, and the read that follows reports its
    /// own errors.</remarks>
    AccessibilityWarmupInfo? WarmUp(nint window, TimeSpan budget, CancellationToken cancellationToken);
}
