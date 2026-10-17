using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the UI Automation tools through the real MCP pipeline (argument binding, result
/// serialization, progress notifications and the client's cancellation). get_ui_elements runs the real
/// UIElementService over an in-memory tree; the element inspection service is mocked. No real window is read.
/// </summary>
public class UIElementToolContractTests
{
    private static readonly nint Handle = 0x1234;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeUiaTree _tree = new();
    private readonly Mock<IElementInspectionService> _inspection = new();

    public static TheoryData<string?> Revisions => InProcessMcpServer.Revisions();

    private Task<InProcessMcpServer> StartServerAsync(
        string? revision = null,
        TaskCompletionSource<RequestId>? requestId = null,
        TaskCompletionSource? callEnded = null)
    {
        var tools = new UIElementTools(new UIElementService(_tree, new UiaWorker()), _inspection.Object);
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
                {
                    // The JSON-RPC id of the call, which a client names when it cancels the call
                    requestId?.TrySetResult(request.JsonRpcRequest.Id);
                    try
                    {
                        return await next(request, token);
                    }
                    finally
                    {
                        callEnded?.TrySetResult();
                    }
                }))
                .WithTools(tools),
            clientOptions: InProcessMcpServer.WithRevision(null, revision));
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task GetUIElements_WithAProgressToken_SendsProgressNotificationsDuringTheWalk(string? revision)
    {
        // Arrange - a window with 250 buttons
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(
            Enumerable.Range(1, 250).Select(i => new FakeUiaNode("Button", $"B{i}")).ToArray()));
        await using var server = await StartServerAsync(revision);
        var progress = new RecordingProgress<ProgressNotificationValue>();

        // Act - the SDK client sends a progress token with the call and routes the notifications to the progress
        var result = await server.Client.CallToolAsync(
            "get_ui_elements", new Dictionary<string, object?> { ["handle"] = (long)Handle }, progress);

        // Assert - one notification per 100 elements read, sent in increasing order (MCP requires progress to increase)
        // and before the result. They are checked where the server wrote them (see InProcessMcpServer.ProgressSent)
        Json(result).GetProperty("totalElementCount").GetInt32().Should().Be(250);
        var sent = server.WaitForProgressSent(2);
        sent.Select(p => p.Progress).Should().Equal(100, 200);
        sent.Should().OnlyContain(p => p.Total == null && p.Message!.Contains("UI elements so far"));
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task GetUIElements_CancelledByTheClient_StopsTheWalkOnTheServer(string? revision)
    {
        // Arrange - 200 panes whose children take 50 ms each to fetch (one cross-process call per pane): a ten-second walk
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(
            Enumerable.Range(1, 200)
                .Select(i => new FakeUiaNode("Pane", $"P{i}") { OnGetChildren = () => Thread.Sleep(50) }.With(new FakeUiaNode("Button", $"B{i}")))
                .ToArray()));
        var requestId = new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(revision, requestId, callEnded);
        using var clientGivesUp = new CancellationTokenSource();
        var call = server.CallToolAsync("get_ui_elements",
            new Dictionary<string, object?> { ["handle"] = (long)Handle, ["timeoutMs"] = 30000 }, cancellationToken: clientGivesUp.Token);
        var id = await requestId.Task.WaitAsync(Timeout);
        SpinWait.SpinUntil(() => _tree.CachedFetches.Count >= 3, Timeout).Should().BeTrue();

        // Act - the client cancels the call (notifications/cancelled with its id)
        await server.Client.SendNotificationAsync(NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = id, Reason = "the user cancelled" });

        // Assert - the walk stopped long before its end, and the call ended without waiting for the timeout
        await callEnded.Task.WaitAsync(Timeout);
        var fetched = _tree.CachedFetches.Count;
        fetched.Should().BeLessThan(201, "the request's token is linked with the walk's timeout");
        await Task.Delay(200);
        _tree.CachedFetches.Count.Should().BeLessThanOrEqualTo(fetched + 1, "the walk does not go on after the call ended");

        await server.AssertNoResponseSentAsync(id);
        clientGivesUp.Cancel();
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<Exception>("the cancelled call gets no result");
    }

    private static JsonElement Json(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.Should().NotBe(true, "the call failed with: {0}", text);
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task GetUIElements_WithControlTypes_ReturnsNestedMatchesOverMcp()
    {
        // Arrange - Buttons inside a Pane and a ToolBar
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("Pane", "Host").With(
                new FakeUiaNode("ToolBar", "Tools").With(
                    new FakeUiaNode("Button", "Save") { Bounds = UiaRect.Empty },
                    new FakeUiaNode("Button", "Open")))));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_ui_elements",
            new Dictionary<string, object?> { ["handle"] = (long)Handle, ["controlTypes"] = "Button", ["format"] = "tree" }));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().Equal("Save", "Open");
        json.GetProperty("totalElementCount").GetInt32().Should().Be(2);
        json.GetProperty("depthReached").GetInt32().Should().Be(3);
        json.GetProperty("truncated").GetBoolean().Should().BeFalse();
        json.TryGetProperty("truncationReason", out _).Should().BeFalse("null members are left out");
        json.GetProperty("elements")[0].TryGetProperty("bounds", out _).Should().BeFalse(
            "an element without geometry has no bounds instead of int.MinValue coordinates");
    }

    [Fact]
    public async Task GetUIElements_WithoutAFormat_ReturnsText()
    {
        // Owner decision D3 (3.0.0): text is the default (RemovedToolsContractTests: a null format, with the production
        // argument binding)
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Button", "Save")));
        await using var server = await StartServerAsync();
        var arguments = new Dictionary<string, object?> { ["handle"] = (long)Handle };

        var json = Json(await server.CallToolAsync("get_ui_elements", arguments));

        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("text").GetString().Should().Contain("Button 'Save'");
        json.TryGetProperty("elements", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetUIElements_TextFormatWithMaxElements_ReturnsCompactLinesOfTheShallowLevelsOverMcp()
    {
        // Arrange (roadmap M3.1)
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("ToolBar", "Tools") { Bounds = new UiaRect(0, 0, 200, 30) }.With(
                new FakeUiaNode("Button", "Save") { AutomationId = "SaveBtn", Bounds = new UiaRect(812, 44, 80, 24), IsEnabled = false },
                new FakeUiaNode("Button", "Open")),
            new FakeUiaNode("Edit", "Name") { Bounds = new UiaRect(5, 50, 300, 20) }));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_ui_elements",
            new Dictionary<string, object?> { ["handle"] = (long)Handle, ["format"] = "text", ["maxElements"] = 3 }));

        // Assert - breadth first: both elements under the window, then the first one below
        json.GetProperty("text").GetString().Should().Be(
            "ToolBar 'Tools' @0,0 200x30\n" +
            "  Button 'Save' aid=SaveBtn @812,44 80x24 [disabled]\n" +
            "Edit 'Name' @5,50 300x20");
        json.TryGetProperty("elements", out _).Should().BeFalse("the text replaces the elements");
        json.GetProperty("totalElementCount").GetInt32().Should().Be(3);
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        json.GetProperty("truncationReason").GetString().Should().Be("MaxElements");
    }

    [Fact]
    public async Task GetUIElements_FlatFormat_ReturnsDepthAndParentOverMcp()
    {
        // Arrange
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("ToolBar", "Tools").With(new FakeUiaNode("Button", "Save"))));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_ui_elements",
            new Dictionary<string, object?> { ["handle"] = (long)Handle, ["format"] = "flat" }));

        // Assert
        var elements = json.GetProperty("elements").EnumerateArray().ToList();
        elements.Select(e => e.GetProperty("name").GetString()).Should().Equal("Tools", "Save");
        elements[0].GetProperty("depth").GetInt32().Should().Be(1);
        elements[0].TryGetProperty("parent", out _).Should().BeFalse("a top-level element has no parent");
        elements[1].GetProperty("depth").GetInt32().Should().Be(2);
        elements[1].GetProperty("parent").GetInt32().Should().Be(0);
        elements.Where(e => e.TryGetProperty("children", out var _)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetUIElements_WithUnknownControlType_ReturnsInvalidControlTypeOverMcp()
    {
        // Arrange
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Editor"));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_ui_elements",
            new Dictionary<string, object?> { ["handle"] = (long)Handle, ["controlTypes"] = "Buton" }));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("InvalidControlType");
        json.GetProperty("error").GetString().Should().Contain("'Buton'").And.Contain("Button");
    }

    [Fact]
    public async Task GetElementAtPoint_BindsNegativeCoordinatesAndSerializesTheSnapshot()
    {
        // Arrange
        var element = new UIElementDetails("Email", "Edit", "EmailBox", "TextBox", "WPF", new BoundsDto(-300, 10, 200, 24),
            IsEnabled: true, IsOffscreen: false, IsKeyboardFocusable: true, HasKeyboardFocus: false, IsPassword: false,
            Patterns: ["Value"], Value: "a@b.c");
        _inspection.Setup(s => s.GetElementAtPointAsync(-250, 20, 3, TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementAtPointResult(true, -250, 20, element,
                [new UIElementAncestor("Form", "Pane", null, "FormPane", null)], 0x5000, "Sign in", 42, "app", 1.5));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_element_at_point",
            new Dictionary<string, object?> { ["x"] = -250, ["y"] = 20 }));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("errorCode").GetString().Should().Be("None");
        json.GetProperty("windowHandle").GetInt64().Should().Be(0x5000);
        json.GetProperty("processName").GetString().Should().Be("app");
        var snapshot = json.GetProperty("element");
        snapshot.GetProperty("controlType").GetString().Should().Be("Edit");
        snapshot.GetProperty("value").GetString().Should().Be("a@b.c");
        snapshot.GetProperty("patterns")[0].GetString().Should().Be("Value");
        json.GetProperty("ancestors")[0].GetProperty("className").GetString().Should().Be("FormPane");
    }

    [Fact]
    public async Task GetFocusedElement_TakesNoRequiredArguments()
    {
        // Arrange
        _inspection.Setup(s => s.GetFocusedElementAsync(TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FocusedElementResult(false, null, null, null, null, null, 0.5, "No element has keyboard focus.",
                UIElementErrorCode.ElementNotFound));
        await using var server = await StartServerAsync();

        // Act
        var json = Json(await server.CallToolAsync("get_focused_element"));

        // Assert
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        json.GetProperty("errorCode").GetString().Should().Be("ElementNotFound");
    }
}
