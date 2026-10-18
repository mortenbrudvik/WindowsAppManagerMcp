namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The UI Automation tree of a WinUI 3 window with a WebView2 hosted as a visual, as it was read on a real desktop (roadmap
/// M3.F43: Windows 11 26200, WebView2 runtime 155, Windows App SDK 1.6, UIA3). The names label the fake's nodes only: the
/// real views are nameless, and the name of the root of Chromium's views is localized ("WAM page - Nettinnhold"), so no
/// code may match them.
/// <code>
/// Window "WAM WinUI3 WebView2 check" (Win32)
///   Pane "Bridge" (Win32: the Microsoft.UI.Content.DesktopChildSiteBridge window)
///     Pane "WAM page" (Win32: msedgewebview2's Chrome_WidgetWin_1 window)
///       Pane "Intermediate D3D Window" (Win32)
///       Pane "WAM page - Web content" (Chrome: the root of Chromium's views, BrowserRootView)
///         Pane "NonClientView"
///           Pane "EmbeddedBrowserTabRootView"
///           Pane "EmbeddedBrowserFrameView" > Pane "BrowserView"
///             Pane "TopContainerView"
///             Pane "SidebarContentsSplitView" > Pane "SidebarContentsSplitView 2" > Pane "MultiContentsView" > Pane "View"
///               Pane "View 2"
///               Document "WAM page" (Chrome, level 11) > Text "WAM WinUI 3 page", Button "Page button", Table "Amounts"
///             Pane "EmbeddedBrowserDownloadView"
///         Pane "TopContainerOverlayView"
///     Pane "XAML" (XAML: the InputSiteWindowClass window)
///       Button "Native button"
///       Pane "WebView2" (Win32: the Chrome_WidgetWin_0 window)
///         Pane "WAM page" again, with its whole subtree (the same runtime ids)
///         Pane "XAML" again (the same runtime id: a loop back to its own ancestor), with Button "Native button" and an
///           empty Pane "WebView2"
/// </code>
/// Each element of a copy is a node of its own with the original's runtime id, as UI Automation gives the same element a
/// new reference on every path to it.
/// </summary>
public sealed class FakeWinUi3WebView2
{
    private readonly Dictionary<string, IReadOnlyList<int>> _ids = [];
    private readonly List<FakeUiaNode> _hosts = [];
    private readonly bool _withDocument;

    /// <param name="withDocument">False: Chromium has not built the page's tree yet (<see cref="AddDocument"/> adds it).</param>
    public FakeWinUi3WebView2(bool withDocument = true)
    {
        _withDocument = withDocument;
        var page = Page(out var viewsRoot, out var documentParent);
        ViewsRoot = viewsRoot;
        DocumentParent = documentParent;
        Document = documentParent.Children.FirstOrDefault(c => c.ControlType == "Document");
        Window = new FakeUiaNode("Window", "WAM WinUI3 WebView2 check") { FrameworkId = "Win32", RuntimeId = Id("window") }.With(
            Node("Pane", "Bridge", "Win32").With(
                page,
                Xaml(Node("Pane", "WebView2", "Win32").With(
                    Page(out _, out _),
                    Xaml(Node("Pane", "WebView2", "Win32"))))));
    }

    /// <summary>The window's element.</summary>
    public FakeUiaNode Window { get; }

    /// <summary>The first copy of the root of Chromium's views (frameworkId Chrome).</summary>
    public FakeUiaNode ViewsRoot { get; }

    /// <summary>The first copy of the view the page's Document is a child of.</summary>
    public FakeUiaNode DocumentParent { get; }

    /// <summary>The first copy of the page's Document, or null before <see cref="AddDocument"/>.</summary>
    public FakeUiaNode? Document { get; private set; }

    /// <summary>The runtime id of the root of Chromium's views.</summary>
    public IReadOnlyList<int> ViewsRootId => Id("WAM page - Web content");

    /// <summary>
    /// Gives every element of the WebView2's part (each copy of the "WAM page" window, Chromium's views and the page) the
    /// process that serves it, as on a real desktop: msedgewebview2.exe's, not the app's. Without it they report none (an
    /// element of the window's process, as every other node of the fixture).
    /// </summary>
    public void ServeWebViewFrom(int processId)
    {
        foreach (var host in _hosts)
        {
            foreach (var node in Subtree(host))
            {
                node.ProcessId = processId;
            }
        }
    }

    private static IEnumerable<FakeUiaNode> Subtree(FakeUiaNode node) => node.Children.SelectMany(Subtree).Prepend(node);

    /// <summary>Chromium builds the page's tree: its Document appears in the first copy, with <paramref name="children"/> elements.</summary>
    public FakeUiaNode AddDocument(int children = 3)
    {
        Document = NewDocument(children);
        DocumentParent.With(Document);
        return Document;
    }

    private IReadOnlyList<int> Id(string key) => _ids.TryGetValue(key, out var id) ? id : _ids[key] = FakeUiaNode.NewRuntimeId();

    private FakeUiaNode Node(string controlType, string name, string framework, string? key = null) =>
        new(controlType, name) { FrameworkId = framework, RuntimeId = Id(key ?? name) };

    private FakeUiaNode Xaml(FakeUiaNode webView) =>
        Node("Pane", "XAML", "XAML").With(
            new FakeUiaNode("Button", "Native button") { FrameworkId = "XAML", RuntimeId = Id("Native button"), ActionPatterns = ["Invoke"] },
            webView);

    private FakeUiaNode Page(out FakeUiaNode viewsRoot, out FakeUiaNode documentParent)
    {
        documentParent = Node("Pane", "View", "Chrome").With(Node("Pane", "View 2", "Chrome"));
        if (_withDocument)
        {
            documentParent.With(NewDocument(3));
        }

        viewsRoot = Node("Pane", "WAM page - Web content", "Chrome").With(
            Node("Pane", "NonClientView", "Chrome").With(
                Node("Pane", "EmbeddedBrowserTabRootView", "Chrome"),
                Node("Pane", "EmbeddedBrowserFrameView", "Chrome").With(
                    Node("Pane", "BrowserView", "Chrome").With(
                        Node("Pane", "TopContainerView", "Chrome"),
                        Node("Pane", "SidebarContentsSplitView", "Chrome").With(
                            Node("Pane", "SidebarContentsSplitView 2", "Chrome").With(
                                Node("Pane", "MultiContentsView", "Chrome").With(documentParent))),
                        Node("Pane", "EmbeddedBrowserDownloadView", "Chrome")))),
            Node("Pane", "TopContainerOverlayView", "Chrome"));
        var host = Node("Pane", "WAM page", "Win32", "host").With(Node("Pane", "Intermediate D3D Window", "Win32"), viewsRoot);
        _hosts.Add(host);
        return host;
    }

    private FakeUiaNode NewDocument(int children)
    {
        var document = Node("Document", "WAM page", "Chrome", "document");
        var elements = new[]
        {
            new FakeUiaNode("Text", "WAM WinUI 3 page") { FrameworkId = "Chrome", RuntimeId = Id("heading") },
            new FakeUiaNode("Button", "Page button")
            {
                FrameworkId = "Chrome", RuntimeId = Id("button"), ActionPatterns = ["Invoke"], Bounds = new UiaRect(40, 120, 90, 24)
            },
            new FakeUiaNode("Table", "Amounts") { FrameworkId = "Chrome", RuntimeId = Id("table") }
        };
        return document.With(elements.Take(children).ToArray());
    }
}
