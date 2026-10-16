using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace WindowsAppManagerMcp.Tests.Infrastructure;

/// <summary>
/// Hosts an MCP server and a connected MCP client in the test process, linked by in-memory pipes.
/// Requests go through the real SDK pipeline (argument binding, filters, result serialization),
/// but only the tools passed in are registered, so no native or desktop calls happen.
/// Every step has a timeout, so a broken pipeline fails the test instead of hanging the run.
/// The contract tests (WindowsAppManagerMcp.Tests) and the end-to-end tests (WindowsAppManagerMcp.E2ETests) share it.
/// An end-to-end test that registers the server's own tools must pass them the harness's guarded services through
/// <c>configureServices</c>, so every native call still goes through the E2E guard.
/// </summary>
public sealed class InProcessMcpServer : IAsyncDisposable
{
    /// <summary>
    /// How long each step (start, connect, stop, a round trip) may take before the test fails: as long as a call
    /// (<see cref="DefaultCallTimeout"/>), so a machine under heavy load can still start a host and connect. At 10 s, contract
    /// tests failed now and then at 11 to 14 s on a busy machine and passed on a rerun (roadmap M3.F29).
    /// </summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="CallToolAsync"/> waits for a tool call by default.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The revision the SDK's own client prefers, and what a test client negotiates by default: server/discover instead of
    /// the initialize handshake, and elicitation as a multi-round-trip request (the tool call returns input_required and
    /// the client retries it with the answer).
    /// </summary>
    public const string LatestRevision = "2026-07-28";

    /// <summary>
    /// An earlier revision with the initialize handshake, which stdio clients that have not moved to
    /// <see cref="NewestInitializeRevision"/> still negotiate: elicitation is a request from the server to the client
    /// while the tool call is open. Pass <see cref="WithRevision"/> as the client options to test this path.
    /// </summary>
    public const string InitializeRevision = "2025-06-18";

    /// <summary>
    /// The newest revision with the initialize handshake: SDK 2.2.0's default on that path (the revision it answers a
    /// client that asks for one it does not know), the one stdio clients such as Claude Code most likely negotiate,
    /// and the one the CI guard (scripts/Test-ToolsSnapshot.ps1) asks for. It added the form and url modes of
    /// elicitation.
    /// </summary>
    public const string NewestInitializeRevision = "2025-11-25";

    /// <summary>
    /// The revisions a protocol-level contract test (elicitation, progress, cancellation, notifications) runs on: the SDK
    /// client's default (<see cref="LatestRevision"/>, null here) and both initialize-handshake revisions.
    /// </summary>
    public static TheoryData<string?> Revisions() => new() { null, InitializeRevision, NewestInitializeRevision };

    /// <summary>
    /// <paramref name="options"/> (or new options) pinned to <paramref name="revision"/>; null leaves the SDK's default,
    /// <see cref="LatestRevision"/> with a fallback to the initialize handshake.
    /// </summary>
    public static McpClientOptions? WithRevision(McpClientOptions? options, string? revision)
    {
        if (revision == null)
        {
            return options;
        }

        options ??= new McpClientOptions();
        options.ProtocolVersion = revision;
        return options;
    }

    private readonly IHost _host;
    private readonly PipeWriter _serverInput;
    private readonly MessageRecordingStream _serverOutput;

    private InProcessMcpServer(
        IHost host, McpClient client, CapturingLoggerProvider logs, PipeWriter serverInput, MessageRecordingStream serverOutput)
    {
        _host = host;
        Client = client;
        Logs = logs;
        _serverInput = serverInput;
        _serverOutput = serverOutput;
    }

    public McpClient Client { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <summary>
    /// Every JSON-RPC message the server has written, in the order it wrote them. The SDK client hands notifications
    /// to their handlers concurrently, so a test that checks the order of notifications reads it here.
    /// </summary>
    public IReadOnlyList<string> ServerMessages => _serverOutput.Messages;

    /// <summary>
    /// The notifications/progress messages the server wrote, in the order it wrote them. The SDK client hands
    /// notifications to their handlers concurrently and drops a progress notification whose handler runs after the
    /// call's result arrived, so tests check progress here, where the server wrote it.
    /// </summary>
    public IReadOnlyList<(double Progress, double? Total, string? Message)> ProgressSent() =>
        ServerMessages
            .Select(message => JsonDocument.Parse(message).RootElement)
            .Where(IsProgressNotification)
            .Select(message => message.GetProperty("params"))
            .Select(p => (
                p.GetProperty("progress").GetDouble(),
                p.TryGetProperty("total", out var total) ? total.GetDouble() : (double?)null,
                p.TryGetProperty("message", out var text) ? text.GetString() : null))
            .ToList();

    /// <summary>
    /// Waits until the server has written <paramref name="count"/> progress notifications, and checks that every one
    /// of them came before the result of the call it belongs to: a progress notification after the result is lost.
    /// </summary>
    public IReadOnlyList<(double Progress, double? Total, string? Message)> WaitForProgressSent(int count)
    {
        SpinWait.SpinUntil(() => ProgressSent().Count >= count, StepTimeout)
            .Should().BeTrue($"the server sends {count} progress notifications");

        var messages = ServerMessages.Select(message => JsonDocument.Parse(message).RootElement).ToList();
        var lastProgress = messages.FindLastIndex(IsProgressNotification);
        var callResult = messages.FindLastIndex(message => message.TryGetProperty("result", out var result) &&
                                                           result.ValueKind == JsonValueKind.Object &&
                                                           result.TryGetProperty("content", out _));
        if (callResult >= 0)
        {
            lastProgress.Should().BeLessThan(callResult, "a progress notification must come before the call's result");
        }

        return ProgressSent();
    }

    private static bool IsProgressNotification(JsonElement message) =>
        message.TryGetProperty("method", out var method) && method.GetString() == NotificationMethods.ProgressNotification;

    /// <summary>
    /// The responses (a result or an error) the server wrote to request <paramref name="id"/>. Requests and notifications
    /// the server sent itself are not responses, even when their id happens to be the same.
    /// </summary>
    public IReadOnlyList<string> ResponsesTo(RequestId id) =>
        ServerMessages
            .Where(message =>
            {
                var root = JsonDocument.Parse(message).RootElement;
                return !root.TryGetProperty("method", out _) &&
                       root.TryGetProperty("id", out var messageId) &&
                       IdText(messageId) == id.ToString() &&
                       (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _));
            })
            .ToList();

    /// <summary>
    /// Checks that the server sent no response to the cancelled request <paramref name="id"/>: MCP says the receiver of
    /// notifications/cancelled should not answer the request. Call it after the call ended on the server; it first gives
    /// a late response time to reach the output and makes a round trip (tools/list: ping does not exist on every revision).
    /// It reads the wire, not the client's call: the SDK client ends its own pending call when notifications/cancelled is
    /// sent for it, answered or not.
    /// </summary>
    public async Task AssertNoResponseSentAsync(RequestId id)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        using (var timeout = new CancellationTokenSource(StepTimeout))
        {
            await Client.ListToolsAsync(cancellationToken: timeout.Token);
        }

        ResponsesTo(id).Should().BeEmpty("the server must not answer a request the client cancelled (request {0})", id);
    }

    private static string? IdText(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();

    /// <summary>The server's services, for checking what it did.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Ends the server's input stream, as an MCP client does when it closes the server's stdin. The SDK then
    /// ends the session and stops the application.
    /// </summary>
    public Task CloseServerInputAsync() => _serverInput.CompleteAsync().AsTask();

    /// <summary>
    /// Starts the server with <paramref name="tools"/> and connects a client to it.
    /// </summary>
    /// <param name="tools">Tools to register.</param>
    /// <param name="configure">Further MCP registrations, for example filters or tool classes.</param>
    /// <param name="configureLogging">
    /// Log filters to apply. By default every category is captured from Information up.
    /// </param>
    /// <param name="clientOptions">
    /// The client's options, for example an elicitation handler that answers for the user. By default the
    /// client declares no capabilities.
    /// </param>
    /// <param name="configureServices">Further service registrations, for example hosted services under test.</param>
    public static async Task<InProcessMcpServer> StartAsync(
        IEnumerable<McpServerTool> tools,
        Action<IMcpServerBuilder>? configure = null,
        Action<ILoggingBuilder>? configureLogging = null,
        McpClientOptions? clientOptions = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var serverOutput = new MessageRecordingStream(serverToClient.Writer.AsStream());
        var logs = new CapturingLoggerProvider();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        configureLogging?.Invoke(builder.Logging);

        var mcp = builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "in-process-test", Version = "1.0.0" })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverOutput)
            .WithTools(tools);
        configure?.Invoke(mcp);
        configureServices?.Invoke(builder.Services);

        var host = builder.Build();
        try
        {
            using (var startTimeout = new CancellationTokenSource(StepTimeout))
            {
                await host.StartAsync(startTimeout.Token);
            }

            using var connectTimeout = new CancellationTokenSource(StepTimeout);
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                clientOptions,
                cancellationToken: connectTimeout.Token);

            return new InProcessMcpServer(host, client, logs, clientToServer.Writer, serverOutput);
        }
        catch
        {
            // Don't leak a started host (and its stream transport) when start-up fails. The original
            // exception is what the test needs to see, so a failure while stopping is ignored.
            try
            {
                using var stopTimeout = new CancellationTokenSource(StepTimeout);
                await host.StopAsync(stopTimeout.Token);
            }
            catch
            {
                // Best effort.
            }
            finally
            {
                host.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Calls a tool and gives up after <paramref name="timeout"/> (default <see cref="DefaultCallTimeout"/>)
    /// with a <see cref="TimeoutException"/>, so a tool or filter that never returns fails the test
    /// instead of hanging the run. Cancelling the call also cancels it on the server.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultCallTimeout;
        using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callTimeout.CancelAfter(limit);

        try
        {
            return await Client.CallToolAsync(toolName, arguments, cancellationToken: callTimeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && callTimeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The call to tool '{toolName}' did not finish within {limit.TotalMilliseconds:0} ms.", ex);
        }
    }

    /// <summary>
    /// Disposes the client, then stops and disposes the host. Each step runs even if an earlier one
    /// throws, so a failing test does not leave a running host behind.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
        }
        finally
        {
            try
            {
                using var stopTimeout = new CancellationTokenSource(StepTimeout);
                await _host.StopAsync(stopTimeout.Token);
            }
            finally
            {
                _host.Dispose();
            }
        }
    }
}

/// <summary>
/// The server's output stream, recording each newline-delimited JSON-RPC message the server writes.
/// </summary>
internal sealed class MessageRecordingStream(Stream inner) : Stream
{
    private readonly object _lock = new();
    private readonly StringBuilder _pending = new();
    private readonly List<string> _messages = [];

    // Keeps the bytes of a character that a write splits from one write to the next
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_lock)
            {
                return _messages.ToList();
            }
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Record(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Record(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Record(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
            var count = _decoder.GetChars(bytes, chars, flush: false);
            _pending.Append(chars, 0, count);
            var text = _pending.ToString();
            var end = text.LastIndexOf('\n');
            if (end < 0)
            {
                return;
            }

            _messages.AddRange(text[..end].Split('\n').Select(m => m.TrimEnd('\r')).Where(m => m.Length > 0));
            _pending.Clear().Append(text[(end + 1)..]);
        }
    }
}

/// <summary>
/// Collects log entries so tests can assert what the server logged.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(new CapturedLogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }
}

public record CapturedLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
