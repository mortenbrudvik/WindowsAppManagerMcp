using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// The real CDP transport (SocketsHttpHandler, ClientWebSocketFactory, CdpConnection) against a DevTools endpoint this
/// test serves itself on 127.0.0.1 (roadmap M5.2). It checks what the in-memory fakes cannot: the headers a real client
/// sends, and how real sockets report a refused handshake, a reset connection and a broken HTTP answer. No browser is
/// started or reached; the port is one the test's own listener holds.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CdpLoopbackIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly LoopbackCdpServer _server = new();
    private CdpTransport _transport = null!;
    private CdpService _sut = null!;

    public Task InitializeAsync()
    {
        // The constructor the server registers, with the HTTP handler it builds
        _transport = new CdpTransport(new ClientWebSocketFactory());
        _sut = new CdpService(_transport, new TcpPortOwnerResolver());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _transport.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task ATabIsReadByTargetId_WithoutAnOriginHeader_FromTheLoopbackAddress()
    {
        // Act - the port's owner is this test process, so the targetId path's owner check passes
        var target = await _sut.FindTargetByIdAsync(_server.Port, Environment.ProcessId, "T1", CallTimeout);
        var snapshot = await _sut.GetPageSnapshotAsync(target.Value!, new CdpPageRequest(true, 0, 20_000), CallTimeout);

        // Assert
        target.Success.Should().BeTrue(target.Error);
        snapshot.Success.Should().BeTrue(snapshot.Error);
        snapshot.Value!.Content.Should().Be("served over a real socket");
        _server.Handshake.Should().NotBeNull();
        _server.Handshake!.Should().NotContainKey("origin", "Chrome 111+ refuses an Origin it was not told to allow");
        _server.Handshake!["host"].Should().Be($"127.0.0.1:{_server.Port}");
        _server.ListRequestHost.Should().Be($"127.0.0.1:{_server.Port}");
    }

    [Fact]
    public async Task AHandshakeRefusedWith403_IsCdpUnavailable_WithTheReason()
    {
        _server.RefuseHandshake = true;
        var target = await _sut.FindTargetByIdAsync(_server.Port, Environment.ProcessId, "T1", CallTimeout);

        var snapshot = await _sut.GetPageSnapshotAsync(target.Value!, new CdpPageRequest(true, 0, 20_000), CallTimeout);

        snapshot.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        snapshot.Error.Should().Contain("403").And.Contain("--remote-allow-origins");
    }

    [Fact]
    public async Task AConnectionResetDuringTheRead_IsCdpTargetClosed()
    {
        _server.ResetOnEvaluate = true;
        var target = await _sut.FindTargetByIdAsync(_server.Port, Environment.ProcessId, "T1", CallTimeout);

        var snapshot = await _sut.GetPageSnapshotAsync(target.Value!, new CdpPageRequest(true, 0, 20_000), CallTimeout);

        snapshot.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed, snapshot.Error);
    }

    [Fact]
    public async Task AnHttpAnswerThatEndsEarly_IsCdpProtocolError()
    {
        _server.EndListEarly = true;

        var targets = await _sut.GetPageTargetsAsync(_server.Port, CallTimeout);

        targets.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError, targets.Error);
        targets.Error.Should().Contain("answered but not like a DevTools endpoint");
    }

    [Fact]
    public async Task ARedirectFromTheListIsNotFollowed()
    {
        // A second endpoint the redirect points at; it must never be asked anything
        var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        try
        {
            var otherPort = ((IPEndPoint)other.LocalEndpoint).Port;
            _server.RedirectListTo = $"http://127.0.0.1:{otherPort}/json/list";

            var targets = await _sut.GetPageTargetsAsync(_server.Port, CallTimeout);

            targets.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError, targets.Error);
            targets.Error.Should().Contain("HTTP 302");
            other.Pending().Should().BeFalse("the transport must not leave 127.0.0.1:cdpPort");
        }
        finally
        {
            other.Stop();
        }
    }

    [Fact]
    public async Task APortNothingListensOn_IsCdpUnavailable()
    {
        // A port this test held a moment ago and released
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var targets = await _sut.GetPageTargetsAsync(port, CallTimeout);

        targets.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable, targets.Error);
    }

    /// <summary>
    /// A minimal DevTools endpoint: GET /json/list lists one page, T1, and /devtools/page/T1 upgrades to a WebSocket that
    /// answers Page.enable and Runtime.evaluate. Each connection is served once and closed.
    /// </summary>
    private sealed class LoopbackCdpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        public LoopbackCdpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        public bool RefuseHandshake { get; set; }

        public bool ResetOnEvaluate { get; set; }

        public bool EndListEarly { get; set; }

        /// <summary>When set, /json/list answers 302 with this Location.</summary>
        public string? RedirectListTo { get; set; }

        /// <summary>The WebSocket handshake's headers, by lower-case name.</summary>
        public Dictionary<string, string>? Handshake { get; private set; }

        public string? ListRequestHost { get; private set; }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try
            {
                await _accepting.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Stopping the listener ends the accept loop with an exception
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var (path, headers) = await ReadRequestAsync(stream);
                    if (path == "/json/list")
                    {
                        ListRequestHost = headers.GetValueOrDefault("host");
                        await ServeListAsync(stream);
                    }
                    else if (path == "/devtools/page/T1")
                    {
                        Handshake = headers;
                        await ServeWebSocketAsync(client, stream, headers);
                    }
                    else
                    {
                        await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    }
                }
                catch (Exception)
                {
                    // A client that went away; the test reads what it got
                }
            }
        }

        private async Task ServeListAsync(NetworkStream stream)
        {
            if (RedirectListTo != null)
            {
                await WriteAsync(stream,
                    $"HTTP/1.1 302 Found\r\nLocation: {RedirectListTo}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            if (EndListEarly)
            {
                // A status line and then nothing: a program that answers, but not with HTTP a client can read
                await WriteAsync(stream, "HTTP/1.1 200 OK\r\n");
                return;
            }

            var body = JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = "T1",
                    type = "page",
                    title = "Loopback",
                    url = "data:text/html,loopback",
                    webSocketDebuggerUrl = $"ws://127.0.0.1:{Port}/devtools/page/T1"
                }
            });
            var bytes = Encoding.UTF8.GetByteCount(body);
            await WriteAsync(stream,
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes}\r\nConnection: close\r\n\r\n{body}");
        }

        private async Task ServeWebSocketAsync(TcpClient client, NetworkStream stream, Dictionary<string, string> headers)
        {
            if (RefuseHandshake)
            {
                await WriteAsync(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await WriteAsync(stream,
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");

            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, _stop.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    return;

                using var command = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                var id = command.RootElement.GetProperty("id").GetInt32();
                var method = command.RootElement.GetProperty("method").GetString();
                if (method == "Runtime.evaluate" && ResetOnEvaluate)
                {
                    // Gone without a close frame, as when the browser process is killed
                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Client.Close();
                    return;
                }

                object reply = method == "Runtime.evaluate"
                    ? new { id, result = new { result = new { type = "object", value = new { title = "Loopback", url = "data:text/html,loopback", content = "served over a real socket", offset = 0, totalLength = 25 } } } }
                    : new { id, result = new { } };
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(reply), WebSocketMessageType.Text, true, _stop.Token);
            }
        }

        /// <summary>Reads one request's head byte by byte, so nothing after it (WebSocket frames) is consumed.</summary>
        private static async Task<(string Path, Dictionary<string, string> Headers)> ReadRequestAsync(NetworkStream stream)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (head.Count < 16 * 1024)
            {
                if (await stream.ReadAsync(one) == 0)
                    break;

                head.Add(one[0]);
                if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                    break;
            }

            var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var path = lines.Length > 0 ? lines[0].Split(' ')[1] : "";
            var headers = lines.Skip(1)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim().ToLowerInvariant(), parts => parts[1].Trim());
            return (path, headers);
        }

        private static Task WriteAsync(NetworkStream stream, string text) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
    }
}
