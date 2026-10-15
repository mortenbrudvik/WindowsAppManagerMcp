using System.Drawing;
using System.Text.Json;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The DOM bridge (roadmap M5.8, <see cref="IBrowserPageService"/>): finds the browser, its debug port and the tab as the
/// tab tools do (<see cref="BrowserCdpTargets"/>: the target policy on the window's or the port's process, the port's
/// listener checked to be that process, fail closed), refuses the browser's own pages, and leaves the CDP work to
/// <see cref="ICdpPageService"/>. browser_element_rect anchors the page's CSS pixels on the screen through
/// <see cref="IBrowserViewportLocator"/>; browser_screenshot scales the capture as the capture tools do.
/// </summary>
public sealed class BrowserPageService : IBrowserPageService
{
    /// <summary>The time limit of a click, a typing, a dialog's answer and an element's lookup, the connect included.</summary>
    public static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The time limit of a capture: a tab brought to the front may take a moment to draw its first frame.</summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long the UI Automation and window reads of browser_element_rect's anchor may take.</summary>
    public static readonly TimeSpan AnchorTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A capture whose long edge is at most this many pixels is PNG, a larger one JPEG, as the capture tools' "auto".</summary>
    public const int PngMaxEdge = CaptureOptions.AutoPngMaxEdge;

    private readonly ICdpPageService _pages;
    private readonly IBrowserViewportLocator _viewports;
    private readonly IInputSession? _session;
    private readonly IBrowserDialogService? _dialogs;
    private readonly BrowserCdpTargets _targets;

    /// <param name="windows">The window and process reads.</param>
    /// <param name="detection">Tells browsers from other programs.</param>
    /// <param name="reading">Tells the browser family (Firefox has no CDP).</param>
    /// <param name="cdp">Finds the tab a window shows, or a tab by id.</param>
    /// <param name="pages">The CDP side of the DOM bridge.</param>
    /// <param name="portOwners">Tells which process listens on a port given without a window.</param>
    /// <param name="viewports">Finds where the window shows the page, for browser_element_rect.</param>
    /// <param name="policy">The user's target policy; the server always passes it.</param>
    /// <param name="portLocator">Finds a window's own debug port; the server always passes it. Without it, cdpPort is needed.</param>
    /// <param name="session">The input session the gate checks for a pause; the server always passes it.</param>
    /// <param name="dialogs">Learns of the dialog an action opened and left open, so browser_handle_dialog may accept it; the
    /// server always passes it.</param>
    public BrowserPageService(
        INativeWindowWrapper windows,
        IBrowserDetectionService detection,
        IBrowserReadingService reading,
        ICdpService cdp,
        ICdpPageService pages,
        ITcpPortOwnerResolver portOwners,
        IBrowserViewportLocator viewports,
        ITargetPolicy? policy = null,
        IDebugPortLocator? portLocator = null,
        IInputSession? session = null,
        IBrowserDialogService? dialogs = null)
    {
        _pages = pages;
        _viewports = viewports;
        _session = session;
        _dialogs = dialogs;
        _targets = new BrowserCdpTargets(windows, detection, reading, cdp, portOwners, policy, portLocator);
    }

    /// <inheritdoc />
    public async Task<BrowserEvaluateResult> EvaluateAsync(
        BrowserTabTarget target, string expression, bool awaitPromise, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var tab = await FindTabAsync(target, cancellationToken).ConfigureAwait(false);
        if (tab.Refusal is { } refusal)
            return BrowserEvaluateResult.Failed(refusal.Code, refusal.Error, tab.Port, target.TargetId);

        var evaluated = await _pages.EvaluateAsync(tab.Target!, expression, awaitPromise, EnterGateAsync,
            TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);
        if (!evaluated.Success)
            return BrowserEvaluateResult.Failed(evaluated.ErrorCode!, evaluated.Error!, tab.Port, tab.Target!.Id);

        var outcome = evaluated.Value!;
        if (outcome.Exception is { } thrown)
        {
            return BrowserEvaluateResult.Failed(nameof(BrowserErrorCode.CdpEvaluationFailed),
                    "The script threw (or its promise was rejected); exception has what it threw, from the page. The page may have " +
                    "changed before it threw.", tab.Port, tab.Target!.Id) with
                {
                    Exception = thrown
                };
        }

        var (value, length, truncated) = Bounded(outcome.Value);
        return new BrowserEvaluateResult(true, tab.Target!.Id, value, outcome.ValueType, length, truncated, CdpPort: tab.Port);
    }

    /// <inheritdoc />
    public async Task<BrowserElementRectResult> GetElementRectAsync(
        BrowserTabTarget target, BrowserElementLocator locator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var tab = await FindTabAsync(target, cancellationToken).ConfigureAwait(false);
        if (tab.Refusal is { } refusal)
            return BrowserElementRectResult.Failed(refusal.Code, refusal.Error, tab.Port, target.TargetId);

        if (tab.WindowHandle is not { } window)
        {
            return BrowserElementRectResult.Failed(ToolErrorReporting.InvalidArgumentsErrorCode,
                "browser_element_rect needs the browser window (handle or window): the rect is placed where that window shows the " +
                "page. Nothing was read.", tab.Port, tab.Target!.Id);
        }

        // A tab named by id must be the one the window shows: every tab of a window has the same viewport, so another tab's
        // element would be placed on the page the window shows
        if (target.TargetId != null)
        {
            var shown = await _targets.FindTabAsync(tab.Browser!, null, cancellationToken).ConfigureAwait(false);
            if (!shown.Success || shown.Value!.Id != tab.Target!.Id)
            {
                return BrowserElementRectResult.Failed(nameof(BrowserErrorCode.ContentAreaNotFound),
                    $"Tab {tab.Target!.Id} is not the tab the window shows ({(shown.Success ? $"it shows tab {shown.Value!.Id}" : $"which tab it shows could not be told: {shown.Error}")}), " +
                    "so its element is not on the screen. Nothing was read. Show the tab first (activate_tab), or leave targetId out " +
                    "for the tab the window shows.", tab.Port, tab.Target.Id);
            }
        }

        var located = await _pages.LocateAsync(tab.Target!, locator, ActionTimeout, cancellationToken).ConfigureAwait(false);
        if (!located.Success)
            return BrowserElementRectResult.Failed(located.ErrorCode!, located.Error!, tab.Port, tab.Target!.Id);

        var box = located.Value!;
        var failed = new BrowserElementRectResult(false, tab.Target!.Id, box.Element, box.MatchCount, CssRect: Round(box.Rect),
            DevicePixelRatio: box.DevicePixelRatio, CdpPort: tab.Port, ErrorCode: nameof(BrowserErrorCode.ContentAreaNotFound));
        if (box.Visibility != "visible")
        {
            // A tab behind another, or a covered or minimized window: the page is not on the screen, and whatever the window
            // shows there is another page's
            return failed with
            {
                Error = $"The tab's page is not shown (its visibilityState is '{Cut(box.Visibility, 20)}'): another tab is in front of " +
                        "it, or its window is minimized or covered, so the element is not on the screen. Show the tab (activate_tab) " +
                        "and bring the window to the front (focus_window), then try again. cssRect is the element's place in the " +
                        "page's viewport."
            };
        }

        if (Math.Abs(box.Scale - 1) > 0.01)
        {
            return failed with
            {
                Error = $"The page is pinch-zoomed ({box.Scale:0.##}x), so its CSS pixels cannot be placed on the screen. Reset the " +
                        "zoom (pinch out) and try again; cssRect is the element's place in the page's viewport."
            };
        }

        var lookup = await _viewports.FindAsync(window, tab.Target!.Url, box.ViewportWidth * box.DevicePixelRatio,
            box.ViewportHeight * box.DevicePixelRatio, AnchorTimeout, cancellationToken).ConfigureAwait(false);
        if (lookup.Viewport is not { } viewport)
        {
            return failed with
            {
                Error = $"Where the window shows the tab's page could not be found: {lookup.Detail}. The tab may not be the one the " +
                        "window shows (activate_tab), or the window stayed behind other windows since the page loaded (Chromium then " +
                        "builds no page tree and hides the page): bring it to the front (focus_window) and try again. cssRect is the " +
                        "element's place in the page's viewport."
            };
        }

        // The viewport's own size over the page's CSS size: devicePixelRatio as the screen shows it (zoom and display scale)
        var scaleX = viewport.Width / box.ViewportWidth;
        var scaleY = viewport.Height / box.ViewportHeight;
        var left = (int)Math.Round(viewport.X + box.Rect.X * scaleX);
        var top = (int)Math.Round(viewport.Y + box.Rect.Y * scaleY);
        var right = (int)Math.Round(viewport.X + (box.Rect.X + box.Rect.Width) * scaleX);
        var bottom = (int)Math.Round(viewport.Y + (box.Rect.Y + box.Rect.Height) * scaleY);
        // The middle of the element's first box in the viewport, as browser_click aims: a wrapped link's boxes together
        // would put the point between its lines, on other text
        var inside = CdpPageService.ClickPoint(box.Quads, box.ViewportWidth, box.ViewportHeight);
        var click = inside is { } p
            ? new ScreenPoint((int)Math.Round(viewport.X + p.X * scaleX), (int)Math.Round(viewport.Y + p.Y * scaleY))
            : null;

        return new BrowserElementRectResult(true, tab.Target.Id, box.Element, box.MatchCount,
            new BoundsDto(left, top, right - left, bottom - top), click, Round(box.Rect), box.DevicePixelRatio, viewport.Source, tab.Port);
    }

    /// <inheritdoc />
    public async Task<BrowserInputResult> ClickAsync(
        BrowserTabTarget target, BrowserElementLocator locator, MouseButton button, int clickCount, CancellationToken cancellationToken = default,
        BrowserDialogAnswer? dialog = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        var tab = await FindTabAsync(target, cancellationToken).ConfigureAwait(false);
        if (tab.Refusal is { } refusal)
            return BrowserInputResult.Failed(refusal.Code, refusal.Error, tab.Port, target.TargetId);

        var clicked = await _pages.ClickAsync(tab.Target!, locator, button, clickCount, EnterGateAsync, ActionTimeout, cancellationToken,
            dialog).ConfigureAwait(false);
        return clicked.Success
            ? ToResult(clicked.Value!, tab)
            : BrowserInputResult.Failed(clicked.ErrorCode!, clicked.Error!, tab.Port, tab.Target!.Id);
    }

    /// <inheritdoc />
    public async Task<BrowserInputResult> TypeAsync(
        BrowserTabTarget target, BrowserTypeOptions options, CancellationToken cancellationToken = default, BrowserDialogAnswer? dialog = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tab = await FindTabAsync(target, cancellationToken).ConfigureAwait(false);
        if (tab.Refusal is { } refusal)
            return BrowserInputResult.Failed(refusal.Code, refusal.Error, tab.Port, target.TargetId);

        var typed = await _pages.TypeAsync(tab.Target!, options, EnterGateAsync, ActionTimeout, cancellationToken, dialog).ConfigureAwait(false);
        return typed.Success
            ? ToResult(typed.Value!, tab)
            : BrowserInputResult.Failed(typed.ErrorCode!, typed.Error!, tab.Port, tab.Target!.Id);
    }

    /// <inheritdoc />
    public async Task<BrowserScreenshot> CaptureAsync(BrowserTabTarget target, int maxDimension, CancellationToken cancellationToken = default)
    {
        var tab = await FindTabAsync(target, cancellationToken).ConfigureAwait(false);
        if (tab.Refusal is { } refusal)
            return new BrowserScreenshot(BrowserScreenshotResult.Failed(refusal.Code, refusal.Error, tab.Port, target.TargetId), null);

        var captured = await _pages.CaptureAsync(tab.Target!, EnterGateAsync, CaptureTimeout, cancellationToken).ConfigureAwait(false);
        if (!captured.Success)
            return new BrowserScreenshot(BrowserScreenshotResult.Failed(captured.ErrorCode!, captured.Error!, tab.Port, tab.Target!.Id), null);

        var shot = captured.Value!;
        try
        {
            using var stream = new MemoryStream(shot.Png, writable: false);
            using var original = new Bitmap(stream);
            var (width, height) = ScaledSize(original.Width, original.Height, maxDimension);
            using var scaled = width == original.Width && height == original.Height ? null : ScreenshotService.ResizeImage(original, width, height);
            var encoding = Math.Max(width, height) <= PngMaxEdge ? ImageEncoding.Png : ImageEncoding.Jpeg;
            var data = ScreenshotService.Encode(scaled ?? original, encoding, CaptureOptions.DefaultQuality);
            return new BrowserScreenshot(
                new BrowserScreenshotResult(true, tab.Target!.Id, width, height, original.Width, original.Height,
                    encoding == ImageEncoding.Png ? "png" : "jpeg", shot.Activated ? true : null, Cut(shot.Url, BrowserTabService.MaxUrlLength),
                    Cut(shot.Title, BrowserTabService.MaxTitleLength), tab.Port),
                new EncodedImage(data, encoding == ImageEncoding.Png ? "image/png" : "image/jpeg"));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            return new BrowserScreenshot(BrowserScreenshotResult.Failed(nameof(BrowserErrorCode.CdpProtocolError),
                $"Tab {tab.Target!.Id} answered Page.captureScreenshot with an image that could not be read ({ex.Message}).", tab.Port,
                tab.Target.Id), null);
        }
    }

    /// <summary>The size of a capture scaled to <paramref name="maxDimension"/> on its long edge; 0 keeps it.</summary>
    public static (int Width, int Height) ScaledSize(int width, int height, int maxDimension)
    {
        var longEdge = Math.Max(width, height);
        if (maxDimension <= 0 || longEdge <= maxDimension)
            return (width, height);

        var scale = (double)maxDimension / longEdge;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// A script's value as returned: its JSON when it is at most <see cref="BrowserEvaluateResult.MaxValueLength"/>
    /// characters, else the start of that JSON as a string (Truncated); with the JSON's length either way.
    /// </summary>
    public static (JsonElement? Value, int? Length, bool? Truncated) Bounded(JsonElement? value)
    {
        if (value is not { } given)
            return (null, null, null);

        var json = given.GetRawText();
        if (json.Length <= BrowserEvaluateResult.MaxValueLength)
            return (given, json.Length, null);

        var cut = BrowserEvaluateResult.MaxValueLength;
        if (char.IsHighSurrogate(json[cut - 1]))
            cut--;

        return (JsonSerializer.SerializeToElement(json[..cut]), json.Length, true);
    }

    private BrowserInputResult ToResult(CdpInputOutcome outcome, FoundTab tab) =>
        new(true, tab.Target!.Id, outcome.Element, outcome.MatchCount, outcome.Point, outcome.TypedLength, outcome.PressedEnter,
            OpenedDialog(outcome.Dialog, outcome.Answer, tab.WindowHandle, _dialogs),
            DialogWarning(outcome.Dialog, outcome.Answer, outcome.AnswerFailure, tab.WindowHandle) ?? outcome.Warning,
            tab.Port);

    /// <summary>
    /// The dialog an action opened, as the result names it: answered (accepted or dismissed), or left open, which is noted
    /// for browser_handle_dialog (<paramref name="dialogs"/>): the agent has now been shown its text.
    /// </summary>
    internal static BrowserOpenedDialog? OpenedDialog(CdpDialog? dialog, CdpAnsweredDialog? answer, nint? window, IBrowserDialogService? dialogs)
    {
        if (dialog == null)
            return null;

        var message = Cut(dialog.Message, CdpDialogText.MaxDialogMessageLength);
        if (answer == null && window is { } shown)
            dialogs?.NoteShown(shown, message);

        return new BrowserOpenedDialog(dialog.Type, message,
            answer == null ? null : answer.Accepted ? BrowserOpenedDialog.Accepted : BrowserOpenedDialog.Dismissed);
    }

    /// <summary>What to do about a dialog the action opened and left open; null when there is none, or it was answered.</summary>
    internal static string? DialogWarning(CdpDialog? dialog, CdpAnsweredDialog? answer, string? answerFailure, nint? window)
    {
        if (dialog == null || answer != null)
            return null;

        var how = window is { } handle
            ? $"answer it deliberately with browser_handle_dialog (handle {(long)handle}), or ask the user"
            : "answer it deliberately with browser_handle_dialog (it needs the browser window's handle), or ask the user";
        return answerFailure != null
            ? $"The action opened a JavaScript dialog (its text is from the page), but it could not be answered ({answerFailure}): it is " +
              $"still open, and the page waits for it. Read it and {how}."
            : $"The action opened a JavaScript dialog (its text is from the page). The page waits for it: {how}. Next time pass the " +
              "dialog option to answer it as the action opens it.";
    }

    /// <summary>
    /// The browser and its tab for a call: refused when the browser is not usable (<see cref="BrowserCdpTargets"/>), the tab
    /// cannot be found, or it shows one of the browser's own pages.
    /// </summary>
    private async Task<FoundTab> FindTabAsync(BrowserTabTarget target, CancellationToken cancellationToken)
    {
        var browser = _targets.Resolve(target, "The DOM bridge tools");
        if (browser.Refusal is { } refusal)
            return new FoundTab(null, null, null, refusal);

        var tab = await _targets.FindTabAsync(browser, target.TargetId, cancellationToken).ConfigureAwait(false);
        if (!tab.Success)
            return new FoundTab(null, browser.Port, browser.WindowHandle, (tab.ErrorCode.ToString(), tab.Error!));

        if (BrowserUrls.IsBrowserOwnPage(tab.Value!.Url))
        {
            return new FoundTab(null, browser.Port, browser.WindowHandle, (nameof(BrowserErrorCode.BrowserInternalPage),
                $"Tab {tab.Value.Id} shows a page that is not a web page ({Scheme(tab.Value.Url)}: the browser's settings, extensions, " +
                "DevTools, an app and the like), which these tools never read or act on. Nothing was done. Navigate the tab to a web " +
                "page first, or ask the user to do this step."));
        }

        return new FoundTab(tab.Value, browser.Port, browser.WindowHandle, null, browser);
    }

    /// <summary>The input gate for CDP, which also tells whether the user paused input while it is held.</summary>
    private async Task<IDisposable> EnterGateAsync(CancellationToken cancellationToken) =>
        new GateHold(await InputGateToolFilter.EnterAsync(_session, cancellationToken).ConfigureAwait(false), _session);

    private static string Scheme(string url)
    {
        var colon = url.IndexOf(':');
        return colon > 0 ? url[..colon].ToLowerInvariant() : "no address";
    }

    private static CssRect Round(CssRect rect) =>
        new(Math.Round(rect.X, 2), Math.Round(rect.Y, 2), Math.Round(rect.Width, 2), Math.Round(rect.Height, 2));

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    private static string? Cut(string? text, int max) => text != null && text.Length > max ? text[..(max - 3)] + "..." : text;

    /// <summary>The tab a call works on (with its port, window and browser), or why not.</summary>
    private sealed record FoundTab(
        CdpTarget? Target, int? Port, nint? WindowHandle, (string Code, string Error)? Refusal, ResolvedBrowser? Browser = null);

    /// <summary>The input gate held by a DOM bridge action, and the input session whose pause stops it.</summary>
    private sealed class GateHold(IDisposable gate, IInputSession? session) : ICdpGateHold
    {
        public bool IsPaused => session?.IsPaused == true;

        public void Dispose() => gate.Dispose();
    }
}
