using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: get_clipboard and set_clipboard over MCP (roadmap M2.16), with the real ClipboardTools and ClipboardService
/// over a fake clipboard. Reading the user's own clipboard is the user's decision, never the model's: the server asks the
/// user when the client can ask (elicitation), returns the content only after a ticked yes, and only the content that was
/// there when they were asked; WAM_ALLOW_USER_CLIPBOARD skips the question. No argument the model sets can do either.
/// </summary>
public sealed class ClipboardToolContractTests
{
    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ClipboardOrigin _origin;
    private readonly List<string> _questions = [];

    public ClipboardToolContractTests()
    {
        _origin = new ClipboardOrigin(_clipboard, new ManualClock(), Timeout.InfiniteTimeSpan);
    }

    private Task<InProcessMcpServer> StartAsync(Func<ElicitRequestParams, ElicitResult>? answer = null, bool allowUserClipboard = false,
        string? revision = null)
    {
        McpClientOptions? options = null;
        if (answer != null)
        {
            options = new McpClientOptions
            {
                Handlers = new McpClientHandlers
                {
                    ElicitationHandler = (request, _) =>
                    {
                        _questions.Add(request!.Message);
                        return ValueTask.FromResult(answer(request));
                    }
                }
            };
        }

        var service = new ClipboardService(_clipboard, _origin, new ClipboardOptions(allowUserClipboard), copyWait: TimeSpan.FromMilliseconds(100));
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddToolErrorReporting().WithServerTools(new ClipboardTools(service)),
            clientOptions: InProcessMcpServer.WithRevision(options, revision));
    }

    private static ElicitResult Answer(string action, bool? confirm = null) => new()
    {
        Action = action,
        Content = confirm is { } value
            ? new Dictionary<string, JsonElement> { [UserConfirmation.ConfirmField] = JsonSerializer.SerializeToElement(value) }
            : null
    };

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    [Theory]
    [MemberData(nameof(InProcessMcpServer.Revisions), MemberType = typeof(InProcessMcpServer))]
    public async Task TheUsersClipboard_IsReadOnlyAfterTheUsersYes(string? revision)
    {
        // Arrange
        _clipboard.UserCopies("a shared link");
        await using var server = await StartAsync(_ => Answer("accept", confirm: true), revision: revision);

        // Act
        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?>());

        // Assert
        result.IsError.Should().NotBe(true, Payload(result).ToString());
        var payload = Payload(result);
        payload.GetProperty("text").GetString().Should().Be("a shared link");
        payload.GetProperty("origin").GetString().Should().Be(nameof(ClipboardContentOrigin.User));
        _questions.Should().ContainSingle().Which.Should().Contain("read your clipboard").And.Contain("password").And.NotContain("a shared link");
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    [InlineData("accept", false)]
    public async Task TheUsersClipboard_WithoutTheUsersYes_IsConfirmationDeclined_AndNothingIsRead(string action, bool? confirm)
    {
        _clipboard.UserCopies("hunter2");
        await using var server = await StartAsync(_ => Answer(action, confirm));

        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?>());

        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(ClipboardErrorCode.ConfirmationDeclined));
        payload.GetProperty("error").GetString().Should().Contain("Nothing was read");
        payload.ToString().Should().NotContain("hunter2");
        _clipboard.Reads.Should().Be(0);
    }

    [Fact]
    public async Task WhatWasCopiedWhileTheUserAnswered_IsNotRead()
    {
        // Arrange - the user says yes to the link; a password manager copies before the read
        _clipboard.UserCopies("a shared link");
        await using var server = await StartAsync(_ =>
        {
            _clipboard.UserCopies("hunter2");
            return Answer("accept", confirm: true);
        });

        // Act
        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?>());

        // Assert
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(ClipboardErrorCode.ClipboardNotFromAgent));
        payload.ToString().Should().NotContain("hunter2");
    }

    [Fact]
    public async Task AClientThatCannotAsk_GetsClipboardNotFromAgent()
    {
        _clipboard.UserCopies("hunter2");
        await using var server = await StartAsync(answer: null);

        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?>());

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ClipboardErrorCode.ClipboardNotFromAgent));
        _clipboard.Reads.Should().Be(0);
    }

    [Fact]
    public async Task WithTheUsersSetting_TheirClipboardIsReadWithoutAQuestion()
    {
        _clipboard.UserCopies("a shared link");
        await using var server = await StartAsync(_ => Answer("accept", confirm: true), allowUserClipboard: true);

        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?>());

        Payload(result).GetProperty("text").GetString().Should().Be("a shared link");
        _questions.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAgentsOwnContent_IsReadWithoutAQuestion()
    {
        await using var server = await StartAsync(_ => Answer("decline"));

        (await server.CallToolAsync("set_clipboard", new Dictionary<string, object?> { ["text"] = "Meeting notes" })).IsError.Should().NotBe(true);
        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?> { ["format"] = "text" });

        var payload = Payload(result);
        payload.GetProperty("text").GetString().Should().Be("Meeting notes");
        payload.GetProperty("origin").GetString().Should().Be(nameof(ClipboardContentOrigin.SetClipboard));
        _questions.Should().BeEmpty();
    }

    [Fact]
    public async Task PrivateContent_IsNotReturned_EvenAfterTheUsersYes()
    {
        _clipboard.UserCopies("hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);
        await using var server = await StartAsync(_ => Answer("accept", confirm: true));

        var result = await server.CallToolAsync("get_clipboard", new Dictionary<string, object?>());

        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ClipboardErrorCode.ClipboardPrivate));
        Payload(result).ToString().Should().NotContain("hunter2");
    }

    [Fact]
    public async Task TheReadTakesNoArgumentThatAllowsTheUsersClipboard()
    {
        // The opt-in is the user's (server environment or their answer), never a flag the model sets
        await using var server = await StartAsync();

        var tool = (await server.Client.ListToolsAsync()).Single(t => t.Name == "get_clipboard");

        tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("format", "maxLength");
        tool.JsonSchema.GetProperty("properties").GetProperty("format").GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()).Should().Equal("text", "html", "files");
    }

    [Theory]
    [InlineData("get_clipboard", """{ "maxLength": 0 }""", "maxLength")]
    [InlineData("get_clipboard", """{ "format": "image" }""", "clipboard format")]
    [InlineData("set_clipboard", """{ "text": "" }""", "1 to 1000000 characters")]
    public async Task InvalidArguments_AreAToolError(string tool, string arguments, string message)
    {
        await using var server = await StartAsync();

        var result = await server.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!);

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("error").GetString().Should().Contain(message);
        _clipboard.Reads.Should().Be(0);
        _clipboard.Writes.Should().Be(0);
    }
}
