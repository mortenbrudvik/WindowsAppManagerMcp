using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Reports exceptions thrown while a tool runs back to the agent as an error result
/// (isError=true) with a JSON body that carries an errorCode:
/// <list type="bullet">
/// <item><see cref="ToolErrorCode"/> (or the code of a <see cref="ToolErrorException"/>): the tool
/// threw an <see cref="McpException"/> on purpose. Its message is passed on unchanged, as the SDK does.</item>
/// <item><see cref="InvalidArgumentsErrorCode"/>: the arguments could not be bound to the tool's
/// parameters (a required argument is missing, or a value has the wrong type).</item>
/// <item><see cref="ErrorCode"/>: anything else, including a result that cannot be serialized.</item>
/// </list>
/// Without this, the MCP SDK replaces every exception that is not an <see cref="McpException"/>
/// with "An error occurred invoking '&lt;tool&gt;'.", so the agent cannot tell what failed or
/// whether a retry makes sense.
/// <para>
/// The same filter logs every tool call in the <see cref="CallLoggerCategory"/> category: one line
/// when the call starts and one when it ends, with the tool name, the duration and the outcome
/// (<c>ok</c>, <c>isError &lt;errorCode&gt;</c>, <c>cancelled</c>, <c>protocolError</c> or
/// <c>unhandled &lt;exception type&gt;</c>). A call that takes at least
/// <see cref="SlowCallThreshold"/> ends with a Warning instead of an Information line. Argument
/// values and result content are never logged: they can hold typed text or page content.
/// </para>
/// </summary>
public static class ToolErrorReporting
{
    /// <summary>The errorCode reported for an unexpected exception that escaped a tool.</summary>
    public const string ErrorCode = "UnhandledException";

    /// <summary>The errorCode reported for an <see cref="McpException"/> a tool threw on purpose.</summary>
    public const string ToolErrorCode = "ToolError";

    /// <summary>The errorCode reported when the call's arguments could not be bound to the tool's parameters.</summary>
    public const string InvalidArgumentsErrorCode = "InvalidArguments";

    /// <summary>The log category of the per-call lines (start, end, duration and outcome of every tool call).</summary>
    public const string CallLoggerCategory = "WindowsAppManagerMcp.ToolCalls";

    /// <summary>A call that runs at least this long is logged with a Warning when it ends.</summary>
    public static readonly TimeSpan SlowCallThreshold = TimeSpan.FromSeconds(10);

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
    /// Call-tool filter: turns an exception thrown by the rest of the pipeline into a
    /// <see cref="CallToolResult"/> with IsError=true and a JSON body with an errorCode. Unexpected
    /// exceptions are logged with their stack trace to stderr; deliberate tool errors and invalid
    /// arguments are expected outcomes and are only logged at Information level. Cancellation and
    /// protocol errors (e.g. an unknown tool name) pass through unchanged so the SDK keeps reporting
    /// them as JSON-RPC errors. Every call is also logged with its duration and outcome (see the
    /// class summary), with <see cref="SlowCallThreshold"/> as the slow-call threshold.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        CreateFilter(SlowCallThreshold)(next);

    /// <summary>
    /// Creates the filter with a different slow-call threshold. <see cref="Filter"/> is this filter with
    /// <see cref="SlowCallThreshold"/>; tests pass zero to check the slow-call Warning without waiting.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateFilter(TimeSpan slowCallThreshold) =>
        next => async (request, cancellationToken) =>
        {
            var call = new CallLog(request, slowCallThreshold);
            call.Started();

            try
            {
                var result = await next(request, cancellationToken).ConfigureAwait(false);
                call.Finished(result.IsError == true ? $"isError {ReportedErrorCode(result) ?? "(no errorCode)"}" : "ok");
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                call.Finished("cancelled");
                throw;
            }
            catch (McpProtocolException)
            {
                call.Finished("protocolError");
                throw;
            }
            catch (Exception ex)
            {
                var toolName = request.Params?.Name ?? "(unknown)";
                var exception = Unwrap(ex);
                var logger = CreateLogger(request.Services, LoggerCategory);

                if (exception is McpProtocolException)
                {
                    call.Finished("protocolError");
                    ExceptionDispatchInfo.Throw(exception);
                }

                if (exception is McpException deliberate)
                {
                    var errorCode = (deliberate as ToolErrorException)?.ErrorCode ?? ToolErrorCode;
                    logger.LogInformation(
                        "Tool '{ToolName}' returned error {ErrorCode}: {ErrorMessage}", toolName, errorCode, deliberate.Message);
                    call.Finished($"isError {errorCode}");
                    return CreateToolErrorResult(toolName, errorCode, deliberate.Message);
                }

                if (IsArgumentBindingFailure(exception))
                {
                    logger.LogInformation(
                        "Tool '{ToolName}' was called with invalid arguments: {ExceptionType}: {ExceptionMessage}",
                        toolName,
                        exception.GetType().FullName,
                        exception.Message);
                    call.Finished($"isError {InvalidArgumentsErrorCode}");
                    return CreateInvalidArgumentsResult(toolName, exception);
                }

                logger.LogError(
                    exception,
                    "Tool '{ToolName}' failed with {ExceptionType}: {ExceptionMessage}",
                    toolName,
                    exception.GetType().FullName,
                    exception.Message);
                call.Finished($"unhandled {exception.GetType().FullName}");

                return CreateErrorResult(toolName, exception);
            }
        };

    /// <summary>
    /// The errorCode of a result the tool itself marked isError, such as a failed capture: the
    /// "errorCode" property of its JSON text block. Only a short identifier is taken, so the log never
    /// receives result content. Null when there is none.
    /// </summary>
    private static string? ReportedErrorCode(CallToolResult result)
    {
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            try
            {
                using var document = JsonDocument.Parse(block.Text);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("errorCode", out var code) &&
                    code.ValueKind == JsonValueKind.String &&
                    code.GetString() is { Length: > 0 and <= 64 } value &&
                    value.All(char.IsAsciiLetterOrDigit))
                {
                    return value;
                }
            }
            catch (JsonException)
            {
                // Not JSON: no code to report
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the error result returned to the agent for an unexpected exception that escaped a tool.
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

        return CreateResult(new ToolErrorResultDto(
            Success: false,
            Error: $"Tool '{toolName}' failed. {error}",
            ErrorCode: ErrorCode,
            ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
            Tool: toolName));
    }

    /// <summary>
    /// Builds the error result for an error a tool reported on purpose: the message is passed on unchanged.
    /// </summary>
    public static CallToolResult CreateToolErrorResult(string toolName, string errorCode, string message)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(errorCode);
        ArgumentNullException.ThrowIfNull(message);

        return CreateResult(new ToolErrorResultDto(
            Success: false,
            Error: message,
            ErrorCode: errorCode,
            ExceptionType: null,
            Tool: toolName));
    }

    private static CallToolResult CreateInvalidArgumentsResult(string toolName, Exception exception)
    {
        var message = exception.Message;

        // "... (Parameter 'arguments')" names the binder's own parameter, not the tool's
        if (exception is ArgumentException { ParamName: { } paramName })
        {
            var suffix = $" (Parameter '{paramName}')";
            if (message.EndsWith(suffix, StringComparison.Ordinal))
            {
                message = message[..^suffix.Length];
            }
        }

        return CreateResult(new ToolErrorResultDto(
            Success: false,
            Error: $"Invalid arguments for tool '{toolName}'. {message} " +
                   "Check the argument names and types against the tool's input schema.",
            ErrorCode: InvalidArgumentsErrorCode,
            ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
            Tool: toolName));
    }

    private static CallToolResult CreateResult(ToolErrorResultDto payload) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, ErrorJsonOptions) }]
    };

    /// <summary>
    /// True when the exception was thrown while the SDK bound the call's arguments to the tool's
    /// parameters, before the tool ran: a missing required argument (ArgumentException) or a value
    /// that cannot be converted to the parameter type (JsonException). The same exception types
    /// thrown by the tool's own code are not argument errors, so the decision is made by where the
    /// exception was thrown: the first frame outside the runtime, System.Text.Json and the shared
    /// throw helpers must belong to <see cref="AIFunctionFactory"/>, which binds the arguments.
    /// </summary>
    private static bool IsArgumentBindingFailure(Exception exception)
    {
        if (exception is not (ArgumentException or JsonException))
        {
            return false;
        }

        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type == null)
            {
                continue;
            }

            var ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("System", StringComparison.Ordinal) ||
                ns.StartsWith("Microsoft.Shared", StringComparison.Ordinal))
            {
                continue;
            }

            while (type.DeclaringType != null)
            {
                type = type.DeclaringType;
            }

            return type == typeof(AIFunctionFactory);
        }

        return false;
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

    private static ILogger CreateLogger(IServiceProvider? services, string category) =>
        services?.GetService<ILoggerFactory>()?.CreateLogger(category) ?? NullLogger.Instance;

    /// <summary>
    /// The start and end lines of one tool call. Only the tool name, the JSON-RPC request id, the
    /// duration and the outcome are logged; never argument values or result content.
    /// </summary>
    private sealed class CallLog(RequestContext<CallToolRequestParams> request, TimeSpan slowCallThreshold)
    {
        private readonly ILogger _logger = CreateLogger(request.Services, CallLoggerCategory);
        private readonly string _toolName = request.Params?.Name ?? "(unknown)";
        private readonly string _requestId = request.JsonRpcRequest?.Id.ToString() ?? string.Empty;
        private readonly Stopwatch _stopwatch = new();

        public void Started()
        {
            _stopwatch.Start();
            _logger.LogInformation("Tool call '{ToolName}' (request {RequestId}) started", _toolName, _requestId);
        }

        public void Finished(string outcome)
        {
            var elapsed = _stopwatch.Elapsed;
            var durationMs = (long)elapsed.TotalMilliseconds;

            if (elapsed >= slowCallThreshold)
            {
                _logger.LogWarning(
                    "Tool call '{ToolName}' (request {RequestId}) finished in {DurationMs} ms: {Outcome}. " +
                    "Slow call: it took at least {SlowCallThresholdMs} ms",
                    _toolName, _requestId, durationMs, outcome, (long)slowCallThreshold.TotalMilliseconds);
                return;
            }

            _logger.LogInformation(
                "Tool call '{ToolName}' (request {RequestId}) finished in {DurationMs} ms: {Outcome}",
                _toolName, _requestId, durationMs, outcome);
        }
    }
}

/// <summary>
/// Error result returned to the agent when a tool throws. Matches the success/error/errorCode
/// shape of the regular tool results. ExceptionType is left out for deliberate tool errors.
/// </summary>
public record ToolErrorResultDto(
    bool Success,
    string Error,
    string ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExceptionType,
    string Tool
);
