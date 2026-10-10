using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The in-process MCP test server must fail fast instead of hanging the run, and must not leave a
/// running host behind when start-up or shutdown fails. Fake tools and in-memory pipes only.
/// </summary>
public class InProcessMcpServerTests
{
    private const string EchoTool = "echo_tool";
    private const string HangingTool = "hanging_tool";

    [Fact]
    public async Task CallToolAsync_ReturnsTheToolResult()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(EchoTool, new Dictionary<string, object?> { ["text"] = "hello" });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("hello");
    }

    [Fact]
    public async Task CallToolAsync_WhenTheToolNeverReturns_ThrowsTimeoutException()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var act = async () => await server.CallToolAsync(HangingTool, timeout: TimeSpan.FromMilliseconds(200));

        // Assert
        (await act.Should().ThrowAsync<TimeoutException>()).WithMessage($"*'{HangingTool}'*200 ms*");
    }

    [Fact]
    public async Task CallToolAsync_WhenTheCallerCancels_ThrowsOperationCanceled()
    {
        // Arrange
        await using var server = await StartServerAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        var act = async () => await server.CallToolAsync(HangingTool, cancellationToken: cancellation.Token);

        // Assert - the caller's own cancellation is not reported as a timeout
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StartAsync_WhenStartUpFails_DisposesTheHost()
    {
        // Arrange - a hosted service that starts, then one that fails to start
        var tracker = new DisposalTracker();

        // Act
        var act = async () => await InProcessMcpServer.StartAsync(
            [],
            builder =>
            {
                builder.Services.AddSingleton(_ => tracker);
                builder.Services.AddHostedService(sp => new StartedService(sp.GetRequiredService<DisposalTracker>()));
                builder.Services.AddHostedService<FailingStartService>();
            });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("start failed");
        tracker.Stopped.Should().BeTrue("the services that did start are stopped");
        tracker.Disposed.Should().BeTrue("the host and its services are disposed");
    }

    [Fact]
    public async Task DisposeAsync_WhenStoppingFails_StillDisposesTheHost()
    {
        // Arrange
        var tracker = new DisposalTracker();
        var server = await InProcessMcpServer.StartAsync(
            [],
            builder =>
            {
                builder.Services.AddSingleton(_ => tracker);
                builder.Services.AddHostedService(sp => new FailingStopService(sp.GetRequiredService<DisposalTracker>()));
            });

        // Act
        var act = async () => await server.DisposeAsync();

        // Assert
        await act.Should().ThrowAsync<Exception>();
        tracker.Disposed.Should().BeTrue("the host is disposed even when StopAsync throws");
    }

    private static Task<InProcessMcpServer> StartServerAsync() =>
        InProcessMcpServer.StartAsync(
        [
            McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = EchoTool }),
            McpServerTool.Create(HangAsync, new McpServerToolCreateOptions { Name = HangingTool }),
        ]);

    private static string Echo(string text) => text;

    private static async Task<string> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return "unreachable";
    }

    private sealed class DisposalTracker : IDisposable
    {
        public bool Stopped { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class StartedService(DisposalTracker tracker) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            tracker.Stopped = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStartService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("start failed");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingStopService(DisposalTracker tracker) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            tracker.Stopped = true;
            throw new InvalidOperationException("stop failed");
        }
    }
}
