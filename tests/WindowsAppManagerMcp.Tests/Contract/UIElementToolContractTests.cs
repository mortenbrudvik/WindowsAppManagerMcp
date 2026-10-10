using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the UI Automation tools through the real MCP pipeline (argument binding, result
/// serialization). get_ui_elements runs the real UIElementService over an in-memory tree. No real
/// window is read.
/// </summary>
public class UIElementToolContractTests
{
    private static readonly nint Handle = 0x1234;

    private readonly FakeUiaTree _tree = new();

    private Task<InProcessMcpServer> StartServerAsync()
    {
        var tools = new UIElementTools(new UIElementService(_tree, new UiaWorker()));
        return InProcessMcpServer.StartAsync([], builder => builder.WithTools(tools));
    }

    private static JsonElement Json(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.Should().NotBe(true, "the call failed with: {0}", text);
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task GetUIElements_WithControlTypes_ReturnsNestedMatchesOverMcp()
    {
        // Arrange - Buttons inside a Pane and a ToolBar
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("Pane", "Host").With(
                new FakeUiaNode("ToolBar", "Tools").With(
                    new FakeUiaNode("Button", "Save") { Bounds = UiaRect.Empty },
                    new FakeUiaNode("Button", "Open")))));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_ui_elements",
            new Dictionary<string, object?> { ["handle"] = (long)Handle, ["controlTypes"] = "Button" }));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().Equal("Save", "Open");
        json.GetProperty("totalElementCount").GetInt32().Should().Be(2);
        json.GetProperty("depthReached").GetInt32().Should().Be(3);
        json.GetProperty("truncated").GetBoolean().Should().BeFalse();
        json.TryGetProperty("truncationReason", out _).Should().BeFalse("null members are left out");
        json.GetProperty("elements")[0].TryGetProperty("bounds", out _).Should().BeFalse(
            "an element without geometry has no bounds instead of int.MinValue coordinates");
    }

    [Fact]
    public async Task GetUIElements_WithUnknownControlType_ReturnsInvalidControlTypeOverMcp()
    {
        // Arrange
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor"));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_ui_elements",
            new Dictionary<string, object?> { ["handle"] = (long)Handle, ["controlTypes"] = "Buton" }));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("InvalidControlType");
        json.GetProperty("error").GetString().Should().Contain("'Buton'").And.Contain("Button");
    }
}
