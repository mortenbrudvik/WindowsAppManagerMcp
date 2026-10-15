namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for BrowserUiaReader, the UI Automation reads of get_browser_url and get_browser_content on the IUiaTree seam
/// (roadmap M3.F24), over an in-memory tree: the locale-independent lookup of the page's address and of the address bar
/// (roadmap M5.7), over Chrome and Edge trees in English and Norwegian (<see cref="FakeBrowserWindow"/>). No real browser
/// or UI Automation client is read.
/// </summary>
public class BrowserUiaReaderTests
{
    /// <summary>A page long enough for the whole of every test page.</summary>
    private const int Whole = ElementReadService.MaxTextLength;

    /// <summary>read_element_text's bytes of JSON per page, which get_browser_content's pages share (roadmap M5.6).</summary>
    private const int Bytes = ElementReadService.MaxTextBytes;

    private readonly FakeUiaTree _tree = new();

    private BrowserUiaReader CreateSut() => new(_tree);

    public static TheoryData<BrowserNames> Languages() => new(BrowserNames.Localized);

    private static FakeUiaNode Window(params FakeUiaNode[] children) =>
        new FakeUiaNode("Window", "Example - Google Chrome").With(children);

    private static FakeUiaNode Edit(
        string name, string? value, bool isPassword = false, string? className = null, string? automationId = null) =>
        new FakeUiaNode("Edit", name)
        {
            IsPassword = isPassword, ActionPatterns = ["Value"], ClassName = className, AutomationId = automationId
        }.WithPattern<IUiaValuePattern>(new FakeValuePattern(value));

    private static FakeUiaNode Document(string name, string? url, UiaRect? bounds = null)
    {
        var document = new FakeUiaNode("Document", name) { Bounds = bounds ?? new UiaRect(0, 100, 1200, 800) };
        return url != null ? document.WithPattern<IUiaValuePattern>(new FakeValuePattern(url)) : document;
    }

    private BrowserUiaUrl ReadUrl(FakeBrowserWindow browser, bool withPages = true, bool foreground = true) =>
        CreateSut().ReadUrl(browser.Window, browser.Title, withPages ? browser.Pages : null, foreground, CancellationToken.None);

    #region get_browser_url: the page's address

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_ReadsTheCommittedUrlFromTheTabsDocument_InEveryLanguage(BrowserNames names)
    {
        // Arrange
        var browser = new FakeBrowserWindow(names);

        // Act
        var url = ReadUrl(browser);

        // Assert
        url.Should().Be(new BrowserUiaUrl(FakeBrowserWindow.CommittedUrl, FakeBrowserWindow.ShownUrl, BrowserUrlSource.Document, false));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_FindsTheSameElements_WhateverTheyAreNamed(BrowserNames names)
    {
        // Arrange - the same window in its language, with every element named like the address bar, and with no names
        var shapes = new[] { names, BrowserNames.Hostile, BrowserNames.Nameless }
            .Select(n => new FakeBrowserWindow(n, edge: names.IsEdge))
            .ToList();

        // Act
        var urls = shapes.Select(browser => ReadUrl(browser)).ToList();
        var addressBars = shapes.Select(browser => ReadUrl(new FakeBrowserWindow(browser.Names, withDocument: false, edge: names.IsEdge), withPages: false)).ToList();

        // Assert - names never decide
        urls.Should().AllBeEquivalentTo(
            new BrowserUiaUrl(FakeBrowserWindow.CommittedUrl, FakeBrowserWindow.ShownUrl, BrowserUrlSource.Document, false));
        addressBars.Should().AllBeEquivalentTo(
            new BrowserUiaUrl(FakeBrowserWindow.ShownUrl, FakeBrowserWindow.ShownUrl, BrowserUrlSource.AddressBar, false));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrlAndContent_TakeTheDocumentBesideTheRenderWidgetsPane_WithTheBrowserBehindAnotherWindow(BrowserNames names)
    {
        // Arrange - Chrome and Edge on a real desktop: the warm-up gives the render widget's pane "Chrome Legacy Window",
        // which holds the page's elements but not its address; the tab's Document sits beside it in the web view. The
        // browser is not in front, so the address bar tells nothing
        var browser = new FakeBrowserWindow(names);

        // Act
        var url = ReadUrl(browser, foreground: false);
        var content = CreateSut().ReadContent(browser.Window, browser.Title, browser.Pages, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        browser.Pages.Should().StartWith(browser.RenderWidgetPane);
        url.Should().Be(new BrowserUiaUrl(FakeBrowserWindow.CommittedUrl, FakeBrowserWindow.ShownUrl, BrowserUrlSource.Document, null));
        content.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        content.Text.Should().StartWith($"Welcome to {FakeBrowserWindow.PageTitle}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrlAndContent_FindTheDocument_WhenTheRenderWidgetIsHiddenBehindOtherWindows(BrowserNames names)
    {
        // Arrange - a browser covered by other windows has no render widget, so the warm-up gives no page; the Document
        // built before stays in the views
        var browser = new FakeBrowserWindow(names, renderWidgetShown: false);

        // Act
        var url = ReadUrl(browser, foreground: false);
        var content = CreateSut().ReadContent(browser.Window, browser.Title, browser.Pages, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        browser.Pages.Should().BeEmpty();
        url.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        url.UrlSource.Should().Be(BrowserUrlSource.Document);
        content.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
    }

    [Fact]
    public void ReadUrlAndContent_SearchTheWindowForTheDocument_WhenNoneIsBesideTheRenderWidgetsPane()
    {
        // Arrange - the pane's parent cannot be read
        var browser = new FakeBrowserWindow(BrowserNames.EdgeNorwegian);
        browser.RenderWidgetPane.ParentException = FakeUiaTree.Vanished();
        browser.SidebarRenderWidgetPane!.ParentException = FakeUiaTree.Vanished();

        // Act
        var url = ReadUrl(browser, foreground: false);
        var content = CreateSut().ReadContent(browser.Window, browser.Title, browser.Pages, 0, Whole, Bytes, CancellationToken.None);

        // Assert - the tab's page, not Edge's sidebar
        url.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        content.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        content.Text.Should().StartWith($"Welcome to {FakeBrowserWindow.PageTitle}");
    }

    [Fact]
    public void ReadContent_ReadsTheRenderWidgetsPane_WhenTheWindowHasNoDocument()
    {
        // Arrange - WebView2 runtime 155 (roadmap M3.F33): the render widget's pane holds the page's elements, and there is
        // no Document anywhere
        var pane = new FakeUiaNode("Pane", FakeBrowserWindow.LegacyWindowName)
        {
            ClassName = AccessibilityWarmup.RenderWidgetHostClass, FrameworkId = "Win32"
        }.With(new FakeUiaNode("Text", "Web content") { FrameworkId = "Chrome" });
        var window = Window(new FakeUiaNode("Pane") { ClassName = "Chrome_WidgetWin_0" }.With(pane));

        // Act
        var content = CreateSut().ReadContent(window, null, [pane], 0, Whole, Bytes, CancellationToken.None);
        var url = CreateSut().ReadUrl(window, null, [pane], false, CancellationToken.None);

        // Assert
        content.Should().Be(new BrowserUiaContent(true, "Web content", null, 0, TotalLength: "Web content".Length));
        url.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrlAndContent_SayThePageMayNotBeShown_WhenItsTreeWasNeverBuilt(BrowserNames names)
    {
        // Arrange - a browser that stayed behind other windows since its page loaded: no render widget and no Document
        var browser = new FakeBrowserWindow(names, withDocument: false, renderWidgetShown: false);

        // Act
        var url = ReadUrl(browser, foreground: false);
        var content = CreateSut().ReadContent(browser.Window, browser.Title, browser.Pages, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        url.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        url.Url.Should().BeNull();
        url.DisplayUrl.Should().Be(FakeBrowserWindow.ShownUrl);
        url.Error.Should().Contain("behind other windows").And.Contain("CDP");
        content.Found.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_TakesThePagesAddress_OfAPageTitledLikeTheAddressBar(BrowserNames names)
    {
        // Arrange - a data: page titled "Address bar", as read on a real desktop: the Document's value is encoded, the
        // address bar shows the text decoded
        const string committed = "data:text/html,%3Ctitle%3EAddress%20bar%3C/title%3E%3Cp%3Ea%20page%3C/p%3E";
        const string shown = "data:text/html,<title>Address%20bar</title><p>a%20page</p>";
        var browser = new FakeBrowserWindow(names, pageTitle: "Address bar", committedUrl: committed, addressBarText: shown);

        // Act
        var front = ReadUrl(browser);
        var behind = ReadUrl(browser, foreground: false);

        // Assert
        front.Should().Be(new BrowserUiaUrl(committed, shown, BrowserUrlSource.Document, false));
        behind.Should().Be(new BrowserUiaUrl(committed, shown, BrowserUrlSource.Document, null));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_TakesAFilePagesAddress_FromItsDocument_WhileTheAddressBarShowsItsPath(BrowserNames names)
    {
        // Arrange - Chrome and Edge show a file: page's address as its path, without file:///
        var browser = new FakeBrowserWindow(names, pageTitle: "WAM file page", committedUrl: FileUrl, addressBarText: FilePath);

        // Act
        var front = ReadUrl(browser);
        var behind = ReadUrl(browser, foreground: false);

        // Assert
        front.Should().Be(new BrowserUiaUrl(FileUrl, FilePath, BrowserUrlSource.Document, false));
        behind.Should().Be(new BrowserUiaUrl(FileUrl, FilePath, BrowserUrlSource.Document, null));
    }

    [Fact]
    public void ReadUrl_TakesAFilePagesPath_FromTheAddressBar_AsItsFileUrl()
    {
        // Arrange - no Document gives the address
        var browser = new FakeBrowserWindow(BrowserNames.EdgeNorwegian, committedUrl: null, addressBarText: FilePath);

        // Act
        var front = ReadUrl(browser);
        var behind = ReadUrl(browser, foreground: false);

        // Assert - a path is a URL shown without its scheme, trusted as any address bar text is
        front.Should().Be(new BrowserUiaUrl(FileUrl, FilePath, BrowserUrlSource.AddressBar, false));
        behind.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        behind.DisplayUrl.Should().Be(FilePath);
        behind.Error.Should().Contain("foreground").And.NotContain("not a URL");
    }

    private const string FilePath = "C:/Users/me/AppData/Local/Temp/wam-m57%20local%20page.html";
    private const string FileUrl = "file:///" + FilePath;

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_SearchesTheWindowForItsDocument_WhenNoPagesAreGiven(BrowserNames names)
    {
        var url = ReadUrl(new FakeBrowserWindow(names), withPages: false);

        url.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        url.UrlSource.Should().Be(BrowserUrlSource.Document);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_ReadsTheAddressBar_WhenThePageHasNoDocumentYet(BrowserNames names)
    {
        // Arrange - Chromium has not built the page's tree
        var browser = new FakeBrowserWindow(names, withDocument: false);

        // Act
        var url = ReadUrl(browser, withPages: false);

        // Assert - the address bar's text, which Chrome shows without the scheme
        url.Should().Be(new BrowserUiaUrl(FakeBrowserWindow.ShownUrl, FakeBrowserWindow.ShownUrl, BrowserUrlSource.AddressBar, false));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_ReadsTheAddressBar_WhenTheDocumentHoldsNoAddress(BrowserNames names)
    {
        var browser = new FakeBrowserWindow(names, committedUrl: null, addressBarText: "https://localhost:3000/a");

        var url = ReadUrl(browser);

        url.Should().Be(new BrowserUiaUrl("https://localhost:3000/a", "https://localhost:3000/a", BrowserUrlSource.AddressBar, false));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_FindsTheAddressBarByStructure_WhenItsClassIsUnknown(BrowserNames names)
    {
        // Arrange - a Chromium browser whose address bar is of another class: the first edit box inside a toolbar
        var browser = new FakeBrowserWindow(names, omniboxClass: null, withDocument: false);

        // Act
        var url = ReadUrl(browser, withPages: false);

        // Assert
        url.Url.Should().Be(FakeBrowserWindow.ShownUrl);
        url.UrlSource.Should().Be(BrowserUrlSource.AddressBar);
    }

    [Fact]
    public void ReadUrl_NeverReturnsTheWindowTitle_ATab_ABookmark_OrAPageElement_NamedLikeTheAddressBar()
    {
        // Arrange - an app window (no toolbar) whose page, tab, title and links are all named like the address bar, and a
        // page whose Document holds no address
        var browser = new FakeBrowserWindow(BrowserNames.ChromeEnglish, pageTitle: "Address bar tips", committedUrl: null,
            withToolbar: false, otherTabTitle: "Address and search bar");

        // Act
        var url = ReadUrl(browser);

        // Assert
        url.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
        url.Url.Should().BeNull();
        url.DisplayUrl.Should().BeNull();
        _tree.Reads.Should().NotContain($"Pattern:IUiaValuePattern:{browser.PageField.Name}",
            "the page's edit box of the omnibox's class and Firefox's id is the page's, not the browser's");
        _tree.Reads.Should().NotContain("Pattern:IUiaValuePattern:Address and search bar");
    }

    [Fact]
    public void ReadUrl_TakesTheDocumentsAddress_InAnAppWindowWithoutAToolbar()
    {
        var browser = new FakeBrowserWindow(BrowserNames.EdgeNorwegian, withToolbar: false);

        var url = ReadUrl(browser);

        url.Should().Be(new BrowserUiaUrl(FakeBrowserWindow.CommittedUrl, null, BrowserUrlSource.Document, null));
    }

    [Fact]
    public void ReadUrl_DoesNotLookInsideThePage_TheTabs_OrTheirRenderWidgets()
    {
        // Arrange - no address bar of the omnibox's class: the whole UI is searched, but not the page or the tabs
        var browser = new FakeBrowserWindow(BrowserNames.ChromeEnglish, omniboxClass: null, withToolbar: false);
        browser.Window.With(new FakeUiaNode("Pane", "Chrome Legacy Window") { ClassName = AccessibilityWarmup.RenderWidgetHostClass }
            .With(Edit("Inside the widget", "https://widget.example/", className: BrowserUiaReader.OmniboxClassName)));

        // Act
        CreateSut().ReadUrl(browser.Window, browser.Title, [], true, CancellationToken.None);

        // Assert
        _tree.ChildListings.Should().NotContain([FakeBrowserWindow.PageTitle, "Address and search bar", "Chrome Legacy Window"]);
    }

    [Fact]
    public void ReadUrl_Firefox_ReadsTheUrlBarByItsAutomationId_OutsideThePage()
    {
        // Arrange - Firefox in Norwegian: a page element with the same DOM id comes first in tree order
        var page = Document("Mozilla", "https://www.mozilla.org/nb-NO/").With(
            Edit("Søk", "https://evil.example/", automationId: BrowserUiaReader.FirefoxUrlBarAutomationId));
        var urlBar = Edit("Søk med Google eller skriv inn adresse", "mozilla.org/nb-NO/",
            automationId: BrowserUiaReader.FirefoxUrlBarAutomationId);
        var window = new FakeUiaNode("Window", "Mozilla — Mozilla Firefox") { ClassName = "MozillaWindowClass" }.With(
            page,
            new FakeUiaNode("ToolBar", "Navigasjon") { AutomationId = "nav-bar" }.With(
                new FakeUiaNode("Button", "Tilbake"),
                new FakeUiaNode("ComboBox") { AutomationId = "urlbar" }.With(urlBar)));

        // Act
        var url = CreateSut().ReadUrl(window, "Mozilla — Mozilla Firefox", null, true, CancellationToken.None);
        var withoutPage = CreateSut().ReadUrl(new FakeUiaNode("Window", "Firefox").With(
            new FakeUiaNode("ToolBar").With(Edit("Adresse", "mozilla.org/nb-NO/", automationId: BrowserUiaReader.FirefoxUrlBarAutomationId))),
            null, null, true, CancellationToken.None);

        // Assert
        url.Should().Be(new BrowserUiaUrl("https://www.mozilla.org/nb-NO/", "mozilla.org/nb-NO/", BrowserUrlSource.Document, false));
        withoutPage.Url.Should().Be("mozilla.org/nb-NO/");
    }

    [Theory]
    [InlineData("chrome://new-tab-page/")]
    [InlineData("chrome-search://local-ntp/local-ntp.html")]
    [InlineData("edge://newtab/")]
    [InlineData("https://ntp.msn.com/edge/ntp?locale=nb-NO&title=Ny%20fane")]
    public void ReadUrl_ReportsTheNewTabPage_FromItsDocument(string newTabPage)
    {
        var browser = new FakeBrowserWindow(BrowserNames.EdgeNorwegian, pageTitle: "Ny fane", committedUrl: newTabPage, addressBarText: "");

        var url = ReadUrl(browser);

        url.ErrorCode.Should().Be(BrowserErrorCode.NewTabPage);
        url.Url.Should().Be(newTabPage);
        url.Error.Should().Contain("new tab page");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadUrl_ReportsTheNewTabPage_WhenTheAddressBarIsEmpty_AndNoDocumentGivesAnAddress(BrowserNames names)
    {
        var browser = new FakeBrowserWindow(names, committedUrl: null, addressBarText: "");
        browser.Omnibox.HasKeyboardFocus = true;

        var url = ReadUrl(browser);

        url.Should().Be(BrowserUiaUrl.Failed(BrowserErrorCode.NewTabPage, url.Error!, null, null, true));
    }

    [Fact]
    public void ReadUrl_DoesNotTrustTheAddressBar_WhileItIsEdited()
    {
        // Arrange - the user is typing in the address bar
        var typing = new FakeBrowserWindow(BrowserNames.ChromeNorwegian, committedUrl: null, addressBarText: "evil.example");
        typing.Omnibox.HasKeyboardFocus = true;
        var searching = new FakeBrowserWindow(BrowserNames.ChromeNorwegian, addressBarText: "hvordan bake brød");
        searching.Omnibox.HasKeyboardFocus = true;

        // Act
        var fromBar = ReadUrl(typing);
        var fromDocument = ReadUrl(searching);

        // Assert - only the page's own address is taken, and typed text that is not URL-like is not returned
        fromBar.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        fromBar.Url.Should().BeNull();
        fromBar.DisplayUrl.Should().Be("evil.example");
        fromBar.IsEditing.Should().BeTrue();
        fromDocument.Should().Be(new BrowserUiaUrl(FakeBrowserWindow.CommittedUrl, null, BrowserUrlSource.Document, true));
    }

    [Fact]
    public void ReadUrl_DoesNotTrustTheAddressBar_WhenTheBrowserIsNotInFront()
    {
        // Arrange - text typed in the address bar and left there; a browser behind another window reports no focus, so its
        // address bar reads as not edited
        var typed = new FakeBrowserWindow(BrowserNames.ChromeNorwegian, committedUrl: null, addressBarText: "intranet.corp/secret-project");
        var committed = new FakeBrowserWindow(BrowserNames.ChromeNorwegian, addressBarText: "intranet.corp/secret-project");

        // Act
        var fromBar = ReadUrl(typed, foreground: false);
        var fromDocument = ReadUrl(committed, foreground: false);

        // Assert - whether the text was typed cannot be told, so only the page's own address is taken
        fromBar.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        fromBar.Url.Should().BeNull();
        fromBar.DisplayUrl.Should().Be("intranet.corp/secret-project");
        fromBar.IsEditing.Should().BeNull();
        fromBar.Error.Should().Contain("foreground").And.NotContain("secret-project");
        fromDocument.Should().Be(
            new BrowserUiaUrl(FakeBrowserWindow.CommittedUrl, "intranet.corp/secret-project", BrowserUrlSource.Document, null));
        _tree.Reads.Should().NotContain(read => read.StartsWith("Interaction:", StringComparison.Ordinal),
            "the focus of a browser that is not in front tells nothing");
    }

    [Fact]
    public void ReadUrl_DoesNotTrustTheAddressBar_WhenItsFocusCannotBeRead()
    {
        // Arrange
        var browser = new FakeBrowserWindow(BrowserNames.EdgeEnglish, committedUrl: null, addressBarText: "intranet.corp/secret-project");
        browser.Omnibox.InteractionException = FakeUiaTree.Vanished();

        // Act
        var url = ReadUrl(browser);

        // Assert - a focus that cannot be read counts as an address bar being edited
        url.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        url.Url.Should().BeNull();
        url.DisplayUrl.Should().Be("intranet.corp/secret-project");
        url.IsEditing.Should().BeNull();
    }

    [Fact]
    public void ReadUrl_ReadsTheAddressBar_WhenTheSearchForThePageTimesOut()
    {
        // Arrange - Chromium's page tree does not answer, its own UI does
        var browser = new FakeBrowserWindow(BrowserNames.ChromeEnglish);
        browser.Window.FindAllException = new TimeoutException("The provider did not answer.");

        // Act
        var url = ReadUrl(browser, withPages: false);

        // Assert
        url.Should().Be(new BrowserUiaUrl(FakeBrowserWindow.ShownUrl, FakeBrowserWindow.ShownUrl, BrowserUrlSource.AddressBar, false));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("Søk på Google eller skriv inn en nettadresse")]
    [InlineData("C:\\Users\\me\\page.html")]
    [InlineData("javascript:alert(1)")]
    public void ReadUrl_RefusesAddressBarTextThatIsNotAUrl(string text)
    {
        var browser = new FakeBrowserWindow(BrowserNames.ChromeNorwegian, committedUrl: null, addressBarText: text);

        var url = ReadUrl(browser);

        url.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        url.Url.Should().BeNull();
        url.DisplayUrl.Should().BeNull();
        url.Error.Should().NotContain(text);
    }

    [Fact]
    public void ReadUrl_ChoosesTheTabsPage_OverDevTools_ASidePanel_AndEdgesSidebar()
    {
        // Arrange - larger pages of the browser's own UI, and a sidebar page, are shown beside the tab's
        var browser = new FakeBrowserWindow(BrowserNames.EdgeEnglish, committedUrl: "https://example.com/", addressBarText: "https://example.com/");
        var devTools = Document("DevTools", "devtools://devtools/bundled/devtools_app.html", new UiaRect(0, 0, 3000, 3000));
        var sidePanel = Document(FakeBrowserWindow.PageTitle, "chrome://read-later.top-chrome/", new UiaRect(0, 0, 3000, 3000));
        IReadOnlyList<IUiaNode> pages = [devTools, sidePanel, browser.Sidebar!, browser.Document];

        // Act
        var url = CreateSut().ReadUrl(browser.Window, browser.Title, pages, true, CancellationToken.None);
        var content = CreateSut().ReadContent(browser.Window, browser.Title, pages, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        url.Url.Should().Be("https://example.com/");
        content.Url.Should().Be("https://example.com/");
        content.Text.Should().StartWith($"Welcome to {FakeBrowserWindow.PageTitle}");
    }

    [Fact]
    public void ReadUrl_PrefersThePageTheAddressBarShows()
    {
        // Arrange - a larger page whose title also starts the window's title
        var browser = new FakeBrowserWindow(BrowserNames.ChromeEnglish);
        var other = Document(FakeBrowserWindow.PageTitle, "https://other.example/", new UiaRect(0, 0, 3000, 3000));

        // Act
        var url = CreateSut().ReadUrl(browser.Window, browser.Title, [other, browser.Document], true, CancellationToken.None);

        // Assert
        url.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
    }

    [Fact]
    public void ReadUrl_LeavesOutAnOffscreenDocument()
    {
        var browser = new FakeBrowserWindow(BrowserNames.ChromeEnglish);
        browser.Document.IsOffscreen = true;

        var url = ReadUrl(browser);

        url.UrlSource.Should().Be(BrowserUrlSource.AddressBar);
        url.Url.Should().Be(FakeBrowserWindow.ShownUrl);
    }

    [Fact]
    public void ReadUrl_NeverReadsTheValueOfAPasswordField()
    {
        // Arrange - a password field is the toolbar's first edit box; a Document whose value is not a URL
        var window = Window(new FakeUiaNode("ToolBar").With(Edit("Secret", "hunter2.example", isPassword: true)),
            Document("Example", "not a url"));

        // Act
        var url = CreateSut().ReadUrl(window, "Example - Google Chrome", null, true, CancellationToken.None);

        // Assert
        url.ErrorCode.Should().Be(BrowserErrorCode.UrlExtractionFailed);
        url.DisplayUrl.Should().BeNull();
        _tree.Reads.Should().NotContain("Pattern:IUiaValuePattern:Secret");
    }

    [Fact]
    public void ReadUrl_SkipsAnElementThatCannotBeRead_AndGoesOnWithItsSiblings()
    {
        // Arrange - the level's cached read fails because of the broken element, so it is read one element at a time
        var window = Window(new FakeUiaNode("ToolBar").With(
            new FakeUiaNode("Button", "Broken") { ReadException = FakeUiaTree.Vanished() },
            Edit("Omnibox", "example.com/", className: BrowserUiaReader.OmniboxClassName)));

        // Act
        var url = CreateSut().ReadUrl(window, null, null, true, CancellationToken.None);

        // Assert
        url.Url.Should().Be("example.com/");
    }

    [Fact]
    public void ReadUrl_SearchesTheAddressBarFourteenLevelsBelowTheWindow_AndNoDeeper()
    {
        // Arrange - the window is level 0; a bar at level 14 is found, one at level 15 is not
        FakeUiaNode Nest(int levels, FakeUiaNode leaf)
        {
            var node = leaf;
            for (var i = 0; i < levels; i++)
            {
                node = new FakeUiaNode("Pane", $"Level {levels - i}").With(node);
            }

            return node;
        }

        var reachable = Window(Nest(13, Edit("Omnibox", "deep.example/", className: BrowserUiaReader.OmniboxClassName)));
        var tooDeep = Window(Nest(14, Edit("Omnibox", "deeper.example/", className: BrowserUiaReader.OmniboxClassName)));

        // Act
        var found = CreateSut().ReadUrl(reachable, null, [], true, CancellationToken.None);
        var missed = CreateSut().ReadUrl(tooDeep, null, [], true, CancellationToken.None);

        // Assert
        found.Url.Should().Be("deep.example/");
        missed.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
    }

    [Fact]
    public void ReadUrl_ReadsAtMostItsElementLimit()
    {
        // Arrange - a level wider than the search reads, the address bar after it
        var wide = Enumerable.Range(0, BrowserUiaReader.MaxAddressBarSearchElements)
            .Select(i => new FakeUiaNode("Button", $"B{i}"))
            .Append(Edit("Late", "late.example/", className: BrowserUiaReader.OmniboxClassName))
            .ToArray();
        var window = Window(new FakeUiaNode("Pane", "Wide").With(wide));

        // Act
        var url = CreateSut().ReadUrl(window, null, [], true, CancellationToken.None);

        // Assert - the address bar past the limit is never read
        url.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
        _tree.ReadNodes.Should().NotContain("Late");
    }

    [Fact]
    public void ReadUrl_ReturnsUrlBarNotFound_WhenTheWindowCannotBeRead()
    {
        var window = Window(Edit("Omnibox", "https://example.com/", className: BrowserUiaReader.OmniboxClassName));
        window.ChildrenException = FakeUiaTree.Vanished();
        window.FindAllException = FakeUiaTree.Vanished();

        var url = CreateSut().ReadUrl(window, null, null, true, CancellationToken.None);

        url.ErrorCode.Should().Be(BrowserErrorCode.UrlBarNotFound);
    }

    [Fact]
    public void ReadUrl_StopsOnceTheTokenIsCancelled()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var act = () => CreateSut().ReadUrl(new FakeBrowserWindow(BrowserNames.ChromeEnglish).Window, null, null, true, cancelled.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    #endregion

    #region get_browser_content

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadContent_ReadsTheTabsPage_WithItsAddress_WithoutTheAddressBar(BrowserNames names)
    {
        // Arrange
        var browser = new FakeBrowserWindow(names);

        // Act
        var content = CreateSut().ReadContent(browser.Window, browser.Title, browser.Pages, 0, Whole, Bytes, CancellationToken.None);

        // Assert - the page's text, not its address; the address bar is not looked up (D-BRW-20)
        content.Found.Should().BeTrue();
        content.Url.Should().Be(FakeBrowserWindow.CommittedUrl);
        content.Text.Should().Be(string.Join(Environment.NewLine,
            $"Welcome to {FakeBrowserWindow.PageTitle}", "Address and search bar", FakeBrowserWindow.AttackerUrl, "Search",
            FakeBrowserWindow.AttackerUrl));
        _tree.ChildListings.Should().NotContain([browser.RootView.Name!, browser.ToolBar.Name ?? "ToolBar"]);
        _tree.Reads.Should().NotContain(read => read.StartsWith("Interaction:", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadContent_ReadsTheDocumentsTextPattern()
    {
        // Arrange
        var text = new FakeTextPattern("The whole article.");
        var window = Window(new FakeUiaNode("Pane").With(
            new FakeUiaNode("Document", "Example").WithPattern<IUiaTextPattern>(text).With(new FakeUiaNode("Text", "Ignored"))));

        // Act
        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        content.Text.Should().Be("The whole article.");
        _tree.ReadNodes.Should().NotContain("Ignored", "the Text pattern reads the document in one call");
    }

    [Fact]
    public void ReadContent_ReadsOnlyMaxLengthCharactersOfTheTextPattern()
    {
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern("abcdefghij")));

        var content = CreateSut().ReadContent(window, null, null, 0, 4, Bytes, CancellationToken.None);

        content.Text.Should().Be("abcd");
    }

    [Fact]
    public void ReadContent_PagesChromiumsText_AsCdpPagesIt_WithNoLineBreakReadTwice()
    {
        // Arrange - the M5.6 desktop check's page as Edge 155 reads it: a <pre> of 1000 lines of 99 characters, joined by
        // "\n", and the next block's text; Chromium's range that starts just after a line break reads it again
        var document = string.Join("\n", Enumerable.Range(0, 1000).Select(i =>
            $"{i:00000} " + string.Concat(Enumerable.Repeat("abcdefghij", 9)) + "abc")) + "\nscoped part";
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern(document) { ChromiumRanges = true }));

        // Act - page on from each nextOffset
        var pages = new List<BrowserUiaContent>();
        do
        {
            pages.Add(CreateSut().ReadContent(window, null, null, pages.Count == 0 ? 0 : pages[^1].NextOffset!.Value, 20_000, Bytes,
                CancellationToken.None));
        }
        while (pages[^1].NextOffset != null);

        // Assert - before the fix the second page started with line 199's "\n" again and nextOffset was 39999
        string.Concat(pages.Select(p => p.Text)).Should().Be(document);
        pages.Select(p => p.Offset).Should().Equal(0, 20_000, 40_000, 60_000, 80_000, 100_000);
        pages[^1].TotalLength.Should().Be(document.Length);
    }

    [Fact]
    public void ReadContent_PagesTheTextPattern_ReadingOnlyThePage()
    {
        // Arrange
        var text = new FakeTextPattern("abcdefghij");
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(text));

        // Act
        var first = CreateSut().ReadContent(window, null, null, 4, 3, Bytes, CancellationToken.None);
        var last = CreateSut().ReadContent(window, null, null, 8, 3, Bytes, CancellationToken.None);

        // Assert - read_element_text's paging: the whole length is known once the last page is read
        first.Should().Be(new BrowserUiaContent(true, "efg", null, 4, TotalLength: null, NextOffset: 7));
        last.Should().Be(new BrowserUiaContent(true, "ij", null, 8, TotalLength: 10, NextOffset: null));
        text.PageLengths.Should().Equal([3, 3], "a page costs its own length, not the document's");
    }

    [Fact]
    public void ReadContent_FitsATextPatternPage_ToTheBytesOfJson_SoTheNextPageSkipsNothing()
    {
        var text = new FakeTextPattern("abcdefghijkl");
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(text));

        var content = CreateSut().ReadContent(window, null, null, 0, 12, maxBytes: 5, CancellationToken.None);

        content.Should().Be(new BrowserUiaContent(true, "abcde", null, 0, TotalLength: null, NextOffset: 5));
    }

    [Fact]
    public void ReadContent_SaysSo_WhenATextPatternPageIsCut()
    {
        // Arrange - a provider that counts "\r\n" as one character, longer than a page of one character
        var text = new FakeTextPattern("\r\nab") { LineBreakIsOneCharacter = true };
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(text));

        // Act
        var content = CreateSut().ReadContent(window, null, null, 0, 1, Bytes, CancellationToken.None);

        // Assert - the rest of the line break is skipped, as read_element_text's textSkipped says
        content.Should().Be(new BrowserUiaContent(true, "\r", null, 0, TotalLength: null, NextOffset: 1, TextSkipped: true));
    }

    [Fact]
    public void ReadContent_LeavesTextSkippedOut_WhenNothingIsCut()
    {
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern("abcdefghij")));

        var content = CreateSut().ReadContent(window, null, null, 0, 4, Bytes, CancellationToken.None);

        content.TextSkipped.Should().BeNull();
    }

    [Fact]
    public void ReadContent_KeepsTheFirstPage_CutAndSaidSo_WhenTheShorterReReadFails()
    {
        // Arrange - six Cyrillic letters take 12 bytes: a page of 6 bytes is read again over fewer, and that read fails
        var text = new FakeTextPattern("жжжжжж");
        text.OnGetTextPage = () =>
        {
            if (text.PageLengths.Count > 0)
            {
                throw new System.Runtime.InteropServices.COMException("The document changed", unchecked((int)0x80004005));
            }
        };
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(text));

        // Act
        var content = CreateSut().ReadContent(window, null, null, 0, 6, maxBytes: 6, CancellationToken.None);

        // Assert
        content.Should().Be(new BrowserUiaContent(true, "жжж", null, 0, TotalLength: 6, NextOffset: null, TextSkipped: true));
    }

    [Fact]
    public void ReadContent_ReturnsAnEmptyPage_PastTheEndOfTheText()
    {
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern("abc")));

        var content = CreateSut().ReadContent(window, null, null, 50, 10, Bytes, CancellationToken.None);

        content.Should().Be(new BrowserUiaContent(true, "", null, 3, TotalLength: 3, NextOffset: null));
    }

    [Fact]
    public void ReadContent_PagesTheWalkedText_AndKnowsItsLength_OnceTheWalkReachedItsEnd()
    {
        // Arrange - no Text pattern: the text is collected from the elements, "One\r\nTwo\r\nThree"
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Text", "One"), new FakeUiaNode("Text", "Two"), new FakeUiaNode("Text", "Three")));
        var whole = string.Join(Environment.NewLine, "One", "Two", "Three");

        // Act
        var first = CreateSut().ReadContent(window, null, null, 0, 4, Bytes, CancellationToken.None);
        var rest = CreateSut().ReadContent(window, null, null, first.NextOffset!.Value, Whole, Bytes, CancellationToken.None);

        // Assert
        first.Should().Be(new BrowserUiaContent(true, whole[..4], null, 0, TotalLength: null, NextOffset: 4));
        rest.Should().Be(new BrowserUiaContent(true, whole[4..], null, 4, TotalLength: whole.Length, NextOffset: null));
    }

    [Fact]
    public void ReadContent_FitsAWalkedPage_ToTheBytesOfJson()
    {
        var window = Window(new FakeUiaNode("Document").With(new FakeUiaNode("Text", "abcdefghij")));

        var content = CreateSut().ReadContent(window, null, null, 2, 8, maxBytes: 3, CancellationToken.None);

        content.Should().Be(new BrowserUiaContent(true, "cde", null, 2, TotalLength: 10, NextOffset: 5));
    }

    [Fact]
    public void ReadContent_TakesThePageWhoseTitleStartsTheWindowTitle_NotAFrameInIt()
    {
        var window = Window(
            new FakeUiaNode("Document", "Page") { Bounds = new UiaRect(0, 0, 100, 100) }.WithPattern<IUiaTextPattern>(new FakeTextPattern("page")).With(
                new FakeUiaNode("Document", "Frame") { Bounds = new UiaRect(0, 0, 900, 900) }.WithPattern<IUiaTextPattern>(new FakeTextPattern("frame"))));

        var content = CreateSut().ReadContent(window, "Page - Google Chrome", null, 0, Whole, Bytes, CancellationToken.None);

        content.Text.Should().Be("page");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Mozilla Firefox")]
    public void ReadContentAndUrl_TakeTheTabsPage_NotALargerFrameInIt_WhatEitherIsTitled(string? frameTitle)
    {
        // Arrange - Firefox (no render widget gives the pages): an untitled page holding a frame of another site, taller
        // than the window (its bounds are not cut to the window), untitled too or titled like the window
        var frame = new FakeUiaNode("Document", frameTitle) { Bounds = new UiaRect(0, 300, 1600, 5000) }
            .WithPattern<IUiaValuePattern>(new FakeValuePattern("https://bank.example/"))
            .WithPattern<IUiaTextPattern>(new FakeTextPattern("frame"));
        var page = new FakeUiaNode("Document") { Bounds = new UiaRect(0, 100, 1600, 900) }
            .WithPattern<IUiaValuePattern>(new FakeValuePattern("https://embedder.example/"))
            .WithPattern<IUiaTextPattern>(new FakeTextPattern("page"))
            .With(new FakeUiaNode("Group").With(new FakeUiaNode("Pane").With(frame)));
        var window = new FakeUiaNode("Window", "Mozilla Firefox").With(new FakeUiaNode("Pane").With(page));

        // Act
        var content = CreateSut().ReadContent(window, "Mozilla Firefox", null, 0, Whole, Bytes, CancellationToken.None);
        var url = CreateSut().ReadUrl(window, "Mozilla Firefox", null, true, CancellationToken.None);

        // Assert - a frame's Document is inside the page's, so it is never the tab's page
        content.Should().Be(new BrowserUiaContent(true, "page", "https://embedder.example/", 0, TotalLength: 4));
        url.Url.Should().Be("https://embedder.example/");
        url.UrlSource.Should().Be(BrowserUrlSource.Document);
    }

    [Fact]
    public void ReadContent_WalksTheDocument_WhenItHasNoTextPattern()
    {
        // Arrange - names of text, edit boxes and links, and values that differ from the name; not the Document's address
        var window = Window(Document("Example", "https://example.com/").With(
            new FakeUiaNode("Text", "  Heading"),
            new FakeUiaNode("Hyperlink", "Docs") { ActionPatterns = ["Invoke", "Value"] }
                .WithPattern<IUiaValuePattern>(new FakeValuePattern("https://example.com/docs")),
            Edit("Search", "query"),
            Edit("Same", "Same"),
            new FakeUiaNode("Button", "Not text"),
            new FakeUiaNode("Text", "   "),
            new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Nested"))));

        // Act
        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        content.Text.Should().Be(string.Join(Environment.NewLine,
            "Heading", "Docs", "https://example.com/docs", "Search", "query", "Same", "Nested"));
        content.Url.Should().Be("https://example.com/");
    }

    [Fact]
    public void ReadContent_NeverReadsTheValueOfAPasswordField()
    {
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Text", "Sign in"),
            Edit("Password", "hunter2", isPassword: true)));

        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        content.Text.Should().Be($"Sign in{Environment.NewLine}Password");
        _tree.Reads.Should().NotContain("Pattern:IUiaValuePattern:Password");
    }

    [Fact]
    public void ReadContent_DoesNotReadTheValueOfAnEditBox_WhosePasswordFlagCannotBeRead()
    {
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Edit", "Field") { ActionPatterns = ["Value"], IdentityException = FakeUiaTree.Vanished() }
                .WithPattern<IUiaValuePattern>(new FakeValuePattern("maybe secret"))));

        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        content.Text.Should().Be("Field");
    }

    [Fact]
    public void ReadContent_StopsWalking_OnceMaxLengthCharactersAreRead()
    {
        // Arrange
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Text", "First line"),
            new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Second line")),
            new FakeUiaNode("Group", "Later").With(new FakeUiaNode("Text", "Never read"))));

        // Act
        var content = CreateSut().ReadContent(window, null, null, 0, 14, Bytes, CancellationToken.None);

        // Assert - the same text as a walk of the whole page cut afterwards
        content.Text.Should().Be($"First line{Environment.NewLine}Se"[..14]);
        _tree.ReadNodes.Should().NotContain("Never read");
        _tree.ChildListings.Should().NotContain("Later");
    }

    [Fact]
    public void ReadContent_ReadsAWideLevelOneElementAtATime()
    {
        // Arrange - more children than one cached read takes
        var items = Enumerable.Range(0, UIElementService.CachedLevelLimit + 1)
            .Select(i => new FakeUiaNode("Text", $"Item {i}"))
            .ToArray();
        var document = new FakeUiaNode("Document", "List").With(items);
        var window = Window(document);

        // Act
        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        content.Text!.Split(Environment.NewLine).Should().HaveCount(items.Length);
        _tree.CachedFetches.Should().NotContain("List");
    }

    [Fact]
    public void ReadContent_WalksTwentyLevelsBelowTheDocument_AndNoDeeper()
    {
        // Arrange - the document is level 0
        var leaf = new FakeUiaNode("Text", "Level 21");
        var node = new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Level 20"), new FakeUiaNode("Group").With(leaf));
        for (var level = 18; level >= 1; level--)
        {
            node = new FakeUiaNode("Group").With(node);
        }

        var window = Window(new FakeUiaNode("Document").With(node));

        // Act
        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        content.Text.Should().Be("Level 20");
    }

    [Fact]
    public void ReadContent_FindsNoPage_WhenThereIsNoOnscreenDocument_RatherThanReadingTheBrowsersPanes()
    {
        // Arrange - the browser's client pane holds its tab titles, bookmarks and address bar, which are not the page (D-BRW-16)
        var panes = Window(
            new FakeUiaNode("Pane", "Sidebar") { Bounds = new UiaRect(0, 0, 250, 900) }.With(new FakeUiaNode("Text", "Side")),
            new FakeUiaNode("Pane", "Page") { Bounds = new UiaRect(250, 100, 1200, 800) }.With(new FakeUiaNode("Text", "Main")));
        var hidden = new FakeBrowserWindow(BrowserNames.ChromeNorwegian);
        hidden.Document.IsOffscreen = true;

        // Act
        var none = CreateSut().ReadContent(panes, null, null, 0, Whole, Bytes, CancellationToken.None);
        var offscreen = CreateSut().ReadContent(hidden.Window, hidden.Title, null, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        none.Should().Be(new BrowserUiaContent(false, null, null));
        offscreen.Should().Be(new BrowserUiaContent(false, null, null));
    }

    [Fact]
    public void ReadContent_FindsThePage_ButNoText_WhenItHasNone()
    {
        var window = Window(new FakeUiaNode("Document").With(new FakeUiaNode("Button", "Only a button")));

        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        content.Should().Be(new BrowserUiaContent(true, null, null));
    }

    [Fact]
    public void ReadContent_FindsNoPage_WhenTheSearchForTheDocumentFails()
    {
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern("text")));
        window.FindAllException = FakeUiaTree.Vanished();

        var content = CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        content.Found.Should().BeFalse();
    }

    [Fact]
    public void ReadContent_LetsAProviderTimeoutOfTheSearchForThePageThrough()
    {
        // Arrange - a browser that does not answer is not a window without a page
        var window = Window(new FakeUiaNode("Document").WithPattern<IUiaTextPattern>(new FakeTextPattern("text")));
        window.FindAllException = new TimeoutException("The provider did not answer.");

        // Act
        var act = () => CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, CancellationToken.None);

        // Assert
        act.Should().Throw<TimeoutException>();
    }

    [Fact]
    public void ReadContent_StopsOnceTheTokenIsCancelled()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var window = Window(new FakeUiaNode("Document").With(
            new FakeUiaNode("Text", "First") { OnRead = cancellation.Cancel },
            new FakeUiaNode("Group").With(new FakeUiaNode("Text", "Second"))));

        // Act
        var act = () => CreateSut().ReadContent(window, null, null, 0, Whole, Bytes, cancellation.Token);

        // Assert
        act.Should().Throw<OperationCanceledException>();
    }

    #endregion
}
