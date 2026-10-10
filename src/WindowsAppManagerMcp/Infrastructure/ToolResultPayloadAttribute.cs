using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Names the DTO that a tool returning <see cref="CallToolResult"/> serializes into its text block.
/// The contract tests use it to check that DTO the way they check the return type of other tools.
/// </summary>
/// <remarks>
/// It must be the type argument the tool passes to ToolImages (the TMetadata of
/// <c>ToolImages.ToCallToolResult(ScreenCapture&lt;TMetadata&gt;)</c> or <c>ToolImages.Create</c>),
/// on every path, failures included. Nothing checks that link by reflection: the contract tests trust
/// this attribute, and only CaptureToolResultContractTests deserializes the real text blocks of the
/// capture tools. Add such a round-trip test for a new tool that uses it.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolResultPayloadAttribute : Attribute
{
    public ToolResultPayloadAttribute(Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        PayloadType = payloadType;
    }

    /// <summary>The type serialized into the result's text block.</summary>
    public Type PayloadType { get; }
}
