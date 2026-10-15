namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The names a browser's UI gives its elements in one display language (roadmap M5.7), as read from Chrome and Edge on a
/// real desktop on 2026-10-15 (Edge 155 and Chrome 155 in English and, through the throwaway profile's own Local State, in
/// Norwegian). Only the fakes use them: the reads must find the same elements whatever they are named.
/// </summary>
/// <param name="TitleSuffix">What the window's title adds to the page's title.</param>
/// <param name="RootViewSuffix">What the name of Chromium's root view adds to the page's title.</param>
/// <param name="ToolBar">The main toolbar's name (Chrome leaves it unnamed).</param>
/// <param name="Forward">The forward button's name, or null for a browser that shows none on a tab without history (Edge).</param>
/// <param name="TabBar">The tab strip's name (Chrome leaves it unnamed).</param>
/// <param name="TabSuffix">What a tab's name adds to its page's title (the page's memory use).</param>
public sealed record BrowserNames(
    string Language,
    string TitleSuffix,
    string RootViewSuffix,
    string? ToolBar,
    string Back,
    string? Forward,
    string Reload,
    string SiteInfo,
    string AddressBar,
    string AppMenu,
    string? TabBar,
    string TabSuffix,
    string NewTab)
{
    public static readonly BrowserNames ChromeEnglish = new("Chrome en", " - Google Chrome", " - Google Chrome", null,
        "Back", "Forward", "Reload", "Not secure", "Address and search bar", "Action required", null,
        " - Memory usage - 16.9 MB", "New tab");

    public static readonly BrowserNames ChromeNorwegian = new("Chrome nb", " - Google Chrome", " - Google Chrome", null,
        "Tilbake", "Frem", "Last inn på nytt", "Ikke sikker", "Adresse- og søkefelt", "Handling påkrevd", null,
        " – minnebruk – 19,2 MB", "Ny fane");

    public static readonly BrowserNames EdgeEnglish = new("Edge en", " - Profile 1 - Microsoft​ Edge", " - Microsoft Edge",
        "App bar", "Back", null, "Refresh", "Not secure", "Address and search bar", "Settings and more (Alt+F)", "Tab bar",
        " - Memory usage - 20.5 MB", "New Tab");

    public static readonly BrowserNames EdgeNorwegian = new("Edge nb", " – Profil 1 – Microsoft​ Edge", " – Microsoft Edge",
        "Applinje", "Tilbake", null, "Oppdater", "Ikke sikret", "Adresse- og søkefelt", "Innstillinger og mer (Alt+F)",
        "Fanefelt", " – Minnebruk – 19,0 MB", "Ny fane");

    /// <summary>A UI whose every element is named like the address bar: a name must not make an element one.</summary>
    public static readonly BrowserNames Hostile = new("hostile", " - Address bar", " - Address and search bar",
        "Address bar", "Address and search bar", "Address bar", "Address and search bar", "Address bar",
        "Address and search bar", "Address bar", "Address and search bar", " - Address bar", "Address and search bar");

    /// <summary>A UI whose elements have no names at all.</summary>
    public static readonly BrowserNames Nameless = new("nameless", "", "", null, "", null, "", "", "", "", null, "", "");

    public static IEnumerable<BrowserNames> Localized => [ChromeEnglish, ChromeNorwegian, EdgeEnglish, EdgeNorwegian];

    public bool IsEdge => Language.StartsWith("Edge", StringComparison.Ordinal);

    public override string ToString() => Language;
}

/// <summary>
/// The UI Automation tree of a Chrome or Edge window (roadmap M5.7), as UIA3 read it from Edge 155 and Chrome 155 on a real
/// desktop on 2026-10-15 (each element's ClassName is its view's class, which no display language changes; the names are
/// <see cref="BrowserNames"/>'). The tab's page is a Document (AutomationId RootWebArea, its Value the committed URL) in the
/// tab's web view, deep in Chromium's views. The window's render widget (Chrome_RenderWidgetHostHWND) has an element of its
/// own, the Win32 pane "Chrome Legacy Window": its parent is that same web view, its children the page's own elements
/// (with no Document above them; once, a moment later, the Document itself), and it has no Value pattern. That pane is what
/// the warm-up's <see cref="IAccessibilityWarmup.FindPages"/> gives as the page (<see cref="Pages"/>). The window walk lists
/// the pane among the window's children (it is a child window); here it sits in the web view, where its parent read leads.
/// A browser window behind other windows has no render widget (Chromium hides the page), and one whose page was never shown
/// has no Document either.
/// <code>
/// Pane "{page}{title suffix}" (Chrome_WidgetWin_1, Win32)
///   Pane (Intermediate D3D Window, Win32)
///   Pane "{page}{root view suffix}" (BrowserRootView)
///     Pane (NonClientView) > Pane (BrowserFrameViewWin; Edge: EdgeBrowserFrameViewWin) > Pane (BrowserView)
///       Pane (TopContainerView)
///         ToolBar (ToolbarView, view_1000; Edge: "App bar", EdgeToolbarView)
///           Button "Back", Button "Forward" (Chrome), Button "Reload"
///           Group (LocationBarView) > Button "Not secure" (LocationIconView), Edit "Address and search bar" (OmniboxViewViews)
///           Button (BrowserAppMenuButton)
///       Chrome: Pane (MultiContentsView) > Pane (Chrome_WidgetWin_1): the tab's web view
///       Edge:   Pane (SidebarContentsSplitView) x3 > Pane (MultiContentsView) > Pane (View): the tab's web view
///                 Pane "Chrome Legacy Window" (Chrome_RenderWidgetHostHWND, Win32): the page's elements again
///                 Pane (View), empty
///                 Document "{page}" (RootWebArea): the tab's page, with a link named like the address bar and an edit box
///                   of the omnibox's class and Firefox's urlbar id (both set by the page)
///               Edge: Edit (Textfield), the find bar's, outside any toolbar
///               Edge with its sidebar open (not read: a throwaway profile shows none): Pane (View) > Pane "Chrome Legacy
///               Window", Document "Copilot", to the right
///       Chrome: Pane (HorizontalTabStripRegionViewOld) > Pane (TabStrip) > Tab (TabContainerImpl) > TabItem (Tab)
///       Edge:   Tab "Tab bar" (EdgeTabStripRegionView) > Pane (EdgeTabStrip) > Tab (EdgeTabContainerImpl) > TabItem (EdgeTab)
/// </code>
/// </summary>
public sealed class FakeBrowserWindow
{
    public const string PageTitle = "mortenbrudvik/WindowsAppManagerMcp";
    public const string CommittedUrl = "https://github.com/mortenbrudvik/WindowsAppManagerMcp";
    public const string ShownUrl = "github.com/mortenbrudvik/WindowsAppManagerMcp";
    public const string AttackerUrl = "https://evil.example/login";
    public const string SidebarUrl = "https://edgeservices.bing.com/edgesvc/chat";

    /// <summary>The name Chromium gives the render widget's window, and so its element, in every language.</summary>
    public const string LegacyWindowName = "Chrome Legacy Window";

    /// <param name="names">The UI's names.</param>
    /// <param name="pageTitle">The page's title, which names the window, its tab and its Document.</param>
    /// <param name="committedUrl">The Document's value, or null for a Document with no Value pattern.</param>
    /// <param name="addressBarText">The address bar's value.</param>
    /// <param name="omniboxClass">The address bar's class, or null for a browser that names it otherwise.</param>
    /// <param name="withToolbar">False: an app or kiosk window, with no toolbar.</param>
    /// <param name="withDocument">False: Chromium has not built the page's tree (a window that stayed behind others).</param>
    /// <param name="renderWidgetShown">False: the window is behind other windows, so Chromium hid its render widget, and the
    /// warm-up finds no page (<see cref="Pages"/> is empty); the Document built before stays in the views.</param>
    /// <param name="otherTabTitle">The title of the background tab.</param>
    /// <param name="sidebar">Edge's sidebar page (default: in an Edge window).</param>
    /// <param name="edge">Edge's shape (default: when the names are Edge's).</param>
    public FakeBrowserWindow(
        BrowserNames names,
        string pageTitle = PageTitle,
        string? committedUrl = CommittedUrl,
        string addressBarText = ShownUrl,
        string? omniboxClass = BrowserUiaReader.OmniboxClassName,
        bool withToolbar = true,
        bool withDocument = true,
        bool renderWidgetShown = true,
        string otherTabTitle = "Address and search bar",
        bool? sidebar = null,
        bool? edge = null)
    {
        var isEdge = edge ?? names.IsEdge;
        var withSidebar = sidebar ?? isEdge;
        Names = names;
        Title = pageTitle + names.TitleSuffix;
        Omnibox = new FakeUiaNode("Edit", names.AddressBar)
        {
            ClassName = omniboxClass, AutomationId = isEdge ? "view_1017" : "view_1012", FrameworkId = "Chrome",
            Bounds = new UiaRect(300, 50, 900, 28), IsKeyboardFocusable = true, ActionPatterns = ["Value"]
        }.WithPattern<IUiaValuePattern>(new FakeValuePattern(addressBarText));

        Document = Page(pageTitle, committedUrl, new UiaRect(0, 120, withSidebar ? 1200 : 1600, 880));
        Sidebar = withSidebar ? Page("Copilot", SidebarUrl, new UiaRect(1200, 120, 400, 880)) : null;
        RenderWidgetPane = LegacyWindow("785", Document, withDocument);
        SidebarRenderWidgetPane = Sidebar != null ? LegacyWindow("911", Sidebar, withDocument) : null;

        var buttons = new List<FakeUiaNode> { View("Button", names.Back, "BackForwardButton", "view_1001") };
        if (!isEdge && names.Forward != null)
        {
            buttons.Add(View("Button", names.Forward, "BackForwardButton", "view_1002"));
        }

        buttons.Add(View("Button", names.Reload, "ReloadButton", "view_1003"));
        buttons.Add(View("Group", null, "LocationBarView").With(
            View("Button", names.SiteInfo, "LocationIconView", isEdge ? "view_1015" : "view_1011"), Omnibox));
        buttons.Add(View("Button", names.AppMenu, "BrowserAppMenuButton", isEdge ? "view_1011" : "view_1007"));
        ToolBar = View("ToolBar", names.ToolBar, isEdge ? "EdgeToolbarView" : "ToolbarView", "view_1000").With(buttons.ToArray());

        var top = View("Pane", null, "TopContainerView");
        if (withToolbar)
        {
            top.With(ToolBar);
        }

        var webView = View("Pane", null, isEdge ? "View" : "Chrome_WidgetWin_1");
        if (renderWidgetShown)
        {
            webView.With(RenderWidgetPane);
        }

        webView.With(View("Pane", null, "View"));
        if (withDocument)
        {
            webView.With(Document);
        }

        var contents = View("Pane", null, "MultiContentsView").With(webView);
        if (Sidebar != null)
        {
            var sidebarView = View("Pane", null, "View");
            if (renderWidgetShown)
            {
                sidebarView.With(SidebarRenderWidgetPane!);
            }

            if (withDocument)
            {
                sidebarView.With(Sidebar);
            }

            contents.With(sidebarView);
        }

        var tabs = View("Tab", null, isEdge ? "EdgeTabContainerImpl" : "TabContainerImpl").With(
            View("TabItem", pageTitle + names.TabSuffix, isEdge ? "EdgeTab" : "Tab", isEdge ? "view_24" : "view_20"),
            View("TabItem", otherTabTitle + names.TabSuffix, isEdge ? "EdgeTab" : "Tab"));
        FakeUiaNode browserView;
        if (isEdge)
        {
            browserView = View("Pane", null, "BrowserView").With(
                top,
                View("Pane", null, "SidebarContentsSplitView").With(
                    View("Pane", null, "SidebarContentsSplitView").With(
                        View("Pane", null, "SidebarContentsSplitView").With(contents),
                        View("Edit", null, "Textfield"))),
                View("Tab", names.TabBar, "EdgeTabStripRegionView").With(
                    View("Pane", null, "EdgeTabStrip", "view_25").With(tabs),
                    View("Button", names.NewTab, "EdgeNewTabButton", "view_28")));
        }
        else
        {
            browserView = View("Pane", null, "BrowserView").With(
                top,
                contents.With(View("Pane", null, "ContentsSeparator")),
                View("Pane", names.TabBar, "HorizontalTabStripRegionViewOld").With(
                    View("Pane", null, "TabStrip", "view_21").With(tabs),
                    View("Button", names.NewTab, "TabStripControlButton")));
        }

        RootView = View("Pane", pageTitle + names.RootViewSuffix, "BrowserRootView").With(
            View("Pane", null, "NonClientView").With(
                View("Pane", null, isEdge ? "EdgeBrowserFrameViewWin" : "BrowserFrameViewWin").With(browserView)));
        Window = new FakeUiaNode("Pane", Title)
        {
            ClassName = "Chrome_WidgetWin_1", FrameworkId = "Win32", Bounds = new UiaRect(0, 0, 1600, 1000)
        }.With(new FakeUiaNode("Pane") { ClassName = "Intermediate D3D Window", FrameworkId = "Win32" }, RootView);
    }

    public BrowserNames Names { get; }

    /// <summary>The window's title: the page's title and the browser's decorations.</summary>
    public string Title { get; }

    /// <summary>The window's element.</summary>
    public FakeUiaNode Window { get; }

    /// <summary>The root of Chromium's views (BrowserRootView).</summary>
    public FakeUiaNode RootView { get; }

    /// <summary>The main toolbar.</summary>
    public FakeUiaNode ToolBar { get; }

    /// <summary>The address bar's edit box.</summary>
    public FakeUiaNode Omnibox { get; }

    /// <summary>The tab's page.</summary>
    public FakeUiaNode Document { get; }

    /// <summary>The element of the tab's render widget: the Win32 pane "Chrome Legacy Window" beside the tab's page.</summary>
    public FakeUiaNode RenderWidgetPane { get; }

    /// <summary>Edge's sidebar page, or null.</summary>
    public FakeUiaNode? Sidebar { get; }

    /// <summary>The element of the sidebar's render widget, or null.</summary>
    public FakeUiaNode? SidebarRenderWidgetPane { get; }

    /// <summary>
    /// The shown pages as the warm-up gives them for the window's visible render widgets
    /// (<see cref="IAccessibilityWarmup.FindPages"/>): each render widget's own pane, whose children are the page's
    /// elements. None when the window is behind other windows (no render widget) or the page's tree is not built.
    /// </summary>
    public IReadOnlyList<IUiaNode> Pages
    {
        get
        {
            var shown = new List<IUiaNode>();
            foreach (var pane in new[] { RenderWidgetPane, SidebarRenderWidgetPane })
            {
                if (pane is { Parent: not null, Children.Count: > 0 })
                {
                    shown.Add(pane);
                }
            }

            return shown;
        }
    }

    /// <summary>The value of the page's edit box of the omnibox's class.</summary>
    public FakeUiaNode PageField { get; private set; } = null!;

    private FakeUiaNode Page(string title, string? url, UiaRect bounds)
    {
        var document = new FakeUiaNode("Document", title) { AutomationId = "RootWebArea", FrameworkId = "Chrome", Bounds = bounds }
            .With(PageElements(title, keep: true));
        return url != null ? document.WithPattern<IUiaValuePattern>(new FakeValuePattern(url)) : document;
    }

    /// <summary>The page's elements: a text, a link named like the address bar and an edit box like the address bar's.</summary>
    private FakeUiaNode[] PageElements(string title, bool keep)
    {
        var field = new FakeUiaNode("Edit", "Search")
        {
            ClassName = BrowserUiaReader.OmniboxClassName, AutomationId = BrowserUiaReader.FirefoxUrlBarAutomationId,
            FrameworkId = "Chrome", ActionPatterns = ["Value"]
        }.WithPattern<IUiaValuePattern>(new FakeValuePattern(AttackerUrl));
        if (keep)
        {
            PageField ??= field;
        }

        return
        [
            new FakeUiaNode("Text", "Welcome to " + title) { FrameworkId = "Chrome" },
            new FakeUiaNode("Hyperlink", "Address and search bar") { FrameworkId = "Chrome", ActionPatterns = ["Invoke", "Value"] }
                .WithPattern<IUiaValuePattern>(new FakeValuePattern(AttackerUrl)),
            field
        ];
    }

    /// <summary>
    /// A render widget's element: the Win32 pane "Chrome Legacy Window", with the page's bounds and, once Chromium built
    /// the page's tree, the page's elements as its children; no Value pattern.
    /// </summary>
    private FakeUiaNode LegacyWindow(string controlId, FakeUiaNode page, bool built)
    {
        var pane = new FakeUiaNode("Pane", LegacyWindowName)
        {
            ClassName = AccessibilityWarmup.RenderWidgetHostClass, AutomationId = controlId, FrameworkId = "Win32",
            Bounds = page.Bounds
        };
        return built ? pane.With(PageElements(page.Name!, keep: false)) : pane;
    }

    private static FakeUiaNode View(string controlType, string? name, string className, string? automationId = null) =>
        new(controlType, string.IsNullOrEmpty(name) ? null : name)
        {
            ClassName = className, AutomationId = automationId, FrameworkId = "Chrome"
        };
}
