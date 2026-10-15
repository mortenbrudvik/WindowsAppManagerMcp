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
    public async Task GetPageTargetsAsync_ReturnsCdpUnavailable_OnHttpError()
    {
        var sut = CreateSut(FakeCdpHttpHandler.Json("not found", HttpStatusCode.NotFound));

        var result = await sut.GetPageTargetsAsync(Port, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("404");
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
        result.Error.Should().Contain("999").And.Contain(BrowserPid.ToString());
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
}
