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

    private static readonly CdpTarget Tab = new("T1", "page", "Docs", "https://example.com/", "ws://127.0.0.1:9222/devtools/page/T1", Port);

    private static CdpTabService CreateSut(
        FakeCdpHttpHandler http, ICdpWebSocketFactory sockets, int? portOwner = BrowserPid, bool listenerTableReadable = true)
    {
        var transport = new CdpTransport(http, sockets);
        var owners = new FakePortOwnerResolver(portOwner, listenerTableReadable);
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
        result.Value!.Shown.Should().BeNull("CDP has no active-tab flag, and the title does not tell");
        result.Value.ShownError.Should().StartWith(nameof(BrowserErrorCode.CdpTargetAmbiguous));
        result.Value.WindowBound.Should().BeTrue();
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
        var tab = new CdpTarget("B", "page", "Docs", "https://example.com/B", "ws://127.0.0.1:9222/devtools/page/B", Port);

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
        var gone = new CdpTarget("GONE", "page", "", "", "ws://127.0.0.1:9222/devtools/page/GONE", Port);

        var result = await sut.ActivateAsync(gone, new RecordingGate().EnterAsync, LongTimeout);

        result.ErrorCode.Should().Be(BrowserErrorCode.CdpTargetNotFound);
        result.Error.Should().Contain("list_tabs");
    }

    [Fact]
    public async Task ActivateAsync_WhenTheGateRefuses_SendsNothing()
    {
        var browser = TwoWindows();
        var sut = CreateSut(browser);

        var act = () => sut.ActivateAsync(new CdpTarget("A", "page", "Inbox", "", null, Port),
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
        stopwatch.Elapsed.Should().BeLessThan(HangBound);
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
        tab.Methods.Should().NotContain("Page.getFrameTree");
    }

    [Fact]
    public async Task NavigateAsync_AJumpWithinThePage_IsSameDocument()
    {
        var tab = new FakeTab { NavigateReply = new { frameId = "F1" } };
        tab.ReadyStates.Enqueue("complete");
        var sut = CreateSut(tab);

        var result = await sut.NavigateAsync(Tab, "https://example.com/#part", BrowserWaitUntil.Load, new RecordingGate().EnterAsync, LongTimeout);

        result.Value!.LoadState.Should().Be("sameDocument");
        tab.Methods.Should().NotContain("Page.getFrameTree");
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

        private readonly object _lock = new();
        private int _frameTrees;
        private string _readyState = "complete";

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

        public Func<bool>? GateHeld { get; init; }

        public Func<JsonElement, object?>? Wait { get; init; }

        public Queue<string> ReadyStates { get; } = new();

        public string Url { get; } = "https://example.com/docs/";

        public string Title { get; } = "Docs - Example";

        public List<string> NavigatedTo { get; } = new();

        public bool? GateHeldAtNavigate { get; private set; }

        public bool? GateHeldAtFrameTree { get; private set; }

        public int EvaluatesBeforeCommit { get; private set; }

        public int Evaluates { get; private set; }

        public IReadOnlyList<string> Methods => Socket.SentCommands.Select(c => c.GetProperty("method").GetString()!).ToList();

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

                        if (NavigateError != null)
                        {
                            socket.ReplyError(command, -32000, NavigateError);
                            break;
                        }

                        if (FireLoadEventBeforeReply)
                            socket.EnqueueEvent("Page.loadEventFired", new { timestamp = 1.5 });

                        socket.Enqueue(JsonSerializer.Serialize(new { id, result = NavigateReply }));
                        break;

                    case "Page.getFrameTree":
                        GateHeldAtFrameTree ??= GateHeld?.Invoke();
                        _frameTrees++;
                        var loader = _frameTrees > CommitAfterFrameTrees ? "NEW" : "OLD";
                        socket.Enqueue(JsonSerializer.Serialize(new { id, result = new { frameTree = new { frame = new { id = "F1", loaderId = loader } } } }));
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

                            socket.ReplyWithValue(command, new { readyState = _readyState, url = Url, title = Title, status = 200 });
                            break;
                        }

                        var value = Wait?.Invoke(command);
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

                    default:
                        socket.ReplyError(command, -32601, "not supported");
                        break;
                }
            }
        }
    }
}
