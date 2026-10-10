using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the UI Automation tools through the real MCP pipeline (argument binding, result
/// serialization). get_ui_elements runs the real UIElementService over an in-memory tree; the
/// element inspection service is mocked. No real window is read.
/// </summary>
public class UIElementToolContractTests
{
    private static readonly nint Handle = 0x1234;

    private readonly FakeUiaTree _tree = new();
    private readonly Mock<IElementInspectionService> _inspection = new();

    private Task<InProcessMcpServer> StartServerAsync()
    {
        var tools = new UIElementTools(new UIElementService(_tree, new UiaWorker()), _inspection.Object);
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

    [Fact]
    public async Task GetElementAtPoint_BindsNegativeCoordinatesAndSerializesTheSnapshot()
    {
        // Arrange
        var element = new UIElementDetails("Email", "Edit", "EmailBox", "TextBox", "WPF", new BoundsDto(-300, 10, 200, 24),
            IsEnabled: true, IsOffscreen: false, IsKeyboardFocusable: true, HasKeyboardFocus: false, IsPassword: false,
            Patterns: ["Value"], Value: "a@b.c");
        _inspection.Setup(s => s.GetElementAtPoint(-250, 20, 3, TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()))
            .Returns(new ElementAtPointResult(true, -250, 20, element,
                [new UIElementAncestor("Form", "Pane", null, "FormPane", null)], 0x5000, "Sign in", 42, "app", 1.5));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_element_at_point",
            new Dictionary<string, object?> { ["x"] = -250, ["y"] = 20 }));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("errorCode").GetString().Should().Be("None");
        json.GetProperty("windowHandle").GetInt64().Should().Be(0x5000);
        json.GetProperty("processName").GetString().Should().Be("app");
        var snapshot = json.GetProperty("element");
        snapshot.GetProperty("controlType").GetString().Should().Be("Edit");
        snapshot.GetProperty("value").GetString().Should().Be("a@b.c");
        snapshot.GetProperty("patterns")[0].GetString().Should().Be("Value");
        json.GetProperty("ancestors")[0].GetProperty("className").GetString().Should().Be("FormPane");
    }

    [Fact]
    public async Task GetFocusedElement_TakesNoRequiredArguments()
    {
        // Arrange
        _inspection.Setup(s => s.GetFocusedElement(TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()))
            .Returns(new FocusedElementResult(false, null, null, null, null, null, 0.5, "No element has keyboard focus.",
                UIElementErrorCode.ElementNotFound));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_focused_element"));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("ElementNotFound");
    }
}
