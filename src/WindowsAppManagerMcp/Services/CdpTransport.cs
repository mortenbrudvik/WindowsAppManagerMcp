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
/// <see cref="ICdpWebSocketFactory"/>, so tests replace the browser with in-memory fakes.
/// </summary>
public sealed class CdpTransport : ICdpTransport, IDisposable
{
    // Chrome's DevTools HTTP server listens on 127.0.0.1. Using the IP avoids a failed ::1
    // attempt for "localhost" and makes the endpoint match the port-owner check.
    private const string Host = "127.0.0.1";

    private const int CopyChunkBytes = 64 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ICdpWebSocketFactory _webSocketFactory;
    private readonly CdpTransportLimits _limits;

    /// <param name="httpHandler">Sends the /json requests; the transport owns and disposes it.</param>
    /// <param name="webSocketFactory">Opens the DevTools WebSockets.</param>
    /// <param name="limits">The size limits, or null for 16 MB per /json body and 64 MB per WebSocket message.</param>
    public CdpTransport(HttpMessageHandler httpHandler, ICdpWebSocketFactory webSocketFactory, CdpTransportLimits? limits = null)
    {
        // Each request is bounded by the per-call timeout token instead.
        _httpClient = new HttpClient(httpHandler) { Timeout = Timeout.InfiniteTimeSpan };
        _webSocketFactory = webSocketFactory;
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

        if (port is < 1 or > 65535)
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpUnavailable,
                $"Invalid CDP port {port}. Use the port the browser was started with (--remote-debugging-port).");
        }

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

    private async Task<CdpResult<JsonDocument>> FetchJsonAsync(int port, string path, CancellationToken token)
    {
        using var response = await _httpClient
            .GetAsync($"http://{Host}:{port}{path}", HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return CdpResult<JsonDocument>.Fail(
                BrowserErrorCode.CdpUnavailable,
                $"GET {path} on {Host}:{port} returned HTTP {(int)response.StatusCode}. Is this a browser debug port?");
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
        "A browser with that many tabs cannot be read through CDP; close some tabs, or omit cdpPort to use UI Automation.");

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
