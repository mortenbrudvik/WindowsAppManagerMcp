using System.Buffers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The size limits of <see cref="CdpTransport"/>; tests pass small ones.
/// </summary>
/// <param name="MaxHttpBodyBytes">The largest /json body that is read.</param>
/// <param name="MaxMessageBytes">The largest WebSocket message that is read.</param>
public record CdpTransportLimits(int MaxHttpBodyBytes = 16 * 1024 * 1024, int MaxMessageBytes = 64 * 1024 * 1024);

/// <summary>
/// The DevTools transport over HTTP and WebSockets (roadmap M5.2). It owns its HttpClient; the WebSockets come from
/// <see cref="ICdpWebSocketFactory"/>, so tests replace the browser with in-memory fakes. Every WebSocket it opens is handed
/// out only while the port's listener is still the browser's process the caller checked (<see cref="ConnectAsync"/>).
/// </summary>
public sealed class CdpTransport : ICdpTransport, IDisposable
{
    // Chrome's DevTools HTTP server listens on 127.0.0.1. Using the IP avoids a failed ::1
    // attempt for "localhost" and makes the endpoint match the port-owner check.
    private const string Host = CdpEndpointValidator.Host;

    private const int CopyChunkBytes = 64 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ICdpWebSocketFactory _webSocketFactory;
    private readonly ITcpPortOwnerResolver _portOwners;
    private readonly CdpTransportLimits _limits;

    /// <summary>The transport the server uses: its HTTP handler is <see cref="CreateHttpHandler"/>.</summary>
    /// <param name="webSocketFactory">Opens the DevTools WebSockets.</param>
    /// <param name="portOwners">Reads which process listens on a port, again once each WebSocket is open.</param>
    /// <param name="limits">The size limits, or null for 16 MB per /json body and 64 MB per WebSocket message.</param>
    public CdpTransport(ICdpWebSocketFactory webSocketFactory, ITcpPortOwnerResolver portOwners, CdpTransportLimits? limits = null)
        : this(CreateHttpHandler(), webSocketFactory, portOwners, limits)
    {
    }

    /// <param name="httpHandler">Sends the /json requests; the transport owns and disposes it.</param>
    /// <param name="webSocketFactory">Opens the DevTools WebSockets.</param>
    /// <param name="portOwners">Reads which process listens on a port, again once each WebSocket is open.</param>
    /// <param name="limits">The size limits, or null for 16 MB per /json body and 64 MB per WebSocket message.</param>
    public CdpTransport(
        HttpMessageHandler httpHandler, ICdpWebSocketFactory webSocketFactory, ITcpPortOwnerResolver portOwners, CdpTransportLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(portOwners);

        // Each request is bounded by the per-call timeout token instead.
        _httpClient = new HttpClient(httpHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _webSocketFactory = webSocketFactory;
        _portOwners = portOwners;
        _limits = limits ?? new CdpTransportLimits();
    }

    /// <inheritdoc />
    public async Task<CdpResult<JsonDocument>> GetJsonAsync(
        int port,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!path.StartsWith("/json", StringComparison.Ordinal))
            throw new ArgumentException($"Only the DevTools /json pages are requested, not '{path}'.", nameof(path));

        var checkedPort = CdpEndpointValidator.ValidatePort(port);
        if (!checkedPort.Success)
            return checkedPort.FailAs<JsonDocument>();

        using var timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        var token = timeoutSource.Token;

        try
        {
            return await FetchJsonAsync(port, path, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (timeoutSource.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpTimeout,
                $"The CDP endpoint on {Host}:{port} did not answer GET {path} within {FormatMs(timeout)}. " +
                "The browser may be unresponsive, or the port may belong to a program that is not a browser.");
        }
        catch (HttpRequestException ex) when (IsConnectionFailure(ex))
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpUnavailable,
                $"Could not connect to CDP on {Host}:{port} ({ex.Message}). " +
                $"Start the browser with --remote-debugging-port={port} (for example with launch_browser_with_debug).");
        }
        catch (HttpRequestException ex)
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"{Host}:{port} answered but not like a DevTools endpoint ({ex.Message}). Is this a browser debug port?");
        }
        catch (JsonException ex)
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"{Host}:{port} did not return JSON for GET {path} ({ex.Message}). Is this a browser debug port?");
        }
        catch (Exception ex)
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"GET {path} on {Host}:{port} failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <inheritdoc />
    public async Task<CdpResult<ICdpConnection>> ConnectAsync(
        Uri webSocketUrl,
        int expectedPort,
        int expectedOwner,
        string label,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var checkedUrl = CdpEndpointValidator.ValidateWebSocketUrl(webSocketUrl.OriginalString, expectedPort, label);
        if (!checkedUrl.Success)
            return checkedUrl.FailAs<ICdpConnection>();

        using var timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        var token = timeoutSource.Token;
        var connect = _webSocketFactory.ConnectAsync(checkedUrl.Value!, token);

        try
        {
            var socket = await connect.WaitAsync(token).ConfigureAwait(false);

            // Before the connection's first command (review F4): it may have reached another program that took the port
            if (ListenerRefusal(expectedPort, expectedOwner, label) is { } refused)
            {
                socket.Abort();
                socket.Dispose();
                return refused;
            }

            return CdpResult<ICdpConnection>.Ok(new CdpConnection(socket, label, _limits.MaxMessageBytes));
        }
        catch (Exception) when (timeoutSource.IsCancellationRequested)
        {
            // A connect that finishes after the timeout leaves a socket nobody would close
            _ = connect.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Abort();
                    t.Result.Dispose();
                }
                else
                {
                    _ = t.Exception;
                }
            }, TaskScheduler.Default);

            cancellationToken.ThrowIfCancellationRequested();
            return CdpResult<ICdpConnection>.Fail(
                BrowserErrorCode.CdpTimeout,
                $"Could not open a CDP WebSocket to {label} within {FormatMs(timeout)}.");
        }
        catch (WebSocketException ex) when (IsForbidden(ex))
        {
            return CdpResult<ICdpConnection>.Fail(
                BrowserErrorCode.CdpUnavailable,
                $"The browser refused the CDP WebSocket to {label} with HTTP 403 ({ex.Message}). The server sends no Origin " +
                "header, so this browser was probably started with --remote-allow-origins set to something that excludes it, " +
                "or another program is listening on the port. Start the browser with launch_browser_with_debug.");
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        {
            return CdpResult<ICdpConnection>.Fail(
                BrowserErrorCode.CdpUnavailable,
                $"Could not open a CDP WebSocket to {label} ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return CdpResult<ICdpConnection>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"Opening the CDP WebSocket to {label} failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Null when <paramref name="port"/>'s listener is still <paramref name="owner"/>, the browser's process the caller checked
    /// before it read the connection's URL; else why the open connection is not used. A browser that exits meanwhile frees its
    /// port, and another program on this computer that takes it would get the commands. Read once the connection is open, a
    /// listener that is the same process shows the connection reached it, as a browser's process does not listen again once
    /// it has stopped. Fails closed when the listener cannot be read.
    /// </summary>
    private CdpResult<ICdpConnection>? ListenerRefusal(int port, int owner, string label)
    {
        bool readable;
        int? now;
        try
        {
            readable = _portOwners.TryGetListeningProcessId(port, out now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            readable = false;
            now = null;
        }

        if (!readable)
        {
            return CdpResult<ICdpConnection>.Fail(
                BrowserErrorCode.CdpPortOwnerMismatch,
                $"Which process listens on CDP port {port} could not be read again once the connection to {label} was open, so " +
                $"the connection cannot be tied to the browser (process {owner}). The connection was closed. Nothing was sent " +
                "on it. Try again.");
        }

        if (now == owner)
            return null;

        var listener = now is int other ? $"process {other} listens on it now" : "nothing listens on it now";
        return CdpResult<ICdpConnection>.Fail(
            BrowserErrorCode.CdpPortOwnerMismatch,
            $"CDP port {port} changed hands before the connection to {label} was open: {listener}, not the browser (process " +
            $"{owner}), so the connection may be another program's. The connection was closed. Nothing was sent on it. The " +
            "browser may have exited; check that it still runs.");
    }

    /// <summary>
    /// The HTTP handler for the /json requests: no proxy, no cookies, and no redirects, which could send a request off
    /// 127.0.0.1:port to wherever the program on the port points it.
    /// </summary>
    public static SocketsHttpHandler CreateHttpHandler() =>
        new() { UseProxy = false, UseCookies = false, AllowAutoRedirect = false };

    private async Task<CdpResult<JsonDocument>> FetchJsonAsync(int port, string path, CancellationToken token)
    {
        var requested = new Uri($"http://{Host}:{port}{path}");
        using var response = await _httpClient
            .GetAsync(requested, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        // A handler that followed a redirect reports where the answer came from; only 127.0.0.1:port's answer is read
        if (response.RequestMessage?.RequestUri is { } answered &&
            (answered.Host != requested.Host || answered.Port != requested.Port))
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"GET {path} on {Host}:{port} was answered from {answered.Host}:{answered.Port}, so it was not read. " +
                "Is this a browser debug port?");
        }

        // A program answered, so the port is not empty: an error or a redirect means it is not a DevTools endpoint
        if (!response.IsSuccessStatusCode)
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"GET {path} on {Host}:{port} answered HTTP {(int)response.StatusCode}, not like a DevTools endpoint. " +
                "Is this a browser debug port?");
        }

        if (response.Content.Headers.ContentLength is long declared && declared > _limits.MaxHttpBodyBytes)
            return TooLarge(port, path);

        var body = new ArrayBufferWriter<byte>(CopyChunkBytes);
        await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            while (true)
            {
                var read = await stream.ReadAsync(body.GetMemory(CopyChunkBytes), token).ConfigureAwait(false);
                if (read == 0)
                    break;

                body.Advance(read);
                if (body.WrittenCount > _limits.MaxHttpBodyBytes)
                    return TooLarge(port, path);
            }
        }

        return CdpResult<JsonDocument>.Ok(JsonDocument.Parse(body.WrittenMemory));
    }

    private CdpResult<JsonDocument> TooLarge(int port, string path) => CdpResult<JsonDocument>.Fail(
        BrowserErrorCode.CdpResponseTooLarge,
        $"GET {path} on {Host}:{port} returned more than {FormatBytes(_limits.MaxHttpBodyBytes)}, so it was not read. " +
        "A browser with that many tabs cannot be read through CDP; close some tabs, or pass useCdp=false to use UI Automation.");

    /// <summary>
    /// True when nothing answered on the port: the connection was refused or reset before HTTP started. Anything else
    /// means a program answered, but not as a DevTools endpoint does.
    /// </summary>
    private static bool IsConnectionFailure(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError ||
        ex.InnerException is SocketException;

    /// <summary>True for a WebSocket handshake that HTTP 403 refused (Chrome 111+ refuses an Origin it does not allow).</summary>
    private static bool IsForbidden(WebSocketException ex) =>
        ex.Message.Contains("'403'", StringComparison.Ordinal) ||
        ex.InnerException?.Message.Contains("'403'", StringComparison.Ordinal) == true;

    private static CancellationTokenSource CreateTimeoutSource(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
        return source;
    }

    private static string FormatMs(TimeSpan timeout) =>
        $"{(long)timeout.TotalMilliseconds} ms";

    private static string FormatBytes(int bytes) =>
        bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes} bytes";
}
