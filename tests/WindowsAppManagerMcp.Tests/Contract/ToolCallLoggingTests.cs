using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the call-tool filter logs one line when each tool call starts and one when it ends,
/// with the tool name, the duration and the outcome, in the WindowsAppManagerMcp.ToolCalls category.
/// The server's log levels keep that category at Information while everything else stays at Warning,
/// and argument values and result content never reach the log.
/// Runs a real MCP client and server in-process over in-memory pipes with fake tools only.
/// </summary>
public class ToolCallLoggingTests
{
    private const string EchoTool = "echo_tool";
    private const string ThrowingTool = "throwing_tool";
    private const string CodedErrorTool = "coded_error_tool";
    private const string DeliberateErrorTool = "deliberate_error_tool";
    private const string ResultErrorTool = "result_error_tool";
    private const string ResultErrorWithoutCodeTool = "result_error_without_code_tool";
    private const string SecretArgument = "secret typed text 4711";
    private const string SecretToolMessage = "secret page content 0815";

    [Fact]
    public async Task SuccessfulCall_LogsStartAndOkWithDuration()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(EchoTool, new Dictionary<string, object?> { ["text"] = "hello" });

        // Assert
        var lines = CallLines(server);
        lines.Should().HaveCount(2);
        lines[0].Level.Should().Be(LogLevel.Information);
        lines[0].Message.Should().Contain($"'{EchoTool}'").And.Contain("started");
        lines[1].Level.Should().Be(LogLevel.Information);
        lines[1].Message.Should().MatchRegex($@"'{EchoTool}' \(request [^)]+\) finished in \d+ ms: ok$");
    }

    [Fact]
    public async Task Call_NeverLogsArgumentValuesOrResultContent()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act - the argument is echoed back, so it is also the result content
        await server.CallToolAsync(EchoTool, new Dictionary<string, object?> { ["text"] = SecretArgument });
        await server.CallToolAsync(DeliberateErrorTool);

        // Assert
        CallLines(server).Should().HaveCount(4)
            .And.OnlyContain(e => !e.Message.Contains(SecretArgument) && !e.Message.Contains(SecretToolMessage));
    }

    [Fact]
    public async Task ToolErrorException_LogsIsErrorWithItsErrorCode()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(CodedErrorTool);

        // Assert
        Finished(server).Message.Should().EndWith(": isError PresetNotFound");
    }

    [Fact]
    public async Task ResultMarkedIsErrorByTheTool_LogsTheErrorCodeFromItsJson()
    {
        // Arrange - the capture tools return isError results with the code inside the metadata JSON
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(ResultErrorTool);

        // Assert
        Finished(server).Message.Should().EndWith(": isError CaptureFailed").And.NotContain(SecretToolMessage);
    }

    [Fact]
    public async Task ResultMarkedIsErrorWithoutAUsableCode_LogsNoErrorCode()
    {
        // Arrange - a code that is not an identifier is result content, not a code
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(ResultErrorWithoutCodeTool);

        // Assert
        Finished(server).Message.Should().EndWith(": isError (no errorCode)");
    }

    [Fact]
    public async Task DeliberateMcpException_LogsIsErrorToolError()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(DeliberateErrorTool);

        // Assert
        Finished(server).Message.Should().EndWith($": isError {ToolErrorReporting.ToolErrorCode}");
    }

    [Fact]
    public async Task InvalidArguments_LogsIsErrorInvalidArguments()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act - "text" is required
        await server.CallToolAsync(EchoTool);

        // Assert
        Finished(server).Message.Should().EndWith($": isError {ToolErrorReporting.InvalidArgumentsErrorCode}");
    }

    [Fact]
    public async Task UnexpectedException_LogsUnhandledWithTheExceptionType()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(ThrowingTool);

        // Assert
        Finished(server).Message.Should().EndWith(": unhandled System.InvalidOperationException");
    }

    [Fact]
    public async Task UnknownTool_LogsProtocolError()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var act = async () => await server.CallToolAsync("no_such_tool");

        // Assert
        await act.Should().ThrowAsync<McpProtocolException>();
        Finished(server).Message.Should().Contain("'no_such_tool'").And.EndWith(": protocolError");
    }

    [Fact]
    public async Task CancelledCall_LogsCancelled()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var logs = new CapturingLoggerProvider();
        using var services = new ServiceCollection().AddLogging(b => b.AddProvider(logs)).BuildServiceProvider();
        var handler = ToolErrorReporting.Filter((_, token) => throw new OperationCanceledException(token));

        // Act
        var act = async () => await handler(CreateRequest(services), cancelled.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        logs.Entries.Where(e => e.Category == ToolErrorReporting.CallLoggerCategory).Last()
            .Message.Should().EndWith(": cancelled");
    }

    [Fact]
    public async Task SlowCall_EndsWithAWarning()
    {
        // Arrange - with a threshold of zero, every call counts as slow
        await using var server = await InProcessMcpServer.StartAsync(
            Tools(),
            builder => builder.AddCallToolFilter(ToolErrorReporting.CreateFilter(TimeSpan.Zero)));

        // Act
        await server.CallToolAsync(EchoTool, new Dictionary<string, object?> { ["text"] = "hello" });

        // Assert
        var finished = Finished(server);
        finished.Level.Should().Be(LogLevel.Warning);
        finished.Message.Should().Contain(": ok").And.Contain("Slow call");
    }

    [Fact]
    public void SlowCallThreshold_IsTenSeconds()
    {
        ToolErrorReporting.SlowCallThreshold.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ServerLogLevels_EnableCallLinesAtInformationAndKeepEverythingElseAtWarning()
    {
        // Arrange
        using var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(new CapturingLoggerProvider()).AddWindowsAppManagerLogLevels())
            .BuildServiceProvider();
        var factory = services.GetRequiredService<ILoggerFactory>();

        // Act
        var callLogger = factory.CreateLogger(ToolErrorReporting.CallLoggerCategory);
        var errorLogger = factory.CreateLogger("WindowsAppManagerMcp.ToolErrors");
        var otherLogger = factory.CreateLogger("Microsoft.Hosting.Lifetime");
        var hostGuardLogger = factory.CreateLogger<HostGuard>();

        // Assert
        callLogger.IsEnabled(LogLevel.Information).Should().BeTrue();
        callLogger.IsEnabled(LogLevel.Debug).Should().BeFalse();
        hostGuardLogger.IsEnabled(LogLevel.Information).Should().BeTrue("the recorded agent host explains later refusals");
        hostGuardLogger.IsEnabled(LogLevel.Debug).Should().BeFalse();
        errorLogger.IsEnabled(LogLevel.Information).Should().BeFalse();
        errorLogger.IsEnabled(LogLevel.Warning).Should().BeTrue();
        otherLogger.IsEnabled(LogLevel.Information).Should().BeFalse();
        otherLogger.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task ServerLogLevels_LogEveryCallButNotTheInformationLinesOfOtherCategories()
    {
        // Arrange - the production log levels on top of the capturing provider
        await using var server = await StartServerAsync(logging => logging.AddWindowsAppManagerLogLevels());

        // Act - a deliberate tool error writes an Information line in the ToolErrors category
        await server.CallToolAsync(EchoTool, new Dictionary<string, object?> { ["text"] = "hello" });
        await server.CallToolAsync(CodedErrorTool);

        // Assert
        CallLines(server).Should().HaveCount(4);
        server.Logs.Entries.Should().NotContain(
            e => e.Category != ToolErrorReporting.CallLoggerCategory && e.Level < LogLevel.Warning);
    }

    [Fact]
    public async Task WithoutServerLogLevels_TheToolErrorsInformationLineIsCaptured()
    {
        // Arrange - counterpart of the test above: proves the capture would see that line
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(CodedErrorTool);

        // Assert
        server.Logs.Entries.Should().Contain(
            e => e.Category == "WindowsAppManagerMcp.ToolErrors" && e.Level == LogLevel.Information);
    }

    private static Task<InProcessMcpServer> StartServerAsync(Action<ILoggingBuilder>? configureLogging = null) =>
        InProcessMcpServer.StartAsync(Tools(), builder => builder.AddToolErrorReporting(), configureLogging);

    private static IEnumerable<McpServerTool> Tools() =>
    [
        McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = EchoTool }),
        McpServerTool.Create(Throws, new McpServerToolCreateOptions { Name = ThrowingTool }),
        McpServerTool.Create(ThrowsWithCode, new McpServerToolCreateOptions { Name = CodedErrorTool }),
        McpServerTool.Create(ThrowsDeliberately, new McpServerToolCreateOptions { Name = DeliberateErrorTool }),
        McpServerTool.Create(ReturnsError, new McpServerToolCreateOptions { Name = ResultErrorTool }),
        McpServerTool.Create(ReturnsErrorWithoutCode, new McpServerToolCreateOptions { Name = ResultErrorWithoutCodeTool }),
    ];

    private static List<CapturedLogEntry> CallLines(InProcessMcpServer server) =>
        server.Logs.Entries.Where(e => e.Category == ToolErrorReporting.CallLoggerCategory).ToList();

    private static CapturedLogEntry Finished(InProcessMcpServer server)
    {
        var lines = CallLines(server);
        lines.Should().HaveCount(2, "one line when the call starts and one when it ends");
        lines[0].Message.Should().EndWith("started");
        return lines[1];
    }

    private static string Echo(string text) => text;

    private static string Throws() => throw new InvalidOperationException(SecretToolMessage);

    private static string ThrowsWithCode() => throw new ToolErrorException("PresetNotFound", SecretToolMessage);

    private static string ThrowsDeliberately() => throw new McpException(SecretToolMessage);

    private static CallToolResult ReturnsError() => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $$"""{"success":false,"error":"{{SecretToolMessage}}","errorCode":"CaptureFailed"}""" }]
    };

    private static CallToolResult ReturnsErrorWithoutCode() => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $$"""{"success":false,"errorCode":"{{SecretToolMessage}}"}""" }]
    };

    private static RequestContext<CallToolRequestParams> CreateRequest(IServiceProvider services) =>
        new(new Mock<McpServer>().Object, new JsonRpcRequest { Method = RequestMethods.ToolsCall })
        {
            Params = new CallToolRequestParams { Name = "some_tool" },
            Services = services
        };
}
