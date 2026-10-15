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

    /// <summary>The whole page's text, as get_browser_content's first page asks for it.</summary>
    private static readonly CdpPageRequest TextPage = new(TextOnly: true, Offset: 0, MaxLength: BrowserContentOptions.DefaultMaxLength);

    /// <summary>The whole page's HTML.</summary>
    private static readonly CdpPageRequest HtmlPage = TextPage with { TextOnly = false };

    /// <summary>What the snapshot script returns for a page whose whole content is <paramref name="content"/>.</summary>
    private static object Snapshot(string title, string url, string content) =>
        new { title, url, content, offset = 0, totalLength = content.Length, matches = (int?)null };

    private static CdpService CreateSut(
        FakeCdpHttpHandler? http = null,
        ICdpWebSocketFactory? sockets = null,
        int? portOwner = BrowserPid,
        CdpTransportLimits? limits = null,
        bool listenerTableReadable = true) =>
        new(
            new CdpTransport(http ?? FakeCdpHttpHandler.Targets(), sockets ?? FakeCdpWebSocketFactory.NeverConnects(), limits),
            new FakePortOwnerResolver(portOwner, listenerTableReadable));

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

    #region CloseBrowserAsync (roadmap M5.4)

    [Theory]
    [InlineData(FakeBrowserClose.ReplyThenClose)]
    [InlineData(FakeBrowserClose.CloseWithoutReply)]
    public async Task CloseBrowserAsync_SendsBrowserClose_OverTheBrowsersOwnConnection(FakeBrowserClose answer)
    {
        // Arrange
        var browser = new FakeCdpBrowser { CloseAnswer = answer };

        // Act
        var result = await CreateSut(browser).CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        browser.CloseCommands.Should().Be(1);
        browser.Socket.WasDisposed.Should().BeTrue("nothing outlives the call");
    }

    [Fact]
    public async Task CloseBrowserAsync_ARefusal_IsCdpProtocolError()
    {
        var browser = new FakeCdpBrowser { CloseAnswer = FakeBrowserClose.Refuse };

        var result = await CreateSut(browser).CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("Browser.close is not allowed");
    }

    [Fact]
    public async Task CloseBrowserAsync_ABrowserThatNeverAnswers_WasSent_ButNotAnswered()
    {
        // Browser.close is on the socket: the browser may be closing, so the caller still waits for its exit
        var browser = new FakeCdpBrowser { CloseAnswer = FakeBrowserClose.Never };

        var result = await CreateSut(browser).CloseBrowserAsync(Port, BrowserPid, ShortTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value.Should().BeFalse("no answer came");
        browser.CloseCommands.Should().Be(1);
    }

    [Theory]
    [InlineData(999, true)]
    [InlineData(null, false)]
    public async Task CloseBrowserAsync_APortThatIsNotTheBrowsersOwn_SendsNothing(int? owner, bool tableReadable)
    {
        // Arrange - another process listens on the port, or which one cannot be read: a browser-level connection controls
        // a whole browser, so only the browser's own port is used
        var browser = new FakeCdpBrowser();

        // Act
        var result = await CreateSut(browser.Http, browser.Sockets, owner, listenerTableReadable: tableReadable)
            .CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("nothing was sent");
        browser.CloseCommands.Should().Be(0);
    }

    [Fact]
    public async Task CloseBrowserAsync_APortNothingListensOn_IsCdpUnavailable()
    {
        var browser = new FakeCdpBrowser();

        var result = await CreateSut(browser.Http, browser.Sockets, portOwner: null).CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        browser.CloseCommands.Should().Be(0);
    }

    #endregion

    #region FindTargetForWindowAsync

    private static CdpWindow TitleOnly(string title) => new(BrowserPid, title);

    /// <summary>A normal window at the pixels of <see cref="FakeCdpBrowser"/>'s DIP bounds at 100 %.</summary>
    private static CdpWindow At(string title, int left, int top, int width, int height,
        CdpWindowState state = CdpWindowState.Normal, double scale = 1.0) =>
        new(BrowserPid, title, new CdpWindowGeometry(left, top, left + width, top + height, state, scale));

    private static CdpService CreateSut(FakeCdpBrowser browser, int? portOwner = BrowserPid) =>
        CreateSut(browser.Http, browser.Sockets, portOwner);

    [Fact]
    public async Task FindTargetForWindowAsync_ReturnsTabShownByWindow()
    {
        var http = FakeCdpHttpHandler.Targets(
            ("A", "Google", "https://www.google.com/"),
            ("B", "GitHub", "https://github.com/"));
        var sut = CreateSut(http);

        var result = await sut.FindTargetForWindowAsync(Port, TitleOnly("GitHub - Google Chrome"), LongTimeout);

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

        var result = await sut.FindTargetForWindowAsync(Port, TitleOnly("GitHub - Google Chrome"), LongTimeout);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
    }

    [Fact]
    public async Task FindTargetForWindowAsync_ReturnsPortOwnerMismatch_WhenPortBelongsToAnotherProcess()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: 999);

        var result = await sut.FindTargetForWindowAsync(Port, TitleOnly("Inbox - Google Chrome"), LongTimeout);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("999").And.Contain(BrowserPid.ToString())
            .And.Contain("the cdpPort that launch_browser_with_debug returned").And.Contain("leave cdpPort out")
            .And.NotContain("port given to");
        http.Requests.Should().BeEmpty("a port owned by another process must not be queried");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_FailsClosed_WhenPortOwnerIsUnknown()
    {
        // Roadmap M5.3: a title is no proof that what answers on the port is the window's browser, so the title path no
        // longer skips the check when the owner cannot be read (the targetId path never did)
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: null, listenerTableReadable: false);

        var result = await sut.FindTargetForWindowAsync(Port, TitleOnly("Inbox - Google Chrome"), LongTimeout);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("Could not read which process owns CDP port 9222").And.Contain("Nothing was read");
        http.Requests.Should().BeEmpty("nothing is asked of a port no process is shown to own");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task FindTargetForWindowAsync_RefusesAPortOutsideTheRange_BeforeReadingItsOwner(int port)
    {
        var owners = new Mock<ITcpPortOwnerResolver>(MockBehavior.Strict);
        var sut = new CdpService(new CdpTransport(FakeCdpHttpHandler.Targets(), FakeCdpWebSocketFactory.NeverConnects()), owners.Object);

        var result = await sut.FindTargetForWindowAsync(port, TitleOnly("Inbox"), LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        owners.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FindTargetForWindowAsync_PassesThroughTargetListErrors()
    {
        var sut = CreateSut(FakeCdpHttpHandler.NeverAnswers());

        var result = await sut.FindTargetForWindowAsync(Port, TitleOnly("Inbox - Google Chrome"), ShortTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
    }

    [Fact]
    public async Task FindTargetForWindowAsync_BindsTheWindowToItsBrowserWindow_WhenOtherWindowsShowTheSameTitle()
    {
        // Two windows show a tab titled Inbox: the title alone is ambiguous, the window's bounds are not
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 1000, 700)
            .Window(2, 400, 300, 1200, 800)
            .Tab("A", "Inbox", 1)
            .Tab("B", "Inbox", 2)
            .Tab("C", "News", 2);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(Port, At("Inbox - Google Chrome", 400, 300, 1200, 800), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be("B");
        browser.Sockets.ConnectedUris.Should().ContainSingle().Which.ToString().Should().Be(browser.BrowserUrl);
        browser.WindowQueries.Should().Be(3);
        browser.Socket.WasDisposed.Should().BeTrue("the browser's connection does not outlive the call");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_ScalesTheBrowsersDipsByTheWindowsDpi()
    {
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 1000, 700)
            .Window(2, 400, 300, 1200, 800)
            .Tab("A", "Inbox", 1)
            .Tab("B", "Inbox", 2);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(
            Port, At("Inbox - Google Chrome", 600, 450, 1800, 1200, scale: 1.5), LongTimeout);

        result.Value!.Id.Should().Be("B");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_ReturnsAmbiguous_WhenTheWindowsOwnTabsShareTheTitle()
    {
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 1000, 700)
            .Window(2, 400, 300, 1200, 800)
            .Tab("A", "Inbox", 1)
            .Tab("B", "Inbox", 2)
            .Tab("C", "Inbox", 2);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(Port, At("Inbox - Google Chrome", 400, 300, 1200, 800), LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetAmbiguous);
        result.Error.Should().Contain("targetId B").And.Contain("targetId C").And.NotContain("targetId A");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_MatchesAMaximizedWindow_AmongTheMaximizedOnes()
    {
        // A maximized window reports its restored bounds, so only its state can be compared
        var browser = new FakeCdpBrowser()
            .Window(1, 100, 100, 800, 600, "maximized")
            .Window(2, 100, 100, 800, 600, "normal")
            .Tab("A", "Inbox", 1)
            .Tab("B", "Inbox", 2);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(
            Port, At("Inbox - Google Chrome", -8, -8, 1936, 1056, CdpWindowState.Maximized), LongTimeout);

        result.Value!.Id.Should().Be("A");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_FallsBackToTheTitleAmongAllTabs_WhenNoBrowserWindowMatches()
    {
        // For example a window on a monitor whose scale differs from the primary one's: as before M5.3
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 1000, 700)
            .Window(2, 400, 300, 1200, 800)
            .Tab("A", "Inbox", 1)
            .Tab("B", "News", 2);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(Port, At("News - Google Chrome", 3000, 200, 900, 600), LongTimeout);

        result.Value!.Id.Should().Be("B");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_FallsBackToTheTitleAmongAllTabs_WhenTheBoundWindowHasNoTabWithTheTitle()
    {
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 1000, 700)
            .Window(2, 400, 300, 1200, 800)
            .Tab("A", "Inbox", 1)
            .Tab("B", "News", 2);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(Port, At("Inbox - Google Chrome", 400, 300, 1200, 800), LongTimeout);

        result.Value!.Id.Should().Be("A");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_FallsBackToTheTitle_WhenTheBrowsersEndpointCannotBeUsed()
    {
        var browser = new FakeCdpBrowser { NotABrowsersVersion = true }
            .Window(1, 0, 0, 1000, 700)
            .Tab("A", "Inbox", 1)
            .Tab("B", "News", 1);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(Port, At("Inbox - Google Chrome", 0, 0, 1000, 700), LongTimeout);

        result.Value!.Id.Should().Be("A");
        browser.Sockets.ConnectedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task FindTargetForWindowAsync_LeavesOutPagesThatAreInNoBrowserWindow()
    {
        // Browser.getWindowForTarget refuses a page that is not a tab of a tab strip
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 1000, 700)
            .Tab("A", "Inbox", 1)
            .Tab("P", "Inbox", windowId: null);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(Port, At("Inbox - Google Chrome", 0, 0, 1000, 700), LongTimeout);

        result.Value!.Id.Should().Be("A");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_ReturnsAmbiguous_WhenATabOfTheBoundWindowIsNotAnswered()
    {
        // A reply lost to the time budget is no proof that the tab is in no window: a partial binding would return A,
        // which the window may not show. The binding is unreadable, so the title decides among all tabs, as before M5.3
        var browser = new FakeCdpBrowser { Unanswered = ["B"] }
            .Window(1, 0, 0, 1000, 700)
            .Window(2, 400, 300, 1200, 800)
            .Tab("A", "Inbox", 1)
            .Tab("B", "Inbox", 1)
            .Tab("C", "News", 2);
        var sut = CreateSut(browser);

        var result = await sut.FindTargetForWindowAsync(
            Port, At("Inbox - Google Chrome", 0, 0, 1000, 700), TimeSpan.FromMilliseconds(600));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetAmbiguous, result.Error);
        result.Error.Should().Contain("targetId A").And.Contain("targetId B");
    }

    [Fact]
    public async Task FindTargetForWindowAsync_LeavesMostOfTheBudget_WhenTheBrowsersConnectionStalls()
    {
        // The binding is best effort: a browser connection that never answers gets a share of the budget, so the title
        // still finds the tab with time left to read its page
        var browser = new FakeCdpBrowser { Unanswered = null }
            .Window(1, 0, 0, 1000, 700)
            .Tab("A", "Inbox", 1)
            .Tab("B", "News", 1);
        var sut = CreateSut(browser);
        var budget = TimeSpan.FromMilliseconds(1500);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.FindTargetForWindowAsync(Port, At("Inbox - Google Chrome", 0, 0, 1000, 700), budget);

        stopwatch.Stop();
        result.Value!.Id.Should().Be("A");
        browser.WindowQueries.Should().Be(2);
        stopwatch.Elapsed.Should().BeLessThan(budget * 0.8, "the binding may use only a share of the budget");
    }

    [Theory]
    [InlineData(9000, 2000)]
    [InlineData(3000, 1000)]
    [InlineData(300, 100)]
    [InlineData(0, 0)]
    public void BindingBudget_IsAThirdOfWhatIsLeft_AndAtMostTwoSeconds(int remainingMs, int expectedMs)
    {
        CdpService.BindingBudget(TimeSpan.FromMilliseconds(remainingMs)).Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public async Task FindTargetForWindowAsync_ReturnsCdpUnavailable_WhenNothingListensOnThePort()
    {
        // A wrong or stale cdpPort: nothing to tie to the window, but no other process holds it either
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: null);

        var result = await sut.FindTargetForWindowAsync(Port, TitleOnly("Inbox - Google Chrome"), LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("Nothing listens on 127.0.0.1:9222").And.Contain("Nothing was read");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FindTargetForWindowAsync_DoesNotConnectToTheBrowser_ForOneTabOrWithoutTheWindowsBounds()
    {
        var oneTab = new FakeCdpBrowser().Window(1, 0, 0, 1000, 700).Tab("A", "Inbox", 1);
        var twoTabs = new FakeCdpBrowser().Window(1, 0, 0, 1000, 700).Tab("A", "Inbox", 1).Tab("B", "News", 1);

        (await CreateSut(oneTab).FindTargetForWindowAsync(Port, At("Inbox", 0, 0, 1000, 700), LongTimeout)).Value!.Id.Should().Be("A");
        (await CreateSut(twoTabs).FindTargetForWindowAsync(Port, TitleOnly("Inbox"), LongTimeout)).Value!.Id.Should().Be("A");

        oneTab.Sockets.ConnectedUris.Should().BeEmpty();
        twoTabs.Sockets.ConnectedUris.Should().BeEmpty();
    }

    #endregion

    #region GetPageSnapshotAsync

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsTitleUrlAndContentFromOneEvaluate()
    {
        var socket = PageAnswering(Snapshot("Inbox", "https://mail.example/#inbox", "3 unread"));
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(new CdpPageSnapshot("Inbox", "https://mail.example/#inbox", "3 unread", Offset: 0, TotalLength: 8));
        socket.SentCommands.Count(c => c.GetProperty("method").GetString() == "Runtime.evaluate").Should().Be(1);
    }

    [Fact]
    public async Task GetPageSnapshotAsync_SendsEvaluateWithReturnByValueAndTimeout()
    {
        var socket = PageAnswering(Snapshot("t", "u", "c"));
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        await sut.GetPageSnapshotAsync(Tab, new CdpPageRequest(TextOnly: true, Offset: 0, MaxLength: 50), TimeSpan.FromSeconds(7));

        var parameters = socket.EvaluateCommand!.Value.GetProperty("params");
        parameters.GetProperty("returnByValue").GetBoolean().Should().BeTrue();
        parameters.GetProperty("timeout").GetInt32().Should().Be(7000);
        var expression = parameters.GetProperty("expression").GetString();
        expression.Should().Contain("innerText").And.Contain("const max = 50;");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_RequestsHtml_WhenNotTextOnly()
    {
        var socket = PageAnswering(Snapshot("t", "u", "<html></html>"));
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        await sut.GetPageSnapshotAsync(Tab, HtmlPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, ShortTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, ShortTimeout);

        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("WebSocket");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_Throws_WhenCallerCancels()
    {
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(new FakeCdpWebSocket()));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReassemblesMessageSplitAcrossFrames()
    {
        // 5-byte frames split multi-byte UTF-8 characters between frames.
        const string content = "Blåbærsyltetøy – 日本語 😀 done";
        var socket = PageAnswering(Snapshot("Ærlig talt", "https://example.com/æ", content), frameSize: 5);
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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
                s.ReplyWithValue(command, Snapshot("t", "u", "the content"));
            }
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
        result.Error.Should().Contain("JavaScript confirm dialog")
            .And.Contain("untrusted")
            .And.Contain("Do not press Enter")
            .And.Contain("browser_handle_dialog")
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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
        result.Error.Should().Contain("target_closed").And.Contain("without targetId");
        socket.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsResponseTooLarge_WhenTheAnswerIsOverTheLimit()
    {
        var socket = PageAnswering(Snapshot("t", "u", new string('a', 4096)));
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket), limits: new CdpTransportLimits(MaxMessageBytes: 1024));

        var result = await sut.GetPageSnapshotAsync(Tab, HtmlPage, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
        result.Error.Should().Contain("maxLength");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_Succeeds_WhenPageEnableIsNeverAnswered()
    {
        // A page may never answer Page.enable; the read does not wait for it
        var socket = PageAnswering(Snapshot("t", "u", "read anyway"));
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, ShortTimeout);

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
                s.ReplyWithValue(command, Snapshot("t", "u", "c"));
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, ShortTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("Page.enable was refused: Page domain is not available (-32000)");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_RefusesAWebSocketUrlOnAnotherPortThanTheTabsOwn()
    {
        var factory = new FakeCdpWebSocketFactory(PageAnswering(Snapshot("t", "u", "c")));
        var sut = CreateSut(sockets: factory);

        var result = await sut.GetPageSnapshotAsync(
            Tab with { WebSocketDebuggerUrl = "ws://127.0.0.1:9333/devtools/page/TAB1" }, TextPage, LongTimeout);

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

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ws://203.0.113.5:9222/devtools/page/TAB1")]
    [InlineData("http://127.0.0.1:9222/devtools/page/TAB1")]
    public async Task GetPageSnapshotAsync_RefusesMissingOrNonLoopbackWebSocketUrl(string? webSocketUrl)
    {
        var factory = new FakeCdpWebSocketFactory(PageAnswering(Snapshot("t", "u", "c")));
        var sut = CreateSut(sockets: factory);

        var result = await sut.GetPageSnapshotAsync(Tab with { WebSocketDebuggerUrl = webSocketUrl }, TextPage, LongTimeout);

        result.Success.Should().BeFalse();
        factory.ConnectedUris.Should().BeEmpty();
    }

    #endregion

    #region Paged content (roadmap M5.6)

    /// <summary>
    /// A fake page whose whole content is <paramref name="whole"/>: it answers Runtime.evaluate as the snapshot script does,
    /// with the slice the script's offset and max ask for, where it starts, and the whole length.
    /// </summary>
    private static FakeCdpWebSocket PageOf(string whole) =>
        new((socket, command) =>
        {
            if (command.GetProperty("method").GetString() != "Runtime.evaluate")
                return;

            var expression = command.GetProperty("params").GetProperty("expression").GetString()!;
            var match = System.Text.RegularExpressions.Regex.Match(expression, @"const offset = (\d+); const max = (\d+);");
            var start = Math.Min(int.Parse(match.Groups[1].Value), whole.Length);
            var end = Math.Min(whole.Length, start + int.Parse(match.Groups[2].Value));
            socket.ReplyWithValue(command, new
            {
                title = "t", url = "u", content = whole[start..end], offset = start, totalLength = whole.Length, matches = (int?)null
            });
        });

    [Fact]
    public async Task GetPageSnapshotAsync_AsksThePageForTheSlice_AndForItsWholeLength()
    {
        // Arrange
        var socket = PageAnswering(Snapshot("t", "u", "c"));
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        // Act
        await sut.GetPageSnapshotAsync(Tab, new CdpPageRequest(TextOnly: true, Offset: 120, MaxLength: 50), LongTimeout);

        // Assert - the page slices, so only the slice crosses the connection
        var expression = socket.EvaluateCommand!.Value.GetProperty("params").GetProperty("expression").GetString();
        expression.Should().Contain("document.body.innerText").And.Contain("const offset = 120; const max = 50;")
            .And.Contain("content.slice(start, end)").And.Contain("totalLength: total")
            .And.NotContain("querySelectorAll", "no selector reads the whole body");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsTheSlice_WithWhereItStarts_AndTheLengthThePageMeasured()
    {
        // Arrange - a page of 100 million characters, larger than the 64 MB message limit: only its slice is sent
        var socket = PageAnswering(new { title = "t", url = "u", content = "bcd", offset = 1, totalLength = 100_000_000, matches = (int?)null });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        // Act
        var result = await sut.GetPageSnapshotAsync(Tab, TextPage with { Offset = 1, MaxLength = 3 }, LongTimeout);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Value.Should().Be(new CdpPageSnapshot("t", "u", "bcd", Offset: 1, TotalLength: 100_000_000));
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReadsAPageLargerThanTheMessageLimit_ASliceAtATime()
    {
        // Arrange - a message limit of 1 KB and a page of 4,096 characters: all of it at once does not fit
        var whole = string.Concat(Enumerable.Range(0, 4096).Select(i => (char)('a' + i % 26)));
        var sut = CreateSut(
            sockets: FakeCdpWebSocketFactory.EachConnect(() => PageOf(whole)), limits: new CdpTransportLimits(MaxMessageBytes: 1024));

        // Act
        var atOnce = await sut.GetPageSnapshotAsync(Tab, TextPage with { MaxLength = 4096 }, LongTimeout);
        var read = new System.Text.StringBuilder();
        for (var offset = 0; offset < whole.Length; offset += 500)
        {
            var slice = await sut.GetPageSnapshotAsync(Tab, TextPage with { Offset = offset, MaxLength = 500 }, LongTimeout);
            slice.Success.Should().BeTrue(slice.Error);
            slice.Value!.TotalLength.Should().Be(4096);
            read.Append(slice.Value.Content);
        }

        // Assert
        atOnce.ErrorCode.Should().Be(BrowserErrorCode.CdpResponseTooLarge);
        read.ToString().Should().Be(whole);
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ScopesTheReadToTheSelectorsMatches_OnePerLine()
    {
        // Arrange
        var socket = PageAnswering(new { title = "t", url = "u", content = "Title\nPart", offset = 0, totalLength = 10, matches = 2 });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        // Act
        var text = await sut.GetPageSnapshotAsync(Tab, TextPage with { Selector = "h1,h2" }, LongTimeout);
        var expression = socket.EvaluateCommand!.Value.GetProperty("params").GetProperty("expression").GetString();

        // Assert
        text.Value!.Content.Should().Be("Title\nPart");
        expression.Should().Contain("document.querySelectorAll(\"h1,h2\")").And.Contain(".join('\\n')").And.Contain("innerText")
            .And.NotContain("document.body.innerText");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetPageSnapshotAsync_JoinsTheSelectorsMatches_WithoutArrayFrom_WhichAPageCanReplace(bool textOnly)
    {
        // Arrange - Prototype.js and MooTools replace Array.from with one that ignores its map function, and the script
        // runs in the page's own world
        var socket = PageAnswering(new { title = "t", url = "u", content = "Title", offset = 0, totalLength = 5, matches = 1 });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        // Act
        await sut.GetPageSnapshotAsync(Tab, TextPage with { TextOnly = textOnly, Selector = "h1" }, LongTimeout);

        // Assert
        var expression = socket.EvaluateCommand!.Value.GetProperty("params").GetProperty("expression").GetString();
        expression.Should().NotContain("Array.from").And.Contain("for (let i = 0; i < found.length; i++)");
    }

    [Theory]
    [InlineData(0, 10, "abc")]   // asked for offset 5: a page that reports another start
    [InlineData(5, 3, "")]       // a start past the whole length
    [InlineData(5, 100, "abcdefghijk")] // more than maxLength (10)
    [InlineData(5, 7, "abc")]    // more than the whole length leaves after the start
    [InlineData(int.MaxValue, int.MaxValue, "a")] // a start whose next page would overflow
    public async Task GetPageSnapshotAsync_ReturnsProtocolError_WhenThePagesSliceDoesNotFitWhatWasAskedFor(
        int offset, int totalLength, string content)
    {
        // Arrange - the script runs in the page's world, where a page can replace Math.min or String.prototype.slice
        var socket = PageAnswering(new { title = "t", url = "u", content, offset, totalLength, matches = (int?)null });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        // Act
        var result = await sut.GetPageSnapshotAsync(Tab, TextPage with { Offset = 5, MaxLength = 10 }, LongTimeout);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("slice");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_TakesAStartAtTheEnd_OfAShorterText()
    {
        var socket = PageAnswering(new { title = "t", url = "u", content = "", offset = 3, totalLength = 3, matches = (int?)null });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage with { Offset = 50, MaxLength = 10 }, LongTimeout);

        result.Value.Should().Be(new CdpPageSnapshot("t", "u", "", Offset: 3, TotalLength: 3));
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReadsTheSelectorsMatchesHtml_WhenNotTextOnly()
    {
        var socket = PageAnswering(new { title = "t", url = "u", content = "<main></main>", offset = 0, totalLength = 13, matches = 1 });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        await sut.GetPageSnapshotAsync(Tab, HtmlPage with { Selector = "main" }, LongTimeout);

        var expression = socket.EvaluateCommand!.Value.GetProperty("params").GetProperty("expression").GetString();
        expression.Should().Contain("parts.push(e.outerHTML)").And.NotContain("innerText");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_PutsTheSelectorInTheScript_OnlyAsAStringLiteral()
    {
        // Arrange - a selector that would close the string and run its own script if it were pasted in
        const string selector = "a\"); window.hacked = true; (\"";
        var socket = PageAnswering(Snapshot("t", "u", "c"));
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        // Act
        await sut.GetPageSnapshotAsync(Tab, TextPage with { Selector = selector }, LongTimeout);

        // Assert
        var expression = socket.EvaluateCommand!.Value.GetProperty("params").GetProperty("expression").GetString();
        expression.Should().NotContain(selector).And.Contain($"document.querySelectorAll({System.Text.Json.JsonSerializer.Serialize(selector)})");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsContentExtractionFailed_WhenNoElementMatchesTheSelector()
    {
        var socket = PageAnswering(new { title = "t", url = "u", content = "", offset = 0, totalLength = 0, matches = 0 });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage with { Selector = "article" }, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.ContentExtractionFailed);
        result.Error.Should().Contain("'article'");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsEvaluationFailed_ForASelectorThatIsNotCss()
    {
        // Arrange - querySelectorAll throws a SyntaxError
        var socket = new FakeCdpWebSocket((s, command) =>
        {
            if (command.GetProperty("method").GetString() == "Runtime.evaluate")
            {
                s.Enqueue(System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = command.GetProperty("id").GetInt32(),
                    result = new
                    {
                        result = new { type = "object" },
                        exceptionDetails = new { text = "Uncaught", exception = new { description = "SyntaxError: '##' is not a valid selector." } }
                    }
                }));
            }
        });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        // Act
        var result = await sut.GetPageSnapshotAsync(Tab, TextPage with { Selector = "##" }, LongTimeout);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpEvaluationFailed);
        result.Error.Should().Contain("is not a valid selector");
    }

    [Fact]
    public async Task GetPageSnapshotAsync_ReturnsProtocolError_WhenTheAnswerDoesNotSayHowLongTheContentIs()
    {
        var socket = PageAnswering(new { title = "t", url = "u", content = "c" });
        var sut = CreateSut(sockets: new FakeCdpWebSocketFactory(socket));

        var result = await sut.GetPageSnapshotAsync(Tab, TextPage, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
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
        // An id carries no evidence that what answers on the port is the window's browser
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: null, listenerTableReadable: false);

        var result = await sut.FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("Could not read which process owns CDP port 9222");
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FindTargetByIdAsync_ReturnsCdpUnavailable_WhenNothingListensOnThePort()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var sut = CreateSut(http, portOwner: null);

        var result = await sut.FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().Contain("Nothing listens on 127.0.0.1:9222");
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
