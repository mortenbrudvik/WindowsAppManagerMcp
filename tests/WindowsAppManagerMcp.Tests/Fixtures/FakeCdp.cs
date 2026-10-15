using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// HttpMessageHandler that answers CDP HTTP requests in-process. No network is used.
/// </summary>
public sealed class FakeCdpHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<Uri> _requests = new();

    public FakeCdpHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>True once the handler was disposed (by the HttpClient that owns it).</summary>
    public bool WasDisposed { get; private set; }

    /// <summary>Answers every request with the given JSON body.</summary>
    public static FakeCdpHttpHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    /// <summary>Answers every request with the content <paramref name="content"/> creates.</summary>
    public static FakeCdpHttpHandler Body(Func<HttpContent> content) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() }));

    /// <summary>
    /// A body of <paramref name="bytes"/> bytes sent without a Content-Length header, as a chunked response arrives, so
    /// only reading it can tell how large it is.
    /// </summary>
    public static HttpContent UnsizedBody(byte[] bytes) => new UnsizedStreamContent(bytes);

    /// <summary>Answers /json/list with page targets whose WebSocket URLs point at 127.0.0.1:9222.</summary>
    public static FakeCdpHttpHandler Targets(params (string Id, string Title, string Url)[] pages) =>
        Targets(9222, pages);

    /// <summary>Answers /json/list with page targets whose WebSocket URLs point at 127.0.0.1:<paramref name="port"/>.</summary>
    public static FakeCdpHttpHandler Targets(int port, params (string Id, string Title, string Url)[] pages) =>
        Json(JsonSerializer.Serialize(pages.Select(p => new
        {
            id = p.Id,
            type = "page",
            title = p.Title,
            url = p.Url,
            webSocketDebuggerUrl = $"ws://127.0.0.1:{port}/devtools/page/{p.Id}"
        })));

    /// <summary>Never answers; only cancellation ends the request.</summary>
    public static FakeCdpHttpHandler NeverAnswers() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    /// <summary>Fails like a closed port: SocketsHttpHandler reports a ConnectionError over a SocketException.</summary>
    public static FakeCdpHttpHandler ConnectionRefused() =>
        new((_, _) => throw new HttpRequestException(
            HttpRequestError.ConnectionError,
            "No connection could be made because the target machine actively refused it. (127.0.0.1:9222)",
            new SocketException((int)SocketError.ConnectionRefused)));

    /// <summary>Fails like a program on the port that answers, but not with HTTP a client can read.</summary>
    public static FakeCdpHttpHandler Fails(HttpRequestError error) =>
        new((_, _) => throw new HttpRequestException(error, $"The response ended prematurely ({error}).", null));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request.RequestUri!);
        }

        return _respond(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }

    /// <summary>Stream content that does not know its length, so no Content-Length header is sent.</summary>
    private sealed class UnsizedStreamContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

/// <summary>
/// In-memory WebSocket that plays the browser side of a CDP session.
/// Sent commands are recorded; a responder decides which messages come back.
/// Without a responder nothing is ever answered, like a page blocked by an alert().
/// </summary>
/// <remarks>
/// Like <see cref="ClientWebSocket"/>, a token that is cancelled before or during a send or a receive aborts the socket
/// (<see cref="AbortedByCancellation"/>): a per-call timeout that reached the socket would end the whole connection.
/// <see cref="Abort"/> only records the call, so a receive that is waiting for a message ends through its token.
/// </remarks>
public sealed class FakeCdpWebSocket : WebSocket
{
    private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
    private readonly Action<FakeCdpWebSocket, JsonElement>? _responder;
    private readonly List<JsonElement> _sent = new();
    private readonly CancellationTokenSource _aborted = new();
    private Frame? _current;
    private int _currentOffset;
    private volatile WebSocketState _state = WebSocketState.Open;
    private CancellationToken _lastReceiveToken;

    public FakeCdpWebSocket(Action<FakeCdpWebSocket, JsonElement>? responder = null)
    {
        _responder = responder;
    }

    /// <summary>The commands sent so far, in order (a copy).</summary>
    public IReadOnlyList<JsonElement> SentCommands
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToList();
            }
        }
    }

    /// <summary>
    /// True when the token passed to the last ReceiveAsync has been cancelled, which shows the
    /// caller bounded the receive (CancellationToken.None can never be cancelled).
    /// </summary>
    public bool ReceiveTokenWasCancelled => _lastReceiveToken.IsCancellationRequested;

    public bool WasAborted { get; private set; }

    /// <summary>True when a cancelled send or receive token aborted the socket, as it aborts a ClientWebSocket.</summary>
    public bool AbortedByCancellation { get; private set; }

    public bool WasDisposed { get; private set; }

    /// <summary>When true, every send waits until the socket is aborted or its token is cancelled (a send stall).</summary>
    public bool StallSends { get; set; }

    /// <summary>The Runtime.evaluate command that was sent, if any.</summary>
    public JsonElement? EvaluateCommand =>
        SentCommands.Cast<JsonElement?>().FirstOrDefault(c => c!.Value.GetProperty("method").GetString() == "Runtime.evaluate");

    /// <summary>Queues a text message, optionally split into frames of <paramref name="frameSize"/> bytes.</summary>
    public void Enqueue(string json, int? frameSize = null) => EnqueueRaw(Encoding.UTF8.GetBytes(json), frameSize);

    /// <summary>Queues a text message of raw bytes, optionally split into frames of <paramref name="frameSize"/> bytes.</summary>
    public void EnqueueRaw(byte[] bytes, int? frameSize = null)
    {
        var size = frameSize ?? Math.Max(bytes.Length, 1);
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var count = Math.Min(size, bytes.Length - offset);
            _incoming.Writer.TryWrite(new Frame(bytes.AsMemory(offset, count).ToArray(), offset + count >= bytes.Length, WebSocketMessageType.Text));
        }
    }

    /// <summary>Queues a CDP event.</summary>
    public void EnqueueEvent(string method, object? parameters = null) =>
        Enqueue(JsonSerializer.Serialize(new { method, @params = parameters ?? new { } }));

    /// <summary>Queues a reply with an empty result to <paramref name="command"/>.</summary>
    public void ReplyEmpty(JsonElement command) =>
        Enqueue("{\"id\":" + command.GetProperty("id").GetInt32() + ",\"result\":{}}");

    /// <summary>Queues an error reply to <paramref name="command"/>.</summary>
    public void ReplyError(JsonElement command, int code, string message) =>
        Enqueue(JsonSerializer.Serialize(new { id = command.GetProperty("id").GetInt32(), error = new { code, message } }));

    /// <summary>Queues a close frame, as when the browser drops the session.</summary>
    public void EnqueueClose() =>
        _incoming.Writer.TryWrite(new Frame(Array.Empty<byte>(), true, WebSocketMessageType.Close));

    /// <summary>Makes the receive that reaches this point in the queue throw <paramref name="exception"/>.</summary>
    public void FailNextReceive(Exception exception) =>
        _incoming.Writer.TryWrite(new Frame(Array.Empty<byte>(), true, WebSocketMessageType.Binary, exception));

    /// <summary>Answers a command with a Runtime.evaluate style result value.</summary>
    public void ReplyWithValue(JsonElement command, object value, int? frameSize = null) =>
        Enqueue(JsonSerializer.Serialize(new
        {
            id = command.GetProperty("id").GetInt32(),
            result = new { result = new { type = "object", value } }
        }), frameSize);

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public override void Abort()
    {
        WasAborted = true;
        _state = WebSocketState.Aborted;
        _aborted.Cancel();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        WasDisposed = true;
        _state = WebSocketState.Closed;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        _lastReceiveToken = cancellationToken;

        if (_current == null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _current = await _incoming.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                AbortByCancellation();
                throw;
            }

            _currentOffset = 0;
        }

        var frame = _current;
        if (frame.Error != null)
        {
            _current = null;
            throw frame.Error;
        }

        if (frame.Type == WebSocketMessageType.Close)
        {
            _current = null;
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "closed");
        }

        var count = Math.Min(buffer.Count, frame.Data.Length - _currentOffset);
        Array.Copy(frame.Data, _currentOffset, buffer.Array!, buffer.Offset, count);
        _currentOffset += count;

        var frameDone = _currentOffset >= frame.Data.Length;
        if (frameDone)
            _current = null;

        return new WebSocketReceiveResult(count, frame.Type, frameDone && frame.EndOfMessage);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            AbortByCancellation();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (WasAborted)
            throw new WebSocketException(WebSocketError.InvalidState, "The WebSocket was aborted.");

        if (StallSends)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
            try
            {
                await Task.Delay(Timeout.Infinite, stall.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AbortByCancellation();
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new WebSocketException(WebSocketError.InvalidState, "The WebSocket was aborted.");
            }
        }

        using var document = JsonDocument.Parse(buffer.AsMemory());
        var command = document.RootElement.Clone();
        lock (_sent)
        {
            _sent.Add(command);
        }

        _responder?.Invoke(this, command);
    }

    private void AbortByCancellation()
    {
        AbortedByCancellation = true;
        WasAborted = true;
        _state = WebSocketState.Aborted;
    }

    private sealed record Frame(byte[] Data, bool EndOfMessage, WebSocketMessageType Type, Exception? Error = null);
}

/// <summary>
/// Hands out a fake WebSocket, or never connects when <see cref="NeverConnects"/> is set.
/// </summary>
public sealed class FakeCdpWebSocketFactory : ICdpWebSocketFactory
{
    private readonly FakeCdpWebSocket? _socket;
    private readonly Func<FakeCdpWebSocket>? _newSocket;
    private readonly Exception? _failure;
    private readonly List<Uri> _connectedUris = new();

    public FakeCdpWebSocketFactory(FakeCdpWebSocket socket)
    {
        _socket = socket;
    }

    private FakeCdpWebSocketFactory(Exception? failure = null)
    {
        _failure = failure;
    }

    private FakeCdpWebSocketFactory(Func<FakeCdpWebSocket> newSocket)
    {
        _newSocket = newSocket;
    }

    /// <summary>Hands out a new socket on every connect, as a browser opens a connection for every call.</summary>
    public static FakeCdpWebSocketFactory EachConnect(Func<FakeCdpWebSocket> newSocket) => new(newSocket);

    public static FakeCdpWebSocketFactory NeverConnects() => new();

    /// <summary>Fails every connect with <paramref name="failure"/>.</summary>
    public static FakeCdpWebSocketFactory Failing(Exception failure) => new(failure);

    public IReadOnlyList<Uri> ConnectedUris
    {
        get
        {
            lock (_connectedUris)
            {
                return _connectedUris.ToList();
            }
        }
    }

    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        lock (_connectedUris)
        {
            _connectedUris.Add(uri);
        }

        if (_failure != null)
            throw _failure;

        if (_newSocket != null)
            return _newSocket();

        if (_socket == null)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return _socket!;
    }
}

/// <summary>
/// Port owner lookup with a fixed answer.
/// </summary>
public sealed class FakePortOwnerResolver : ITcpPortOwnerResolver
{
    private readonly int? _owner;

    public FakePortOwnerResolver(int? owner)
    {
        _owner = owner;
    }

    public int? GetListeningProcessId(int port) => _owner;
}
