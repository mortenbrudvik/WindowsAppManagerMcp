using System.Net;
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

    public FakeCdpHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public List<Uri> Requests { get; } = new();

    /// <summary>Answers every request with the given JSON body.</summary>
    public static FakeCdpHttpHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));

    /// <summary>Answers /json/list with page targets whose WebSocket URLs point at 127.0.0.1.</summary>
    public static FakeCdpHttpHandler Targets(params (string Id, string Title, string Url)[] pages) =>
        Json(JsonSerializer.Serialize(pages.Select(p => new
        {
            id = p.Id,
            type = "page",
            title = p.Title,
            url = p.Url,
            webSocketDebuggerUrl = $"ws://127.0.0.1:9222/devtools/page/{p.Id}"
        })));

    /// <summary>Never answers; only cancellation ends the request.</summary>
    public static FakeCdpHttpHandler NeverAnswers() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    /// <summary>Fails like a closed port.</summary>
    public static FakeCdpHttpHandler ConnectionRefused() =>
        new((_, _) => throw new HttpRequestException("No connection could be made because the target machine actively refused it."));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return _respond(request, cancellationToken);
    }
}

/// <summary>
/// In-memory WebSocket that plays the browser side of a CDP session.
/// Sent commands are recorded; a responder decides which messages come back.
/// Without a responder nothing is ever answered, like a page blocked by an alert().
/// </summary>
public sealed class FakeCdpWebSocket : WebSocket
{
    private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
    private readonly Action<FakeCdpWebSocket, JsonElement>? _responder;
    private Frame? _current;
    private int _currentOffset;
    private WebSocketState _state = WebSocketState.Open;
    private CancellationToken _lastReceiveToken;

    public FakeCdpWebSocket(Action<FakeCdpWebSocket, JsonElement>? responder = null)
    {
        _responder = responder;
    }

    public List<JsonElement> SentCommands { get; } = new();

    /// <summary>
    /// True when the token passed to the last ReceiveAsync has been cancelled, which shows the
    /// caller bounded the receive (CancellationToken.None can never be cancelled).
    /// </summary>
    public bool ReceiveTokenWasCancelled => _lastReceiveToken.IsCancellationRequested;

    public bool WasAborted { get; private set; }

    public bool WasDisposed { get; private set; }

    /// <summary>The Runtime.evaluate command that was sent, if any.</summary>
    public JsonElement? EvaluateCommand =>
        SentCommands.Cast<JsonElement?>().FirstOrDefault(c => c!.Value.GetProperty("method").GetString() == "Runtime.evaluate");

    /// <summary>Queues a text message, optionally split into frames of <paramref name="frameSize"/> bytes.</summary>
    public void Enqueue(string json, int? frameSize = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var size = frameSize ?? Math.Max(bytes.Length, 1);
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var count = Math.Min(size, bytes.Length - offset);
            _incoming.Writer.TryWrite(new Frame(bytes.AsMemory(offset, count).ToArray(), offset + count >= bytes.Length, WebSocketMessageType.Text));
        }
    }

    /// <summary>Queues a close frame, as when the browser drops the session.</summary>
    public void EnqueueClose() =>
        _incoming.Writer.TryWrite(new Frame(Array.Empty<byte>(), true, WebSocketMessageType.Close));

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
            _current = await _incoming.Reader.ReadAsync(cancellationToken);
            _currentOffset = 0;
        }

        var frame = _current;
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

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var document = JsonDocument.Parse(buffer.AsMemory());
        var command = document.RootElement.Clone();
        SentCommands.Add(command);
        _responder?.Invoke(this, command);
        return Task.CompletedTask;
    }

    private sealed record Frame(byte[] Data, bool EndOfMessage, WebSocketMessageType Type);
}

/// <summary>
/// Hands out a fake WebSocket, or never connects when <see cref="NeverConnects"/> is set.
/// </summary>
public sealed class FakeCdpWebSocketFactory : ICdpWebSocketFactory
{
    private readonly FakeCdpWebSocket? _socket;

    public FakeCdpWebSocketFactory(FakeCdpWebSocket socket)
    {
        _socket = socket;
    }

    private FakeCdpWebSocketFactory()
    {
    }

    public static FakeCdpWebSocketFactory NeverConnects() => new();

    public List<Uri> ConnectedUris { get; } = new();

    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ConnectedUris.Add(uri);
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
