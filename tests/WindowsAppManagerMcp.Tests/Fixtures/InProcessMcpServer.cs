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
/// </summary>
public sealed class InProcessMcpServer : IAsyncDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;

    private InProcessMcpServer(IHost host, McpClient client, CapturingLoggerProvider logs)
    {
        _host = host;
        Client = client;
        Logs = logs;
    }

    public McpClient Client { get; }

    public CapturingLoggerProvider Logs { get; }

    public static async Task<InProcessMcpServer> StartAsync(
        IEnumerable<McpServerTool> tools,
        Action<IMcpServerBuilder>? configure = null)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var logs = new CapturingLoggerProvider();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        var mcp = builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "in-process-test", Version = "1.0.0" })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools(tools);
        configure?.Invoke(mcp);

        var host = builder.Build();
        using var startTimeout = new CancellationTokenSource(StepTimeout);
        await host.StartAsync(startTimeout.Token);

        using var connectTimeout = new CancellationTokenSource(StepTimeout);
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
            cancellationToken: connectTimeout.Token);

        return new InProcessMcpServer(host, client, logs);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        using var stopTimeout = new CancellationTokenSource(StepTimeout);
        await _host.StopAsync(stopTimeout.Token);
        _host.Dispose();
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
