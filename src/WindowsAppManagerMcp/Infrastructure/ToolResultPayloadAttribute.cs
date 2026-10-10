using ModelContextProtocol.Protocol;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Names the DTO that a tool returning <see cref="CallToolResult"/> serializes into its text block.
/// The contract tests use it to check that DTO the way they check the return type of other tools.
/// </summary>
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
