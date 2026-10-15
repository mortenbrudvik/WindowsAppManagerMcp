namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The names a browser's UI gives its elements in one display language (roadmap M5.7). Only the fakes use them: the
/// reads must find the same elements whatever they are named.
/// </summary>
public sealed record BrowserNames(
    string Language,
    string Frame,
    string TitleSuffix,
    string Back,
    string Forward,
    string Reload,
    string SiteInfo,
    string AddressBar,
    string NewTab,
    string Bookmarks,
    string AppMenu)
{
    public static readonly BrowserNames ChromeEnglish = new("Chrome en", "Google Chrome", " - Google Chrome",
        "Back", "Forward", "Reload", "View site information", "Address and search bar", "New Tab", "Bookmarks", "Chrome");

    public static readonly BrowserNames ChromeNorwegian = new("Chrome nb", "Google Chrome", " - Google Chrome",
        "Tilbake", "Frem", "Last inn på nytt", "Vis informasjon om nettstedet", "Adresse- og søkefelt", "Ny fane", "Bokmerker",
        "Chrome");

    public static readonly BrowserNames EdgeEnglish = new("Edge en", "Microsoft Edge",
        " and 1 more page - Personal - Microsoft​ Edge",
        "Back", "Forward", "Refresh", "View site information", "Address bar", "New tab", "Favorites bar", "Settings and more");

    public static readonly BrowserNames EdgeNorwegian = new("Edge nb", "Microsoft Edge",
        " og 1 side til - Personlig - Microsoft​ Edge",
        "Tilbake", "Fremover", "Oppdater", "Vis informasjon om nettstedet", "Adresselinje", "Ny fane", "Favorittlinje",
        "Innstillinger og mer");

    /// <summary>A UI whose every element is named like the address bar: a name must not make an element one.</summary>
    public static readonly BrowserNames Hostile = new("hostile", "Address and search bar", " - Address bar",
        "Address and search bar", "Address bar", "Address and search bar", "Address bar", "Address and search bar",
        "Address bar", "Address and search bar", "Address bar");

    /// <summary>A UI whose elements have no names at all.</summary>
    public static readonly BrowserNames Nameless = new("nameless", "", "", "", "", "", "", "", "", "", "");

    public static IEnumerable<BrowserNames> Localized => [ChromeEnglish, ChromeNorwegian, EdgeEnglish, EdgeNorwegian];

    public bool IsEdge => Frame == "Microsoft Edge" || Language.StartsWith("Edge", StringComparison.Ordinal);

    public override string ToString() => Language;
}

/// <summary>
/// The UI Automation tree of a Chrome or Edge window as the browsers build it from Chromium's views (roadmap M5.7). The
/// structure and the class names follow Chromium's views (each element's ClassName is its view's class, which no display
/// language changes) and the WebView2 tree read on a real desktop (<see cref="FakeWinUi3WebView2"/>); they were not read
/// from a real Chrome or Edge window, which the tests cannot open.
/// <code>
/// Pane "{page} - Google Chrome" (Chrome_WidgetWin_1, Win32)
///   Pane "Google Chrome" (BrowserRootView)
///     Pane (NonClientView) > Pane (BrowserFrameViewWin) > Pane (BrowserView)
///       Pane (TopContainerView)
///         Pane (TabStripRegionView) > Tab (TabStrip) > TabItem "{page}" (Tab), TabItem "{other}" (Tab), Button "New Tab"
///         ToolBar (ToolbarView)                               Edge: inside one more Pane (View)
///           Button "Back", Button "Forward", Button "Reload"
///           Group (LocationBarView) > Button "View site information", Edit "Address and search bar" (OmniboxViewViews)
///           Button "Chrome" (BrowserAppMenuButton)
///         ToolBar "Bookmarks" (BookmarkBarView) > Button "Address bar tips" (a bookmark)
///       Pane (SidebarContentsSplitView) > Pane (MultiContentsView) > Pane (ContentsWebView)
///         Document "{page}" (Chrome): the tab's page, its Value the committed URL, with a link named like the address bar
///           and an edit box of the omnibox's class and Firefox's urlbar id (both set by the page)
///         Edge: Document "Copilot" (Chrome): the sidebar's page, to the right
/// </code>
/// </summary>
public sealed class FakeBrowserWindow
{
    public const string PageTitle = "mortenbrudvik/WindowsAppManagerMcp";
    public const string CommittedUrl = "https://github.com/mortenbrudvik/WindowsAppManagerMcp";
    public const string ShownUrl = "github.com/mortenbrudvik/WindowsAppManagerMcp";
    public const string AttackerUrl = "https://evil.example/login";
    public const string SidebarUrl = "https://edgeservices.bing.com/edgesvc/chat";

    /// <param name="names">The UI's names.</param>
    /// <param name="pageTitle">The page's title, which names the window, its tab and its Document.</param>
    /// <param name="committedUrl">The Document's value, or null for a Document with no Value pattern.</param>
    /// <param name="addressBarText">The address bar's value.</param>
    /// <param name="omniboxClass">The address bar's class, or null for a browser that names it otherwise.</param>
    /// <param name="withToolbar">False: an app or kiosk window, with no toolbar.</param>
    /// <param name="withDocument">False: Chromium has not built the page's tree.</param>
    /// <param name="otherTabTitle">The title of the background tab.</param>
    /// <param name="sidebar">Edge's sidebar page (default: in an Edge window).</param>
    /// <param name="edge">Edge's shape: its toolbar one level deeper (default: when the names are Edge's).</param>
    public FakeBrowserWindow(
        BrowserNames names,
        string pageTitle = PageTitle,
        string? committedUrl = CommittedUrl,
        string addressBarText = ShownUrl,
        string? omniboxClass = BrowserUiaReader.OmniboxClassName,
        bool withToolbar = true,
        bool withDocument = true,
        string otherTabTitle = "Address and search bar",
        bool? sidebar = null,
        bool? edge = null)
    {
        var isEdge = edge ?? names.IsEdge;
        Names = names;
        Title = pageTitle + names.TitleSuffix;
        Omnibox = new FakeUiaNode("Edit", names.AddressBar)
        {
            ClassName = omniboxClass, FrameworkId = "Chrome", Bounds = new UiaRect(300, 50, 900, 28), IsKeyboardFocusable = true,
            ActionPatterns = ["Value"]
        }.WithPattern<IUiaValuePattern>(new FakeValuePattern(addressBarText));

        Document = Page(pageTitle, committedUrl, new UiaRect(0, 120, (sidebar ?? isEdge) ? 1200 : 1600, 880));
        Sidebar = (sidebar ?? isEdge) ? Page("Copilot", SidebarUrl, new UiaRect(1200, 120, 400, 880)) : null;

        ToolBar = new FakeUiaNode("ToolBar") { ClassName = "ToolbarView", FrameworkId = "Chrome" }.With(
            View("Button", names.Back, "BackForwardButton"),
            View("Button", names.Forward, "BackForwardButton"),
            View("Button", names.Reload, "ReloadButton"),
            View("Group", null, "LocationBarView").With(View("Button", names.SiteInfo, "LocationIconView"), Omnibox),
            View("Button", names.AppMenu, "BrowserAppMenuButton"));

        var top = View("Pane", null, "TopContainerView").With(
            View("Pane", null, "TabStripRegionView").With(
                View("Tab", null, "TabStrip").With(
                    View("TabItem", pageTitle, "Tab"),
                    View("TabItem", otherTabTitle, "Tab"),
                    View("Button", names.NewTab, "NewTabButton"))));
        if (withToolbar)
        {
            top.With(
                isEdge ? View("Pane", null, "View").With(ToolBar) : ToolBar,
                View("ToolBar", names.Bookmarks, "BookmarkBarView").With(View("Button", "Address bar tips", "BookmarkButton")));
        }

        var contents = View("Pane", null, "MultiContentsView");
        if (withDocument)
        {
            contents.With(View("Pane", null, "ContentsWebView").With(Document));
            if (Sidebar != null)
            {
                contents.With(View("Pane", null, "SidePanel").With(Sidebar));
            }
        }

        Window = new FakeUiaNode("Pane", Title)
        {
            ClassName = "Chrome_WidgetWin_1", FrameworkId = "Win32", Bounds = new UiaRect(0, 0, 1600, 1000)
        }.With(
            View("Pane", names.Frame, "BrowserRootView").With(
                View("Pane", null, "NonClientView").With(
                    View("Pane", null, "BrowserFrameViewWin").With(
                        View("Pane", null, "BrowserView").With(
                            top,
                            View("Pane", null, "SidebarContentsSplitView").With(contents))))));
    }

    public BrowserNames Names { get; }

    /// <summary>The window's title: the page's title and the browser's decorations.</summary>
    public string Title { get; }

    /// <summary>The window's element.</summary>
    public FakeUiaNode Window { get; }

    /// <summary>The main toolbar.</summary>
    public FakeUiaNode ToolBar { get; }

    /// <summary>The address bar's edit box.</summary>
    public FakeUiaNode Omnibox { get; }

    /// <summary>The tab's page.</summary>
    public FakeUiaNode Document { get; }

    /// <summary>Edge's sidebar page, or null.</summary>
    public FakeUiaNode? Sidebar { get; }

    /// <summary>The shown pages, as the render widgets give them (<see cref="IAccessibilityWarmup.FindPages"/>).</summary>
    public IReadOnlyList<IUiaNode> Pages => Sidebar != null ? [Document, Sidebar] : [Document];

    /// <summary>The value of the page's edit box of the omnibox's class.</summary>
    public FakeUiaNode PageField { get; private set; } = null!;

    private FakeUiaNode Page(string title, string? url, UiaRect bounds)
    {
        var field = new FakeUiaNode("Edit", "Search")
        {
            ClassName = BrowserUiaReader.OmniboxClassName, AutomationId = BrowserUiaReader.FirefoxUrlBarAutomationId,
            FrameworkId = "Chrome", ActionPatterns = ["Value"]
        }.WithPattern<IUiaValuePattern>(new FakeValuePattern(AttackerUrl));
        PageField ??= field;
        var document = new FakeUiaNode("Document", title) { FrameworkId = "Chrome", Bounds = bounds }.With(
            new FakeUiaNode("Text", "Welcome to " + title) { FrameworkId = "Chrome" },
            new FakeUiaNode("Hyperlink", "Address and search bar") { FrameworkId = "Chrome", ActionPatterns = ["Invoke", "Value"] }
                .WithPattern<IUiaValuePattern>(new FakeValuePattern(AttackerUrl)),
            field);
        return url != null ? document.WithPattern<IUiaValuePattern>(new FakeValuePattern(url)) : document;
    }

    private static FakeUiaNode View(string controlType, string? name, string className) =>
        new(controlType, string.IsNullOrEmpty(name) ? null : name) { ClassName = className, FrameworkId = "Chrome" };
}
