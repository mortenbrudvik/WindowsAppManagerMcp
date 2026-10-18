using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for interacting with browsers via Chrome DevTools Protocol (CDP).
/// Provides richer content access when the browser is started with a debug port.
/// </summary>
/// <remarks>
/// Every method is bounded by <c>timeout</c>: no call waits longer than that for the browser.
/// CDP failures, including timeouts, are returned as a failed <see cref="CdpResult{T}"/> with a
/// specific <see cref="BrowserErrorCode"/>. Only cancellation of <c>cancellationToken</c> by the
/// caller throws (<see cref="OperationCanceledException"/>).
/// </remarks>
public interface ICdpService
{
    /// <summary>
    /// Lists the page targets (tabs) of whatever listens on the debug port. Nothing ties the list to a browser, so its tabs
    /// carry no <see cref="CdpTarget.OwnerProcessId"/>, and no connection is opened to them: a tab to work on comes from
    /// <see cref="GetOwnedPageTargetsAsync"/>, <see cref="FindTargetForWindowAsync"/> or <see cref="FindTargetByIdAsync"/>.
    /// </summary>
    /// <param name="port">The CDP debug port.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<IReadOnlyList<CdpTarget>>> GetPageTargetsAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the page targets (tabs) of the browser whose process is <paramref name="ownerProcessId"/>: its listener on the
    /// port is read before the request and again once the list has come (review F4), so a list another program answered
    /// after the browser left its port is not used. CdpPortOwnerMismatch when the listener is another process at either
    /// read, or cannot be read (fails closed); CdpUnavailable when nothing listens before the request. Each tab carries
    /// <paramref name="ownerProcessId"/> as its <see cref="CdpTarget.OwnerProcessId"/>, which every connection to it is
    /// checked against once open (<see cref="ICdpTransport.ConnectAsync"/>).
    /// </summary>
    /// <param name="port">The CDP debug port.</param>
    /// <param name="ownerProcessId">The browser's process, which must listen on the port.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<IReadOnlyList<CdpTarget>>> GetOwnedPageTargetsAsync(
        int port,
        int ownerProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the browser's /json/version (roadmap M5.1): whether a DevTools endpoint answers on the port, and its
    /// browser-level WebSocket URL, which must be ws:// on 127.0.0.1, on <paramref name="port"/> and under
    /// /devtools/browser/ (<see cref="Services.CdpEndpointValidator"/>). Only 127.0.0.1:<paramref name="port"/> is asked;
    /// nothing is connected. CdpUnavailable when nothing listens, CdpTimeout when it does not answer in time, and
    /// CdpProtocolError when the answer is not a browser's DevTools endpoint or its URL is refused.
    /// </summary>
    /// <param name="port">The CDP debug port.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<CdpBrowserVersion>> GetBrowserVersionAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the tab that a browser window is showing (roadmap M5.3). The port's listener must be the window's process:
    /// CdpPortOwnerMismatch when it is another process, and also when it cannot be read (fails closed, as the targetId
    /// path does). With <see cref="CdpWindow.Geometry"/> and more than one tab, the window is first bound to its browser
    /// window (Browser.getWindowForTarget over the browser's DevTools connection, <see cref="Services.CdpWindowBinder"/>),
    /// and the tab is the one there whose exact title the window shows; CDP reports no active tab, so the title picks it.
    /// When no browser window matches, the binding cannot be read (also when a reply did not come), or the bound window has
    /// no tab with the title, the title is matched among all tabs. A page sets its own title, so when several tabs have a
    /// title the window shows, the one taken is the one whose document.visibilityState, read in an isolated world the
    /// page's scripts cannot reach, is "visible" (<see cref="Services.CdpShownTab"/>); never the longest title. The binding
    /// and that read each take at most a third of what is left of <paramref name="timeout"/> (at most 2 s). Nothing
    /// listening on the port is CdpUnavailable. Fails with CdpTargetNotFound when no tab matches, and CdpTargetAmbiguous
    /// when several do and exactly one shown tab cannot be told (none or several shown, a read that failed, a minimized
    /// window, candidates in several browser windows, or a binding that could not be read, which is then asked for also
    /// without <see cref="CdpWindow.Geometry"/>). Over <see cref="Services.CdpService.MaxBoundTabs"/> tabs, only the
    /// browser windows of the tabs with the title are asked for, when several have it.
    /// </summary>
    /// <param name="port">The CDP debug port.</param>
    /// <param name="window">The window: its process, its title and, when known, where it is.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<CdpTarget>> FindTargetForWindowAsync(
        int port,
        CdpWindow window,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the tab with the CDP target id <paramref name="targetId"/>, from an earlier result's targetId or a
    /// CdpTargetAmbiguous error. The port must belong to the window's process: an explicit id carries no evidence that
    /// the tab belongs to the window, so this check fails closed (CdpPortOwnerMismatch) when the port's owner cannot be
    /// read. CdpTargetNotFound when no page target has the id (the tab may have closed).
    /// </summary>
    /// <param name="port">The CDP debug port.</param>
    /// <param name="windowProcessId">The ID of the process that owns the window.</param>
    /// <param name="targetId">The CDP target id.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<CdpTarget>> FindTargetByIdAsync(
        int port,
        int windowProcessId,
        string targetId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the title, URL and one page of the content of a tab with a single Runtime.evaluate call (roadmap M5.6): the
    /// page measures its text (or HTML) and returns only the slice <paramref name="request"/> asks for with its whole
    /// length, so a page larger than the message limit can still be read a slice at a time.
    /// Fails with CdpTimeout when the page does not answer (for example while a JavaScript dialog
    /// is open), with CdpDialogOpen when the browser reports an open dialog, with CdpTargetClosed when the tab's
    /// connection ends during the read, with CdpResponseTooLarge when the answer is over the size limit, with
    /// CdpEvaluationFailed when the page throws (a selector that is not valid CSS), with ContentExtractionFailed when
    /// no element matches the selector, and with CdpPortOwnerMismatch, nothing sent, when the browser that listed the tab
    /// (<see cref="CdpTarget.OwnerProcessId"/>, none: fails closed) no longer listens on its port once the connection is open.
    /// </summary>
    /// <param name="target">The tab to read.</param>
    /// <param name="request">What to read: text or HTML, the slice, and the selector.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<CdpPageSnapshot>> GetPageSnapshotAsync(
        CdpTarget target,
        CdpPageRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks a debug browser to close (roadmap M5.4): Browser.close over the browser-level DevTools connection from its own
    /// /json/version, which closes every window without beforeunload prompts. The port's listener must be
    /// <paramref name="browserProcessId"/> (CdpPortOwnerMismatch otherwise, also when it cannot be read: nothing is sent).
    /// Success with true once the browser answers or ends the connection as it closes; success with false when Browser.close
    /// was sent but not answered within <paramref name="timeout"/> (a busy browser may still be closing); a failure when it
    /// was not sent. Whether it exited is the caller's to check.
    /// </summary>
    /// <param name="port">The browser's DevTools port.</param>
    /// <param name="browserProcessId">The browser's main process, which must listen on the port.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<bool>> CloseBrowserAsync(
        int port,
        int browserProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The slice of a tab's content to read (roadmap M5.6).
/// </summary>
/// <param name="TextOnly">True for the page text (innerText), false for the HTML (outerHTML).</param>
/// <param name="Offset">Where the slice starts, in UTF-16 characters of the text (0 or more).</param>
/// <param name="MaxLength">The most characters of the slice (1 or more).</param>
/// <param name="Selector">A CSS selector: the text (or HTML) of each element it matches, one per line, instead of the
/// whole page's; null for the whole page.</param>
public record CdpPageRequest(bool TextOnly, int Offset, int MaxLength, string? Selector = null);

/// <summary>
/// Represents a CDP browser target (tab).
/// </summary>
/// <param name="Port">The debug port that listed the tab. Its WebSocket URL must be on the same port (roadmap M5.2); it is
/// required, so the check can never compare the URL with itself.</param>
/// <param name="OwnerProcessId">The browser's process, read as the port's listener before the tab list was requested and
/// again once it came (<see cref="ICdpService.GetOwnedPageTargetsAsync"/>); a connection to the tab is used only while it
/// still listens there (review F4). Null when no read tied the list to a browser: then no connection is opened to the tab
/// (fails closed).</param>
public record CdpTarget(
    string Id,
    string Type,
    string Title,
    string Url,
    string? WebSocketDebuggerUrl,
    int Port,
    int? OwnerProcessId = null
);

/// <summary>
/// The desktop window a tab is looked up for (roadmap M5.3).
/// </summary>
/// <param name="ProcessId">The process that owns the window; the debug port must be its own.</param>
/// <param name="Title">The window's title, which shows the active tab's title.</param>
/// <param name="Geometry">Where the window is and its state, or null when it could not be read (the title alone decides).</param>
public record CdpWindow(int ProcessId, string Title, CdpWindowGeometry? Geometry = null);

/// <summary>A desktop window's state, as Browser.getWindowForTarget's windowState names it.</summary>
public enum CdpWindowState
{
    Normal,
    Minimized,
    Maximized
}

/// <summary>
/// A desktop window's rectangle in physical pixels (GetWindowRect), its state and its DPI scale (GetDpiForWindow / 96).
/// </summary>
public record CdpWindowGeometry(int Left, int Top, int Right, int Bottom, CdpWindowState State, double Scale);

/// <summary>
/// The browser window a tab is in, as Browser.getWindowForTarget reports it: its id and bounds in DIPs (the restored
/// bounds when it is not normal) and its state ("normal", "minimized", "maximized" or "fullscreen").
/// </summary>
public record CdpBrowserWindow(int WindowId, int Left, int Top, int Width, int Height, string State);

/// <summary>
/// What a DevTools endpoint's /json/version reports about its browser (roadmap M5.1).
/// </summary>
/// <param name="Browser">The product and version, for example "Chrome/141.0.7390.55" or "Edg/141.0.3537.57"; empty when
/// the endpoint does not report it.</param>
/// <param name="ProtocolVersion">The DevTools protocol version, for example "1.3"; empty when not reported.</param>
/// <param name="WebSocketDebuggerUrl">The browser-level DevTools WebSocket URL, already checked to be ws:// on 127.0.0.1
/// and on <paramref name="Port"/>.</param>
/// <param name="Port">The DevTools port that answered.</param>
public record CdpBrowserVersion(
    string Browser,
    string ProtocolVersion,
    Uri WebSocketDebuggerUrl,
    int Port)
{
    /// <summary>The DevTools HTTP endpoint, http://127.0.0.1:port, as CDP clients such as Playwright take it.</summary>
    public string HttpEndpoint => $"http://{Services.CdpEndpointValidator.Host}:{Port}";
}

/// <summary>
/// Title, URL and a slice of the content read from a tab at the same moment.
/// </summary>
/// <param name="Content">The slice of the text (or HTML) asked for.</param>
/// <param name="Offset">Where the slice starts: the offset asked for, or the text's length when it is shorter.</param>
/// <param name="TotalLength">The whole text's length, measured in the page before it was sliced; null when not reported.</param>
public record CdpPageSnapshot(
    string Title,
    string Url,
    string Content,
    int Offset = 0,
    int? TotalLength = null
);
