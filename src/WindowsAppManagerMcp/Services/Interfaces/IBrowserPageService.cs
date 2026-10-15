using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The DOM bridge (roadmap M5.8): browser_evaluate, browser_element_rect, browser_click, browser_type and
/// browser_screenshot on a debug browser's tab, through CDP only (browser_handle_dialog is <see cref="IBrowserDialogService"/>'s), for a browser window (its own debug port, found as
/// the reading tools find it, or cdpPort) or a debug port alone.
/// </summary>
/// <remarks>
/// Before anything is sent, the browser is checked as the tab tools check it (<see cref="IBrowserTabService"/>): a
/// recognized Chromium browser, allowed by the user's target policy (with a port alone, the process that listens on it),
/// whose port's listener is that browser's process (fail closed). The tab must show a web page (BrowserInternalPage
/// otherwise: the browser's own pages, extensions' and apps' pages; an allow list, checked on the listed URL and again on
/// the call's connection). The calls that change the page take the input gate only
/// for the commands that change it, and are refused while the user has paused input (PausedByUser, InputBusy: a tool
/// error). The results carry their errorCode as a string, null on success.
/// </remarks>
public interface IBrowserPageService
{
    /// <summary>Runs a script in the tab's page and returns its value (cut), or what it threw.</summary>
    Task<BrowserEvaluateResult> EvaluateAsync(
        BrowserTabTarget target, string expression, bool awaitPromise, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// An element's rectangle in physical screen pixels; needs the window (<see cref="BrowserTabTarget.WindowHandle"/>), and
    /// a tab that is the one the window shows and whose page is shown (ContentAreaNotFound otherwise).
    /// </summary>
    Task<BrowserElementRectResult> GetElementRectAsync(
        BrowserTabTarget target, BrowserElementLocator locator, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clicks an element with CDP's mouse events; with <paramref name="dialog"/>, answers a JavaScript dialog the click opens
    /// on the same connection (roadmap M5.8, dialogs).
    /// </summary>
    Task<BrowserInputResult> ClickAsync(
        BrowserTabTarget target, BrowserElementLocator locator, MouseButton button, int clickCount, CancellationToken cancellationToken = default,
        BrowserDialogAnswer? dialog = null);

    /// <summary>Types text into a field with Input.insertText; with <paramref name="dialog"/>, answers a dialog the typing opens.</summary>
    Task<BrowserInputResult> TypeAsync(
        BrowserTabTarget target, BrowserTypeOptions options, CancellationToken cancellationToken = default, BrowserDialogAnswer? dialog = null);

    /// <summary>Captures the tab's viewport, scaled to <paramref name="maxDimension"/> on its long edge (0: full size).</summary>
    Task<BrowserScreenshot> CaptureAsync(BrowserTabTarget target, int maxDimension, CancellationToken cancellationToken = default);
}
