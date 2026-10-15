using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// BrowserTabService (roadmap M5.5): which browser and port a tab tool works on (a window's own debug port, an explicit
/// cdpPort, or a port alone), the target policy on that browser, the tab, and how the CDP outcome becomes the tool's
/// result. CDP itself is mocked (<see cref="ICdpTabService"/>, <see cref="ICdpService"/>); its fakes are in
/// CdpTabServiceTests.
/// </summary>
public class BrowserTabServiceTests
{
    private const int Port = 9222;
    private const uint BrowserPid = 4242;
    private static readonly nint BrowserWindow = 0x2B0C3D;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IBrowserReadingService> _reading = new();
    private readonly Mock<ICdpService> _cdp = new();
    private readonly Mock<ICdpTabService> _tabs = new();
    private readonly Mock<IDebugPortLocator> _locator = new();
    private readonly InputSession _session = new(new FakeInputWrapper());
    private ITargetPolicy _policy = new TargetPolicy(TargetPolicySettings.Default);
    private FakePortOwnerResolver _owners = new(owner: (int)BrowserPid);

    private static readonly CdpTarget Shown = new("SHOWN", "page", "Docs", "https://example.com/docs/", "ws://127.0.0.1:9222/devtools/page/SHOWN", Port);
    private static readonly CdpTarget Other = new("OTHER", "page", "Other", "https://example.com/other", "ws://127.0.0.1:9222/devtools/page/OTHER", Port);

    public BrowserTabServiceTests()
    {
        ShowBrowser(BrowserWindow, BrowserPid, "chrome", "Docs - Google Chrome");
        _reading.Setup(r => r.GetBrowserType(It.IsAny<nint>(), It.IsAny<string>()))
            .Returns((nint _, string name) => name.Contains("firefox", StringComparison.OrdinalIgnoreCase) ? "firefox" : "chrome");
        _locator.Setup(l => l.Find((int)BrowserPid)).Returns(new DebugPortLookup(DebugPortLookupStatus.Found, Port, "found"));
        _cdp.Setup(c => c.FindTargetForWindowAsync(Port, It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(Shown));
        _cdp.Setup(c => c.FindTargetByIdAsync(Port, It.IsAny<int>(), "OTHER", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(Other));
    }

    private void ShowBrowser(nint handle, uint processId, string processName, string title)
    {
        _windows.Setup(w => w.IsWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetAncestor(handle, WindowAncestor.Root)).Returns(handle);
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns(processId);
        _windows.Setup(w => w.GetProcessName(processId)).Returns(processName);
        _windows.Setup(w => w.GetWindowText(handle)).Returns(title);
        _windows.Setup(w => w.GetClassName(handle)).Returns("Chrome_WidgetWin_1");
        _windows.Setup(w => w.GetWindowRect(handle)).Returns((0, 0, 800, 600));
        _windows.Setup(w => w.GetDpiForWindow(handle)).Returns(96u);
    }

    private BrowserTabService CreateSut() => new(
        _windows.Object, new BrowserDetectionService(), _reading.Object, _cdp.Object, _tabs.Object, _owners, _policy, _locator.Object,
        _session);

    private static BrowserTabTarget ByWindow(string? targetId = null, int? cdpPort = null) => new(BrowserWindow, cdpPort, targetId);

    #region Which browser

    [Fact]
    public async Task AWindowThatIsNoBrowser_IsNotABrowser_AndNoPortIsLookedFor()
    {
        ShowBrowser(BrowserWindow, BrowserPid, "notepad", "Untitled - Notepad");

        var result = await CreateSut().ListTabsAsync(ByWindow());

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotABrowser));
        _locator.Verify(l => l.Find(It.IsAny<int>()), Times.Never);
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AWindowThePolicyDenies_IsRefused_BeforeItsCommandLineIsRead()
    {
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["chrome"] });

        var result = await CreateSut().NavigateAsync(ByWindow(), new BrowserNavigateOptions("https://example.com/", false, BrowserWaitUntil.Load, 15_000));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("Nothing was done");
        _locator.Verify(l => l.Find(It.IsAny<int>()), Times.Never);
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ThePolicy_AppliesAlsoWithAnExplicitCdpPort()
    {
        // The reads exempt an explicit cdpPort (M5.3); an action does not
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["chrome"] });

        var result = await CreateSut().ActivateTabAsync(ByWindow("OTHER", cdpPort: Port));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.TargetDeniedByPolicy));
        _cdp.VerifyNoOtherCalls();
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Firefox_HasNoCdp()
    {
        ShowBrowser(BrowserWindow, BrowserPid, "firefox", "Docs — Mozilla Firefox");

        var result = await CreateSut().ListTabsAsync(ByWindow());

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpUnavailable));
        result.Error.Should().Contain("Firefox");
        _locator.Verify(l => l.Find(It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(DebugPortLookupStatus.NoDebugPort, nameof(BrowserErrorCode.CdpUnavailable))]
    [InlineData(DebugPortLookupStatus.NotListening, nameof(BrowserErrorCode.CdpUnavailable))]
    [InlineData(DebugPortLookupStatus.HeldByOtherProcess, nameof(BrowserErrorCode.CdpPortOwnerMismatch))]
    public async Task ABrowserWithoutItsOwnDebugPort_IsRefused_WithWhatTheLookupFound(DebugPortLookupStatus status, string errorCode)
    {
        _locator.Setup(l => l.Find((int)BrowserPid)).Returns(new DebugPortLookup(status, null, "What the lookup found."));

        var result = await CreateSut().WaitAsync(ByWindow(), new BrowserWaitCondition("x", null, null, null), 1000);

        result.ErrorCode.Should().Be(errorCode);
        result.Error.Should().Contain("What the lookup found.").And.Contain("launch_browser_with_debug");
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnExplicitCdpPort_IsUsed_WithoutLookingForTheWindowsOwn()
    {
        _tabs.Setup(t => t.ListTabsAsync(5555, (int)BrowserPid, It.IsAny<CdpWindow?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTabListing>.Ok(new CdpTabListing([], true, null, null)));

        var result = await CreateSut().ListTabsAsync(ByWindow(cdpPort: 5555));

        result.Success.Should().BeTrue(result.Error);
        result.CdpPort.Should().Be(5555);
        _locator.Verify(l => l.Find(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task APortAlone_NamesTheBrowserThatListensOnIt()
    {
        _tabs.Setup(t => t.ListTabsAsync(Port, (int)BrowserPid, null, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTabListing>.Ok(new CdpTabListing([new CdpTab(Shown, 1, false)], false, null, null)));

        var result = await CreateSut().ListTabsAsync(new BrowserTabTarget(null, Port));

        result.Success.Should().BeTrue(result.Error);
        result.Tabs!.Single().TargetId.Should().Be("SHOWN");
        result.WindowBound.Should().BeNull("no window was named");
        result.ShownTargetId.Should().BeNull();
        result.Tabs!.Single().InWindow.Should().BeNull();
    }

    [Fact]
    public async Task APortAlone_ThePolicyAppliesToItsListener()
    {
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["chrome"] });

        var result = await CreateSut().ListTabsAsync(new BrowserTabTarget(null, Port));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain($"port {Port}");
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task APortAlone_WhoseListenerIsNoBrowser_IsNotABrowser()
    {
        _windows.Setup(w => w.GetProcessName(BrowserPid)).Returns("python");

        var result = await CreateSut().ListTabsAsync(new BrowserTabTarget(null, Port));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotABrowser));
        result.Error.Should().Contain("python");
    }

    [Fact]
    public async Task APortAlone_WhoseOwnerCannotBeRead_FailsClosed()
    {
        _owners = new FakePortOwnerResolver(owner: null, tableReadable: false);

        var result = await CreateSut().ListTabsAsync(new BrowserTabTarget(null, Port));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpPortOwnerMismatch));
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task APortAlone_NothingListensOn_IsCdpUnavailable()
    {
        _owners = new FakePortOwnerResolver(owner: null);

        var result = await CreateSut().ListTabsAsync(new BrowserTabTarget(null, Port));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpUnavailable));
    }

    [Fact]
    public async Task AHandleThatIsNoWindow_IsInvalidHandle()
    {
        var result = await CreateSut().ListTabsAsync(new BrowserTabTarget(0x1234));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.InvalidHandle));
    }

    #endregion

    #region list_tabs

    [Fact]
    public async Task ListTabs_MarksTheWindowsTabs_AndTheShownTab_AndCutsLongTitlesAndUrls()
    {
        var longTitle = new string('t', 500);
        var longUrl = "https://example.com/" + new string('u', 5000);
        var tabs = new List<CdpTab>
        {
            new(Shown with { Title = longTitle, Url = longUrl }, 1, true),
            new(Other, 2, false)
        };
        _tabs.Setup(t => t.ListTabsAsync(Port, (int)BrowserPid, It.IsAny<CdpWindow?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTabListing>.Ok(new CdpTabListing(tabs, true, Shown, null)));

        var result = await CreateSut().ListTabsAsync(ByWindow());

        result.Success.Should().BeTrue(result.Error);
        result.ShownTargetId.Should().Be("SHOWN");
        result.WindowBound.Should().BeTrue();
        result.Tabs![0].InWindow.Should().BeTrue();
        result.Tabs[1].InWindow.Should().BeFalse();
        result.Tabs[0].Title.Should().HaveLength(BrowserTabService.MaxTitleLength).And.EndWith("...");
        result.Tabs[0].Url.Should().HaveLength(BrowserTabService.MaxUrlLength).And.EndWith("...");
        result.Note.Should().BeNull();
        result.TabCount.Should().Be(2);
        result.Truncated.Should().BeNull();
    }

    [Fact]
    public async Task ListTabs_ListsAtMost100Tabs()
    {
        var tabs = Enumerable.Range(0, 150).Select(i => new CdpTab(Shown with { Id = $"T{i}" }, 1, false)).ToList();
        _tabs.Setup(t => t.ListTabsAsync(Port, (int)BrowserPid, It.IsAny<CdpWindow?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTabListing>.Ok(new CdpTabListing(tabs, false, null, "CdpTargetNotFound: none")));

        var result = await CreateSut().ListTabsAsync(ByWindow());

        result.Tabs.Should().HaveCount(BrowserTabsResult.MaxListedTabs);
        result.TabCount.Should().Be(150);
        result.Truncated.Should().BeTrue();
        result.WindowBound.Should().BeFalse();
        result.Note.Should().Contain("could not be matched");
        result.Tabs!.Should().OnlyContain(t => t.InWindow == null, "an unbound window marks no tab");
    }

    [Fact]
    public async Task ListTabs_PassesTheWindowsTitleAndBounds_ToTheBinding()
    {
        CdpWindow? passed = null;
        _tabs.Setup(t => t.ListTabsAsync(Port, (int)BrowserPid, It.IsAny<CdpWindow?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback((int _, int _, CdpWindow? window, TimeSpan _, CancellationToken _) => passed = window)
            .ReturnsAsync(CdpResult<CdpTabListing>.Ok(new CdpTabListing([], true, null, null)));

        await CreateSut().ListTabsAsync(ByWindow());

        passed.Should().Be(new CdpWindow((int)BrowserPid, "Docs - Google Chrome",
            new CdpWindowGeometry(0, 0, 800, 600, CdpWindowState.Normal, 1.0)));
    }

    #endregion

    #region activate_tab, browser_navigate, wait_for_browser

    [Fact]
    public async Task ActivateTab_FindsTheTabById_OnTheWindowsPort_AndActivatesIt()
    {
        _tabs.Setup(t => t.ActivateAsync(Other, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(Other));

        var result = await CreateSut().ActivateTabAsync(ByWindow("OTHER"));

        result.Should().Be(new BrowserTabResult(true, "OTHER", "Other", "https://example.com/other", Port));
        _cdp.Verify(c => c.FindTargetByIdAsync(Port, (int)BrowserPid, "OTHER", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ActivateTab_ATabThatIsNotThere_IsItsLookupsError()
    {
        _cdp.Setup(c => c.FindTargetByIdAsync(Port, It.IsAny<int>(), "GONE", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Fail(BrowserErrorCode.CdpTargetNotFound, "No tab with targetId GONE"));

        var result = await CreateSut().ActivateTabAsync(ByWindow("GONE"));

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.CdpTargetNotFound));
        result.TargetId.Should().Be("GONE");
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Navigate_WithoutTargetId_NavigatesTheTabTheWindowShows()
    {
        _tabs.Setup(t => t.NavigateAsync(Shown, "https://example.com/docs/", BrowserWaitUntil.Load, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpNavigation>.Ok(new CdpNavigation("https://example.com/docs/", "Docs - Example", 200, "load", false)));

        var result = await CreateSut().NavigateAsync(ByWindow(), new BrowserNavigateOptions("https://example.com/docs/", false, BrowserWaitUntil.Load, 15_000));

        result.Success.Should().BeTrue(result.Error);
        result.TargetId.Should().Be("SHOWN");
        result.LoadState.Should().Be("load");
        result.HttpStatus.Should().Be(200);
        result.NewTab.Should().BeNull();
        result.ElapsedMs.Should().NotBeNull();
    }

    [Fact]
    public async Task Navigate_ErrorText_IsNavigationFailed_WithTheBrowsersReason()
    {
        _tabs.Setup(t => t.NavigateAsync(It.IsAny<CdpTarget>(), It.IsAny<string>(), It.IsAny<BrowserWaitUntil>(), It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpNavigation>.Ok(new CdpNavigation(null, null, null, "failed", false, "net::ERR_NAME_NOT_RESOLVED")));

        var result = await CreateSut().NavigateAsync(ByWindow(), new BrowserNavigateOptions("https://nowhere.invalid/", false, BrowserWaitUntil.Load, 15_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.NavigationFailed));
        result.ErrorText.Should().Be("net::ERR_NAME_NOT_RESOLVED");
        result.Error.Should().Contain("net::ERR_NAME_NOT_RESOLVED");
    }

    [Fact]
    public async Task Navigate_ALoadThatRunsOutOfTime_IsWaitTimedOut_StillLoading()
    {
        _tabs.Setup(t => t.NavigateAsync(It.IsAny<CdpTarget>(), It.IsAny<string>(), It.IsAny<BrowserWaitUntil>(), It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpNavigation>.Ok(new CdpNavigation("https://example.com/slow", "Slow", null, "loading", true)));

        var result = await CreateSut().NavigateAsync(ByWindow(), new BrowserNavigateOptions("https://example.com/slow", false, BrowserWaitUntil.Load, 2_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.WaitTimedOut));
        result.LoadState.Should().Be("loading");
        result.Url.Should().Be("https://example.com/slow");
        result.Error.Should().Contain("2000 ms").And.Contain("wait_for_browser");
    }

    [Fact]
    public async Task Navigate_NewTab_OpensATabFirst_AndNavigatesIt()
    {
        var opened = Other with { Id = "NEW1" };
        _tabs.Setup(t => t.OpenTabAsync(Port, (int)BrowserPid, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpTarget>.Ok(opened));
        _tabs.Setup(t => t.NavigateAsync(opened, "https://example.com/", BrowserWaitUntil.DomContentLoaded, It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpNavigation>.Ok(new CdpNavigation("https://example.com/", "Example", 200, "domcontentloaded", false)));

        var result = await CreateSut().NavigateAsync(ByWindow(), new BrowserNavigateOptions("https://example.com/", true, BrowserWaitUntil.DomContentLoaded, 15_000));

        result.Success.Should().BeTrue(result.Error);
        result.TargetId.Should().Be("NEW1");
        result.NewTab.Should().BeTrue();
        _cdp.Verify(c => c.FindTargetForWindowAsync(It.IsAny<int>(), It.IsAny<CdpWindow>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Navigate_TheGateItHandsOn_RefusesWhileInputIsPaused()
    {
        _session.Pause(InputControlSources.Hotkey);
        _tabs.Setup(t => t.NavigateAsync(It.IsAny<CdpTarget>(), It.IsAny<string>(), It.IsAny<BrowserWaitUntil>(), It.IsAny<CdpGate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(async (CdpTarget _, string _, BrowserWaitUntil _, CdpGate gate, TimeSpan _, CancellationToken token) =>
            {
                using (await gate(token))
                {
                    return CdpResult<CdpNavigation>.Ok(new CdpNavigation(null, null, null, "load", false));
                }
            });

        var act = () => CreateSut().NavigateAsync(ByWindow(), new BrowserNavigateOptions("https://example.com/", false, BrowserWaitUntil.Load, 15_000));

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
    }

    [Fact]
    public async Task Wait_NotSatisfied_IsWaitTimedOut_WithWhatWasMissing()
    {
        _tabs.Setup(t => t.WaitAsync(Shown, It.IsAny<BrowserWaitCondition>(), TimeSpan.FromMilliseconds(5000), BrowserTabService.MinimumCheckTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpWaitOutcome>.Ok(new CdpWaitOutcome(false, "https://example.com/", "Example", "no element matches '#results'")));

        var result = await CreateSut().WaitAsync(ByWindow(), new BrowserWaitCondition(null, null, null, "#results"), 5000);

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.WaitTimedOut));
        result.Error.Should().Contain("#results");
        result.Url.Should().Be("https://example.com/");
        result.TargetId.Should().Be("SHOWN");
    }

    [Fact]
    public async Task Wait_Satisfied_IsASuccess()
    {
        _tabs.Setup(t => t.WaitAsync(Other, It.IsAny<BrowserWaitCondition>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpWaitOutcome>.Ok(new CdpWaitOutcome(true, "https://example.com/other", "Other", null)));

        var result = await CreateSut().WaitAsync(ByWindow("OTHER"), new BrowserWaitCondition("other", null, null, null), 5000);

        result.Success.Should().BeTrue();
        result.Satisfied.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.CdpPort.Should().Be(Port);
    }

    #endregion
}
