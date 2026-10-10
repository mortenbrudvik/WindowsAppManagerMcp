using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: when a tool throws, the agent gets an error result (isError=true) that names the
/// exception type and message, instead of the SDK's opaque "An error occurred invoking '&lt;tool&gt;'.".
/// Runs a real MCP client and server in-process over in-memory pipes with fake tools only.
/// </summary>
public class ToolErrorReportingTests
{
    private const string ThrowingTool = "throwing_tool";
    private const string UnserializableTool = "unserializable_tool";
    private const string EchoTool = "echo_tool";

    [Fact]
    public async Task ToolThatThrows_ReturnsErrorResultWithExceptionTypeAndMessage()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync(ThrowingTool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.ErrorCode);
        payload.GetProperty("exceptionType").GetString().Should().Be("System.InvalidOperationException");
        payload.GetProperty("tool").GetString().Should().Be(ThrowingTool);
        payload.GetProperty("error").GetString().Should()
            .Contain("InvalidOperationException").And.Contain("No monitors found");
    }

    [Fact]
    public async Task ToolWhoseResultCannotBeSerialized_ReturnsErrorResultWithSerializerMessage()
    {
        // Arrange - the same failure mode launch_browser_with_debug had with its nint? handle.
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync(UnserializableTool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("exceptionType").GetString().Should().Be("System.NotSupportedException");
        payload.GetProperty("error").GetString().Should().Contain("System.IntPtr");
    }

    [Fact]
    public async Task ToolThatThrows_IsLoggedWithTheException()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.Client.CallToolAsync(ThrowingTool);

        // Assert
        server.Logs.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Exception is InvalidOperationException &&
            e.Message.Contains(ThrowingTool));
    }

    [Fact]
    public async Task ToolThatSucceeds_ResultIsUnchanged()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync(
            EchoTool, new Dictionary<string, object?> { ["text"] = "hello" });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("hello");
    }

    [Fact]
    public async Task ToolCalledWithoutRequiredArgument_ReturnsErrorNamingTheParameter()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync(EchoTool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("exceptionType").GetString().Should().Be("System.ArgumentException");
        payload.GetProperty("error").GetString().Should().Contain("'text'");
    }

    [Fact]
    public async Task ErrorText_IsReadableWithoutUnicodeEscapes()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync(ThrowingTool);

        // Assert - the agent reads the raw text, so quotes must not be escaped as unicode sequences.
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("Tool 'throwing_tool' failed.");
    }

    [Fact]
    public async Task UnknownTool_StaysAProtocolError()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var act = async () => await server.Client.CallToolAsync("no_such_tool");

        // Assert
        await act.Should().ThrowAsync<McpProtocolException>();
    }

    [Fact]
    public void AddToolErrorReporting_RegistersACallToolFilter()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddMcpServer();

        // Act
        builder.AddToolErrorReporting();

        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.Filters.CallToolFilters.Should().ContainSingle();
    }

    private static Task<InProcessMcpServer> StartServerAsync() =>
        InProcessMcpServer.StartAsync(
            [
                McpServerTool.Create(Throws, new McpServerToolCreateOptions { Name = ThrowingTool }),
                McpServerTool.Create(ReturnsHandle, new McpServerToolCreateOptions { Name = UnserializableTool }),
                McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = EchoTool }),
            ],
            builder => builder.AddToolErrorReporting());

    private static JsonElement ParsePayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Throws() => throw new InvalidOperationException("No monitors found");

    private static HandleResult ReturnsHandle() => new(new nint(42));

    private static string Echo(string text) => text;

    public record HandleResult(nint? Handle);
}
