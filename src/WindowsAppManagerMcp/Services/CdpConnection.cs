using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// One DevTools WebSocket connection (roadmap M5.2): commands get increasing ids and their replies are matched by id,
/// events go to the waiters that registered for them, and a dialog, a detach or a closed socket is reported to every
/// command still waiting.
/// </summary>
/// <remarks>
/// <para>
/// Only the connection's own lifetime token ever reaches the socket. A <see cref="ClientWebSocket"/> aborts itself when
/// a token passed to a send or receive is cancelled, so a per-command timeout or the caller's cancellation acts only on
/// the command's wait: on a timeout the command is forgotten, its late reply is dropped, and the connection stays open.
/// The one exception is a send that does not finish within its command's timeout: the socket may hold half a message,
/// so the connection is aborted (CdpTimeout for that command and every later one).
/// </para>
/// <para>
/// A command's entry is registered before it is sent, and an event waiter before the command that causes the event,
/// because a reply can arrive before the send returns.
/// </para>
/// </remarks>
public sealed class CdpConnection : ICdpConnection
{
    private const int ReceiveChunkBytes = 64 * 1024;
    private static readonly TimeSpan ReaderExitBound = TimeSpan.FromSeconds(1);

    private readonly WebSocket _socket;
    private readonly string _label;
    private readonly int _maxMessageBytes;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<int, PendingCommand> _pending = new();
    private readonly List<EventWaiter> _waiters = new();
    private readonly Task _reader;
    private int _lastId;
    private int _disposed;
    private CdpResult<bool>? _closed;
    private CdpDialog? _dialog;

    /// <param name="socket">An open WebSocket to a DevTools endpoint; the connection owns and disposes it.</param>
    /// <param name="label">Names the connection in messages, for example "tab 8C1F...".</param>
    /// <param name="maxMessageBytes">The largest message the browser may send; a larger one ends the connection
    /// (CdpResponseTooLarge).</param>
    public CdpConnection(WebSocket socket, string label, int maxMessageBytes)
    {
        _socket = socket;
        _label = label;
        _maxMessageBytes = maxMessageBytes;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <inheritdoc />
    public CdpResult<bool>? Closed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    /// <inheritdoc />
    public CdpDialog? OpenDialog
    {
        get
        {
            lock (_gate)
            {
                return _dialog;
            }
        }
    }

    /// <inheritdoc />
    public async Task<CdpResult<CdpReply>> SendAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        CdpSendOptions options = CdpSendOptions.None)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _lastId);
        var pending = new PendingCommand(options);
        lock (_gate)
        {
            if (_closed is { } closed)
                return closed.FailAs<CdpReply>();

            if (_dialog is { } dialog && !pending.AllowedWhileDialogOpen)
                return CdpResult<CdpReply>.Fail(BrowserErrorCode.CdpDialogOpen, CdpDialogText.Describe(dialog));

            _pending[id] = pending;
        }

        var message = Serialize(id, method, parameters);
        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callTimeout.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);

        try
        {
            var sent = await SendMessageAsync(message, callTimeout.Token).ConfigureAwait(false);
            if (sent != null)
            {
                Forget(id);
                return sent;
            }

            return await pending.Reply.Task.WaitAsync(callTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callTimeout.IsCancellationRequested)
        {
            Forget(id);
            cancellationToken.ThrowIfCancellationRequested();
            return CdpResult<CdpReply>.Fail(
                BrowserErrorCode.CdpTimeout,
                $"The DevTools connection to {_label} got no answer to {method} within {FormatMs(timeout)}.");
        }
    }

    /// <inheritdoc />
    public ICdpEventWaiter ExpectEvent(string method, Func<JsonElement, bool>? filter = null)
    {
        var waiter = new EventWaiter(this, method, filter);
        lock (_gate)
        {
            if (_closed is { } closed)
                waiter.Complete(closed.FailAs<JsonElement>());
            else
                _waiters.Add(waiter);
        }

        return waiter;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The reader's receive waits on the lifetime token; a fake socket's Abort does not end it.
        _lifetime.Cancel();

        // Abort instead of a close handshake: a blocked page would never answer it.
        _socket.Abort();
        _socket.Dispose();

        try
        {
            await _reader.WaitAsync(ReaderExitBound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reader ends with the socket; it is not waited for longer.
        }

        Close(CdpResult<bool>.Fail(
            BrowserErrorCode.CdpProtocolError,
            $"The DevTools connection to {_label} was disposed before it answered."));
    }

    /// <summary>
    /// Sends one message under the send lock. Null when it was sent; otherwise why the command failed. A send that is
    /// still running when <paramref name="callToken"/> fires aborts the connection.
    /// </summary>
    private async Task<CdpResult<CdpReply>?> SendMessageAsync(byte[] message, CancellationToken callToken)
    {
        await _sendLock.WaitAsync(callToken).ConfigureAwait(false);
        try
        {
            if (Closed is { } closed)
                return closed.FailAs<CdpReply>();

            var send = _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, _lifetime.Token);
            try
            {
                await send.WaitAsync(callToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callToken.IsCancellationRequested && !send.IsCompleted)
            {
                // A stalled send may have written half a message: nothing more can be sent on this socket
                Close(CdpResult<bool>.Fail(
                    BrowserErrorCode.CdpTimeout,
                    $"The DevTools connection to {_label} was aborted after a send timeout."));
                _socket.Abort();
                _ = send.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                throw;
            }

            return null;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException ||
                                   (ex is OperationCanceledException && _lifetime.IsCancellationRequested && !callToken.IsCancellationRequested))
        {
            Close(_lifetime.IsCancellationRequested
                ? CdpResult<bool>.Fail(BrowserErrorCode.CdpProtocolError, $"The DevTools connection to {_label} was disposed.")
                : TargetClosed($"sending failed: {ex.Message}"));
            return Closed!.FailAs<CdpReply>();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new ArrayBufferWriter<byte>(ReceiveChunkBytes);
        try
        {
            while (true)
            {
                buffer.ResetWrittenCount();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer.GetMemory(ReceiveChunkBytes), _lifetime.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        Close(TargetClosed("the browser closed the DevTools connection"));
                        return;
                    }

                    buffer.Advance(received.Count);
                    if (buffer.WrittenCount > _maxMessageBytes)
                    {
                        Close(CdpResult<bool>.Fail(
                            BrowserErrorCode.CdpResponseTooLarge,
                            $"{_label} sent a DevTools message larger than {FormatBytes(_maxMessageBytes)}, so it was not read. " +
                            "Read less of the page at a time, for example with a smaller maxLength."));
                        return;
                    }
                }
                while (!received.EndOfMessage);

                if (!Dispatch(buffer.WrittenMemory))
                    return;
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Disposed: DisposeAsync reports what is left
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            Close(TargetClosed($"the connection was reset: {ex.Message}"));
        }
        catch (Exception ex)
        {
            Close(CdpResult<bool>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"Reading from the DevTools connection to {_label} failed ({ex.GetType().Name}: {ex.Message})."));
        }
    }

    /// <summary>Handles one message; false when it ended the connection.</summary>
    private bool Dispatch(ReadOnlyMemory<byte> bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            Close(CdpResult<bool>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"{_label} sent a DevTools message that is not JSON ({ex.Message})."));
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return true;

            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var idValue))
            {
                DeliverReply(idValue, root);
                return true;
            }

            if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
            {
                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                return DeliverEvent(method.GetString() ?? "", parameters);
            }

            return true;
        }
    }

    private void DeliverReply(int id, JsonElement root)
    {
        PendingCommand? pending;
        lock (_gate)
        {
            // A reply to a command that timed out, or to none, is dropped
            if (!_pending.Remove(id, out pending))
                return;
        }

        CdpProtocolFault? fault = null;
        if (root.TryGetProperty("error", out var error))
        {
            var code = error.ValueKind == JsonValueKind.Object &&
                       error.TryGetProperty("code", out var c) &&
                       c.ValueKind == JsonValueKind.Number &&
                       c.TryGetInt32(out var codeValue)
                ? codeValue
                : 0;
            var text = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                ? m.ToString()
                : error.ToString();
            fault = new CdpProtocolFault(code, text);
        }

        var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
        pending.Reply.TrySetResult(CdpResult<CdpReply>.Ok(new CdpReply(result, fault)));
    }

    private bool DeliverEvent(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Page.javascriptDialogOpening":
                OpenDialogReported(CdpDialogText.FromEvent(parameters));
                break;

            case "Page.javascriptDialogClosed":
                lock (_gate)
                {
                    _dialog = null;
                }
                break;

            case "Inspector.detached":
                var reason = CdpReplies.GetString(parameters, "reason");
                Close(TargetClosed(reason.Length > 0 ? $"DevTools detached: {reason}" : "DevTools detached"));
                return false;

            case "Inspector.targetCrashed":
                Close(TargetClosed("the tab crashed"));
                return false;
        }

        List<EventWaiter> matched;
        lock (_gate)
        {
            matched = _waiters.Where(w => w.Method == method && w.Matches(parameters)).ToList();
            foreach (var waiter in matched)
                _waiters.Remove(waiter);
        }

        foreach (var waiter in matched)
            waiter.Complete(CdpResult<JsonElement>.Ok(parameters));

        return true;
    }

    /// <summary>Records the dialog and fails every waiting command that may not run while it is open.</summary>
    private void OpenDialogReported(CdpDialog dialog)
    {
        var failure = CdpResult<CdpReply>.Fail(BrowserErrorCode.CdpDialogOpen, CdpDialogText.Describe(dialog));
        List<KeyValuePair<int, PendingCommand>> blocked;
        lock (_gate)
        {
            _dialog = dialog;
            blocked = _pending.Where(p => !p.Value.AllowedWhileDialogOpen).ToList();
            foreach (var command in blocked)
                _pending.Remove(command.Key);
        }

        foreach (var command in blocked)
            command.Value.Reply.TrySetResult(failure);
    }

    /// <summary>Ends the connection with <paramref name="reason"/>, once: every waiting command and event fails with it.</summary>
    private void Close(CdpResult<bool> reason)
    {
        List<PendingCommand> pending;
        List<EventWaiter> waiters;
        lock (_gate)
        {
            if (_closed != null)
                return;

            _closed = reason;
            pending = _pending.Values.ToList();
            _pending.Clear();
            waiters = _waiters.ToList();
            _waiters.Clear();
        }

        foreach (var command in pending)
            command.Reply.TrySetResult(reason.FailAs<CdpReply>());
        foreach (var waiter in waiters)
            waiter.Complete(reason.FailAs<JsonElement>());
    }

    private void Forget(int id)
    {
        lock (_gate)
        {
            _pending.Remove(id);
        }
    }

    private void Forget(EventWaiter waiter)
    {
        lock (_gate)
        {
            _waiters.Remove(waiter);
        }
    }

    private CdpResult<bool> TargetClosed(string detail) => CdpResult<bool>.Fail(
        BrowserErrorCode.CdpTargetClosed,
        $"The DevTools connection to {_label} ended ({detail}). The tab closed, crashed, or the browser exited. " +
        "Call again with the window and without targetId to read the tab it shows now; if that returns CdpUnavailable, " +
        "the browser is gone.");

    private static byte[] Serialize(int id, string method, object? parameters)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parameters != null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters, parameters.GetType());
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string FormatMs(TimeSpan timeout) => $"{(long)timeout.TotalMilliseconds} ms";

    private static string FormatBytes(int bytes) =>
        bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes} bytes";

    private sealed class PendingCommand(CdpSendOptions options)
    {
        public bool AllowedWhileDialogOpen { get; } = options.HasFlag(CdpSendOptions.AllowWhileDialogOpen);

        public TaskCompletionSource<CdpResult<CdpReply>> Reply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class EventWaiter(CdpConnection connection, string method, Func<JsonElement, bool>? filter) : ICdpEventWaiter
    {
        private readonly TaskCompletionSource<CdpResult<JsonElement>> _event =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Method { get; } = method;

        public bool Matches(JsonElement parameters)
        {
            try
            {
                return filter?.Invoke(parameters) ?? true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Complete(CdpResult<JsonElement> result) => _event.TrySetResult(result);

        public async Task<CdpResult<JsonElement>> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
            try
            {
                return await _event.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested)
            {
                connection.Forget(this);
                cancellationToken.ThrowIfCancellationRequested();
                return CdpResult<JsonElement>.Fail(
                    BrowserErrorCode.CdpTimeout,
                    $"{connection._label} sent no {Method} event within {FormatMs(timeout)}.");
            }
        }

        public void Dispose() => connection.Forget(this);
    }
}
