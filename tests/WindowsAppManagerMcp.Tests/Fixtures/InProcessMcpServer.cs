using System.Collections.Concurrent;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Hosts an MCP server and a connected MCP client in the test process, linked by in-memory pipes.
/// Requests go through the real SDK pipeline (argument binding, filters, result serialization),
/// but only the tools passed in are registered, so no native or desktop calls happen.
/// Every step has a timeout, so a broken pipeline fails the test instead of hanging the run.
/// </summary>
public sealed class InProcessMcpServer : IAsyncDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="CallToolAsync"/> waits for a tool call by default.</summary>
    public static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    private readonly IHost _host;

    private InProcessMcpServer(IHost host, McpClient client, CapturingLoggerProvider logs)
    {
        _host = host;
        Client = client;
        Logs = logs;
    }

    public McpClient Client { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <summary>
    /// Starts the server with <paramref name="tools"/> and connects a client to it.
    /// </summary>
    /// <param name="tools">Tools to register.</param>
    /// <param name="configure">Further MCP registrations, for example filters or tool classes.</param>
    /// <param name="configureLogging">
    /// Log filters to apply. By default every category is captured from Information up.
    /// </param>
    public static async Task<InProcessMcpServer> StartAsync(
        IEnumerable<McpServerTool> tools,
        Action<IMcpServerBuilder>? configure = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var logs = new CapturingLoggerProvider();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        configureLogging?.Invoke(builder.Logging);

        var mcp = builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "in-process-test", Version = "1.0.0" })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools(tools);
        configure?.Invoke(mcp);

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
                cancellationToken: connectTimeout.Token);

            return new InProcessMcpServer(host, client, logs);
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
