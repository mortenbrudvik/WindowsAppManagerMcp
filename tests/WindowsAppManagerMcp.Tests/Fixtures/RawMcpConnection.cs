using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An in-process MCP server with a raw JSON-RPC connection instead of the SDK's client: the test writes each message
/// itself and reads the server's answers line by line, as a stdio client does. It covers what the SDK client cannot
/// send, such as an initialize request for a revision it does not know. In-memory pipes; nothing native runs unless the
/// test registers it.
/// </summary>
public sealed class RawMcpConnection : IAsyncDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    private readonly IHost _host;
    private readonly Stream _toServer;
    private readonly StreamReader _fromServer;

    private RawMcpConnection(IHost host, Stream toServer, StreamReader fromServer)
    {
        _host = host;
        _toServer = toServer;
        _fromServer = fromServer;
    }

    /// <summary>Starts a server with the registrations <paramref name="configure"/> adds.</summary>
    public static async Task<RawMcpConnection> StartAsync(Action<IMcpServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        configure(builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()));

        var host = builder.Build();
        using (var timeout = new CancellationTokenSource(StepTimeout))
        {
            await host.StartAsync(timeout.Token);
        }

        return new RawMcpConnection(
            host,
            clientToServer.Writer.AsStream(),
            new StreamReader(serverToClient.Reader.AsStream(), new UTF8Encoding(false)));
    }

    /// <summary>Sends one JSON-RPC message (one line).</summary>
    public async Task SendAsync(string json)
    {
        using var compact = JsonDocument.Parse(json);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(compact.RootElement) + "\n");
        using var timeout = new CancellationTokenSource(StepTimeout);
        await _toServer.WriteAsync(line, timeout.Token);
        await _toServer.FlushAsync(timeout.Token);
    }

    /// <summary>
    /// Reads the server's messages until the response to request <paramref name="id"/>, and returns it. Notifications and
    /// requests from the server on the way are skipped.
    /// </summary>
    public async Task<JsonElement> ReadResponseAsync(int id)
    {
        using var timeout = new CancellationTokenSource(StepTimeout);
        while (true)
        {
            var line = await _fromServer.ReadLineAsync(timeout.Token)
                       ?? throw new InvalidOperationException($"The server closed its output before it answered request {id}.");
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("method", out _) &&
                root.TryGetProperty("id", out var messageId) &&
                messageId.ValueKind == JsonValueKind.Number &&
                messageId.GetInt32() == id)
            {
                return root.Clone();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // The end of the input ends the session, as a closed stdin does
            await _toServer.DisposeAsync();
            using var timeout = new CancellationTokenSource(StepTimeout);
            await _host.StopAsync(timeout.Token);
        }
        finally
        {
            _fromServer.Dispose();
            _host.Dispose();
        }
    }
}
