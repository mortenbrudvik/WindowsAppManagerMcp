using System.Diagnostics;
using System.Net;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for CdpService using an in-process fake browser (fake HTTP handler and WebSocket) behind the real
/// CdpTransport (roadmap M5.2). No real browser or network is involved.
/// </summary>
public class CdpServiceTests
{
    private const int Port = 9222;
    private const int BrowserPid = 4242;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    // Generous bound so slow CI machines do not flake; a hang would never finish.
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(10);

    private static readonly CdpTarget Tab = new(
        Id: "TAB1",
        Type: "page",
        Title: "Inbox",
        Url: "https://mail.example/",
        WebSocketDebuggerUrl: "ws://127.0.0.1:9222/devtools/page/TAB1",
        Port: Port);

    private static CdpService CreateSut(
        FakeCdpHttpHandler? http = null,
        ICdpWebSocketFactory? sockets = null,
        int? portOwner = BrowserPid,
        CdpTransportLimits? limits = null) =>
        new(
            new CdpTransport(http ?? FakeCdpHttpHandler.Targets(), sockets ?? FakeCdpWebSocketFactory.NeverConnects(), limits),
            new FakePortOwnerResolver(portOwner));

    /// <summary>A fake page that answers Runtime.evaluate with the given snapshot.</summary>
    private static FakeCdpWebSocket PageAnswering(object snapshot, int? frameSize = null) =>
        new((socket, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
                socket.ReplyWithValue(command, snapshot, frameSize);
        });

    #region GetPageTargetsAsync

    [Fact]
    public async Task GetPageTargetsAsync_ReturnsOnlyPageTargets()
    {
        var http = FakeCdpHttpHandler.Json("""
            [
              { "id": "P1", "type": "page", "title": "Inbox", "url": "https://mail.example/", "webSocketDebuggerUrl": "ws://127.0.0.1:9222/devtools/page/P1" },
              { "id": "W1", "type": "service_worker", "title": "sw", "url": "https://mail.example/sw.js" },
              { "id": "F1", "type": "iframe", "title": "ad", "url": "https://ads.example/" }
            ]
            """);
        var sut = CreateSut(http);

        var result = await sut.GetPageTargetsAsync(Port, LongTimeout);

        result.Success.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Id.Should().Be("P1");
    }

    [Fact]
    public async Task GetPageTargetsAsync_QueriesLoopbackAddress()
    {
        var http = FakeCdpHttpHandler.Targets();
        var sut = CreateSut(http);

        await sut.GetPageTargetsAsync(Port, LongTimeout);

        http.Requests.Should().ContainSingle()
            .Which.Should().Be(new Uri("http://127.0.0.1:9222/json/list"));
    }

    [Fact]
    public async Task GetPageTargetsAsync_ReturnsCdpTimeout_WhenEndpointNeverAnswers()
    {
        var sut = CreateSut(FakeCdpHttpHandler.NeverAnswers());
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.GetPageTargetsAsync(Port, ShortTimeout);

        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("200 ms");
    }

    [Fact]
    public async Task GetPageTargetsAsync_ReturnsCdpUnavailable_WhenConnectionIsRefused()
    {
        var sut = CreateSut(FakeCdpHttpHandler.ConnectionRefused());

        var result = await sut.GetPageTargetsAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("--remote-debugging-port=9222");
    }

    [Fact]
    public async Task GetPageTargetsAsync_ReturnsCdpProtocolError_OnHttpError()
    {
        // A program answered (a web server that has no /json/list, say), so the port is not empty: not CdpUnavailable
        var sut = CreateSut(FakeCdpHttpHandler.Json("not found", HttpStatusCode.NotFound));

        var result = await sut.GetPageTargetsAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("HTTP 404").And.Contain("not like a DevTools endpoint");
    }

    [Fact]
    public async Task GetPageTargetsAsync_ReturnsCdpProtocolError_WhenResponseIsNotATargetList()
    {
        var sut = CreateSut(FakeCdpHttpHandler.Json("<html>hello</html>"));

        var result = await sut.GetPageTargetsAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task GetPageTargetsAsync_ReturnsCdpUnavailable_ForInvalidPort(int port)
    {
        var http = FakeCdpHttpHandler.Targets();
        var sut = CreateSut(http);

        var result = await sut.GetPageTargetsAsync(port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("the cdpPort that launch_browser_with_debug returned", "its browser was started with port 0 (roadmap M5.1)");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPageTargetsAsync_Throws_WhenCallerCancels()
    {
        var sut = CreateSut(FakeCdpHttpHandler.NeverAnswers());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => sut.GetPageTargetsAsync(Port, LongTimeout, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region FindTargetForWindowAsync

    [Fact]
    public async Task FindTargetForWindowAsync_ReturnsTabShownByWindow()
    {
        var http = FakeCdpHttpHandler.Targets(
            ("A", "Google", "https://www.google.com/"),
            ("B", "GitHub", "https://github.com/"));
        var sut = CreateSut(http);

        var result = await sut.FindTargetForWindowAsync(Port, BrowserPid, "GitHub - Google Chrome", LongTimeout);

        result.Success.Should().BeTrue();
        result.Value!.Id.Should().Be("B");
        result.Value.Url.Should().Be("https://github.com/");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_DoesNotReturnAnotherTab_WhenWindowTabIsNotListed()
    {
        // Old behavior: 'GitHub - Google Chrome'.Contains('Google') returned the Google tab with Success=true.
        var http = FakeCdpHttpHandler.Targets(
            ("A", "Google", "https://www.google.com/"),
            ("B", "", "about:blank"));
        var sut = CreateSut(http);

        var result = await sut.FindTargetForWindowAsync(Port, BrowserPid, "GitHub - Google Chrome", LongTimeout);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public async Task FindTargetForWindowAsync_ReturnsPortOwnerMismatch_WhenPortBelongsToAnotherProcess()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: 999);

        var result = await sut.FindTargetForWindowAsync(Port, BrowserPid, "Inbox - Google Chrome", LongTimeout);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("999").And.Contain(BrowserPid.ToString())
            .And.Contain("the cdpPort that launch_browser_with_debug returned").And.NotContain("port given to");
        http.Requests.Should().BeEmpty("a port owned by another process must not be queried");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_MatchesByTitle_WhenPortOwnerIsUnknown()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: null);

        var result = await sut.FindTargetForWindowAsync(Port, BrowserPid, "Inbox - Google Chrome", LongTimeout);

        result.Success.Should().BeTrue();
        result.Value!.Id.Should().Be("A");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_PassesThroughTargetListErrors()
    {
        var sut = CreateSut(FakeCdpHttpHandler.NeverAnswers());

        var result = await sut.FindTargetForWindowAsync(Port, BrowserPid, "Inbox - Google Chrome", ShortTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
    }

    #endregion

    #region GetPageSnapshotAsync

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsTitleUrlAndContentFromOneEvaluate()
    {
        var socket = PageAnswering(new { title = "Inbox", url = "https://mail.example/#inbox", content = "3 unread" });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(new CdpPageSnapshot("Inbox", "https://mail.example/#inbox", "3 unread"));
        socket.SentCommands.Count(c => c.GetProperty("method").GetString() == "Runtime.evaluate").Should().Be(1);
    }

    [Fact]
    public async Task GetPageSnapshotAsync_SendsEvaluateWithReturnByValueAndTimeout()
    {
        var socket = PageAnswering(new { title = "t", url = "u", content = "c" });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: 50, TimeSpan.FromSeconds(7));

        var parameters = socket.EvaluateCommand!.Value.GetProperty("params");
        parameters.GetProperty("returnByValue").GetBoolean().Should().BeTrue();
        parameters.GetProperty("timeout").GetInt32().Should().Be(7000);
        var expression = parameters.GetProperty("expression").GetString();
        expression.Should().Contain("innerText").And.Contain("const max = 50;");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_RequestsHtml_WhenNotTextOnly()
    {
        var socket = PageAnswering(new { title = "t", url = "u", content = "<html></html>" });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        await sut.GetPageSnapshotAsync(Tab, textOnly: false, maxLength: null, LongTimeout);

        var expression = socket.EvaluateCommand!.Value.GetProperty("params").GetProperty("expression").GetString();
        expression.Should().Contain("outerHTML").And.NotContain("innerText");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsCdpTimeout_WhenPageNeverAnswers()
    {
        // A page blocked by alert()/confirm() never answers Runtime.evaluate.
        var socket = new FakeCdpWebSocket();
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, ShortTimeout);

        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("dialog").And.Contain("200 ms");
        socket.ReceiveTokenWasCancelled.Should().BeTrue("the receive must be bounded by the timeout token");
        socket.WasAborted.Should().BeTrue();
        socket.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsCdpTimeout_WhenWebSocketNeverConnects()
    {
        var sut = CreateSut(sockets: FakeCdpWebSocketFactory.NeverConnects());
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, ShortTimeout);

        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("WebSocket");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_Throws_WhenCallerCancels()
    {
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(new FakeCdpWebSocket()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReassemblesMessageSplitAcrossFrames()
    {
        // 5-byte frames split multi-byte UTF-8 characters between frames.
        const string content = "Blåbærsyltetøy – 日本語 😀 done";
        var socket = PageAnswering(new { title = "Ærlig talt", url = "https://example.com/æ", content }, frameSize: 5);
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.Success.Should().BeTrue();
        result.Value!.Content.Should().Be(content);
        result.Value.Title.Should().Be("Ærlig talt");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_IgnoresEventsAndOtherReplies()
    {
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            var method = command.GetProperty("method").GetString();
            var id = command.GetProperty("id").GetInt32();
            if (method == "Page.enable")
            {
                s.Enqueue("{\"id\":" + id + ",\"result\":{}}");
                s.Enqueue("""{"method":"Page.frameStoppedLoading","params":{"frameId":"F"}}""");
            }
            else if (method == "Runtime.evaluate")
            {
                s.Enqueue("""{"method":"Runtime.consoleAPICalled","params":{"type":"log"}}""");
                s.ReplyWithValue(command, new { title = "t", url = "u", content = "the content" });
            }
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.Success.Should().BeTrue();
        result.Value!.Content.Should().Be("the content");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsDialogOpen_WhenBrowserReportsDialog()
    {
        // The dialog event arrives, Runtime.evaluate never answers: report the dialog without waiting for the timeout.
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Page.enable")
            {
                s.Enqueue("""{"method":"Page.javascriptDialogOpening","params":{"url":"https://mail.example/","message":"Leave site?","type":"beforeunload","hasBrowserHandler":true}}""");
            }
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
        result.Error.Should().Contain("beforeunload").And.Contain("Leave site?");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_DialogOpen_SaysToInspectTheDialogInsteadOfPressingEnter()
    {
        // Enter accepts a confirm/beforeunload dialog, so the error must not recommend it as a way to
        // dismiss the dialog. It points to inspecting the dialog or asking the user instead.
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Page.enable")
            {
                s.Enqueue("""{"method":"Page.javascriptDialogOpening","params":{"url":"https://shop.example/","message":"Delete all items?","type":"confirm","hasBrowserHandler":true}}""");
            }
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
        result.Error.Should().Contain("JavaScript confirm dialog")
            .And.Contain("untrusted")
            .And.Contain("Do not press Enter")
            .And.Contain("capture_window")
            .And.Contain("ask the user");
        result.Error.Should().NotContainAny("press Enter or Escape", "Dismiss the dialog (for example");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsEvaluationFailed_WhenPageThrows()
    {
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
            {
                s.Enqueue("{\"id\":" + command.GetProperty("id").GetInt32() + """
                    ,"result":{
                      "result":{"type":"object","subtype":"error","description":"TypeError: boom"},
                      "exceptionDetails":{"exceptionId":1,"text":"Uncaught","exception":{"type":"object","description":"TypeError: boom"}}}}
                    """);
            }
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpEvaluationFailed);
        result.Error.Should().Contain("TypeError: boom");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsProtocolError_WhenExceptionDetailsIsNotAnObject()
    {
        // Only a program that is not a real DevTools endpoint sends this; it must not throw out of ICdpService
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
                s.Enqueue("{\"id\":" + command.GetProperty("id").GetInt32() + ",\"result\":{\"exceptionDetails\":\"x\"}}");
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("exceptionDetails");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsEvaluationFailed_WhenBrowserRejectsCommand()
    {
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
            {
                s.Enqueue("{\"id\":" + command.GetProperty("id").GetInt32() + ""","error":{"code":-32000,"message":"Cannot find context with specified id"}}""");
            }
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpEvaluationFailed);
        result.Error.Should().Contain("Cannot find context");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsTargetClosed_WhenBrowserClosesConnection()
    {
        // Was CdpProtocolError before roadmap M5.2: a closed tab is not a protocol fault, and it does not come back
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
                s.EnqueueClose();
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsTargetClosed_WithTheReason_WhenTheTabDetaches()
    {
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
                s.EnqueueEvent("Inspector.detached", new { reason = "target_closed" });
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
        result.Error.Should().Contain("target_closed").And.Contain("without targetId");
        socket.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsResponseTooLarge_WhenTheAnswerIsOverTheLimit()
    {
        var socket = PageAnswering(new { title = "t", url = "u", content = new string('a', 4096) });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket), limits: new CdpTransportLimits(MaxMessageBytes: 1024));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: false, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
        result.Error.Should().Contain("maxLength");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_Succeeds_WhenPageEnableIsNeverAnswered()
    {
        // A page may never answer Page.enable; the read does not wait for it
        var socket = PageAnswering(new { title = "t", url = "u", content = "read anyway" });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, ShortTimeout);

        result.Success.Should().BeTrue();
        result.Value!.Content.Should().Be("read anyway");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_Succeeds_WhenPageEnableIsRefused_ButTheReadWorks()
    {
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Page.enable")
                s.ReplyError(command, -32000, "Page domain is not available");
            else
                s.ReplyWithValue(command, new { title = "t", url = "u", content = "c" });
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetPageSnapshotAsync_SaysPageEnableWasRefused_WhenTheReadThenTimesOut()
    {
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Page.enable")
                s.ReplyError(command, -32000, "Page domain is not available");
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, ShortTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("Page.enable was refused: Page domain is not available (-32000)");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_RefusesAWebSocketUrlOnAnotherPortThanTheTabsOwn()
    {
        var factory = new FakeCdpWebSocketFactory(PageAnswering(new { title = "t", url = "u", content = "c" }));
        var sut = CreateSut(sockets: factory);

        var result = await sut.GetPageSnapshotAsync(
            Tab with { WebSocketDebuggerUrl = "ws://127.0.0.1:9333/devtools/page/TAB1" }, true, null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().StartWith("Refusing");
        factory.ConnectedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPageTargetsAsync_GivesEachTabThePortThatListedIt()
    {
        var sut = CreateSut(FakeCdpHttpHandler.Targets(9333, ("A", "Inbox", "https://mail.example/")));

        var result = await sut.GetPageTargetsAsync(9333, LongTimeout);

        result.Value.Should().ContainSingle().Which.Port.Should().Be(9333);
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsProtocolError_WhenResponseIsNotJson()
    {
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
                s.Enqueue("this is not json");
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, textOnly: true, maxLength: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ws://203.0.113.5:9222/devtools/page/TAB1")]
    [InlineData("http://127.0.0.1:9222/devtools/page/TAB1")]
    public async Task GetPageSnapshotAsync_RefusesMissingOrNonLoopbackWebSocketUrl(string? webSocketUrl)
    {
        var factory = new FakeCdpWebSocketFactory(PageAnswering(new { title = "t", url = "u", content = "c" }));
        var sut = CreateSut(sockets: factory);

        var result = await sut.GetPageSnapshotAsync(Tab with { WebSocketDebuggerUrl = webSocketUrl }, true, null, LongTimeout);

        result.Success.Should().BeFalse();
        factory.ConnectedUris.Should().BeEmpty();
    }

    #endregion

    #region FindTargetByIdAsync

    [Fact]
    public async Task FindTargetByIdAsync_ReturnsTheTabWithThatId_EvenWhenTitlesAreTheSame()
    {
        var http = FakeCdpHttpHandler.Targets(
            ("A", "Inbox", "https://mail.example/a"),
            ("B", "Inbox", "https://mail.example/b"));
        var sut = CreateSut(http);

        var result = await sut.FindTargetByIdAsync(Port, BrowserPid, "B", LongTimeout);

        result.Success.Should().BeTrue();
        result.Value!.Url.Should().Be("https://mail.example/b");
        result.Value.Port.Should().Be(Port);
    }

    [Fact]
    public async Task FindTargetByIdAsync_ReturnsTargetNotFound_ForATabThatIsGone()
    {
        var sut = CreateSut(FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/")));

        var result = await sut.FindTargetByIdAsync(Port, BrowserPid, "GONE", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
        result.Error.Should().Contain("GONE").And.Contain("may have closed").And.Contain("without targetId");
    }

    [Fact]
    public async Task FindTargetByIdAsync_ReturnsPortOwnerMismatch_WithoutListingTabs_WhenThePortIsAnotherProcesss()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: 999);

        var result = await sut.FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        http.Requests.Should().BeEmpty("a port owned by another process must not be queried");
    }

    [Fact]
    public async Task FindTargetByIdAsync_FailsClosed_WhenThePortsOwnerCannotBeRead()
    {
        // The title path skips an unknown owner and still needs the title to match; an id carries no such evidence
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: null);

        var result = await sut.FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("Could not read which process owns CDP port 9222");
        http.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task FindTargetByIdAsync_ReturnsCdpUnavailable_ForAnInvalidPort_BeforeTheOwnerCheck(int port)
    {
        // The real resolver reads no owner for such a port; the typo is the port, not a browser that is gone
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: null);

        var result = await sut.FindTargetByIdAsync(port, BrowserPid, "A", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain($"Invalid CDP port {port}");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FindTargetByIdAsync_DoesNotLookUpAMalformedId()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http);

        var result = await sut.FindTargetByIdAsync(Port, BrowserPid, "../browser", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
        http.Requests.Should().BeEmpty();
    }

    #endregion

    #region GetBrowserVersionAsync

    private const string BrowserWs = "ws://127.0.0.1:9222/devtools/browser/6f1c2d3e-4a5b-6c7d-8e9f-0a1b2c3d4e5f";

    private static FakeCdpHttpHandler VersionAnswering(string? webSocketDebuggerUrl = BrowserWs) =>
        FakeCdpHttpHandler.Json(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["Browser"] = "Chrome/141.0.7390.55",
            ["Protocol-Version"] = "1.3",
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/141.0.0.0 Safari/537.36",
            ["webSocketDebuggerUrl"] = webSocketDebuggerUrl
        }));

    [Fact]
    public async Task GetBrowserVersionAsync_ReturnsTheBrowserAndItsDevToolsEndpoints()
    {
        var sut = CreateSut(VersionAnswering());

        var result = await sut.GetBrowserVersionAsync(Port, LongTimeout);

        result.Success.Should().BeTrue();
        result.Value!.Browser.Should().Be("Chrome/141.0.7390.55");
        result.Value.ProtocolVersion.Should().Be("1.3");
        result.Value.WebSocketDebuggerUrl.Should().Be(new Uri(BrowserWs));
        result.Value.HttpEndpoint.Should().Be("http://127.0.0.1:9222");
        result.Value.Port.Should().Be(Port);
    }

    [Fact]
    public async Task GetBrowserVersionAsync_AsksOnlyTheLoopbackAddressOnThePort()
    {
        var http = VersionAnswering();
        var sut = CreateSut(http);

        await sut.GetBrowserVersionAsync(Port, LongTimeout);

        http.Requests.Should().ContainSingle()
            .Which.Should().Be(new Uri("http://127.0.0.1:9222/json/version"));
    }

    [Theory]
    [InlineData("ws://127.0.0.1:9333/devtools/browser/B1", "port 9333")]
    [InlineData("ws://localhost:9222/devtools/browser/B1", "only ws:// on 127.0.0.1")]
    [InlineData("ws://[::1]:9222/devtools/browser/B1", "only ws:// on 127.0.0.1")]
    [InlineData("ws://10.0.0.5:9222/devtools/browser/B1", "only ws:// on 127.0.0.1")]
    [InlineData("wss://127.0.0.1:9222/devtools/browser/B1", "only ws:// on 127.0.0.1")]
    public async Task GetBrowserVersionAsync_RefusesABrowserUrlOffTheLoopbackPort(string listed, string reason)
    {
        // Whatever listens on the port writes /json/version: a URL elsewhere could be another program's endpoint
        var sut = CreateSut(VersionAnswering(listed));

        var result = await sut.GetBrowserVersionAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("Refusing").And.Contain(reason);
    }

    [Fact]
    public async Task GetBrowserVersionAsync_RefusesAPageUrlListedAsTheBrowsers()
    {
        var sut = CreateSut(VersionAnswering("ws://127.0.0.1:9222/devtools/page/TAB1"));

        var result = await sut.GetBrowserVersionAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("/devtools/browser/");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/devtools/browser/B1")]
    [InlineData(" ")]
    [InlineData("not a url")]
    public async Task GetBrowserVersionAsync_IsAProtocolError_WithoutABrowserUrl(string? listed)
    {
        // Chromium always lists an absolute ws:// URL; a program that answers /json/version without one is not a
        // browser's DevTools endpoint, and asking it again will not change that (CdpUnavailable would be asked again)
        var sut = CreateSut(VersionAnswering(listed));

        var result = await sut.GetBrowserVersionAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("webSocketDebuggerUrl").And.Contain("127.0.0.1:9222");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"Chrome\"")]
    public async Task GetBrowserVersionAsync_IsAProtocolError_WhenTheAnswerIsNotAnObject(string json)
    {
        var sut = CreateSut(FakeCdpHttpHandler.Json(json));

        var result = await sut.GetBrowserVersionAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("/json/version");
    }

    [Fact]
    public async Task GetBrowserVersionAsync_IsCdpUnavailable_WhenNothingListens()
    {
        var sut = CreateSut(FakeCdpHttpHandler.ConnectionRefused());

        var result = await sut.GetBrowserVersionAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
    }

    [Fact]
    public async Task GetBrowserVersionAsync_IsCdpTimeout_WithinItsTimeout()
    {
        var sut = CreateSut(FakeCdpHttpHandler.NeverAnswers());
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.GetBrowserVersionAsync(Port, ShortTimeout);

        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
    }

    [Fact]
    public async Task GetBrowserVersionAsync_IsCdpResponseTooLarge_OverTheBodyLimit()
    {
        var sut = CreateSut(VersionAnswering(), limits: new CdpTransportLimits(MaxHttpBodyBytes: 64));

        var result = await sut.GetBrowserVersionAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task GetBrowserVersionAsync_RequestsNothing_ForAnInvalidPort(int port)
    {
        var http = VersionAnswering();
        var sut = CreateSut(http);

        var result = await sut.GetBrowserVersionAsync(port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        http.Requests.Should().BeEmpty();
    }

    #endregion
}
