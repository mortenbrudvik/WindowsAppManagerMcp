using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// ToolImages builds the capture tools' MCP results: an image block followed by a metadata text
/// block, with isError set from the metadata.
/// </summary>
public class ToolImagesTests
{
    [Fact]
    public void ToCallToolResult_WithImage_PutsTheImageFirstAndTheMetadataSecond()
    {
        // Arrange
        var image = new EncodedImage([1, 2, 3, 250], "image/png");
        var capture = new ScreenCapture<ScreenshotResult>(TestDataFactory.CreateScreenshotResult(imageFormat: "png"), image);

        // Act
        var result = ToolImages.ToCallToolResult(capture);

        // Assert
        result.IsError.Should().BeFalse();
        result.Content.Should().HaveCount(2);
        var imageBlock = result.Content[0].Should().BeOfType<ImageContentBlock>().Subject;
        imageBlock.Type.Should().Be("image");
        imageBlock.MimeType.Should().Be("image/png");
        imageBlock.DecodedData.ToArray().Should().Equal(image.Data);
        result.Content[1].Should().BeOfType<TextContentBlock>();
    }

    [Fact]
    public void ToCallToolResult_WithImage_SendsTheImageAsBase64Once()
    {
        // Arrange - the image file's bytes go to the SDK as they are; on the wire MCP carries them as base64
        var image = new EncodedImage([1, 2, 3, 250], "image/png");
        var capture = new ScreenCapture<ScreenshotResult>(TestDataFactory.CreateScreenshotResult(imageFormat: "png"), image);

        // Act
        var wire = JsonSerializer.SerializeToElement(ToolImages.ToCallToolResult(capture), McpJsonUtilities.DefaultOptions);

        // Assert
        var block = wire.GetProperty("content")[0];
        block.GetProperty("type").GetString().Should().Be("image");
        block.GetProperty("data").GetString().Should().Be(Convert.ToBase64String(image.Data), "the bytes are base64-encoded exactly once");
        block.GetProperty("mimeType").GetString().Should().Be("image/png");
    }

    [Fact]
    public void ToCallToolResult_WithoutImage_ReturnsOnlyTheMetadata()
    {
        // Arrange
        var capture = new ScreenCapture<ScreenshotResult>(TestDataFactory.CreateScreenshotResult(), null);

        // Act
        var result = ToolImages.ToCallToolResult(capture);

        // Assert
        result.IsError.Should().BeFalse();
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>();
    }

    [Fact]
    public void ToCallToolResult_ForAFailedCapture_SetsIsError()
    {
        // Arrange
        var failure = TestDataFactory.CreateScreenshotResult(success: false, error: "boom", errorCode: "CaptureFailed");

        // Act
        var result = ToolImages.ToCallToolResult(new ScreenCapture<ScreenshotResult>(failure, null));

        // Assert
        result.IsError.Should().BeTrue();
        var json = Json(result);
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("error").GetString().Should().Be("boom");
        json.GetProperty("errorCode").GetString().Should().Be("CaptureFailed");
    }

    [Fact]
    public void Metadata_IsCamelCaseJsonWithoutNullMembers()
    {
        // Arrange - FilePath, OriginalWidth and Error are null on this result
        var capture = new ScreenCapture<ScreenshotResult>(TestDataFactory.CreateScreenshotResult(regionX: -100, regionY: 50), null);

        // Act
        var json = Json(ToolImages.ToCallToolResult(capture));

        // Assert
        json.GetProperty("imageToScreen").GetProperty("offsetX").GetInt32().Should().Be(-100);
        json.GetProperty("capturedRegion").GetProperty("y").GetInt32().Should().Be(50);
        json.GetProperty("coordinateSpace").GetString().Should().Be("physical-virtual-screen");
        json.TryGetProperty("filePath", out _).Should().BeFalse();
        json.TryGetProperty("error", out _).Should().BeFalse();
        json.TryGetProperty("originalWidth", out _).Should().BeFalse();
        json.TryGetProperty("ImageToScreen", out _).Should().BeFalse("member names are camelCase");
    }

    [Fact]
    public void Metadata_KeepsNonAsciiTextAndQuotesReadable()
    {
        // Arrange - a window title in Cyrillic with quotes, as an error message carries it
        var failure = TestDataFactory.CreateScreenshotResult(success: false, error: "Окно 'Отчёт' <закрыто>", errorCode: "CaptureFailed");

        // Act
        var text = ToolImages.ToCallToolResult(new ScreenCapture<ScreenshotResult>(failure, null)).Content.OfType<TextContentBlock>().Single().Text;

        // Assert - the relaxed encoder of every tool result, not six-byte \uXXXX escapes
        text.Should().Contain("Окно 'Отчёт' <закрыто>");
    }

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;
}
