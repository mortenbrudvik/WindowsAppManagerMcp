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

    /// <summary>
    /// The pages of the visible render widgets inside <paramref name="window"/>, which a walk of the window lists under the
    /// element of each render widget's parent window (roadmap M3.F35): Chromium's own views lie between that element and the
    /// page, deeper than a walk reaches. A render widget that is the window itself is left out, since the window's own tree
    /// is the page. Call it on the UI Automation worker thread, after <see cref="WarmUp"/> of the same window: it reuses the
    /// render widgets the warm-up found, so a window without web content costs no further call. A window without render
    /// widgets whose WebView2 page is hosted as a visual (roadmap M3.F43) gives that page, to be listed in place of the
    /// children of the root of Chromium's views (<see cref="RenderWidgetPage.ViewsRoot"/>): the one its warm-up found, or,
    /// for a window warmed up before, the one a bounded search finds again.
    /// </summary>
    /// <param name="window">The window about to be walked.</param>
    /// <param name="cancellationToken">The read's deadline: ends the search with the pages found so far.</param>
    /// <returns>The pages, at most <see cref="Services.AccessibilityWarmup.MaxRenderWidgets"/>, or none.</returns>
    /// <remarks>Never throws for a provider's failure: a page that cannot be read is left out.</remarks>
    IReadOnlyList<RenderWidgetPage> FindPages(nint window, CancellationToken cancellationToken);
}

/// <summary>The page of a render widget, as a walk lists it (<see cref="IAccessibilityWarmup.FindPages"/>, roadmap M3.F35).</summary>
/// <param name="RenderWidget">The Chrome_RenderWidgetHostHWND window Chromium renders the page into.</param>
/// <param name="Host">The render widget's parent window, whose element the page is listed under.</param>
/// <param name="Root">The page's element: its Document, or the render widget's own element when that holds the page's
/// elements with no Document above them (WebView2 runtime 155's "Chrome Legacy Window" pane).</param>
/// <param name="ViewsRoot">For a WebView2 page hosted as a visual (roadmap M3.F43), which has no render widget: the runtime id
/// of the root of Chromium's views (the first element of the Chrome framework below the window's own elements), whose
/// children the page replaces in the walk; <paramref name="RenderWidget"/> is then the window and <paramref name="Host"/>
/// zero. Null for a render widget's page.</param>
public sealed record RenderWidgetPage(nint RenderWidget, nint Host, IUiaNode Root, IReadOnlyList<int>? ViewsRoot = null);
