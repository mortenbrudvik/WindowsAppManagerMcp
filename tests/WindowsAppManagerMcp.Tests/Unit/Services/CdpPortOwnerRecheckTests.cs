namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The DevTools port's listener is read again after each request and once each connection is open (review F4): the owner
/// is checked before a request, but a browser that exits meanwhile frees its port, and another program on this computer
/// that takes it would answer the request or accept the connection. A tab list is used (the reads', list_tabs' and
/// browser_navigate's new tab's), and a command sent on any connection (the reads, the tab tools, the DOM bridge,
/// close_debug_browser), only while the listener is still the process that was checked; a listener table that cannot be
/// read fails closed. The browser is the in-process fake behind the real CdpTransport; the listener is a fake that changes
/// between reads.
/// </summary>
public class CdpPortOwnerRecheckTests
{
    private const int Port = 9222;
    private const int BrowserPid = 4242;
    private const int OtherPid = 999;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The port's listener as each read finds it, in order; the last answer repeats.</summary>
    private sealed class ChangingPortOwner(params (bool Readable, int? Owner)[] answers) : ITcpPortOwnerResolver
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public int? GetListeningProcessId(int port) => TryGetListeningProcessId(port, out var owner) ? owner : null;

        public bool TryGetListeningProcessId(int port, out int? processId)
        {
            var answer = answers[Math.Min(Interlocked.Increment(ref _reads) - 1, answers.Length - 1)];
            processId = answer.Readable ? answer.Owner : null;
            return answer.Readable;
        }
    }

    private static CdpService CreateSut(FakeCdpHttpHandler http, ICdpWebSocketFactory sockets, ITcpPortOwnerResolver owners) =>
        new(new CdpTransport(http, sockets, owners), owners);

    /// <summary>The browser's process listening on the port.</summary>
    private static readonly (bool Readable, int? Owner) Browser = (true, BrowserPid);

    /// <summary>The reads, the tab tools and the DOM bridge over one transport and one listener, as the server builds them.</summary>
    private static (CdpService Cdp, CdpTabService Tabs, CdpPageService Pages) CreateAll(FakeCdpBrowser browser, ITcpPortOwnerResolver owners)
    {
        var transport = new CdpTransport(browser.Http, browser.Sockets, owners);
        var cdp = new CdpService(transport, owners);
        return (cdp, new CdpTabService(cdp, transport, owners), new CdpPageService(transport));
    }

    /// <summary>A tab as the reads find it: listed by the browser whose process was checked on the port.</summary>
    private static CdpTarget OwnedTab(string id = "T1") =>
        new(id, "page", "Inbox", "https://mail.example/", $"ws://127.0.0.1:{Port}/devtools/page/{id}", Port, BrowserPid);

    /// <summary>The input gate, always taken.</summary>
    private static Task<IDisposable> Gate(CancellationToken cancellationToken) => Task.FromResult<IDisposable>(new Released());

    private sealed class Released : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>What the second read finds: another process, nothing, or a table that cannot be read.</summary>
    public static TheoryData<bool, int?> ListenerAfterTheRequest() => new()
    {
        { true, OtherPid },
        { true, null },
        { false, null }
    };

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task FindTargetForWindow_ATabListAnsweredAfterTheBrowserLeftItsPort_IsNotUsed(bool readable, int? owner)
    {
        // Arrange
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var owners = new ChangingPortOwner((true, BrowserPid), (readable, owner));

        // Act
        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), owners)
            .FindTargetForWindowAsync(Port, new CdpWindow(BrowserPid, "Inbox - Google Chrome"), LongTimeout);

        // Assert - the list may be another program's: its tabs (and their WebSocket URLs) are not used
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain($"CDP port {Port}").And.Contain("Nothing was read");
        owners.Reads.Should().Be(2);
    }

    [Fact]
    public async Task FindTargetForWindow_ABrowserStillOnItsPort_IsRead()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var owners = new ChangingPortOwner((true, BrowserPid));

        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), owners)
            .FindTargetForWindowAsync(Port, new CdpWindow(BrowserPid, "Inbox - Google Chrome"), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be("A");
        owners.Reads.Should().Be(2, "once before the request and once after it");
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task FindTargetById_ATabListAnsweredAfterTheBrowserLeftItsPort_IsNotUsed(bool readable, int? owner)
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));
        var owners = new ChangingPortOwner((true, BrowserPid), (readable, owner));

        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), owners)
            .FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("Nothing was read");
    }

    [Fact]
    public async Task FindTargetById_ABrowserStillOnItsPort_IsRead()
    {
        var http = FakeCdpHttpHandler.Targets(("A", "Inbox", "https://mail.example/"));

        var result = await CreateSut(http, FakeCdpWebSocketFactory.NeverConnects(), new ChangingPortOwner((true, BrowserPid)))
            .FindTargetByIdAsync(Port, BrowserPid, "A", LongTimeout);

        result.Success.Should().BeTrue(result.Error);
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task CloseBrowser_AConnectionOpenedAfterTheBrowserLeftItsPort_SendsNothing(bool readable, int? owner)
    {
        // Arrange - the connection may be another program's: Browser.close is not sent on it
        var browser = new FakeCdpBrowser();
        var owners = new ChangingPortOwner((true, BrowserPid), (readable, owner));

        // Act
        var result = await CreateSut(browser.Http, browser.Sockets, owners).CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        result.Error.Should().Contain("The connection was closed. Nothing was sent on it.",
            "close_debug_browser puts this after 'It was ended without Browser.close:', where 'It was closed' would read as the browser");
        browser.CloseCommands.Should().Be(0);
        browser.Socket.WasDisposed.Should().BeTrue("the connection is closed");
    }

    [Fact]
    public async Task CloseBrowser_ABrowserStillOnItsPort_IsSentBrowserClose()
    {
        var browser = new FakeCdpBrowser();
        var owners = new ChangingPortOwner((true, BrowserPid));

        var result = await CreateSut(browser.Http, browser.Sockets, owners).CloseBrowserAsync(Port, BrowserPid, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        browser.CloseCommands.Should().Be(1);
        owners.Reads.Should().Be(2);
    }

    #region Every connection: the listener is read again once the WebSocket is open, before its first command

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task Transport_AConnectionOpenedAfterTheBrowserLeftItsPort_IsClosed_AndGetsNoCommand(bool readable, int? owner)
    {
        // Arrange - the browser's process was checked; another program took the port before the connection opened
        var socket = new FakeCdpWebSocket();
        var owners = new ChangingPortOwner((readable, owner));
        var transport = new CdpTransport(FakeCdpHttpHandler.Targets(), new FakeCdpWebSocketFactory(socket), owners);

        // Act
        var connected = await transport.ConnectAsync(
            new Uri($"ws://127.0.0.1:{Port}/devtools/page/T1"), Port, BrowserPid, "tab T1", LongTimeout, CancellationToken.None);

        // Assert
        connected.Success.Should().BeFalse();
        connected.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        connected.Error.Should().Contain($"CDP port {Port}").And.Contain("tab T1")
            .And.Contain("The connection was closed. Nothing was sent on it.").And.NotContain("It was closed");
        socket.SentCommands.Should().BeEmpty();
        socket.WasDisposed.Should().BeTrue("the connection is closed");
        owners.Reads.Should().Be(1);
    }

    [Fact]
    public async Task Transport_AConnectionToTheBrowserStillOnItsPort_IsOpen()
    {
        var socket = new FakeCdpWebSocket();
        var owners = new ChangingPortOwner(Browser);
        var transport = new CdpTransport(FakeCdpHttpHandler.Targets(), new FakeCdpWebSocketFactory(socket), owners);

        var connected = await transport.ConnectAsync(
            new Uri($"ws://127.0.0.1:{Port}/devtools/page/T1"), Port, BrowserPid, "tab T1", LongTimeout, CancellationToken.None);

        connected.Success.Should().BeTrue(connected.Error);
        await connected.Value!.DisposeAsync();
        owners.Reads.Should().Be(1, "once the connection is open");
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task GetPageSnapshot_OfATabWhosePortChangedHandsBeforeItsConnection_SendsNoRuntimeEvaluate(bool readable, int? owner)
    {
        // Arrange - the tab is found while the browser listens (before and after its tab list), then the port changes hands
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1).Visibility("T1", "visible");
        var owners = new ChangingPortOwner(Browser, Browser, (readable, owner));
        var (cdp, _, _) = CreateAll(browser, owners);
        var tab = await cdp.FindTargetByIdAsync(Port, BrowserPid, "T1", LongTimeout);
        tab.Success.Should().BeTrue(tab.Error);

        // Act
        var snapshot = await cdp.GetPageSnapshotAsync(tab.Value!, new CdpPageRequest(true, 0, 1_000), LongTimeout);

        // Assert - what took the port gets no command: no Page.enable, no Runtime.evaluate
        snapshot.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        snapshot.Error.Should().Contain("Nothing was sent");
        browser.PageCommands.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task BrowserType_OnATabWhosePortChangedHands_SendsNoInsertText(bool readable, int? owner)
    {
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var (_, _, pages) = CreateAll(browser, new ChangingPortOwner((readable, owner)));

        var typed = await pages.TypeAsync(OwnedTab(), new BrowserTypeOptions("my password", "#q", false, true), Gate, LongTimeout);

        typed.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpPortOwnerMismatch));
        browser.PageCommands.Should().BeEmpty("no Input.insertText, nor any other command, reaches what took the port");
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task BrowserEvaluate_OnATabWhosePortChangedHands_SendsNoRuntimeEvaluate(bool readable, int? owner)
    {
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var (_, _, pages) = CreateAll(browser, new ChangingPortOwner((readable, owner)));

        var evaluated = await pages.EvaluateAsync(OwnedTab(), "document.title", false, Gate, LongTimeout);

        evaluated.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpPortOwnerMismatch));
        browser.PageCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task BrowserClick_OnATabWhosePortChangedHands_SendsNoMouseEvent()
    {
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var (_, _, pages) = CreateAll(browser, new ChangingPortOwner((true, OtherPid)));

        var clicked = await pages.ClickAsync(OwnedTab(), new BrowserElementLocator("#save", null, null, null), MouseButton.Primary, 1, Gate,
            LongTimeout);

        clicked.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpPortOwnerMismatch));
        browser.PageCommands.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task OpenTab_WhenThePortChangesHandsBeforeTheBrowsersConnection_SendsNoCreateTarget(bool readable, int? owner)
    {
        // Arrange - the browser's process is checked, then another program takes the port before the browser's connection
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var owners = new ChangingPortOwner(Browser, (readable, owner));
        var (_, tabs, _) = CreateAll(browser, owners);

        // Act
        var opened = await tabs.OpenTabAsync(Port, BrowserPid, Gate, LongTimeout);

        // Assert
        opened.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        opened.Error.Should().Contain("Nothing was sent");
        browser.Created.Should().BeEmpty();
        browser.Socket.SentCommands.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task ActivateTab_WhenThePortChangedHands_SendsNoActivateTarget(bool readable, int? owner)
    {
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var (_, tabs, _) = CreateAll(browser, new ChangingPortOwner((readable, owner)));

        var activated = await tabs.ActivateAsync(OwnedTab(), Gate, LongTimeout);

        activated.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        browser.Activated.Should().BeEmpty();
        browser.Socket.SentCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task Navigate_AndWait_OnATabWhosePortChangedHands_SendNothing()
    {
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var (_, tabs, _) = CreateAll(browser, new ChangingPortOwner((true, OtherPid)));

        var navigated = await tabs.NavigateAsync(OwnedTab(), "https://example.com/", BrowserWaitUntil.Load, Gate, LongTimeout);
        var waited = await tabs.WaitAsync(OwnedTab(), new BrowserWaitCondition(null, "Inbox", null, null), LongTimeout, TimeSpan.FromSeconds(3));

        navigated.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        waited.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        browser.PageCommands.Should().BeEmpty("no Page.navigate and no Runtime.evaluate");
    }

    [Fact]
    public async Task ATabNoOwnerCheckListed_IsNeitherConnectedTo_NorSentAnything()
    {
        // Arrange - a tab from the plain tab list, which no listener read tied to the browser: no connection is opened to it
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1).Visibility("T1", "visible");
        var owners = new ChangingPortOwner(Browser);
        var (cdp, tabs, pages) = CreateAll(browser, owners);
        var unowned = (await cdp.GetPageTargetsAsync(Port, LongTimeout)).Value!.Single();

        // Act
        var snapshot = await cdp.GetPageSnapshotAsync(unowned, new CdpPageRequest(true, 0, 1_000), LongTimeout);
        var typed = await pages.TypeAsync(unowned, new BrowserTypeOptions("text", null, false, false), Gate, LongTimeout);
        var activated = await tabs.ActivateAsync(unowned, Gate, LongTimeout);

        // Assert - fails closed
        unowned.OwnerProcessId.Should().BeNull();
        snapshot.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        typed.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpPortOwnerMismatch));
        activated.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        browser.Sockets.ConnectedUris.Should().BeEmpty();
    }

    [Fact]
    public async Task TheWindowBinding_OverAConnectionOpenedAfterThePortChangedHands_SendsNoGetWindowForTarget()
    {
        // Arrange - the window's bounds ask for the binding; the port changes hands after the tab list was read
        var browser = new FakeCdpBrowser().Window(1, 0, 0, 800, 600).Tab("A", "Inbox", 1).Tab("B", "Docs", 1);
        var owners = new ChangingPortOwner(Browser, Browser, (true, OtherPid));
        var (cdp, _, _) = CreateAll(browser, owners);
        var window = new CdpWindow(BrowserPid, "Inbox - Google Chrome", new CdpWindowGeometry(0, 0, 800, 600, CdpWindowState.Normal, 1.0));

        // Act
        await cdp.FindTargetForWindowAsync(Port, window, LongTimeout);

        // Assert
        browser.WindowQueries.Should().Be(0);
    }

    [Fact]
    public async Task WhichTabIsShown_IsNotReadOverConnectionsOpenedAfterThePortChangedHands()
    {
        // Arrange - two tabs with the window's title in one browser window: their visibility would be read on their own
        // connections, which open after the port changed hands
        var browser = new FakeCdpBrowser().Window(1, 0, 0, 800, 600)
            .Tab("B", "Docs", 1).Visibility("B", "hidden")
            .Tab("C", "Docs", 1).Visibility("C", "visible");
        var owners = new ChangingPortOwner(Browser, Browser, Browser, (true, OtherPid));
        var (cdp, _, _) = CreateAll(browser, owners);
        var window = new CdpWindow(BrowserPid, "Docs - Google Chrome", new CdpWindowGeometry(0, 0, 800, 600, CdpWindowState.Normal, 1.0));

        // Act
        var found = await cdp.FindTargetForWindowAsync(Port, window, LongTimeout);

        // Assert - none is taken, and neither tab got a command
        found.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetAmbiguous);
        browser.PageCommands.Should().BeEmpty();
    }

    #endregion

    #region The tab tools' tab lists: the listener is read again once the list has come

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task ListTabs_ATabListAnsweredAfterTheBrowserLeftItsPort_IsNotUsed(bool readable, int? owner)
    {
        // Arrange - the browser listens when the port is checked and when the list is asked for; then the port changes hands
        var browser = new FakeCdpBrowser().Window(1, 0, 0, 800, 600).Tab("A", "Inbox", 1);
        var owners = new ChangingPortOwner(Browser, Browser, (readable, owner));
        var (_, tabs, _) = CreateAll(browser, owners);

        // Act
        var listed = await tabs.ListTabsAsync(Port, BrowserPid, window: null, LongTimeout);

        // Assert - the list may be another program's: its tabs are not listed, and nothing more is asked
        listed.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        listed.Error.Should().Contain($"CDP port {Port}").And.Contain("Nothing was read");
        browser.WindowQueries.Should().Be(0);
    }

    [Fact]
    public async Task ListTabs_OfABrowserStillOnItsPort_ListsTabsTiedToIt()
    {
        var browser = new FakeCdpBrowser().Window(1, 0, 0, 800, 600).Tab("A", "Inbox", 1);
        var (_, tabs, _) = CreateAll(browser, new ChangingPortOwner(Browser));

        var listed = await tabs.ListTabsAsync(Port, BrowserPid, window: null, LongTimeout);

        listed.Success.Should().BeTrue(listed.Error);
        listed.Value!.Tabs.Should().ContainSingle().Which.Target.OwnerProcessId.Should().Be(BrowserPid);
    }

    [Theory]
    [MemberData(nameof(ListenerAfterTheRequest))]
    public async Task OpenTab_ATabListAnsweredAfterTheBrowserLeftItsPort_IsNotUsed_AndSaysTheTabWasOpened(bool readable, int? owner)
    {
        // Arrange - the browser opens the tab; the port changes hands before its tab list answers
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var owners = new ChangingPortOwner(Browser, Browser, Browser, (readable, owner));
        var (_, tabs, _) = CreateAll(browser, owners);

        // Act
        var opened = await tabs.OpenTabAsync(Port, BrowserPid, Gate, LongTimeout);

        // Assert - its listed WebSocket URL may be another program's, so the tab is not handed on to be navigated
        opened.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        browser.Created.Should().ContainSingle();
        opened.Error.Should().Contain("opened tab NEW1").And.Contain("about:blank").And.Contain("not navigated");
        opened.OpenedTargetId.Should().Be("NEW1", "browser_navigate names the tab it leaves on about:blank");
    }

    [Fact]
    public async Task OpenTab_OfABrowserStillOnItsPort_ReturnsTheNewTabTiedToIt()
    {
        var browser = new FakeCdpBrowser().Tab("T1", "Inbox", 1);
        var (_, tabs, _) = CreateAll(browser, new ChangingPortOwner(Browser));

        var opened = await tabs.OpenTabAsync(Port, BrowserPid, Gate, LongTimeout);

        opened.Success.Should().BeTrue(opened.Error);
        opened.Value!.OwnerProcessId.Should().Be(BrowserPid);
    }

    #endregion
}
