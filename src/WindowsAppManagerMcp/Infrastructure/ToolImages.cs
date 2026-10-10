using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Builds the MCP result of a capture tool: an image content block the model can see, followed by a
/// text block with the capture's metadata as JSON. This is the only place that creates image content,
/// so an SDK change to <see cref="ImageContentBlock"/> (later SDK versions take the bytes instead of a
/// base64 string) is made here and nowhere else.
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
    /// serialized with the options the SDK uses for ordinary tool results, so it has the same shape
    /// (camelCase, nulls left out) as the JSON of every other tool.
    /// </summary>
    public static CallToolResult Create<TMetadata>(TMetadata metadata, EncodedImage? image, bool isError)
    {
        var content = new List<ContentBlock>(2);
        if (image != null)
        {
            content.Add(new ImageContentBlock
            {
                Data = Convert.ToBase64String(image.Data),
                MimeType = image.MimeType
            });
        }

        content.Add(new TextContentBlock
        {
            Text = JsonSerializer.Serialize(metadata, McpJsonUtilities.DefaultOptions)
        });

        return new CallToolResult
        {
            Content = content,
            IsError = isError
        };
    }
}
