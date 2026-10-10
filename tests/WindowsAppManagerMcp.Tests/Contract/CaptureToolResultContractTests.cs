using System.Drawing;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the capture tools return an MCP image content block the model can see, followed by a
/// text block with the metadata, through the real SDK pipeline. Runs the real ScreenshotTools and
/// ScreenshotService in an in-process MCP server; only the native capture wrapper, the monitors,
/// the window service and the UI Automation walk are mocked, so nothing touches the screen.
/// </summary>
public class CaptureToolResultContractTests
{
    private readonly Mock<IScreenCaptureWrapper> _captureWrapper =
        TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 1600, captureHeight: 900, windowOriginX: -1700, windowOriginY: 220);

    private readonly Mock<IMonitorService> _monitorService = TestDataFactory.CreateMockMonitorService(
        [TestDataFactory.CreateMonitorInfo(boundsX: -2560, boundsY: 365, boundsWidth: 2560, boundsHeight: 1440, scaleFactor: 1.5)]);

    private readonly Mock<IUIElementService> _uiElementService = TestDataFactory.CreateMockUIElementService();
    private readonly Mock<IWindowService> _windowService = TestDataFactory.CreateMockWindowService();

    [Fact]
    public async Task TakeScreenshot_ReturnsAnImageBlockThatMatchesTheMetadata()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("take_screenshot", new Dictionary<string, object?> { ["monitorIndex"] = 0 });

        // Assert
        result.IsError.Should().NotBe(true, "the call failed with: {0}", TextOf(result));
        result.Content.Should().HaveCount(2);
        var image = result.Content[0].Should().BeOfType<ImageContentBlock>().Subject;
        image.MimeType.Should().Be("image/jpeg");

        var metadata = Metadata(result);
        metadata.GetProperty("success").GetBoolean().Should().BeTrue();
        metadata.GetProperty("mimeType").GetString().Should().Be("image/jpeg");
        metadata.GetProperty("imageFormat").GetString().Should().Be("jpeg");
        metadata.GetProperty("coordinateSpace").GetString().Should().Be("physical-virtual-screen");
        metadata.TryGetProperty("imageData", out _).Should().BeFalse("the image is sent once, as image content");

        var bytes = Convert.FromBase64String(image.Data);
        metadata.GetProperty("byteSize").GetInt64().Should().Be(bytes.Length);
        DecodeSize(bytes).Should().Be((metadata.GetProperty("width").GetInt32(), metadata.GetProperty("height").GetInt32()));
        metadata.GetProperty("width").GetInt32().Should().Be(1568, "the default long edge is 1568");

        var region = metadata.GetProperty("capturedRegion");
        region.GetProperty("x").GetInt32().Should().Be(-2560);
        region.GetProperty("y").GetInt32().Should().Be(365);
        var transform = metadata.GetProperty("imageToScreen");
        transform.GetProperty("offsetX").GetInt32().Should().Be(-2560);
        transform.GetProperty("offsetY").GetInt32().Should().Be(365);
        transform.GetProperty("scaleX").GetDouble().Should().BeApproximately(2560.0 / 1568, 1e-6);
        metadata.GetProperty("originalWidth").GetInt32().Should().Be(2560);
        metadata.GetProperty("scaleFactor").GetDouble().Should().Be(1.5);
    }

    [Fact]
    public async Task CaptureWindow_WithoutFrame_ReportsTheClientOrigin()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("capture_window", new Dictionary<string, object?>
        {
            ["handle"] = 12345,
            ["includeFrame"] = false,
            ["maxDimension"] = 0,
        });

        // Assert
        result.IsError.Should().NotBe(true, "the call failed with: {0}", TextOf(result));
        _captureWrapper.Verify(c => c.CaptureWindow(new nint(12345), false), Times.Once);
        var metadata = Metadata(result);
        metadata.GetProperty("capturedRegion").GetProperty("x").GetInt32().Should().Be(-1700);
        metadata.GetProperty("capturedRegion").GetProperty("y").GetInt32().Should().Be(220);
        metadata.GetProperty("imageToScreen").GetProperty("scaleX").GetDouble().Should().Be(1);
        metadata.GetProperty("width").GetInt32().Should().Be(1600);
        metadata.GetProperty("captureMethod").GetString().Should().Be("PrintWindow");
    }

    [Fact]
    public async Task CaptureRegion_SmallRegion_IsPng()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("capture_region", new Dictionary<string, object?>
        {
            ["x"] = 10, ["y"] = 20, ["width"] = 300, ["height"] = 120,
        });

        // Assert
        result.Content[0].Should().BeOfType<ImageContentBlock>().Which.MimeType.Should().Be("image/png");
    }

    [Fact]
    public async Task CaptureRegion_WithAnUnknownFormat_ReturnsIsErrorWithInvalidFormat()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("capture_region", new Dictionary<string, object?>
        {
            ["x"] = 0, ["y"] = 0, ["width"] = 100, ["height"] = 100, ["format"] = "webp",
        });

        // Assert
        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>();
        var metadata = Metadata(result);
        metadata.GetProperty("success").GetBoolean().Should().BeFalse();
        metadata.GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.InvalidFormat));
        _captureWrapper.Verify(
            c => c.CaptureScreenRegion(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWithElements_ReturnsTheAnnotatedImageAndTheElementLegend()
    {
        // Arrange - one button inside the window captured at (-1700, 220)
        var button = new UIElementInfo("OK", "Button", "ok", new BoundsDto(-1600, 300, 80, 30), IsEnabled: true, IsOffscreen: false);
        _uiElementService.Setup(u => u.GetUIElements(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<CancellationToken>()))
            .Returns(TestDataFactory.CreateUIElementResult(elements: [button]));
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("capture_with_elements", new Dictionary<string, object?>
        {
            ["handle"] = 12345,
            ["maxDimension"] = 800,
        });

        // Assert
        result.IsError.Should().NotBe(true, "the call failed with: {0}", TextOf(result));
        result.Content.Select(c => c.Type).Should().Equal("image", "text");
        var metadata = Metadata(result);
        metadata.GetProperty("width").GetInt32().Should().Be(800);
        var element = metadata.GetProperty("elements").EnumerateArray().Should().ContainSingle().Subject;
        var imageBounds = element.GetProperty("imageBounds");
        imageBounds.GetProperty("x").GetInt32().Should().Be(50, "(-1600 - -1700) / 2");
        imageBounds.GetProperty("y").GetInt32().Should().Be(40, "(300 - 220) / 2");
        element.GetProperty("screenBounds").GetProperty("x").GetInt32().Should().Be(-1600);
    }

    [Fact]
    public async Task LegacyArguments_SaveToFileAndMaxWidth_StillWriteAFileWithoutImageContent()
    {
        // Arrange
        await using var server = await StartServerAsync();
        string? filePath = null;

        try
        {
            // Act
            var result = await server.CallToolAsync("capture_window", new Dictionary<string, object?>
            {
                ["handle"] = 12345,
                ["saveToFile"] = true,
                ["maxWidth"] = 400,
            });

            // Assert
            result.IsError.Should().NotBe(true, "the call failed with: {0}", TextOf(result));
            result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>();
            var metadata = Metadata(result);
            filePath = metadata.GetProperty("filePath").GetString();
            File.Exists(filePath).Should().BeTrue();
            metadata.GetProperty("width").GetInt32().Should().Be(400);
            metadata.GetProperty("imageToScreen").GetProperty("scaleX").GetDouble().Should().Be(4);
        }
        finally
        {
            if (filePath != null && File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Theory]
    [InlineData("take_screenshot")]
    [InlineData("capture_region")]
    [InlineData("capture_window")]
    [InlineData("capture_with_elements")]
    public async Task ToolsList_AdvertisesTheNewAndTheLegacyCaptureOptions(string toolName)
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var tools = await server.Client.ListToolsAsync();

        // Assert
        var schema = tools.Single(t => t.Name == toolName).JsonSchema;
        var properties = schema.GetProperty("properties");
        foreach (var name in new[] { "format", "quality", "maxDimension", "output", "outputPath", "overwrite", "saveToFile", "maxWidth" })
        {
            properties.TryGetProperty(name, out _).Should().BeTrue("{0} accepts '{1}'", toolName, name);
        }

        properties.GetProperty("format").GetProperty("default").GetString().Should().Be("auto");
        properties.GetProperty("output").GetProperty("default").GetString().Should().Be("image");
    }

    [Theory]
    [InlineData("take_screenshot")]
    [InlineData("capture_region")]
    [InlineData("capture_window")]
    [InlineData("capture_with_elements")]
    public void Description_TellsClientsWithoutTheSkillHowToConvertImagePoints(string toolName)
    {
        // tools/list is all that clients without the plugin skill see.
        var description = ServerToolCatalog.Description(toolName);

        description.Should().Contain("image content");
        description.Should().Contain("screenX = imageToScreen.offsetX + imageX * imageToScreen.scaleX");
        description.Should().Contain("never multiply by it", "scaleFactor invites a wrong DPI multiply");
        description.Should().NotContainEquivalentOf("base64");
    }

    private Task<InProcessMcpServer> StartServerAsync()
    {
        var screenshotService = new ScreenshotService(_captureWrapper.Object, _monitorService.Object);
        var tools = new ScreenshotTools(screenshotService, _uiElementService.Object, _windowService.Object);
        return InProcessMcpServer.StartAsync([], builder => builder.AddToolErrorReporting().WithTools(tools));
    }

    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static JsonElement Metadata(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    private static (int Width, int Height) DecodeSize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var bitmap = new Bitmap(stream);
        return (bitmap.Width, bitmap.Height);
    }
}
