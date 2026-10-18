using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: when a tool throws, the agent gets an error result (isError=true) that names the
/// exception type and message, instead of the SDK's opaque "An error occurred invoking '&lt;tool&gt;'.".
/// Runs a real MCP client and server in-process over in-memory pipes with fake tools only.
/// </summary>
public class ToolErrorReportingTests
{
    private const string ThrowingTool = "throwing_tool";
    private const string UnserializableTool = "unserializable_tool";
    private const string EchoTool = "echo_tool";
    private const string CountTool = "count_tool";
    private const string DeliberateErrorTool = "deliberate_error_tool";
    private const string CodedErrorTool = "coded_error_tool";
    private const string BodyArgumentExceptionTool = "body_argument_exception_tool";
    private const string BodyJsonExceptionTool = "body_json_exception_tool";
    private const string OutcomeTool = "outcome_tool";
    private const string ListTool = "list_tool";
    private const string BatchTool = "batch_tool";
    private const string NestedTool = "nested_tool";
    private const string PairTool = "pair_tool";

    [Fact]
    public async Task ToolThatThrows_ReturnsErrorResultWithExceptionTypeAndMessage()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(ThrowingTool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.ErrorCode);
        payload.GetProperty("exceptionType").GetString().Should().Be("System.InvalidOperationException");
        payload.GetProperty("tool").GetString().Should().Be(ThrowingTool);
        payload.GetProperty("error").GetString().Should()
            .Contain("InvalidOperationException").And.Contain("No monitors found");
    }

    [Fact]
    public async Task ToolWhoseResultCannotBeSerialized_ReturnsErrorResultWithSerializerMessage()
    {
        // Arrange - the same failure mode launch_browser_with_debug had with its nint? handle.
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(UnserializableTool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("exceptionType").GetString().Should().Be("System.NotSupportedException");
        payload.GetProperty("error").GetString().Should().Contain("System.IntPtr");
    }

    [Fact]
    public async Task ToolThatThrows_IsLoggedWithTheException()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(ThrowingTool);

        // Assert
        server.Logs.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Exception is InvalidOperationException &&
            e.Message.Contains(ThrowingTool));
    }

    [Fact]
    public async Task ToolThatSucceeds_ResultIsUnchanged()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(
            EchoTool, new Dictionary<string, object?> { ["text"] = "hello" });

        // Assert
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("hello");
    }

    [Fact]
    public async Task ToolThatReportsFailure_GoesOutWithIsError_AndItsBodyUnchanged()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(OutcomeTool, new Dictionary<string, object?> { ["success"] = false });

        // Assert - roadmap M2.1: a result that says success false is an error result, with the tool's own JSON body
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be("InvalidHandle");
        payload.GetProperty("newBounds").GetProperty("width").GetInt32().Should().Be(800);
        payload.TryGetProperty("tool", out _).Should().BeFalse("the tool's own result is passed on, not replaced");
    }

    [Fact]
    public async Task ToolThatReportsSuccess_HasNoIsError()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(OutcomeTool, new Dictionary<string, object?> { ["success"] = true });

        // Assert
        result.IsError.Should().NotBe(true);
        ParsePayload(result).GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ToolThatReturnsAListOrABatchSummary_HasNoIsError()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act - an empty list, and items that failed, are data: only a top-level success false is a failed call
        var list = await server.CallToolAsync(ListTool);
        var batch = await server.CallToolAsync(BatchTool);

        // Assert
        list.IsError.Should().NotBe(true);
        batch.IsError.Should().NotBe(true);
    }

    [Fact]
    public async Task ToolThatReportsFailure_IsLoggedWithItsErrorCode()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(OutcomeTool, new Dictionary<string, object?> { ["success"] = false });

        // Assert
        server.Logs.Entries.Should().Contain(e =>
            e.Category == ToolErrorReporting.CallLoggerCategory && e.Message.Contains("isError InvalidHandle"));
    }

    [Theory]
    [InlineData("""{"success":false,"errorCode":"X"}""", true)]
    [InlineData("""{"success":true}""", false)]
    [InlineData("""{"Success":false}""", false)]
    [InlineData("""{"success":"false"}""", false)]
    [InlineData("""[{"success":false}]""", false)]
    [InlineData("""{"results":[{"success":false}]}""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void ReportsFailure_IsTrueOnlyForATopLevelSuccessFalse(string text, bool expected)
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = text }] };

        ToolOutcome.ReportsFailure(result).Should().Be(expected);
    }

    [Fact]
    public void WithIsError_KeepsAnIsErrorTheToolSetItself()
    {
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = """{"success":true}""" }] };

        ToolOutcome.WithIsError(result).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ToolCalledWithoutRequiredArgument_ReturnsInvalidArgumentsNamingTheParameter()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(EchoTool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("exceptionType").GetString().Should().Be("System.ArgumentException");
        payload.GetProperty("error").GetString().Should().Be(
            $"Invalid arguments for tool '{EchoTool}'. The required argument 'text' is missing. Check the argument names and types " +
            "against the tool's input schema. Nothing was done.");
    }

    [Fact]
    public async Task ToolCalledWithArgumentOfWrongType_ReturnsInvalidArguments()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(
            CountTool, new Dictionary<string, object?> { ["count"] = "not a number" });

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("exceptionType").GetString().Should().Be("System.Text.Json.JsonException");
        payload.GetProperty("error").GetString().Should().Be(
            $"Invalid arguments for tool '{CountTool}'. count is not an integer. Check the argument names and types against the " +
            "tool's input schema. Nothing was done.");
    }

    [Theory]
    [InlineData("""[ { "x": 1 }, { "x": 2, "unit": "percent" } ]""", "items[1] has a member 'unit' that it does not take.")]
    [InlineData("""[ { "x": 1, "inner": { "y": 2, "z": 3 } } ]""", "items[0].inner has a member 'z' that it does not take.")]
    [InlineData("""[ { "x": 1, "weird name": 2 } ]""", "items[0] has a member 'weird name' that it does not take.")]
    [InlineData("""[ { "x": "left" } ]""", "items[0].x does not have the type the input schema gives it.")]
    [InlineData("""[ { "x": 1, "inner": 5 } ]""", "items[0].inner does not have the type the input schema gives it.")]
    [InlineData("""{ "x": 1 }""", "items is not an array.")]
    public async Task ABindingFailureInsideAnArgument_NamesWhereItIs_AndNoDotNetType(string items, string expected)
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(NestedTool, new Dictionary<string, object?>
        {
            ["items"] = JsonSerializer.Deserialize<JsonElement>(items), ["note"] = "n"
        });

        // Assert - System.Text.Json's own text names CLR types (WindowsAppManagerMcp...Item), which mean nothing to the agent
        var error = ParsePayload(result).GetProperty("error").GetString();
        error.Should().Be($"Invalid arguments for tool '{NestedTool}'. {expected} Check the argument names and types against the " +
                          "tool's input schema. Nothing was done.");
    }

    [Fact]
    public async Task AWrongTypeThatCouldBeEitherArgument_SaysAnArgument_WithItsType()
    {
        // Arrange - both arguments are strings, and only the binder knows which one it was reading
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(PairTool, new Dictionary<string, object?> { ["a"] = "x", ["b"] = "y" });

        // Assert
        ParsePayload(result).GetProperty("error").GetString().Should().Be(
            $"Invalid arguments for tool '{PairTool}'. An argument is not an integer. Check the argument names and types against the " +
            "tool's input schema. Nothing was done.");
    }

    [Fact]
    public async Task InvalidArguments_AreNotLoggedAsErrors()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(EchoTool);

        // Assert - the agent's own mistake is not a server fault
        server.Logs.Entries.Should().NotContain(e =>
            e.Category == "WindowsAppManagerMcp.ToolErrors" && e.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// ArgumentException and JsonException are only argument errors when argument binding throws
    /// them; the same types thrown by the tool's own code are server-side failures.
    /// </summary>
    [Theory]
    [InlineData(BodyArgumentExceptionTool, "System.ArgumentException")]
    [InlineData(BodyJsonExceptionTool, "System.Text.Json.JsonException")]
    public async Task SameExceptionTypeThrownByTheTool_StaysUnhandledException(string tool, string exceptionType)
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(tool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.ErrorCode);
        payload.GetProperty("exceptionType").GetString().Should().Be(exceptionType);
    }

    [Fact]
    public async Task DeliberateMcpException_KeepsItsMessageAndGetsToolErrorCode()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(DeliberateErrorTool);

        // Assert - like the SDK, the McpException message reaches the agent unchanged
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.ToolErrorCode);
        payload.GetProperty("error").GetString().Should().Be("The thing is not ready yet.");
        payload.GetProperty("tool").GetString().Should().Be(DeliberateErrorTool);
        payload.TryGetProperty("exceptionType", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ToolErrorException_ReportsItsOwnErrorCode()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(CodedErrorTool);

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be("PresetNotFound");
        payload.GetProperty("error").GetString().Should().Be("Layout preset 'x' was not found.");
    }

    [Fact]
    public async Task DeliberateToolErrors_AreNotLoggedAsErrors()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        await server.CallToolAsync(DeliberateErrorTool);
        await server.CallToolAsync(CodedErrorTool);

        // Assert - an ordinary "not found" must not write an error with a stack trace to stderr
        server.Logs.Entries.Should().NotContain(e =>
            e.Category == "WindowsAppManagerMcp.ToolErrors" && (e.Level >= LogLevel.Warning || e.Exception != null));
    }

    [Fact]
    public async Task Filter_WhenCallIsCancelled_RethrowsTheCancellation()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var handler = ToolErrorReporting.Filter((_, token) => throw new OperationCanceledException(token));

        // Act
        var act = async () => await handler(CreateRequest(), cancelled.Token);

        // Assert - the SDK's own cancellation handling must see it
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Filter_WhenToolCancelsWithoutTheCallBeingCancelled_ReturnsAnErrorResult()
    {
        // Arrange - e.g. a tool's internal timeout
        var handler = ToolErrorReporting.Filter((_, _) => throw new TaskCanceledException("Internal timeout"));

        // Act
        var result = await handler(CreateRequest(), CancellationToken.None);

        // Assert
        result.IsError.Should().BeTrue();
        ParsePayload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.ErrorCode);
    }

    [Fact]
    public async Task ErrorText_IsReadableWithoutUnicodeEscapes()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync(ThrowingTool);

        // Assert - the agent reads the raw text, so quotes must not be escaped as unicode sequences.
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("Tool 'throwing_tool' failed.");
    }

    [Fact]
    public async Task UnknownTool_StaysAProtocolError()
    {
        // Arrange
        await using var server = await StartServerAsync();

        // Act
        var act = async () => await server.CallToolAsync("no_such_tool");

        // Assert
        await act.Should().ThrowAsync<McpProtocolException>();
    }

    [Fact]
    public void AddToolErrorReporting_RegistersACallToolFilter()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = services.AddMcpServer();

        // Act
        builder.AddToolErrorReporting();

        // Assert
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.Filters.Request.CallToolFilters.Should().ContainSingle();
    }

    private static Task<InProcessMcpServer> StartServerAsync() =>
        InProcessMcpServer.StartAsync(
            [
                McpServerTool.Create(Throws, new McpServerToolCreateOptions { Name = ThrowingTool }),
                McpServerTool.Create(ReturnsHandle, new McpServerToolCreateOptions { Name = UnserializableTool }),
                McpServerTool.Create(Echo, new McpServerToolCreateOptions { Name = EchoTool }),
                McpServerTool.Create(Count, new McpServerToolCreateOptions { Name = CountTool }),
                McpServerTool.Create(ThrowsDeliberately, new McpServerToolCreateOptions { Name = DeliberateErrorTool }),
                McpServerTool.Create(ThrowsWithCode, new McpServerToolCreateOptions { Name = CodedErrorTool }),
                McpServerTool.Create(ThrowsArgumentException, new McpServerToolCreateOptions { Name = BodyArgumentExceptionTool }),
                McpServerTool.Create(ThrowsJsonException, new McpServerToolCreateOptions { Name = BodyJsonExceptionTool }),
                McpServerTool.Create(Outcome, new McpServerToolCreateOptions { Name = OutcomeTool }),
                McpServerTool.Create(List, new McpServerToolCreateOptions { Name = ListTool }),
                McpServerTool.Create(Batch, new McpServerToolCreateOptions { Name = BatchTool }),
                McpServerTool.Create(Nested, new McpServerToolCreateOptions { Name = NestedTool }),
                McpServerTool.Create(Pair, new McpServerToolCreateOptions { Name = PairTool }),
            ],
            builder => builder.AddToolErrorReporting());

    private static JsonElement ParsePayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Throws() => throw new InvalidOperationException("No monitors found");

    private static HandleResult ReturnsHandle() => new(new nint(42));

    private static string Echo(string text) => text;

    private static int Count(int count) => count;

    private static int Nested(NestedItem[] items, string? note = null) => items.Length;

    private static int Pair(int a, int b) => a + b;

    private static string ThrowsDeliberately() => throw new McpException("The thing is not ready yet.");

    private static string ThrowsWithCode() => throw new ToolErrorException("PresetNotFound", "Layout preset 'x' was not found.");

    private static string ThrowsArgumentException() => throw new ArgumentException("Value is out of range.", "value");

    private static int ThrowsJsonException() => JsonSerializer.Deserialize<int>("\"not a number\"");

    private static RequestContext<CallToolRequestParams> CreateRequest() =>
        new(new Mock<McpServer>().Object, new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = "some_tool" });

    private static WindowOperationResultLike Outcome(bool success) =>
        success
            ? new WindowOperationResultLike(true, new BoundsDto(0, 0, 800, 600))
            : new WindowOperationResultLike(false, new BoundsDto(0, 0, 800, 600), "Invalid window handle", "InvalidHandle");

    private static IReadOnlyList<WindowOperationResultLike> List() => [];

    private static BatchWindowResult Batch() =>
        new(1, 0, 1, [new WindowResultItem(42, false, Error: "Invalid window handle", ErrorCode: "InvalidHandle")]);

    public record HandleResult(nint? Handle);

    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public record NestedItem(int X, NestedInner? Inner = null);

    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public record NestedInner(int Y);

    public record WindowOperationResultLike(bool Success, BoundsDto? NewBounds, string? Error = null, string? ErrorCode = null);
}
