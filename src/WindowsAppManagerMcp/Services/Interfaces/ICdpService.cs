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
    /// Lists the page targets (tabs) of the browser listening on the debug port.
    /// </summary>
    /// <param name="port">The CDP debug port.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<IReadOnlyList<CdpTarget>>> GetPageTargetsAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the tab that a browser window is showing.
    /// Fails with CdpPortOwnerMismatch when the port belongs to another process than the window,
    /// and with CdpTargetNotFound or CdpTargetAmbiguous when no single tab matches the window title.
    /// </summary>
    /// <param name="port">The CDP debug port.</param>
    /// <param name="windowProcessId">The ID of the process that owns the window.</param>
    /// <param name="windowTitle">The window title.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<CdpTarget>> FindTargetForWindowAsync(
        int port,
        int windowProcessId,
        string windowTitle,
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
    /// Reads the title, URL and content of a tab with a single Runtime.evaluate call.
    /// Fails with CdpTimeout when the page does not answer (for example while a JavaScript dialog
    /// is open), with CdpDialogOpen when the browser reports an open dialog, with CdpTargetClosed when the tab's
    /// connection ends during the read, and with CdpResponseTooLarge when the answer is over the size limit.
    /// </summary>
    /// <param name="target">The tab to read.</param>
    /// <param name="textOnly">True for the page text (innerText), false for the full HTML.</param>
    /// <param name="maxLength">Maximum content length in characters, or null for no limit.</param>
    /// <param name="timeout">Maximum time to wait for the browser.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<CdpResult<CdpPageSnapshot>> GetPageSnapshotAsync(
        CdpTarget target,
        bool textOnly,
        int? maxLength,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a CDP browser target (tab).
/// </summary>
/// <param name="Port">The debug port that listed the tab. Its WebSocket URL must be on the same port (roadmap M5.2); it is
/// required, so the check can never compare the URL with itself.</param>
public record CdpTarget(
    string Id,
    string Type,
    string Title,
    string Url,
    string? WebSocketDebuggerUrl,
    int Port
);

/// <summary>
/// Title, URL and content read from a tab at the same moment.
/// </summary>
public record CdpPageSnapshot(
    string Title,
    string Url,
    string Content
);
