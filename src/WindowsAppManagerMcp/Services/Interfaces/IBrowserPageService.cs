using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The DOM bridge (roadmap M5.8): browser_evaluate, browser_handle_dialog, browser_element_rect, browser_click, browser_type
/// and browser_screenshot on a debug browser's tab, through CDP only, for a browser window (its own debug port, found as
/// the reading tools find it, or cdpPort) or a debug port alone.
/// </summary>
/// <remarks>
/// Before anything is sent, the browser is checked as the tab tools check it (<see cref="IBrowserTabService"/>): a
/// recognized Chromium browser, allowed by the user's target policy (with a port alone, the process that listens on it),
/// whose port's listener is that browser's process (fail closed). The tab must not be one of the browser's own pages
/// (BrowserInternalPage: chrome:, edge:, devtools:, extensions'). The calls that change the page take the input gate only
/// for the commands that change it, and are refused while the user has paused input (PausedByUser, InputBusy: a tool
/// error). The results carry their errorCode as a string, null on success.
/// </remarks>
public interface IBrowserPageService
{
    /// <summary>Runs a script in the tab's page and returns its value (cut), or what it threw.</summary>
    Task<BrowserEvaluateResult> EvaluateAsync(
        BrowserTabTarget target, string expression, bool awaitPromise, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>Accepts or dismisses the tab's open JavaScript dialog.</summary>
    Task<BrowserDialogResult> HandleDialogAsync(
        BrowserTabTarget target, bool accept, string? promptText, CancellationToken cancellationToken = default);

    /// <summary>An element's rectangle in physical screen pixels; needs the window (<see cref="BrowserTabTarget.WindowHandle"/>).</summary>
    Task<BrowserElementRectResult> GetElementRectAsync(
        BrowserTabTarget target, BrowserElementLocator locator, CancellationToken cancellationToken = default);

    /// <summary>Clicks an element with CDP's mouse events.</summary>
    Task<BrowserInputResult> ClickAsync(
        BrowserTabTarget target, BrowserElementLocator locator, MouseButton button, int clickCount, CancellationToken cancellationToken = default);

    /// <summary>Types text into a field with Input.insertText.</summary>
    Task<BrowserInputResult> TypeAsync(BrowserTabTarget target, BrowserTypeOptions options, CancellationToken cancellationToken = default);

    /// <summary>Captures the tab's viewport, scaled to <paramref name="maxDimension"/> on its long edge (0: full size).</summary>
    Task<BrowserScreenshot> CaptureAsync(BrowserTabTarget target, int maxDimension, CancellationToken cancellationToken = default);
}
