using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Builds the MCP result of a capture tool: an image content block the model can see, followed by a
/// text block with the capture's metadata as JSON. This is the only place that creates image content,
/// so an SDK change to <see cref="ImageContentBlock"/> is made here and nowhere else (SDK 2.x took the
/// image bytes through <see cref="ImageContentBlock.FromBytes"/> where 0.5 took a base64 string).
/// </summary>
public static class ToolImages
{
    /// <summary>
    /// The tool result for <paramref name="capture"/>: [image, metadata] when it carries an image,
    /// otherwise [metadata] alone. isError is true when the capture failed.
    /// </summary>
    public static CallToolResult ToCallToolResult<TMetadata>(ScreenCapture<TMetadata> capture)
        where TMetadata : ICaptureMetadata
    {
        ArgumentNullException.ThrowIfNull(capture);
        return Create(capture.Metadata, capture.Image, isError: !capture.Metadata.Success);
    }

    /// <summary>
    /// A tool result with an optional image block and a metadata text block. The metadata is
    /// serialized with the options the SDK uses for ordinary tool results and the relaxed encoder
    /// (<see cref="ToolJson.SdkResultOptions"/>), so it has the same shape (camelCase, nulls left out)
    /// as the JSON of every other tool. A tool whose result carries fixed-value enums passes
    /// <see cref="ToolJson.SerializerOptions"/>, the options the server's typed results are serialized
    /// with (get_desktop_state, element_action).
    /// </summary>
    public static CallToolResult Create<TMetadata>(TMetadata metadata, EncodedImage? image, bool isError, JsonSerializerOptions? options = null)
    {
        var content = new List<ContentBlock>(2);
        if (image != null)
        {
            // FromBytes takes the encoded image file itself; the SDK base64-encodes it when the result is sent
            content.Add(ImageContentBlock.FromBytes(image.Data, image.MimeType));
        }

        content.Add(new TextContentBlock
        {
            Text = JsonSerializer.Serialize(metadata, options ?? ToolJson.SdkResultOptions)
        });

        return new CallToolResult
        {
            Content = content,
            IsError = isError
        };
    }
}
