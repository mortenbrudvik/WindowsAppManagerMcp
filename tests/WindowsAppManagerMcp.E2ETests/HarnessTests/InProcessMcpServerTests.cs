using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The E2E tier can host the real MCP pipeline (argument binding, filters, result serialization) with the shared
/// <see cref="InProcessMcpServer"/>, which run_steps (roadmap M6.2) and the end-to-end runs of M7.6 need. These tests
/// register a fake tool only, over in-memory pipes: no native call, no desktop, no fixture.
/// </summary>
public class InProcessMcpServerTests
{
    private const string GreetTool = "greet_tool";

    [Fact]
    public async Task CallToolAsync_RunsTheToolThroughTheMcpPipeline()
    {
        // Arrange
        await using var server = await InProcessMcpServer.StartAsync(
            [McpServerTool.Create(Greet, new McpServerToolCreateOptions { Name = GreetTool })]);

        // Act
        var result = await server.CallToolAsync(GreetTool, new Dictionary<string, object?> { ["name"] = "fixture" });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("hello fixture");
    }

    [Fact]
    public async Task StartAsync_HandsTheToolTheServicesTheTestRegisters()
    {
        // Arrange - as the contract tests register the server's tool classes, and as a scenario will pass the
        // harness's guarded services to them: the SDK creates the tool class for each call from these registrations
        var greeting = new Greeting("guarded");
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.WithTools<GreetingTools>(),
            configureServices: services => services.AddSingleton(greeting));

        // Act
        var result = await server.CallToolAsync(GreetTool, new Dictionary<string, object?> { ["name"] = "fixture" });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("guarded fixture");
        server.Services.GetRequiredService<Greeting>().Should().BeSameAs(greeting);
    }

    private static string Greet(string name) => $"hello {name}";

    public sealed record Greeting(string Word);

    [McpServerToolType]
    public sealed class GreetingTools(Greeting greeting)
    {
        [McpServerTool(Name = GreetTool)]
        public string Greet(string name) => $"{greeting.Word} {name}";
    }
}
