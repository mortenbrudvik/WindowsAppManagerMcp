using ModelContextProtocol;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// A deliberate tool error with a specific error code, for tools that return plain data and so
/// cannot report <c>success: false</c> in their result. The call returns <c>isError: true</c> with
/// this message and <see cref="ErrorCode"/> (see <see cref="ToolErrorReporting"/>).
/// </summary>
/// <remarks>
/// Like any <see cref="McpException"/>, the message is sent to the agent unchanged, so it must not
/// contain sensitive details.
/// </remarks>
public class ToolErrorException : McpException
{
    public ToolErrorException(string errorCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ErrorCode = errorCode;
    }

    /// <summary>The errorCode reported to the agent, for example a LayoutErrorCode name.</summary>
    public string ErrorCode { get; }
}
