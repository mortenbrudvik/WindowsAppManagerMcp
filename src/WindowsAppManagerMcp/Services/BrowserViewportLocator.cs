using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Finds where a browser window shows a tab's page (roadmap M5.8, <see cref="IBrowserViewportLocator"/>): the tab's
/// Document through UI Automation first (read on a real Edge and Chrome 155: the Document, AutomationId RootWebArea, bounds
/// the page's viewport), on the UI Automation worker under the call's deadline and after the same warm-up as the reads;
/// else the window's visible Chrome_RenderWidgetHostHWND child of the viewport's size. A window that stayed behind other
/// windows since its page loaded has no Document (Chromium builds the page's tree only while it is shown) and may have no
/// visible render widget either: then nothing is found.
/// </summary>
public sealed class BrowserViewportLocator : IBrowserViewportLocator
{
    /// <summary>How far, in pixels, a found rectangle's size may be from the viewport's: rounding of fractional scales.</summary>
    public const int SizeTolerancePx = 4;

    private readonly INativeWindowWrapper _windows;
    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly BrowserUiaReader _reader;

    /// <param name="windows">The window reads (the render widgets' rectangles).</param>
    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the UI Automation read under its deadline.</param>
    /// <param name="warmup">Warms the page's accessibility tree up and finds its render widgets; the server always passes it.</param>
    public BrowserViewportLocator(INativeWindowWrapper windows, IUiaTree tree, IUiaWorker worker, IAccessibilityWarmup? warmup = null)
    {
        _windows = windows;
        _tree = tree;
        _worker = worker;
        _warmup = warmup;
        _reader = new BrowserUiaReader(tree);
    }

    /// <inheritdoc />
    public async Task<BrowserViewportLookup> FindAsync(
        nint window, string pageUrl, double expectedWidth, double expectedHeight, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);
        var document = await ReadDocumentAsync(window, pageUrl, timeout, cancellationToken).ConfigureAwait(false);
        string? documentDetail = null;
        if (document is { } bounds)
        {
            if (Fits(bounds.Width, bounds.Height, expectedWidth, expectedHeight))
            {
                return new BrowserViewportLookup(new BrowserViewport(bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    BrowserViewport.DocumentSource));
            }

            documentDetail = $"the tab's Document is {bounds.Width:0} x {bounds.Height:0} pixels, not the page's " +
                             $"{expectedWidth:0} x {expectedHeight:0}";
        }

        var widgets = new List<(int Left, int Top, int Right, int Bottom)>();
        var seen = 0;
        _windows.EnumChildWindows(window, child =>
        {
            seen++;
            if (_windows.GetClassName(child) == AccessibilityWarmup.RenderWidgetHostClass && _windows.IsWindowVisible(child) &&
                _windows.GetWindowRect(child) is { } rect &&
                Fits(rect.Right - rect.Left, rect.Bottom - rect.Top, expectedWidth, expectedHeight))
            {
                widgets.Add(rect);
            }

            return widgets.Count <= 1 && seen < AccessibilityWarmup.MaxChildWindows;
        });

        if (widgets.Count == 1)
        {
            var (left, top, right, bottom) = widgets[0];
            return new BrowserViewportLookup(new BrowserViewport(left, top, right - left, bottom - top, BrowserViewport.RenderWidgetSource));
        }

        var why = widgets.Count > 1
            ? "two render widgets of the page's size are shown (split tabs?), so neither can be told for the tab's"
            : documentDetail ?? "UI Automation shows no Document with the tab's address, and no shown render widget has the page's size";
        return new BrowserViewportLookup(null, why);
    }

    /// <summary>The tab's Document's bounds through UI Automation, or null.</summary>
    private async Task<UiaRect?> ReadDocumentAsync(nint window, string pageUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await _worker.RunAsync(UiaTargets.TargetProcessId(_windows, window), token =>
            {
                if (!_tree.IsAvailable())
                    return (UiaRect?)null;

                _warmup?.WarmUp(window, AccessibilityWarmup.BudgetFor(timeout), token);
                var node = _tree.FromHandle(window);
                if (node == null)
                    return null;

                var pages = _warmup?.FindPages(window, token)?.Select(page => page.Root).ToList();
                return _reader.FindPageBounds(node, _windows.GetWindowText(window), pages, pageUrl, token);
            }, deadline.Token, TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A read that timed out, was refused or failed finds nothing; the render widget may still tell
            return null;
        }
    }

    /// <summary>Whether a rectangle is the viewport's size, within <see cref="SizeTolerancePx"/> pixels or 1 %.</summary>
    public static bool Fits(double width, double height, double expectedWidth, double expectedHeight)
    {
        if (!(width > 0 && height > 0 && expectedWidth > 0 && expectedHeight > 0))
            return false;

        bool Near(double actual, double expected) => Math.Abs(actual - expected) <= Math.Max(SizeTolerancePx, expected / 100);
        return Near(width, expectedWidth) && Near(height, expectedHeight);
    }
}
