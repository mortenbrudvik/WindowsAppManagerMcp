using System.Diagnostics;
using System.Text.Json;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// CdpTabService (roadmap M5.5) over the in-memory fakes behind the real CdpTransport: a fake debug browser
/// (<see cref="FakeCdpBrowser"/>) for the tab list, the window binding, Target.activateTarget and Target.createTarget, and a
/// fake tab for Page.navigate, its load and the waits. No real browser or network is involved.
/// </summary>
public class CdpTabServiceTests
{
    private const int Port = 9222;
    private const int BrowserPid = 4242;

    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(10);

    private static readonly CdpTarget Tab = new("T1", "page", "Docs", "https://example.com/", "ws://127.0.0.1:9222/devtools/page/T1", Port, BrowserPid);

    private static CdpTabService CreateSut(
        FakeCdpHttpHandler http, ICdpWebSocketFactory sockets, int? portOwner = BrowserPid, bool listenerTableReadable = true)
    {
        var owners = new FakePortOwnerResolver(portOwner, listenerTableReadable);
        var transport = new CdpTransport(http, sockets, owners);
        return new CdpTabService(new CdpService(transport, owners), transport, owners);
    }

    private static CdpTabService CreateSut(FakeCdpBrowser browser, int? portOwner = BrowserPid, bool listenerTableReadable = true) =>
        CreateSut(browser.Http, browser.Sockets, portOwner, listenerTableReadable);

    private static CdpTabService CreateSut(FakeTab tab) =>
        CreateSut(FakeCdpHttpHandler.Targets(), new FakeCdpWebSocketFactory(tab.Socket));

    /// <summary>A normal window at the pixels of a browser window's DIP bounds at 100 %.</summary>
    private static CdpWindow At(string title, int left, int top, int width, int height) =>
        new(BrowserPid, title, new CdpWindowGeometry(left, top, left + width, top + height, CdpWindowState.Normal, 1.0));

    /// <summary>Two browser windows side by side: window 1 with tabs A (Inbox) and C (Docs), window 2 with tab B (Docs).</summary>
    private static FakeCdpBrowser TwoWindows() => new FakeCdpBrowser()
        .Window(1, 0, 0, 800, 600)
        .Window(2, 900, 0, 800, 600)
        .Tab("A", "Inbox", 1)
        .Tab("B", "Docs", 2)
        .Tab("C", "Docs", 1);

    #region ListTabsAsync

    [Fact]
    public async Task ListTabsAsync_WithoutAWindow_ListsEveryTab_WithTheWindowItIsIn()
    {
        var sut = CreateSut(TwoWindows());

        var result = await sut.ListTabsAsync(Port, BrowserPid, window: null, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Tabs.Select(t => (t.Target.Id, t.WindowId, t.InWindow)).Should().Equal(("A", 1, false), ("B", 2, false), ("C", 1, false));
        result.Value.WindowBound.Should().BeFalse();
        result.Value.Shown.Should().BeNull();
    }

    [Fact]
    public async Task ListTabsAsync_WithAWindow_ListsItsTabsFirst_AndTheTabItShows()
    {
        var sut = CreateSut(TwoWindows());

        var result = await sut.ListTabsAsync(Port, BrowserPid, At("Docs - Google Chrome", 0, 0, 800, 600), LongTimeout);

        // Window 1 shows Docs: tab C, not window 2's B with the same title
        result.Success.Should().BeTrue(result.Error);
        result.Value!.Tabs.Select(t => (t.Target.Id, t.InWindow)).Should().Equal(("A", true), ("C", true), ("B", false));
        result.Value.WindowBound.Should().BeTrue();
        result.Value.Shown!.Id.Should().Be("C");
        result.Value.ShownError.Should().BeNull();
    }

    [Fact]
    public async Task ListTabsAsync_TwoTabsOfTheWindowWithItsTitle_TellNoShownTab_AndSayWhy()
    {
        var browser = new FakeCdpBrowser().Window(1, 0, 0, 800, 600).Tab("A", "Same", 1).Tab("B", "Same", 1);
        var sut = CreateSut(browser);

        var result = await sut.ListTabsAsync(Port, BrowserPid, At("Same - Google Chrome", 0, 0, 800, 600), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Shown.Should().BeNull("CDP has no active-tab flag, the title does not tell, and which is shown cannot be read");
        result.Value.ShownError.Should().StartWith(nameof(BrowserErrorCode.CdpTargetAmbiguous));
        result.Value.WindowBound.Should().BeTrue();
    }

    [Fact]
    public async Task ListTabsAsync_ABackgroundTabTitledAsTheWholeWindow_IsNotTheShownTab()
    {
        // As the reads pick the tab: a page that sets its title to the window's whole caption does not become the shown tab
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 800, 600)
            .Tab("REAL", "Inbox", 1)
            .Tab("SPOOF", "Inbox - Google Chrome", 1)
            .Visibility("REAL", "visible")
            .Visibility("SPOOF", "hidden");
        var sut = CreateSut(browser);

        var result = await sut.ListTabsAsync(Port, BrowserPid, At("Inbox - Google Chrome", 0, 0, 800, 600), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Shown!.Id.Should().Be("REAL");
        result.Value.ShownError.Should().BeNull();
    }

    [Fact]
    public async Task ListTabsAsync_TwoTabsOfTheWindowWithItsTitle_TellTheOneThatIsShown()
    {
        var browser = new FakeCdpBrowser()
            .Window(1, 0, 0, 800, 600)
            .Tab("A", "Same", 1)
            .Tab("B", "Same", 1)
            .Visibility("A", "hidden")
            .Visibility("B", "visible");
        var sut = CreateSut(browser);

        var result = await sut.ListTabsAsync(Port, BrowserPid, At("Same - Google Chrome", 0, 0, 800, 600), LongTimeout);

        result.Value!.Shown!.Id.Should().Be("B");
    }

    [Fact]
    public async Task ListTabsAsync_WhenWhichWindowEachTabIsInCannotBeRead_ATabShownElsewhere_IsNotTheShownTab()
    {
        // As the reads: the window is covered (its tab hidden) and another window shows a tab with its title
        var browser = new FakeCdpBrowser { NotABrowsersVersion = true }
            .Window(1, 0, 0, 800, 600)
            .Window(2, 900, 0, 800, 600)
            .Tab("REAL", "Same", 1)
            .Tab("OTHER", "Same", 2)
            .Visibility("REAL", "hidden")
            .Visibility("OTHER", "visible");
        var sut = CreateSut(browser);

        var result = await sut.ListTabsAsync(Port, BrowserPid, At("Same - Google Chrome", 0, 0, 800, 600), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.WindowBound.Should().BeFalse();
        result.Value.Shown.Should().BeNull();
        result.Value.ShownError.Should().StartWith(nameof(BrowserErrorCode.CdpTargetAmbiguous))
            .And.Contain("which browser window each of them is in could not be read");
    }

    [Fact]
    public async Task ListTabsAsync_OverTheBoundTabLimit_ListsTheTabsWithoutWindows_ButStillTellsTheShownTab()
    {
        // CdpService.MaxBoundTabs (200) tabs and two more: too many to look up every one's browser window, so the list has
        // none; the windows of the two tabs with the window's title are still read, to tell which of them it shows
        var browser = new FakeCdpBrowser().Window(1, 0, 0, 800, 600);
        for (var i = 0; i < 200; i++)
            browser.Tab($"F{i}", $"Filler {i}", 1);
        browser.Tab("A", "Same", 1).Tab("B", "Same", 1).Visibility("A", "hidden").Visibility("B", "visible");
        var sut = CreateSut(browser);

        var result = await sut.ListTabsAsync(Port, BrowserPid, At("Same - Google Chrome", 0, 0, 800, 600), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Shown!.Id.Should().Be("B");
        result.Value.ShownError.Should().BeNull();
        result.Value.WindowBound.Should().BeFalse();
        result.Value.Tabs.Should().OnlyContain(t => t.WindowId == null && !t.InWindow);
        browser.WindowQueries.Should().Be(2);
    }

    [Fact]
    public async Task ListTabsAsync_AWindowThatMatchesNoBrowserWindow_IsNotBound_AndItsTitlePicksAmongAllTabs()
    {
        var sut = CreateSut(TwoWindows());

        var result = await sut.ListTabsAsync(Port, BrowserPid, At("Inbox - Google Chrome", 3000, 500, 640, 480), LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.WindowBound.Should().BeFalse();
        result.Value.Tabs.Should().OnlyContain(t => !t.InWindow);
        result.Value.Shown!.Id.Should().Be("A");
    }

    [Fact]
    public async Task ListTabsAsync_WhenTheBrowserDoesNotAnswerTheWindowLookup_ListsTheTabsWithoutWindows()
    {
        var browser = TwoWindows();
        browser.Unanswered = null;
        var sut = CreateSut(browser);

        var result = await sut.ListTabsAsync(Port, BrowserPid, window: null, TimeSpan.FromSeconds(2));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Tabs.Should().HaveCount(3).And.OnlyContain(t => t.WindowId == null);
    }

    [Fact]
    public async Task ListTabsAsync_APortOfAnotherProcess_IsCdpPortOwnerMismatch_AndAsksNothing()
    {
        var browser = TwoWindows();
        var sut = CreateSut(browser, portOwner: 999);

        var result = await sut.ListTabsAsync(Port, BrowserPid, window: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        browser.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTabsAsync_APortWhoseOwnerCannotBeRead_FailsClosed()
    {
        var browser = TwoWindows();
        var sut = CreateSut(browser, listenerTableReadable: false);

        var result = await sut.ListTabsAsync(Port, BrowserPid, window: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        browser.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTabsAsync_APortNothingListensOn_IsCdpUnavailable()
    {
        var sut = CreateSut(TwoWindows(), portOwner: null);

        var result = await sut.ListTabsAsync(Port, BrowserPid, window: null, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
    }

    #endregion

    #region ActivateAsync and OpenTabAsync

    [Fact]
    public async Task ActivateAsync_SendsTargetActivateTarget_OverTheBrowsersConnection_InsideTheGate()
    {
        var browser = TwoWindows();
        var gate = new RecordingGate();
        var sut = CreateSut(browser);
        var tab = new CdpTarget("B", "page", "Docs", "https://example.com/B", "ws://127.0.0.1:9222/devtools/page/B", Port, BrowserPid);

        var result = await sut.ActivateAsync(tab, gate.EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        browser.Activated.Should().Equal("B");
        browser.Sockets.ConnectedUris.Should().Equal(new Uri(browser.BrowserUrl));
        gate.Entered.Should().Be(1);
        gate.Held.Should().BeFalse("the gate is released once the browser answered");
    }

    [Fact]
    public async Task ActivateAsync_ATabThatClosed_IsCdpTargetNotFound()
    {
        var sut = CreateSut(TwoWindows());
        var gone = new CdpTarget("GONE", "page", "", "", "ws://127.0.0.1:9222/devtools/page/GONE", Port, BrowserPid);

        var result = await sut.ActivateAsync(gone, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
        result.Error.Should().Contain("list_tabs");
    }

    [Fact]
    public async Task ActivateAsync_WhenTheGateRefuses_SendsNothing()
    {
        var browser = TwoWindows();
        var sut = CreateSut(browser);

        var act = () => sut.ActivateAsync(new CdpTarget("A", "page", "Inbox", "", null, Port, BrowserPid),
            _ => throw new ToolErrorException(nameof(WindowErrorCode.PausedByUser), "paused"), LongTimeout);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        browser.Activated.Should().BeEmpty();
        browser.Socket.SentCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenTabAsync_CreatesATabOnAboutBlank_InsideTheGate_AndReturnsItAsListed()
    {
        var browser = TwoWindows();
        browser.LastActiveWindow = 2;
        var gate = new RecordingGate();
        var sut = CreateSut(browser);

        var result = await sut.OpenTabAsync(Port, BrowserPid, gate.EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Id.Should().Be("NEW1");
        result.Value.WebSocketDebuggerUrl.Should().Be("ws://127.0.0.1:9222/devtools/page/NEW1");
        browser.Created.Should().Equal("about:blank");
        gate.Entered.Should().Be(1);
    }

    [Fact]
    public async Task OpenTabAsync_ATabThatIsNeverListed_IsCdpTargetNotFound_WithinTheTimeout()
    {
        var browser = TwoWindows();
        browser.HideCreatedTabs = true;
        var sut = CreateSut(browser);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.OpenTabAsync(Port, BrowserPid, new RecordingGate().EnterAsync, TimeSpan.FromMilliseconds(600));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
        result.OpenedTargetId.Should().Be("NEW1", "the tab was opened, and the result names it");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    [Fact]
    public async Task OpenTabAsync_ATabListThatFailsOnceTheTabIsOpen_SaysTheTabWasOpened()
    {
        // Arrange - the browser opens the tab, then its tab list cannot be read (it stopped answering)
        var browser = TwoWindows();
        var owners = new FakePortOwnerResolver(BrowserPid);
        var transport = new CdpTransport(browser.Http, browser.Sockets, owners);
        var real = new CdpService(transport, owners);
        var cdp = new Mock<ICdpService>();
        cdp.Setup(c => c.GetBrowserVersionAsync(Port, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((int port, TimeSpan timeout, CancellationToken token) => real.GetBrowserVersionAsync(port, timeout, token));
        cdp.Setup(c => c.GetOwnedPageTargetsAsync(Port, BrowserPid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<IReadOnlyList<CdpTarget>>.Fail(BrowserErrorCode.CdpUnavailable, "Nothing answered on port 9222."));
        var sut = new CdpTabService(cdp.Object, transport, owners);

        // Act - a budget the steps before the list (version, connection, Target.createTarget) do not use up under load; the
        // list is asked for again until it is nearly spent
        var result = await sut.OpenTabAsync(Port, BrowserPid, new RecordingGate().EnterAsync, TimeSpan.FromSeconds(3));

        // Assert - the list's own error, after what happened to the tab, and what to do with the tab instead of opening another
        browser.Created.Should().ContainSingle();
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        result.Error.Should().StartWith("The browser opened tab NEW1 on about:blank, but it was not navigated: ")
            .And.Contain("Nothing answered on port 9222.")
            .And.EndWith("The tab stays open, so do not open another one: once list_tabs lists it, navigate it with targetId NEW1.");
        result.OpenedTargetId.Should().Be("NEW1");
    }

    [Fact]
    public async Task OpenTabAsync_APortOfAnotherProcess_OpensNothing()
    {
        var browser = TwoWindows();
        var sut = CreateSut(browser, portOwner: 999);

        var result = await sut.OpenTabAsync(Port, BrowserPid, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpPortOwnerMismatch);
        browser.Created.Should().BeEmpty();
    }

    #endregion

    #region NavigateAsync

    [Fact]
    public async Task NavigateAsync_WaitsForTheNewDocument_NotTheOldDocumentsReadyState()
    {
        // The old page is complete; Page.navigate answers before the tab has the new document, which then loads
        var tab = new FakeTab { CommitAfterFrameTrees = 2 };
        tab.ReadyStates.Enqueue("loading");
        tab.ReadyStates.Enqueue("interactive");
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/docs/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.LoadState.Should().Be("load");
        result.Value.TimedOut.Should().BeFalse();
        result.Value.Url.Should().Be(tab.Url);
        result.Value.Title.Should().Be(tab.Title);
        result.Value.HttpStatus.Should().Be(200);
        tab.EvaluatesBeforeCommit.Should().Be(0, "the old document's readyState must never be read as the new one's");
        tab.Methods.Should().ContainInOrder("Page.enable", "Page.navigate", "Page.getFrameTree", "Runtime.evaluate");
    }

    [Fact]
    public async Task NavigateAsync_DomContentLoaded_IsReachedAtInteractive()
    {
        var tab = new FakeTab();
        tab.ReadyStates.Enqueue("loading");
        tab.ReadyStates.Enqueue("interactive");
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.DomContentLoaded, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.LoadState.Should().Be("domcontentloaded");
        tab.Evaluates.Should().Be(2);
    }

    [Fact]
    public async Task NavigateAsync_SendsTheUrlAsData_AndRegistersTheLoadEventBeforeNavigating()
    {
        var tab = new FakeTab { FireLoadEventBeforeReply = true };
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/?q=\"x\"", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        tab.NavigatedTo.Should().Equal("https://example.com/?q=\"x\"");
    }

    [Fact]
    public async Task NavigateAsync_SendsPageNavigateInsideTheGate_AndWaitsForTheLoadOutsideIt()
    {
        var gate = new RecordingGate();
        var tab = new FakeTab { CommitAfterFrameTrees = 1, GateHeld = () => gate.Held };
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);

        await sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.Load, gate.EnterAsync, LongTimeout);

        tab.GateHeldAtNavigate.Should().BeTrue();
        tab.GateHeldAtFrameTree.Should().BeFalse("a page's load never holds up the input calls");
        gate.Entered.Should().Be(1);
    }

    [Fact]
    public async Task NavigateAsync_WhenTheGateRefuses_DoesNotNavigate()
    {
        var tab = new FakeTab();
        var sut = CreateSut(tab);

        var act = () => sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.Load,
            _ => throw new ToolErrorException(nameof(InputErrorCode.InputBusy), "busy"), LongTimeout);

        await act.Should().ThrowAsync<ToolErrorException>();
        tab.NavigatedTo.Should().BeEmpty();
    }

    [Fact]
    public async Task NavigateAsync_ErrorText_IsReturned_WithoutWaiting()
    {
        var tab = new FakeTab { NavigateReply = new { frameId = "F1", loaderId = "NEW", errorText = "net::ERR_NAME_NOT_RESOLVED" } };
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://nowhere.invalid/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue();
        result.Value!.ErrorText.Should().Be("net::ERR_NAME_NOT_RESOLVED");
        result.Value.LoadState.Should().Be("failed");
        tab.MethodsAfterNavigate.Should().NotContain("Page.getFrameTree");
    }

    [Fact]
    public async Task NavigateAsync_AUrlThatAnswersWithAFile_IsADownload_NotAnErrorPage()
    {
        // Chromium answers a navigation that becomes a download with isDownload and net::ERR_ABORTED; the tab keeps its page
        var tab = new FakeTab { NavigateReply = new { frameId = "F1", loaderId = "NEW", errorText = "net::ERR_ABORTED", isDownload = true } };
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/report.pdf", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.LoadState.Should().Be("download");
        result.Value.ErrorText.Should().Be("net::ERR_ABORTED");
        tab.MethodsAfterNavigate.Should().NotContain("Page.getFrameTree");
    }

    [Fact]
    public async Task NavigateAsync_AnHttpErrorWithAnEmptyBody_LoadsTheBrowsersErrorPage_WithTheServersStatus()
    {
        // A 4xx/5xx answer without a body is net::ERR_HTTP_RESPONSE_CODE_FAILURE, but the server did answer, as a 404 with a
        // body does: the call waits for the browser's error page and reports the status of the document's response
        var tab = new FakeTab
        {
            NavigateReply = new { frameId = "F1", loaderId = "NEW", errorText = "net::ERR_HTTP_RESPONSE_CODE_FAILURE" },
            BeforeNavigateReply = socket => socket.EnqueueEvent("Network.responseReceived", new
            {
                requestId = "NEW", loaderId = "NEW", frameId = "F1", type = "Document",
                response = new { url = "https://example.com/gone", status = 500 }
            }),
            ProbeUrl = "chrome-error://chromewebdata/",
            ProbeStatus = null,
            CommitAfterFrameTrees = 1
        };
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/gone", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.LoadState.Should().Be("load");
        result.Value.ErrorText.Should().BeNull();
        result.Value.HttpStatus.Should().Be(500);
        result.Value.Url.Should().Be("https://example.com/gone", "the error page's own address is internal; the tab shows the one asked for");
    }

    [Fact]
    public async Task NavigateAsync_AJumpWithinThePage_IsSameDocument()
    {
        var tab = new FakeTab { NavigateReply = new { frameId = "F1" } };
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/#part", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.LoadState.Should().Be("sameDocument");
        tab.MethodsAfterNavigate.Should().NotContain("Page.getFrameTree");
    }

    [Fact]
    public async Task NavigateAsync_WaitUntilNone_ReturnsOnceTheNavigationStarted()
    {
        var tab = new FakeTab();
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.None, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.LoadState.Should().Be("notWaited");
        result.Value.Url.Should().BeNull();
        tab.Methods.Should().NotContain(["Page.getFrameTree", "Runtime.evaluate"]);
    }

    [Fact]
    public async Task NavigateAsync_WaitUntilNone_DoesNotWaitForTheServersAnswer()
    {
        // Chromium answers Page.navigate only once the server has answered; none returns once the command is on its way
        var tab = new FakeTab { NavigateNeverAnswers = true };
        var sut = CreateSut(tab);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.NavigateAsync(Tab, "https://example.com/slow", BrowserWaitUntil.None, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.LoadState.Should().Be("notWaited");
        // The call returns once GateHold (a second) has passed; waiting for the answer that never comes would take the whole
        // LongTimeout. HangBound sits far from both, so a loaded test run does not fail this and a wait for the answer does
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        HangBound.Should().BeLessThan(LongTimeout);
        tab.NavigatedTo.Should().Equal("https://example.com/slow");
    }

    [Fact]
    public async Task NavigateAsync_AServerThatIsSlowToAnswer_TimesOut_AsStillLoading_NotAsCdpTimeout()
    {
        // No Page.navigate reply in time, but the tab answers: the navigation is under way and the server is slow
        var tab = new FakeTab { NavigateNeverAnswers = true };
        var sut = CreateSut(tab);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.NavigateAsync(Tab, "https://example.com/slow", BrowserWaitUntil.Load, new RecordingGate().EnterAsync,
            TimeSpan.FromMilliseconds(1500));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.TimedOut.Should().BeTrue();
        result.Value.LoadState.Should().Be("loading");
        result.Value.Url.Should().BeNull("the tab still shows the old page, which is not the one asked for");
        // The call ends at its 1.5 s limit; the reply never comes, so a call that ignored the limit would not end at all.
        // HangBound catches that without failing a loaded test run that is slow to fire the limit's timer
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    [Fact]
    public async Task NavigateAsync_WithTheDialogOption_AnswersTheBeforeUnloadPrompt_AndLoadsThePage()
    {
        // The page being left asks whether to leave (beforeunload): the browser answers Page.navigate only once it is answered
        var tab = new FakeTab { BeforeUnloadOnNavigate = true, CommitAfterFrameTrees = 0 };
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/docs/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout,
            dialogAnswer: new BrowserDialogAnswer(true));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.LoadState.Should().Be("load");
        result.Value.Dialog.Should().Be(new CdpAnsweredDialog(new CdpDialog("beforeunload", "Leave site?", "", tab.Url), true));
        tab.Methods.Should().ContainInOrder("Page.navigate", "Page.handleJavaScriptDialog");
    }

    [Fact]
    public async Task NavigateAsync_WithoutTheDialogOption_ABeforeUnloadPrompt_IsCdpDialogOpen_PointingToBrowserHandleDialog()
    {
        var tab = new FakeTab { BeforeUnloadOnNavigate = true };
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/docs/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
        result.Error.Should().Contain("browser_handle_dialog").And.NotContain("capture_window");
        tab.Methods.Should().NotContain("Page.handleJavaScriptDialog");
    }

    [Fact]
    public async Task NavigateAsync_ATabThatAnswersNothing_IsCdpTimeout_WithTheDialogGuidance()
    {
        // Edge does not report a dialog that was already open to a new DevTools connection: nothing answers at all
        var tab = new FakeTab { NavigateNeverAnswers = true, Unresponsive = true };
        var sut = CreateSut(tab);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync,
            TimeSpan.FromMilliseconds(1500));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("dialog");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    [Fact]
    public async Task NavigateAsync_APageThatRedirectsItselfRightAfterItCommits_IsLoaded_AsTheFinalPage()
    {
        // location.replace in the head or a meta refresh 0: the navigation's own document (NEW) is replaced by a third
        // before the wait sees it; any document other than the one before the navigation is past the commit
        var tab = new FakeTab { ProbeUrl = "https://example.com/final" };
        tab.FrameLoaders.Enqueue("OLD"); // read before Page.navigate
        tab.FrameLoaders.Enqueue("OLD");
        tab.FrameLoaders.Enqueue("THIRD");
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.NavigateAsync(Tab, "https://example.com/r", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Success.Should().BeTrue(result.Error);
        result.Value!.LoadState.Should().Be("load");
        result.Value.TimedOut.Should().BeFalse();
        result.Value.Url.Should().Be("https://example.com/final");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
        tab.Methods.Should().ContainInOrder("Page.getFrameTree", "Page.navigate");
    }

    [Fact]
    public async Task NavigateAsync_APageThatNeverLoads_TimesOut_AsStillLoading()
    {
        var tab = new FakeTab();
        tab.ReadyStates.Enqueue("loading");
        var sut = CreateSut(tab);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync,
            TimeSpan.FromMilliseconds(700));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.TimedOut.Should().BeTrue();
        result.Value.LoadState.Should().Be("loading");
        result.Value.Url.Should().Be(tab.Url);
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    [Fact]
    public async Task NavigateAsync_ADialogThatBlocksTheNavigation_IsCdpDialogOpen()
    {
        var tab = new FakeTab
        {
            OnNavigate = socket => socket.EnqueueEvent("Page.javascriptDialogOpening",
                new { url = "https://example.com/", message = "Leave site?", type = "beforeunload", hasBrowserHandler = true })
        };
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/next", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
    }

    [Fact]
    public async Task NavigateAsync_ATabThatCloses_IsCdpTargetClosed()
    {
        var tab = new FakeTab { OnNavigate = socket => socket.EnqueueEvent("Inspector.detached", new { reason = "target_closed" }) };
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetClosed);
    }

    [Fact]
    public async Task NavigateAsync_ARefusedNavigation_IsCdpProtocolError()
    {
        var tab = new FakeTab { NavigateError = "Cannot navigate to invalid URL" };
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpProtocolError);
        result.Error.Should().Contain("Cannot navigate to invalid URL");
    }

    #endregion

    #region WaitAsync

    [Fact]
    public async Task WaitAsync_IsSatisfiedOnceTheSelectorMatches_OverOneConnection()
    {
        var checks = 0;
        var tab = new FakeTab { Wait = _ => new { url = "https://example.com/r", title = "Results", selector = ++checks >= 3 } };
        var factory = new FakeCdpWebSocketFactory(tab.Socket);
        var sut = CreateSut(FakeCdpHttpHandler.Targets(), factory);

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition(null, null, null, "#results"), LongTimeout, TimeSpan.FromSeconds(3));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Satisfied.Should().BeTrue();
        result.Value.Url.Should().Be("https://example.com/r");
        checks.Should().Be(3);
        factory.ConnectedUris.Should().HaveCount(1, "a wait polls on one connection");
    }

    [Fact]
    public async Task WaitAsync_MatchesUrlAndTitleIgnoringCase_AndNeedsEveryCondition()
    {
        var tab = new FakeTab { Wait = _ => new { url = "https://Example.com/Done", title = "All DONE", text = false } };
        var sut = CreateSut(tab);

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition("example.com/done", "done", "thanks", null),
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));

        result.Value!.Satisfied.Should().BeFalse("the text condition does not hold");
        result.Value.Detail.Should().Contain("text").And.Contain("thanks").And.NotContain("URL").And.NotContain("title");
    }

    [Fact]
    public async Task WaitAsync_ThatTimesOut_ReturnsThePageAsLastSeen()
    {
        var tab = new FakeTab { Wait = _ => new { url = "https://example.com/", title = "Example", selector = false } };
        var sut = CreateSut(tab);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition(null, null, null, "#results"), TimeSpan.FromMilliseconds(600),
            TimeSpan.FromSeconds(3));

        result.Success.Should().BeTrue(result.Error);
        result.Value!.Satisfied.Should().BeFalse();
        result.Value.Title.Should().Be("Example");
        result.Value.Detail.Should().Contain("#results");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    [Fact]
    public async Task WaitAsync_WithTimeoutZero_ChecksOnce()
    {
        var checks = 0;
        var tab = new FakeTab { Wait = _ => new { url = "https://example.com/", title = "Example", selector = ++checks > 5 } };
        var sut = CreateSut(tab);

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition(null, null, null, "p"), TimeSpan.Zero, TimeSpan.FromSeconds(3));

        result.Value!.Satisfied.Should().BeFalse();
        checks.Should().Be(1);
    }

    [Fact]
    public async Task WaitAsync_ASelectorThatIsNotValidCss_IsCdpEvaluationFailed_AtOnce()
    {
        var tab = new FakeTab { Wait = _ => new { url = "https://example.com/", title = "Example", badSelector = "'##' is not a valid selector." } };
        var sut = CreateSut(tab);

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition(null, null, null, "##"), LongTimeout, TimeSpan.FromSeconds(3));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpEvaluationFailed);
        result.Error.Should().Contain("not valid CSS");
    }

    [Fact]
    public async Task WaitAsync_ACheckBetweenTwoDocuments_IsSkipped()
    {
        // During a navigation the page's context is destroyed: the browser answers with an error, and the wait goes on
        var checks = 0;
        var tab = new FakeTab { Wait = _ => ++checks == 1 ? null : new { url = "https://example.com/next", title = "Next" } };
        var sut = CreateSut(tab);

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition("next", null, null, null), LongTimeout, TimeSpan.FromSeconds(3));

        result.Value!.Satisfied.Should().BeTrue();
        checks.Should().Be(2);
    }

    [Fact]
    public async Task WaitAsync_ADialogThatOpens_IsCdpDialogOpen()
    {
        var tab = new FakeTab { Wait = _ => FakeTab.OpenDialog };
        var sut = CreateSut(tab);

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition("x", null, null, null), LongTimeout, TimeSpan.FromSeconds(3));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpDialogOpen);
    }

    [Fact]
    public async Task WaitAsync_APageThatAnswersNoCheck_IsCdpTimeout_WithTheDialogGuidance_NotWaitTimedOut()
    {
        // A dialog that was already open is not reported to a new DevTools connection (Edge): no check is ever answered,
        // and waiting longer cannot help
        var tab = new FakeTab { Wait = _ => FakeTab.NoAnswer };
        var sut = CreateSut(tab);
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.WaitAsync(Tab, new BrowserWaitCondition("x", null, null, null), TimeSpan.FromMilliseconds(600),
            TimeSpan.FromMilliseconds(300));

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTimeout);
        result.Error.Should().Contain("dialog").And.NotContain("longer timeoutMs");
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
    }

    [Fact]
    public void BuildWaitExpression_PutsTheTextAndSelectorInAsJsonStrings()
    {
        var expression = CdpTabService.BuildWaitExpression(
            new BrowserWaitCondition(null, null, "It's \"DONE\"", "a[href='x'] </script>"));

        expression.Should().Contain(JsonSerializer.Serialize("it's \"done\""), "the text is lower-cased and only ever data");
        expression.Should().Contain(JsonSerializer.Serialize("a[href='x'] </script>"));
        expression.Should().Contain("const text = ").And.Contain("const selector = ");
    }

    #endregion

    /// <summary>Takes the gate and records whether it is held, as InputGateToolFilter.EnterAsync would hand it out.</summary>
    private sealed class RecordingGate
    {
        public int Entered { get; private set; }

        public bool Held { get; private set; }

        public Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
        {
            Entered++;
            Held = true;
            return Task.FromResult<IDisposable>(new Release(this));
        }

        private sealed class Release(RecordingGate gate) : IDisposable
        {
            public void Dispose() => gate.Held = false;
        }
    }

    /// <summary>
    /// A tab's DevTools connection: Page.enable, Page.navigate (to the loader NEW unless <see cref="NavigateReply"/> says
    /// otherwise), Page.getFrameTree (the old loader until <see cref="CommitAfterFrameTrees"/> queries have been answered),
    /// and Runtime.evaluate: the load probe's <see cref="ReadyStates"/> (the last one repeats), or a wait check's
    /// <see cref="Wait"/> (null answers with an error, as a destroyed context does).
    /// </summary>
    private sealed class FakeTab
    {
        /// <summary>What <see cref="Wait"/> returns to have the page open a JavaScript dialog instead of answering.</summary>
        public static readonly object OpenDialog = new();

        /// <summary>What <see cref="Wait"/> returns to have the page not answer the check at all.</summary>
        public static readonly object NoAnswer = new();

        private readonly object _lock = new();
        private int _frameTrees;
        private string _readyState = "complete";
        private string? _lastLoader;

        public FakeTab()
        {
            Socket = new FakeCdpWebSocket(Respond);
        }

        public FakeCdpWebSocket Socket { get; }

        public object NavigateReply { get; init; } = new { frameId = "F1", loaderId = "NEW" };

        public string? NavigateError { get; init; }

        public int CommitAfterFrameTrees { get; init; }

        public bool FireLoadEventBeforeReply { get; init; }

        public Action<FakeCdpWebSocket>? OnNavigate { get; init; }

        /// <summary>Sent before Page.navigate's reply, such as the document's Network.responseReceived.</summary>
        public Action<FakeCdpWebSocket>? BeforeNavigateReply { get; init; }

        /// <summary>Page.navigate is never answered, as Chromium does not answer it until the server has answered.</summary>
        public bool NavigateNeverAnswers { get; init; }

        /// <summary>The page answers no Runtime.evaluate, as a page blocked by a dialog nobody reported.</summary>
        public bool Unresponsive { get; init; }

        /// <summary>
        /// Page.navigate opens the page's beforeunload prompt, and is answered once the prompt is accepted (the navigation then
        /// goes on), as Chromium does.
        /// </summary>
        public bool BeforeUnloadOnNavigate { get; init; }

        private int? _blockedNavigate;

        /// <summary>The main frame's loaderId for each Page.getFrameTree in turn (the last repeats), instead of OLD then NEW.</summary>
        public Queue<string> FrameLoaders { get; } = new();

        public Func<bool>? GateHeld { get; init; }

        public Func<JsonElement, object?>? Wait { get; init; }

        public Queue<string> ReadyStates { get; } = new();

        public string Url => ProbeUrl;

        public string ProbeUrl { get; init; } = "https://example.com/docs/";

        public int? ProbeStatus { get; init; } = 200;

        public string Title { get; } = "Docs - Example";

        public List<string> NavigatedTo { get; } = new();

        public bool? GateHeldAtNavigate { get; private set; }

        public bool? GateHeldAtFrameTree { get; private set; }

        public int EvaluatesBeforeCommit { get; private set; }

        public int Evaluates { get; private set; }

        public IReadOnlyList<string> Methods => Socket.SentCommands.Select(c => c.GetProperty("method").GetString()!).ToList();

        public IReadOnlyList<string> MethodsAfterNavigate => Methods.SkipWhile(m => m != "Page.navigate").Skip(1).ToList();

        private void Respond(FakeCdpWebSocket socket, JsonElement command)
        {
            lock (_lock)
            {
                var id = command.GetProperty("id").GetInt32();
                switch (command.GetProperty("method").GetString())
                {
                    case "Page.enable":
                        socket.ReplyEmpty(command);
                        break;

                    case "Page.navigate":
                        NavigatedTo.Add(command.GetProperty("params").GetProperty("url").GetString()!);
                        GateHeldAtNavigate = GateHeld?.Invoke();
                        if (OnNavigate != null)
                        {
                            OnNavigate(socket);
                            break;
                        }

                        if (NavigateNeverAnswers)
                            break;

                        if (BeforeUnloadOnNavigate)
                        {
                            _blockedNavigate = id;
                            socket.EnqueueEvent("Page.javascriptDialogOpening",
                                new { url = Url, message = "Leave site?", type = "beforeunload", defaultPrompt = "", hasBrowserHandler = true });
                            break;
                        }

                        if (NavigateError != null)
                        {
                            socket.ReplyError(command, -32000, NavigateError);
                            break;
                        }

                        if (FireLoadEventBeforeReply)
                            socket.EnqueueEvent("Page.loadEventFired", new { timestamp = 1.5 });

                        BeforeNavigateReply?.Invoke(socket);

                        socket.Enqueue(JsonSerializer.Serialize(new { id, result = NavigateReply }));
                        break;

                    case "Page.getFrameTree":
                        GateHeldAtFrameTree ??= GateHeld?.Invoke();
                        _frameTrees++;
                        if (FrameLoaders.Count > 0)
                            _lastLoader = FrameLoaders.Count > 1 ? FrameLoaders.Dequeue() : FrameLoaders.Peek();
                        var loader = _lastLoader ?? (_frameTrees > CommitAfterFrameTrees ? "NEW" : "OLD");
                        socket.Enqueue(JsonSerializer.Serialize(new { id, result = new { frameTree = new { frame = new { id = "F1", loaderId = loader } } } }));
                        break;

                    case "Runtime.evaluate" when Unresponsive:
                        break;

                    case "Runtime.evaluate":
                        var expression = command.GetProperty("params").GetProperty("expression").GetString()!;
                        if (expression.Contains("readyState", StringComparison.Ordinal))
                        {
                            Evaluates++;
                            if (_frameTrees <= CommitAfterFrameTrees)
                                EvaluatesBeforeCommit++;

                            if (ReadyStates.Count > 0)
                                _readyState = ReadyStates.Dequeue();

                            socket.ReplyWithValue(command, new { readyState = _readyState, url = Url, title = Title, status = ProbeStatus });
                            break;
                        }

                        var value = Wait?.Invoke(command);
                        if (ReferenceEquals(value, NoAnswer))
                            break;

                        if (ReferenceEquals(value, OpenDialog))
                        {
                            socket.EnqueueEvent("Page.javascriptDialogOpening",
                                new { url = Url, message = "Are you sure?", type = "confirm", hasBrowserHandler = true });
                        }
                        else if (value == null)
                        {
                            socket.ReplyError(command, -32000, "Execution context was destroyed.");
                        }
                        else
                        {
                            socket.ReplyWithValue(command, value);
                        }

                        break;

                    case "Page.handleJavaScriptDialog" when _blockedNavigate is { } navigate:
                        _blockedNavigate = null;
                        socket.ReplyEmpty(command);
                        socket.EnqueueEvent("Page.javascriptDialogClosed", new { result = true, userInput = "" });
                        if (command.GetProperty("params").GetProperty("accept").GetBoolean())
                            socket.Enqueue(JsonSerializer.Serialize(new { id = navigate, result = NavigateReply }));
                        break;

                    case "Page.handleJavaScriptDialog":
                        socket.ReplyError(command, -32602, "No dialog is showing");
                        break;

                    default:
                        socket.ReplyError(command, -32601, "not supported");
                        break;
                }
            }
        }
    }
}
