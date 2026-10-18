using System.Text.Json;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The only part of the server that talks to a browser's DevTools endpoint over the network (roadmap M5.2): plain HTTP
/// GETs of its /json pages and WebSocket connections that carry CDP commands.
/// </summary>
/// <remarks>
/// Every request goes to 127.0.0.1 and is bounded by its timeout: the /json GETs follow no redirect, and a WebSocket URL
/// must be on 127.0.0.1 too. Failures come back as a failed <see cref="CdpResult{T}"/>
/// with a specific <see cref="BrowserErrorCode"/>; only cancellation of the caller's token throws
/// (<see cref="OperationCanceledException"/>). Nothing outlives the tool call that made it: a connection is opened for one
/// call and disposed at its end, so there is no pool or cache that could hand one call's tab to another.
/// </remarks>
public interface ICdpTransport
{
    /// <summary>
    /// GETs a JSON page of the DevTools HTTP endpoint on 127.0.0.1:<paramref name="port"/>, such as /json/list or
    /// /json/version. A body over the size limit fails with CdpResponseTooLarge before it is read in full.
    /// </summary>
    /// <param name="port">The DevTools port.</param>
    /// <param name="path">The page; it must start with "/json".</param>
    /// <param name="timeout">Maximum time for the whole request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The parsed JSON, which the caller disposes.</returns>
    Task<CdpResult<JsonDocument>> GetJsonAsync(int port, string path, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a DevTools WebSocket connection: a tab's webSocketDebuggerUrl, or the browser's from /json/version. The URL
    /// must be ws:// on 127.0.0.1 and on <paramref name="expectedPort"/>
    /// (<see cref="Services.CdpEndpointValidator"/>), or nothing is connected. Once the connection is open, and before it
    /// is handed out (so before its first command), the port's listener is read again: a browser that exited after its
    /// process was checked leaves its port to any program on this computer, which would get the commands. When the listener
    /// is not <paramref name="expectedOwner"/>, or cannot be read (fails closed), the connection is closed with nothing sent
    /// and the result is CdpPortOwnerMismatch (review F4).
    /// </summary>
    /// <param name="webSocketUrl">The DevTools WebSocket URL.</param>
    /// <param name="expectedPort">The DevTools port the URL was listed by; a URL on another port is refused.</param>
    /// <param name="expectedOwner">The browser's process, checked to listen on the port before the URL was read; the
    /// connection is used only while it still listens there.</param>
    /// <param name="label">Names the connection in messages, for example "tab 8C1F...".</param>
    /// <param name="timeout">Maximum time for the connect.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    /// <returns>An open connection, which the caller disposes.</returns>
    Task<CdpResult<ICdpConnection>> ConnectAsync(
        Uri webSocketUrl,
        int expectedPort,
        int expectedOwner,
        string label,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// One DevTools WebSocket connection that correlates commands with their replies by id and dispatches events.
/// </summary>
/// <remarks>
/// A command's timeout or the caller's cancellation ends only that command; the connection stays open. The connection
/// ends when the browser closes it, the tab detaches or crashes (CdpTargetClosed), a message is too large
/// (CdpResponseTooLarge) or unreadable (CdpProtocolError), or it is disposed; every command still waiting then fails
/// with that reason, and so does every later one. While the page has a JavaScript dialog open, a command fails at once
/// with CdpDialogOpen unless it is sent with <see cref="CdpSendOptions.AllowWhileDialogOpen"/>.
/// </remarks>
public interface ICdpConnection : IAsyncDisposable
{
    /// <summary>Why the connection ended, or null while it is open.</summary>
    CdpResult<bool>? Closed { get; }

    /// <summary>The JavaScript dialog the page has open, or null.</summary>
    CdpDialog? OpenDialog { get; }

    /// <summary>
    /// Sends a command and waits for its reply. A reply with a CDP error is a successful result whose
    /// <see cref="CdpReply.Error"/> is set (<see cref="Services.CdpReplies.ToFailure"/> turns it into an error code).
    /// </summary>
    /// <param name="method">The CDP method, for example "Runtime.evaluate".</param>
    /// <param name="parameters">The params object, serialized as JSON, or null for none.</param>
    /// <param name="timeout">Maximum time to wait for the reply; then the result is CdpTimeout.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="options">Whether the command may be sent while a dialog is open.</param>
    Task<CdpResult<CdpReply>> SendAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        CdpSendOptions options = CdpSendOptions.None);

    /// <summary>
    /// Answers the first JavaScript dialog the page opens after this call (Page.javascriptDialogOpening) at once on this
    /// connection, with Page.handleJavaScriptDialog (<paramref name="promptText"/> only for a prompt): the dialog then does not
    /// count as open, so the commands waiting on the page go on once it is answered (roadmap M5.8, dialogs). A later dialog
    /// is not answered. The browser answers only a dialog opened while a connection with Page.enable is attached (Chrome
    /// and Edge 155 refuse one opened before with "No dialog is showing"), so call it before the command that may open one.
    /// </summary>
    /// <returns>Completes once the answer came back: the dialog and the answer, or why it failed (the dialog then counts as
    /// open). Never completes when no dialog opens, until the connection ends.</returns>
    Task<CdpResult<CdpAnsweredDialog>> AnswerNextDialog(bool accept, string? promptText);

    /// <summary>
    /// Starts listening for the next event <paramref name="method"/> that passes <paramref name="filter"/>. Register it
    /// before sending the command that causes the event, so an event that arrives first is not missed.
    /// </summary>
    ICdpEventWaiter ExpectEvent(string method, Func<JsonElement, bool>? filter = null);
}

/// <summary>An event a connection is listening for (<see cref="ICdpConnection.ExpectEvent"/>).</summary>
public interface ICdpEventWaiter : IDisposable
{
    /// <summary>Waits for the event and returns its params; CdpTimeout after <paramref name="timeout"/>.</summary>
    Task<CdpResult<JsonElement>> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>How a command is sent (<see cref="ICdpConnection.SendAsync"/>).</summary>
[Flags]
public enum CdpSendOptions
{
    None = 0,

    /// <summary>Sent and awaited even while the page has a JavaScript dialog open (for example Page.handleJavaScriptDialog).</summary>
    AllowWhileDialogOpen = 1
}

/// <summary>The browser's reply to a command: its result, or the CDP error it answered with.</summary>
/// <param name="Result">The reply's result object (a copy that outlives the message), or an undefined element.</param>
/// <param name="Error">The CDP error, or null.</param>
public record CdpReply(JsonElement Result, CdpProtocolFault? Error);

/// <summary>A CDP error reply ({"error": {"code", "message"}}).</summary>
public record CdpProtocolFault(int Code, string Message);

/// <summary>A JavaScript dialog the page has open (Page.javascriptDialogOpening). Its text comes from the page.</summary>
public record CdpDialog(string Type, string Message, string? DefaultPrompt, string Url);

/// <summary>A JavaScript dialog <see cref="ICdpConnection.AnswerNextDialog"/> answered: the dialog and whether it was accepted.</summary>
public sealed record CdpAnsweredDialog(CdpDialog Dialog, bool Accepted);
