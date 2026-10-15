namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// BrowserViewportLocator (roadmap M5.8): where a browser window shows a tab's page, for browser_element_rect. The tab's
/// Document through UI Automation (a fake tree shaped as Edge 155 was read on a real desktop, <see cref="FakeBrowserWindow"/>),
/// else a visible render widget window of the viewport's size (a mocked window wrapper); never a pane of another size.
/// </summary>
public class BrowserViewportLocatorTests
{
    private static readonly nint BrowserWindow = 0x2B0C3D;
    private static readonly nint RenderWidget = 0x2B0D10;
    private static readonly nint OtherWidget = 0x2B0D20;

    private readonly FakeUiaTree _tree = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Dictionary<nint, (string Class, bool Visible, (int, int, int, int) Rect)> _children = new();

    public BrowserViewportLocatorTests()
    {
        _windows.Setup(w => w.GetWindowText(BrowserWindow)).Returns("Page - Microsoft Edge");
        _windows.Setup(w => w.EnumChildWindows(BrowserWindow, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> visit) =>
            {
                foreach (var child in _children.Keys)
                {
                    if (!visit(child))
                        break;
                }
            });
        _windows.Setup(w => w.GetClassName(It.IsAny<nint>())).Returns((nint h) => _children.TryGetValue(h, out var c) ? c.Class : "");
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => _children.TryGetValue(h, out var c) && c.Visible);
        _windows.Setup(w => w.GetWindowRect(It.IsAny<nint>()))
            .Returns((nint h) => _children.TryGetValue(h, out var c) ? c.Rect : ((int, int, int, int)?)null);
    }

    private BrowserViewportLocator CreateSut() => new(_windows.Object, _tree, new InlineUiaWorker());

    private void Widget(nint handle, int left, int top, int right, int bottom, bool visible = true) =>
        _children[handle] = (AccessibilityWarmup.RenderWidgetHostClass, visible, (left, top, right, bottom));

    [Fact]
    public async Task TheTabsDocument_OfTheViewportsSize_IsTheAnchor()
    {
        // Edge's page is 1200 x 880 pixels at (0, 120), beside the sidebar's
        var browser = new FakeBrowserWindow(BrowserNames.EdgeEnglish);
        _tree.WithWindow(BrowserWindow, browser.Window);

        var found = await CreateSut().FindAsync(BrowserWindow, FakeBrowserWindow.CommittedUrl, 1200, 880, TimeSpan.FromSeconds(5));

        found.Viewport.Should().Be(new BrowserViewport(0, 120, 1200, 880, BrowserViewport.DocumentSource));
    }

    [Fact]
    public async Task ADocumentOfAnotherSize_IsNotTheAnchor_AndNoRenderWidgetOfTheSize_FindsNothing()
    {
        var browser = new FakeBrowserWindow(BrowserNames.EdgeEnglish);
        _tree.WithWindow(BrowserWindow, browser.Window);
        Widget(RenderWidget, 0, 120, 1200, 1000);

        var found = await CreateSut().FindAsync(BrowserWindow, FakeBrowserWindow.CommittedUrl, 1280, 720, TimeSpan.FromSeconds(5));

        found.Viewport.Should().BeNull();
        found.Detail.Should().Contain("1200 x 880").And.Contain("1280 x 720");
    }

    [Fact]
    public async Task ADocumentWithAnotherAddress_IsNotTheTabs_NorIsTheShownPagesRenderWidget()
    {
        // A background tab of the window has the shown tab's viewport size, so the shown page's render widget fits it too:
        // the address of the shown Document tells it is another page
        var browser = new FakeBrowserWindow(BrowserNames.EdgeEnglish);
        _tree.WithWindow(BrowserWindow, browser.Window);
        Widget(RenderWidget, 0, 120, 1200, 1000);

        var found = await CreateSut().FindAsync(BrowserWindow, "https://example.com/another-tab", 1200, 880, TimeSpan.FromSeconds(5));

        found.Viewport.Should().BeNull();
        found.Detail.Should().Contain("another page").And.Contain(FakeBrowserWindow.CommittedUrl);
    }

    [Fact]
    public async Task ADocumentAtTheSitesRoot_IsNotTheDocumentOfAPageUnderIt()
    {
        // The window shows the repository's page; the tab asked for is the site's root, in the background
        var browser = new FakeBrowserWindow(BrowserNames.EdgeEnglish);
        _tree.WithWindow(BrowserWindow, browser.Window);
        Widget(RenderWidget, 0, 120, 1200, 1000);

        var found = await CreateSut().FindAsync(BrowserWindow, "https://github.com/", 1200, 880, TimeSpan.FromSeconds(5));

        found.Viewport.Should().BeNull();
    }

    [Fact]
    public async Task WithoutADocument_TheVisibleRenderWidgetOfTheViewportsSize_IsTheAnchor()
    {
        // A window that stayed behind others since its page loaded has no page tree
        _tree.WithWindow(BrowserWindow, new FakeBrowserWindow(BrowserNames.EdgeEnglish, withDocument: false).Window);
        Widget(OtherWidget, 1700, 120, 2100, 1000);
        Widget(RenderWidget, -1904, 87, -624, 807);

        var found = await CreateSut().FindAsync(BrowserWindow, FakeBrowserWindow.CommittedUrl, 1280, 720, TimeSpan.FromSeconds(5));

        found.Viewport.Should().Be(new BrowserViewport(-1904, 87, 1280, 720, BrowserViewport.RenderWidgetSource));
    }

    [Fact]
    public async Task AHiddenRenderWidget_IsNotTheAnchor()
    {
        Widget(RenderWidget, 0, 120, 1280, 840, visible: false);

        var found = await CreateSut().FindAsync(BrowserWindow, FakeBrowserWindow.CommittedUrl, 1280, 720, TimeSpan.FromSeconds(5));

        found.Viewport.Should().BeNull();
    }

    [Fact]
    public async Task TwoRenderWidgetsOfTheViewportsSize_AreNeitherTheAnchor()
    {
        // Split tabs side by side: neither can be told for the tab's
        Widget(RenderWidget, 0, 120, 1280, 840);
        Widget(OtherWidget, 1280, 120, 2560, 840);

        var found = await CreateSut().FindAsync(BrowserWindow, FakeBrowserWindow.CommittedUrl, 1280, 720, TimeSpan.FromSeconds(5));

        found.Viewport.Should().BeNull();
        found.Detail.Should().Contain("two render widgets");
    }

    [Fact]
    public async Task AUiAutomationReadThatFails_FallsBackToTheRenderWidget()
    {
        _tree.FromHandleException = new TimeoutException("The provider did not answer.");
        Widget(RenderWidget, 0, 120, 1280, 840);

        var found = await CreateSut().FindAsync(BrowserWindow, FakeBrowserWindow.CommittedUrl, 1280, 720, TimeSpan.FromSeconds(5));

        found.Viewport!.Source.Should().Be(BrowserViewport.RenderWidgetSource);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, true)]
    [InlineData(1921, 1077, 1920, 1080, true)]
    [InlineData(1925, 1080, 1920, 1080, true)]
    [InlineData(1945, 1080, 1920, 1080, false)]
    [InlineData(1280, 720, 1281.5, 721.5, true)]
    [InlineData(1280, 720, 1920, 1080, false)]
    [InlineData(0, 0, 0, 0, false)]
    [InlineData(double.PositiveInfinity, 720, 1280, 720, false)]
    public void Fits_IsTheViewportsSize_WithinAFewPixelsOrOnePercent(double width, double height, double expectedWidth, double expectedHeight, bool fits)
    {
        BrowserViewportLocator.Fits(width, height, expectedWidth, expectedHeight).Should().Be(fits);
    }
}
