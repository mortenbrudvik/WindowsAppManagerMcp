using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The argument rules of list_tabs, activate_tab, browser_navigate and wait_for_browser (roadmap M5.5), checked before the
/// tab service is asked anything: the URL allow-list, time limits refused (not clamped) outside their ranges, which
/// browser and tab a call names, and the pause, which refuses the two tools that change the browser.
/// </summary>
public class BrowserTabToolsTests
{
    private const long Handle = 2821181;
    private const string TargetId = "2B7E4F1A9C3D5E6F7A8B9C0D1E2F3A4B";

    private readonly Mock<IBrowserTabService> _tabs = new();
    private readonly InputSession _session = new(new FakeInputWrapper());

    public BrowserTabToolsTests()
    {
        _tabs.Setup(t => t.ListTabsAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserTabsResult(true));
        _tabs.Setup(t => t.ActivateTabAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserTabResult(true));
        _tabs.Setup(t => t.NavigateAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserNavigateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserNavigateResult(true));
        _tabs.Setup(t => t.WaitAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserWaitCondition>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserWaitResult(true, true));
    }

    private BrowserTabTools CreateSut() => new(_tabs.Object, windowService: null, _session);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> act) =>
        (await act.Should().ThrowAsync<ToolErrorException>()).Which;

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://127.0.0.1:8080/page.html?q=1#top")]
    [InlineData("HTTPS://Example.com")]
    [InlineData("https://example.com/json/list")]
    [InlineData("http://127.0.0.1:8080/jsonp/data")]
    [InlineData("about:blank")]
    public async Task BrowserNavigate_OpensWebPagesAndAboutBlank(string url)
    {
        var result = await CreateSut().BrowserNavigate(url, handle: Handle);

        result.Success.Should().BeTrue();
        _tabs.Verify(t => t.NavigateAsync(new BrowserTabTarget((nint)Handle, null, null),
            new BrowserNavigateOptions(url, false, BrowserWaitUntil.Load, BrowserTabTools.DefaultNavigateTimeoutMs, null), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task BrowserNavigate_PassesTheDialogOptionOn()
    {
        await CreateSut().BrowserNavigate("https://example.com/", handle: Handle, dialog: BrowserDialogChoice.Accept, promptText: "Kari");

        _tabs.Verify(t => t.NavigateAsync(It.IsAny<BrowserTabTarget>(),
            new BrowserNavigateOptions("https://example.com/", false, BrowserWaitUntil.Load, BrowserTabTools.DefaultNavigateTimeoutMs,
                new BrowserDialogAnswer(true, "Kari")), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:void(0)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Users/me/.ssh/id_rsa")]
    [InlineData("chrome://settings/passwords")]
    [InlineData("edge://settings")]
    [InlineData("brave://wallet")]
    [InlineData("devtools://devtools/bundled/inspector.html")]
    [InlineData("chrome-extension://abcdefghijklmnop/popup.html")]
    [InlineData("view-source:https://example.com/")]
    [InlineData("about:config")]
    [InlineData("blob:https://example.com/0f1e")]
    [InlineData("vbscript:msgbox")]
    [InlineData("ms-settings:privacy")]
    [InlineData("example.com")]
    [InlineData("http:example.com")]
    [InlineData("https://")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com/\nX")]
    [InlineData("http://127.0.0.1:9222/json/list")]
    [InlineData("http://localhost:9222/json/close/ABC")]
    [InlineData("http://LOCALHOST:9222/JSON/activate/ABC")]
    [InlineData("http://127.1:9222/json/version")]
    [InlineData("http://0x7f000001:9222/json")]
    [InlineData("http://[::1]:9222/devtools/page/ABC")]
    [InlineData("http://tabs.localhost:9222/json/new?https://example.com")]
    [InlineData("http://0.0.0.0:9222/json/list")]
    [InlineData("http://127.0.0.1:9222/./json/list")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BrowserNavigate_RefusesEveryOtherUrl_AndDoesNothing(string url)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserNavigate(url, handle: Handle));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain("Nothing was done");
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserNavigate_RefusesAnOverlongUrl()
    {
        var url = "https://example.com/" + new string('a', BrowserNavigationUrls.MaxLength);

        (await RefusedAsync(() => CreateSut().BrowserNavigate(url, handle: Handle))).Message.Should().Contain("at most");
    }

    [Theory]
    [InlineData(999)]
    [InlineData(60_001)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task BrowserNavigate_RefusesATimeoutOutsideItsRange_InsteadOfClampingIt(int timeoutMs)
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserNavigate("https://example.com/", handle: Handle, timeoutMs: timeoutMs));

        refused.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        refused.Message.Should().Contain("1000-60000");
        _tabs.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(60_000)]
    public async Task BrowserNavigate_TakesTheEdgesOfItsRange(int timeoutMs)
    {
        await CreateSut().BrowserNavigate("https://example.com/", handle: Handle, timeoutMs: timeoutMs);

        _tabs.Verify(t => t.NavigateAsync(It.IsAny<BrowserTabTarget>(),
            It.Is<BrowserNavigateOptions>(o => o.TimeoutMs == timeoutMs), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task BrowserNavigate_NewTabWithATargetId_IsRefused()
    {
        var refused = await RefusedAsync(() => CreateSut().BrowserNavigate("https://example.com/", handle: Handle, targetId: TargetId, newTab: true));

        refused.Message.Should().Contain("newTab");
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BrowserNavigate_WithAPortAlone_NeedsATargetId_ButANewTabDoesNot()
    {
        await RefusedAsync(() => CreateSut().BrowserNavigate("https://example.com/", cdpPort: 9222));
        await CreateSut().BrowserNavigate("https://example.com/", cdpPort: 9222, newTab: true);
        await CreateSut().BrowserNavigate("https://example.com/", cdpPort: 9222, targetId: TargetId);

        _tabs.Verify(t => t.NavigateAsync(new BrowserTabTarget(null, 9222, null), It.Is<BrowserNavigateOptions>(o => o.NewTab), It.IsAny<CancellationToken>()));
        _tabs.Verify(t => t.NavigateAsync(new BrowserTabTarget(null, 9222, TargetId), It.IsAny<BrowserNavigateOptions>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task TheTabTools_NeedAWindowOrAPort()
    {
        (await RefusedAsync(() => CreateSut().ListTabs())).Message.Should().Contain("cdpPort");
        await RefusedAsync(() => CreateSut().ActivateTab(TargetId));
        await RefusedAsync(() => CreateSut().BrowserNavigate("https://example.com/"));
        await RefusedAsync(() => CreateSut().WaitForBrowser(urlContains: "x"));
        _tabs.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(65_536)]
    public async Task ACdpPortOutside1To65535_IsRefused(int port)
    {
        (await RefusedAsync(() => CreateSut().ListTabs(cdpPort: port))).Message.Should().Contain("1 to 65535");
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListTabs_TakesAPortAlone()
    {
        await CreateSut().ListTabs(cdpPort: 9222);

        _tabs.Verify(t => t.ListTabsAsync(new BrowserTabTarget(null, 9222, null), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData("not a tab id")]
    [InlineData("../json/close")]
    [InlineData("")]
    public async Task AMalformedTargetId_IsRefused(string targetId)
    {
        await RefusedAsync(() => CreateSut().ActivateTab(targetId, handle: Handle));
        await RefusedAsync(() => CreateSut().BrowserNavigate("https://example.com/", handle: Handle, targetId: targetId));
        await RefusedAsync(() => CreateSut().WaitForBrowser(handle: Handle, targetId: targetId, urlContains: "x"));
        _tabs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhileInputIsPaused_TheToolsThatChangeTheBrowserAreRefused_AndTheReadsStillWork()
    {
        _session.Pause(InputControlSources.Hotkey);

        (await RefusedAsync(() => CreateSut().ActivateTab(TargetId, handle: Handle))).ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        (await RefusedAsync(() => CreateSut().BrowserNavigate("https://example.com/", handle: Handle))).ErrorCode
            .Should().Be(nameof(WindowErrorCode.PausedByUser));
        (await CreateSut().ListTabs(handle: Handle)).Success.Should().BeTrue();
        (await CreateSut().WaitForBrowser(handle: Handle, urlContains: "x")).Success.Should().BeTrue();
        _tabs.Verify(t => t.ActivateTabAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<CancellationToken>()), Times.Never);
        _tabs.Verify(t => t.NavigateAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserNavigateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForBrowser_NeedsACondition_AndIgnoresEmptyOnes()
    {
        (await RefusedAsync(() => CreateSut().WaitForBrowser(handle: Handle, urlContains: "", selector: ""))).Message
            .Should().Contain("at least one condition");

        await CreateSut().WaitForBrowser(handle: Handle, titleContains: "Done", textContains: "");

        _tabs.Verify(t => t.WaitAsync(It.IsAny<BrowserTabTarget>(), new BrowserWaitCondition(null, "Done", null, null),
            BrowserTabTools.DefaultWaitTimeoutMs, It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60_001)]
    public async Task WaitForBrowser_RefusesATimeoutOutsideItsRange_InsteadOfClampingIt(int timeoutMs)
    {
        (await RefusedAsync(() => CreateSut().WaitForBrowser(handle: Handle, urlContains: "x", timeoutMs: timeoutMs))).Message
            .Should().Contain("0-60000");
        _tabs.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60_000)]
    public async Task WaitForBrowser_TakesTheEdgesOfItsRange(int timeoutMs)
    {
        await CreateSut().WaitForBrowser(handle: Handle, urlContains: "x", timeoutMs: timeoutMs);

        _tabs.Verify(t => t.WaitAsync(It.IsAny<BrowserTabTarget>(), It.IsAny<BrowserWaitCondition>(), timeoutMs, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task WaitForBrowser_WithAPortAlone_NeedsATargetId()
    {
        (await RefusedAsync(() => CreateSut().WaitForBrowser(cdpPort: 9222, urlContains: "x"))).Message.Should().Contain("targetId");

        await CreateSut().WaitForBrowser(cdpPort: 9222, targetId: TargetId, urlContains: "x");

        _tabs.Verify(t => t.WaitAsync(new BrowserTabTarget(null, 9222, TargetId), It.IsAny<BrowserWaitCondition>(), It.IsAny<int>(), It.IsAny<CancellationToken>()));
    }
}
