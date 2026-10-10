using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Reports exceptions thrown while a tool runs back to the agent.
/// Without this, the MCP SDK replaces every exception that is not an <see cref="McpException"/>
/// with "An error occurred invoking '&lt;tool&gt;'.", so the agent cannot tell what failed or
/// whether a retry makes sense. This covers exceptions from the tool method itself, from
/// argument binding, and from serializing the tool's return value.
/// </summary>
public static class ToolErrorReporting
{
    /// <summary>The errorCode reported for an exception that escaped a tool.</summary>
    public const string ErrorCode = "UnhandledException";

    private const string LoggerCategory = "WindowsAppManagerMcp.ToolErrors";

    // The SDK's options plus relaxed escaping, so quotes in exception messages stay readable for
    // the agent instead of being escaped as unicode sequences. The text goes to an MCP client, not HTML.
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Registers <see cref="Filter"/> in the call-tool pipeline of the MCP server.
    /// </summary>
    public static IMcpServerBuilder AddToolErrorReporting(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCallToolFilter(Filter);
    }

    /// <summary>
    /// Call-tool filter: logs an exception thrown by the rest of the pipeline to stderr and turns it
    /// into a <see cref="CallToolResult"/> with IsError=true and a JSON body that names the exception
    /// type and message. Cancellation and protocol errors (e.g. an unknown tool name) pass through
    /// unchanged so the SDK keeps reporting them as JSON-RPC errors.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (McpProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var toolName = request.Params?.Name ?? "(unknown)";
                var exception = Unwrap(ex);

                CreateLogger(request.Services).LogError(
                    exception,
                    "Tool '{ToolName}' failed with {ExceptionType}: {ExceptionMessage}",
                    toolName,
                    exception.GetType().FullName,
                    exception.Message);

                return CreateErrorResult(toolName, exception);
            }
        };

    /// <summary>
    /// Builds the error result returned to the agent for an exception that escaped a tool.
    /// </summary>
    public static CallToolResult CreateErrorResult(string toolName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(exception);

        var error = $"{exception.GetType().Name}: {exception.Message}";
        if (exception.InnerException is { } inner)
        {
            error += $" ---> {inner.GetType().Name}: {inner.Message}";
        }

        var payload = new ToolErrorResultDto(
            Success: false,
            Error: $"Tool '{toolName}' failed. {error}",
            ErrorCode: ErrorCode,
            ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
            Tool: toolName);

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, ErrorJsonOptions) }]
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case TargetInvocationException { InnerException: { } inner }:
                    exception = inner;
                    break;
                case AggregateException aggregate when aggregate.InnerExceptions.Count == 1:
                    exception = aggregate.InnerExceptions[0];
                    break;
                default:
                    return exception;
            }
        }
    }

    private static ILogger CreateLogger(IServiceProvider? services) =>
        services?.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory) ?? NullLogger.Instance;
}

/// <summary>
/// Error result returned to the agent when a tool throws. Matches the success/error/errorCode
/// shape of the regular tool results.
/// </summary>
public record ToolErrorResultDto(
    bool Success,
    string Error,
    string ErrorCode,
    string ExceptionType,
    string Tool
);
