using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Where a browser window shows a tab's page on the screen (roadmap M5.8), so a rectangle in the page's CSS pixels can be
/// turned into physical screen pixels: the tab's Document through UI Automation (its BoundingRectangle; the Document whose
/// address is the tab's), else the visible render widget window (Chrome_RenderWidgetHostHWND) of the page's size. Both must
/// be the size of the page's viewport in device pixels, so a docked DevTools pane, a side panel or another tab's page is
/// never taken for it. Nothing is computed from screenX or the window's outer and inner sizes, which leave out the
/// browser's own toolbars and side panels differently in every browser.
/// </summary>
public interface IBrowserViewportLocator
{
    /// <summary>
    /// The viewport of the page at <paramref name="pageUrl"/> in <paramref name="window"/>, which must be
    /// <paramref name="expectedWidth"/> by <paramref name="expectedHeight"/> physical pixels (innerWidth and innerHeight
    /// times devicePixelRatio, within a few pixels). Read-only; bounded by <paramref name="timeout"/>.
    /// </summary>
    Task<BrowserViewportLookup> FindAsync(
        nint window, string pageUrl, double expectedWidth, double expectedHeight, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>The viewport found, or why none was (<see cref="Detail"/>).</summary>
public sealed record BrowserViewportLookup(BrowserViewport? Viewport, string? Detail = null);
