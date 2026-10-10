using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the layout tools that return plain data (get_layout, list_layouts) report their
/// deliberate errors with the layout error code, not as UnhandledException. Runs the real
/// LayoutPresetTools through an in-process MCP server with the server's error reporting filter
/// and a mocked ILayoutService.
/// </summary>
public class LayoutToolErrorContractTests
{
    private readonly Mock<ILayoutService> _layoutService = new();

    [Fact]
    public async Task GetLayout_UnknownName_ReturnsPresetNotFound()
    {
        // Arrange
        _layoutService.Setup(l => l.GetPreset("typo")).Returns((LayoutPreset?)null);
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync(
            "get_layout", new Dictionary<string, object?> { ["name"] = "typo" });

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(LayoutErrorCode.PresetNotFound));
        payload.GetProperty("error").GetString().Should().Be(
            "Layout preset 'typo' was not found. Call list_layouts to see the available presets.");
        payload.GetProperty("tool").GetString().Should().Be("get_layout");
        server.Logs.Entries.Should().NotContain(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning,
            "an ordinary not-found must not be logged as a server error");
    }

    [Fact]
    public async Task ListLayouts_StorageUnavailable_ReturnsStorageUnavailable()
    {
        // Arrange
        _layoutService.Setup(l => l.GetStorageError())
            .Returns(@"The layouts folder 'C:\Locked\layouts' cannot be written. Set WAM_LAYOUTS_PATH to a writable folder.");
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync("list_layouts");

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(LayoutErrorCode.StorageUnavailable));
        payload.GetProperty("error").GetString().Should().Contain(@"C:\Locked\layouts").And.Contain("WAM_LAYOUTS_PATH");
    }

    [Fact]
    public async Task GetLayout_WithoutName_ReturnsInvalidArguments()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.Client.CallToolAsync("get_layout");

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain("'name'");
        _layoutService.Verify(l => l.GetPreset(It.IsAny<string>()), Times.Never);
    }

    private Task<InProcessMcpServer> StartServerAsync() =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .AddToolErrorReporting()
                .WithTools(new LayoutPresetTools(_layoutService.Object)));

    private static JsonElement ParsePayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }
}
