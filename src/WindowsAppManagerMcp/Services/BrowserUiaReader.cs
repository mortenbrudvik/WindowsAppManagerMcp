using System.Text;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The UI Automation reads of get_browser_url and get_browser_content (roadmap M3.F24, M5.7): the page's address and its
/// text, read through <see cref="IUiaTree"/>, so both engines serve them and the unit tests run them over an in-memory tree.
/// Nothing is looked up by name, which changes with the browser's display language and which a page sets (its title names
/// the window and its tab, its links and fields name themselves): the reads key on control types, class names,
/// AutomationIds and the tree's structure, and a URL is returned only when it is one (<see cref="BrowserUrls"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The page: the shown tab's Document. The caller passes the roots of the window's shown pages, those of its visible
/// render widgets (<see cref="IAccessibilityWarmup.FindPages"/>; a background tab's render widget is hidden). In Chrome and
/// Edge such a root is the render widget's own pane ("Chrome Legacy Window"), which lists the page's elements but holds no
/// address, and the tab's Document is beside it, among its parent's children. When the roots give no Document (a browser
/// behind other windows has no render widget; Firefox has none), the window's Documents are searched, and a frame's,
/// inside another Document (whose title, size and address its page sets), is left out; only when there is none either is
/// a pane the page (WebView2 runtime 155). Of those, offscreen ones and the browser's own pages (docked
/// DevTools, Chrome's side panels) are left out, and the tab's page is the one the address bar shows, else the one whose
/// name (the page's title) starts the window's title, else the largest. Its Value pattern holds the address the tab
/// committed, with its scheme.</item>
/// <item>The address bar: the edit box of class OmniboxViewViews (Chromium's views: Chrome, Edge, Brave), else the one with
/// the AutomationId urlbar-input (Firefox), else the first edit box inside a toolbar; looked for breadth first from the
/// window's children, at most <see cref="MaxAddressBarSearchDepth"/> levels down and <see cref="MaxAddressBarSearchElements"/>
/// elements, and never inside a Document or a render widget (the page, whose elements' class names and ids the page sets)
/// or a tab. Its value is what it shows (Chrome leaves out the scheme; Chrome and Edge show a file page as its path, taken
/// as its file:/// URL), and only its Value or Text pattern is read, never its name. Its text is taken for the URL only
/// when it is known not to be edited: the browser is the foreground window (one behind another window reports no keyboard
/// focus, also while text typed in its address bar is left there) and the address bar's focus reads false.</item>
/// <item>Call it on the UI Automation worker, inside the read the caller runs under its deadline (<see cref="IUiaWorker"/>):
/// every read can wait for the browser. Once the token is cancelled the reads stop with
/// <see cref="OperationCanceledException"/>.</item>
/// <item>Any other failure (a provider error, an element that disappeared) leaves out what could not be read: an element
/// that cannot be read is skipped with what is below it, and a search that fails finds nothing. A provider that times out
/// (<see cref="TimeoutException"/>) in the search for the page is the exception: the read of the text stops with it, so a
/// browser that does not answer is not reported as a window without a page, and the read of the address goes on to the
/// address bar.</item>
/// <item>The children of an element are listed in one call, and a level of at most
/// <see cref="UIElementService.CachedLevelLimit"/> children is then read in one more call with their properties and class
/// names, as the tree walk reads them; a wider level, or one whose cached read fails, is read one element at a time.</item>
/// <item>The value of a password field is never read: an edit box's password flag is read first, and one whose flag
/// cannot be read is treated as a password field.</item>
/// </list>
/// </remarks>
public sealed class BrowserUiaReader
{
    /// <summary>How many levels below the window the search for the address bar reads (Chrome's is about 8 down).</summary>
    public const int MaxAddressBarSearchDepth = 14;

    /// <summary>The most elements the search for the address bar reads.</summary>
    public const int MaxAddressBarSearchElements = 600;

    /// <summary>How many levels below the page's document the walk for its text reads.</summary>
    public const int MaxTextDepth = 20;

    /// <summary>The class of the address bar's edit box in Chromium's views (Chrome, Edge, Brave), in every language.</summary>
    public const string OmniboxClassName = "OmniboxViewViews";

    /// <summary>The AutomationId of Firefox's address bar (its DOM id), in every language.</summary>
    public const string FirefoxUrlBarAutomationId = "urlbar-input";

    /// <summary>
    /// How many levels above a Document found in the window are read to tell a frame (inside another Document) from a tab's
    /// page; one that is deeper is taken for a frame.
    /// </summary>
    public const int MaxPageAncestorDepth = 40;

    private readonly IUiaTree _tree;

    /// <param name="tree">The UI Automation tree.</param>
    public BrowserUiaReader(IUiaTree tree)
    {
        _tree = tree;
    }

    /// <summary>
    /// The page's address: the shown tab's Document's when it holds a URL (<see cref="BrowserUrlSource.Document"/>), else the
    /// address bar's text when it is URL-like and known not to be edited (<see cref="BrowserUrlSource.AddressBar"/>). A new
    /// tab page (or an empty address bar) is <see cref="BrowserErrorCode.NewTabPage"/>; an address bar being edited, or that
    /// may be (the browser is not in front, or its focus cannot be read), holding something else than a URL or unreadable,
    /// with no Document to read, <see cref="BrowserErrorCode.UrlExtractionFailed"/>; neither found,
    /// <see cref="BrowserErrorCode.UrlBarNotFound"/>.
    /// </summary>
    /// <param name="window">The browser window's element.</param>
    /// <param name="windowTitle">The window's title, which starts with the shown page's title; null when unknown.</param>
    /// <param name="pages">The roots of the window's shown pages, or none to search the window for its Documents.</param>
    /// <param name="foreground">Whether the browser is the foreground window. A browser behind another window reports no
    /// keyboard focus, so its address bar's focus is then not read and tells nothing (<see cref="BrowserUiaUrl.IsEditing"/>
    /// null).</param>
    /// <param name="cancellationToken">The read's deadline.</param>
    public BrowserUiaUrl ReadUrl(
        IUiaNode window, string? windowTitle, IReadOnlyList<IUiaNode>? pages, bool foreground, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bar = FindAddressBar(window, cancellationToken);
        var shown = bar != null ? ValueOf(bar, cancellationToken) : null;
        var editing = bar != null && foreground ? HasKeyboardFocus(bar) : null;
        var display = BrowserUrls.IsUrlLike(shown) ? shown : null;

        // Text that may be typed does not choose the page
        Page? page;
        try
        {
            page = FindPage(window, windowTitle, pages, editing == false ? display : null, cancellationToken);
        }
        catch (TimeoutException)
        {
            // The page did not answer in time; the address bar, part of the browser's own UI, may still tell
            page = null;
        }

        if (page?.Url is { } url)
        {
            return BrowserUrls.IsNewTabPage(url)
                ? BrowserUiaUrl.Failed(BrowserErrorCode.NewTabPage, NewTabPageMessage, url, display, editing)
                : new BrowserUiaUrl(url, display, BrowserUrlSource.Document, editing);
        }

        if (bar == null)
        {
            return BrowserUiaUrl.Failed(BrowserErrorCode.UrlBarNotFound,
                "Found neither the page's address nor the browser's address bar through UI Automation. The window may be an " +
                "app or kiosk window without an address bar, or the browser's accessibility tree is not available; read it " +
                "through CDP instead (a browser started with launch_browser_with_debug, or cdpPort).",
                null, null, null);
        }

        // No page at all: Chromium may not have built its tree
        var noPage = page == null ? " " + PageNotShownHint : string.Empty;
        if (shown == null)
        {
            return BrowserUiaUrl.Failed(BrowserErrorCode.UrlExtractionFailed,
                "Found the address bar, but its text could not be read, nor the page's own address." + noPage, null, null, editing);
        }

        if (string.IsNullOrWhiteSpace(shown))
        {
            return BrowserUiaUrl.Failed(BrowserErrorCode.NewTabPage, NewTabPageMessage, null, null, editing);
        }

        if (editing == true)
        {
            return BrowserUiaUrl.Failed(BrowserErrorCode.UrlExtractionFailed,
                "The address bar has the keyboard focus, so its text may be typed rather than the page's address, and the " +
                "page's own address could not be read. Read again once the address bar is left (Escape), or through CDP.",
                null, display, editing);
        }

        if (display == null)
        {
            return BrowserUiaUrl.Failed(BrowserErrorCode.UrlExtractionFailed,
                "The address bar holds text that is not a URL, and the page's own address could not be read." + noPage,
                null, null, editing);
        }

        if (editing == null)
        {
            return BrowserUiaUrl.Failed(BrowserErrorCode.UrlExtractionFailed,
                (foreground
                    ? "Whether the address bar is being edited could not be read"
                    : "The browser is not the foreground window, so whether its address bar is being edited cannot be told") +
                ": its text may be typed rather than the page's address, and the page's own address could not be read. " +
                (page == null ? PageNotShownHint : "Read again (the page may still be loading), or through CDP."),
                null, display, editing);
        }

        return new BrowserUiaUrl(BrowserUrls.AddressOf(display), display, BrowserUrlSource.AddressBar, editing);
    }

    /// <summary>
    /// The page the browser shows and one page of its text (roadmap M5.6), with read_element_text's paging: the shown tab's
    /// Document (as <see cref="ReadUrl"/> finds it, without reading the address bar). Its Text pattern is read a page at a
    /// time (<see cref="IUiaTextPattern.GetTextPage"/>, so a page costs its own length and not the document's, and offsets
    /// count the provider's characters), made to fit <paramref name="maxBytes"/> bytes of JSON
    /// (<see cref="TextPages.Fit"/>); its whole length is known once the last page is read. Without a Text pattern the text
    /// is collected from the names of its text, edit box and link elements and from the values that differ from them, one
    /// per line, at most <see cref="MaxTextDepth"/> levels down, until it reaches the page's end (plus one character, to
    /// tell whether more follows), and the page is sliced from it. <see cref="BrowserUiaContent.Found"/> is false when the
    /// window shows no such page; the text is null when the page has none or it could not be read.
    /// </summary>
    /// <param name="window">The browser window's element.</param>
    /// <param name="windowTitle">The window's title, which starts with the shown page's title; null when unknown.</param>
    /// <param name="pages">The roots of the window's shown pages, or none to search the window for its Documents.</param>
    /// <param name="offset">Where the page starts (0 or more).</param>
    /// <param name="maxLength">The most characters returned (1 or more). The walk stops once it has read to the page's
    /// end, so a long page is not read to its end only to be cut.</param>
    /// <param name="maxBytes">The most bytes the page's text takes in the result's JSON.</param>
    /// <param name="cancellationToken">The read's deadline.</param>
    public BrowserUiaContent ReadContent(
        IUiaNode window, string? windowTitle, IReadOnlyList<IUiaNode>? pages, int offset, int maxLength, int maxBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        offset = Math.Max(0, offset);
        maxLength = Math.Max(1, maxLength);
        if (FindPage(window, windowTitle, pages, display: null, cancellationToken) is not { } page)
        {
            return new BrowserUiaContent(false, null, null);
        }

        try
        {
            if (_tree.GetPattern<IUiaTextPattern>(page.Root.Node) is { } text)
            {
                // A shorter read that fails cuts the page already read instead of failing the read, as read_element_text's does
                var read = TextPages.Fit(text.GetTextPage(offset, maxLength), maxLength, maxBytes, fewer =>
                {
                    try
                    {
                        return text.GetTextPage(offset, fewer);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return null;
                    }
                });
                var end = read.Start + read.Length;
                return new BrowserUiaContent(true, read.Text, page.Url, read.Start,
                    TotalLength: read.HasMore ? null : end, NextOffset: read.HasMore ? end : null, TextSkipped: read.Cut ? true : null);
            }

            // One character past the page tells whether more follows
            var limit = (int)Math.Min(int.MaxValue, (long)offset + maxLength + 1);
            if (CollectText(page.Root, limit, cancellationToken) is not { } collected)
            {
                return new BrowserUiaContent(true, null, page.Url);
            }

            var start = Math.Min(offset, collected.Length);
            var stop = start + TextPages.FittingLength(collected.AsSpan(start), maxLength, maxBytes);
            return new BrowserUiaContent(true, collected[start..stop], page.Url, start,
                TotalLength: collected.Length < limit ? collected.Length : null, NextOffset: stop < collected.Length ? stop : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BrowserUiaContent(true, null, page.Url);
        }
    }

    /// <summary>
    /// Why a browser window may show no page through UI Automation, and what to do: read on a real desktop on 2026-10-15,
    /// an Edge window that stayed behind another window since its page loaded had no render widget and no Document.
    /// </summary>
    public const string PageNotShownHint =
        "Chromium builds a page's accessibility tree only while the page is shown, so a browser window that has stayed " +
        "behind other windows since the page loaded may have none. Read it through CDP (a browser started with " +
        "launch_browser_with_debug, or cdpPort), or read again once the window has been shown.";

    private const string NewTabPageMessage =
        "The tab shows the browser's new tab page, which has no address of a site. Navigate the tab first, or read another window.";

    /// <summary>
    /// The address bar: breadth first from the window's children, the first edit box of class
    /// <see cref="OmniboxClassName"/> or with the AutomationId <see cref="FirefoxUrlBarAutomationId"/>, else the first edit box
    /// met inside a toolbar; null when there is neither. Documents, render widgets and tabs are not looked inside.
    /// </summary>
    private Candidate? FindAddressBar(IUiaNode window, CancellationToken cancellationToken)
    {
        var pending = new Queue<(Candidate Element, int Depth, bool InToolBar)>();
        foreach (var child in Children(window, cancellationToken))
        {
            pending.Enqueue((child, 1, false));
        }

        Candidate? inToolBar = null;
        var read = 0;
        while (pending.Count > 0 && read < MaxAddressBarSearchElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth, insideToolBar) = pending.Dequeue();
            read++;
            if (Complete(element) is not { Properties: { } properties } known)
            {
                continue;
            }

            // The page, whose elements' class names and ids the page sets, and the tab strip's tabs, named by their pages
            if (properties.ControlType is "Document" or "TabItem" ||
                string.Equals(known.ClassName, AccessibilityWarmup.RenderWidgetHostClass, StringComparison.Ordinal))
            {
                continue;
            }

            if (properties.ControlType == "Edit")
            {
                if (string.Equals(known.ClassName, OmniboxClassName, StringComparison.Ordinal) ||
                    string.Equals(properties.AutomationId, FirefoxUrlBarAutomationId, StringComparison.Ordinal))
                {
                    return known;
                }

                if (insideToolBar)
                {
                    inToolBar ??= known;
                }
            }

            if (depth < MaxAddressBarSearchDepth)
            {
                foreach (var child in Children(known.Node, cancellationToken))
                {
                    pending.Enqueue((child, depth + 1, insideToolBar || properties.ControlType == "ToolBar"));
                }
            }
        }

        return inToolBar;
    }

    /// <summary>
    /// The shown tab's page (<see cref="Best"/>): of <paramref name="roots"/>, each a Document or a render widget's pane that
    /// stands for the Document beside it (<see cref="DocumentBeside"/>); when they give no Document, of the window's
    /// Documents that are not a frame's; else the pane of a page that has no Document element (WebView2 runtime 155,
    /// roadmap M3.F33). Null when there is none. A provider that times out in the search for the window's Documents throws
    /// <see cref="TimeoutException"/>, unless a pane was found.
    /// </summary>
    private Page? FindPage(
        IUiaNode window, string? windowTitle, IReadOnlyList<IUiaNode>? roots, string? display, CancellationToken cancellationToken)
    {
        // The render widgets' pages: a root that is not a Document is the widget's own pane, and Chrome and Edge keep the
        // tab's Document beside it, in the web view that is the pane's parent
        Page? shown = null;
        if (roots is { Count: > 0 })
        {
            var resolved = roots.Select(root => DocumentBeside(root, cancellationToken) ?? new Candidate(root, null, null)).ToList();
            shown = Best(resolved, searched: false, windowTitle, display, cancellationToken);
            if (shown is { IsDocument: true })
            {
                return shown;
            }
        }

        // No render widget gave a Document: a browser behind other windows has no render widget, though the Document built
        // before stays in its views; Firefox has none
        IReadOnlyList<Candidate> candidates;
        try
        {
            candidates = _tree.FindAll(window, UiaScope.Descendants,
                UiaCondition.PropertyEquals(UiaProperty.ControlType, "Document"), cancellationToken)
                .Select(e => new Candidate(e)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (ex is not TimeoutException || shown != null))
        {
            return shown;
        }

        return Best(candidates, searched: true, windowTitle, display, cancellationToken) ?? shown;
    }

    /// <summary>
    /// The page among <paramref name="candidates"/>: of those that are onscreen and not the browser's own UI, the one
    /// <paramref name="display"/> shows, else the one whose name starts <paramref name="windowTitle"/>, else one with an
    /// address, else the largest. A Document found by a search (<paramref name="searched"/>) inside another Document is a
    /// frame's and is left out. Null when there is none.
    /// </summary>
    private Page? Best(
        IReadOnlyList<Candidate> candidates, bool searched, string? windowTitle, string? display, CancellationToken cancellationToken)
    {
        Page? best = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            cancellationToken.ThrowIfCancellationRequested();
            if ((candidate.Properties ?? TryRead(candidate.Node)) is not { IsOffscreen: false } properties)
            {
                continue;
            }

            var root = candidate with { Properties = properties };
            var value = properties.ControlType == "Document" ? ValueOf(root, cancellationToken) : null;
            var url = BrowserUrls.IsAbsoluteUrl(value) ? value : null;
            if (BrowserUrls.IsBrowserUi(url))
            {
                continue;
            }

            var page = new Page(root, url, properties.ControlType == "Document",
                BrowserUrls.Shows(display, url),
                !string.IsNullOrEmpty(properties.Name) && !string.IsNullOrEmpty(windowTitle) &&
                windowTitle.StartsWith(properties.Name, StringComparison.Ordinal),
                Math.Max(0, properties.Bounds.Width) * Math.Max(0, properties.Bounds.Height));
            if (best != null && !page.Outranks(best))
            {
                continue;
            }

            // A frame's Document, which its page sizes and titles, is not a tab's page. The search lists the Documents in
            // tree order, so the first is inside no other; the others are looked at only when they would win
            if (searched && index > 0 && IsInDocument(candidate.Node, cancellationToken))
            {
                continue;
            }

            best = page;
        }

        return best;
    }

    /// <summary>
    /// The page a render widget's root stands for: the root when it is a Document; else (the widget's own pane, "Chrome
    /// Legacy Window", which lists the page's elements but holds no address) the first Document among the children of its
    /// parent, the tab's web view, where Chrome and Edge keep the tab's Document beside the pane (read on a real desktop on
    /// 2026-10-15). Null when there is none, or it cannot be read.
    /// </summary>
    private Candidate? DocumentBeside(IUiaNode root, CancellationToken cancellationToken)
    {
        if (TryRead(root) is not { } properties)
        {
            return null;
        }

        if (properties.ControlType == "Document")
        {
            return new Candidate(root, properties, null);
        }

        IUiaNode? parent;
        try
        {
            parent = _tree.GetParent(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return parent == null
            ? null
            : Children(parent, cancellationToken).Select(Complete)
                .FirstOrDefault(child => child is { Properties.ControlType: "Document" });
    }

    /// <summary>
    /// Whether the element lies inside a Document (a frame of a page): its ancestors are read, at most
    /// <see cref="MaxPageAncestorDepth"/> of them. One whose ancestors cannot be read, or that lies deeper, counts as a frame.
    /// A provider that times out throws <see cref="TimeoutException"/>.
    /// </summary>
    private bool IsInDocument(IUiaNode node, CancellationToken cancellationToken)
    {
        try
        {
            var ancestor = _tree.GetParent(node);
            for (var depth = 0; ancestor != null; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth >= MaxPageAncestorDepth || _tree.Read(ancestor).ControlType == "Document")
                {
                    return true;
                }

                ancestor = _tree.GetParent(ancestor);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// What an address bar or a Document holds: its Value pattern's value, else its Text pattern's text; null when it has
    /// neither or it cannot be read. Never its name. A password field's value and text are not read.
    /// </summary>
    private string? ValueOf(Candidate element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var properties = element.Properties ?? _tree.Read(element.Node);
            if (MayBePassword(element.Node, properties))
            {
                return null;
            }

            if (_tree.GetPattern<IUiaValuePattern>(element.Node) is { } value)
            {
                return value.Value;
            }

            return properties.ControlType == "Edit" && _tree.GetPattern<IUiaTextPattern>(element.Node) is { } text
                ? text.GetText(-1)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Whether the element has the keyboard focus, or null when that cannot be read.</summary>
    private bool? HasKeyboardFocus(Candidate element)
    {
        try
        {
            return _tree.ReadInteraction(element.Node).HasKeyboardFocus;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The text collected from the content area's elements, trimmed, or null when there is none.</summary>
    private string? CollectText(Candidate area, int? maxLength, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        Collect(area, 0, text, maxLength, cancellationToken);
        var result = text.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Adds the element's text and walks its children, depth first in tree order. An element that cannot be read is
    /// skipped with what is below it.
    /// </summary>
    private void Collect(Candidate element, int depth, StringBuilder text, int? maxLength, CancellationToken cancellationToken)
    {
        UiaNodeProperties properties;
        try
        {
            properties = element.Properties ?? _tree.Read(element.Node);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        var name = properties.Name;
        if (properties.ControlType is "Text" or "Edit" or "Hyperlink" && !string.IsNullOrWhiteSpace(name))
        {
            text.AppendLine(name);
        }

        // The page's own Document holds its address as its value, not its text
        if (depth > 0 &&
            (element.Patterns is not { } patterns || (patterns & UiaPatterns.Value) != 0) &&
            ReadValue(element.Node, properties) is { } value && !string.IsNullOrWhiteSpace(value) && value != name)
        {
            text.AppendLine(value);
        }

        if (depth >= MaxTextDepth || Full(text, maxLength))
        {
            return;
        }

        foreach (var child in Children(element.Node, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Full(text, maxLength))
            {
                return;
            }

            Collect(child, depth + 1, text, maxLength, cancellationToken);
        }
    }

    /// <summary>The element's Value pattern's value, or null when it has none, it is a password field or it fails.</summary>
    private string? ReadValue(IUiaNode node, UiaNodeProperties properties)
    {
        try
        {
            return MayBePassword(node, properties) ? null : _tree.GetPattern<IUiaValuePattern>(node)?.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the element is an edit box that is a password field, or whose password flag cannot be read. Only edit
    /// boxes are password fields, so no other element costs the read.
    /// </summary>
    private bool MayBePassword(IUiaNode node, UiaNodeProperties properties)
    {
        if (properties.ControlType != "Edit")
        {
            return false;
        }

        try
        {
            return _tree.ReadIdentity(node).IsPassword;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// True once the text, trimmed, holds <paramref name="maxLength"/> characters: whatever is read after it would be cut
    /// off, so the trimmed text starts as the walk of the whole page would.
    /// </summary>
    private static bool Full(StringBuilder text, int? maxLength)
    {
        if (maxLength is not int max)
        {
            return false;
        }

        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end - start >= max;
    }

    /// <summary>
    /// The children of an element: listed in one call, then, for a level of at most
    /// <see cref="UIElementService.CachedLevelLimit"/>, read in one more call with their properties, class names and
    /// patterns. A wider level, or one whose cached read fails, comes as elements to read one at a time; a level that
    /// cannot be listed, or whose provider timed out, as none.
    /// </summary>
    private IReadOnlyList<Candidate> Children(IUiaNode node, CancellationToken cancellationToken)
    {
        IReadOnlyList<IUiaNode> children;
        try
        {
            children = _tree.GetChildren(node, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }

        if (children.Count == 0)
        {
            return [];
        }

        if (children.Count <= UIElementService.CachedLevelLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return _tree.GetChildElements(node, cancellationToken).Select(e => new Candidate(e)).ToList();
            }
            catch (TimeoutException)
            {
                // Read one at a time, each would wait again
                return [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A provider failed for the read as a whole (often for one child): read the children one at a time
            }
        }

        return children.Select(child => new Candidate(child, null, null)).ToList();
    }

    /// <summary>
    /// The element with its properties and class name: as its cached read gave them, else read now (two calls); null when
    /// its properties cannot be read. A class name that cannot be read is left null.
    /// </summary>
    private Candidate? Complete(Candidate element)
    {
        if (element.Cached)
        {
            return element;
        }

        if (TryRead(element.Node) is not { } properties)
        {
            return null;
        }

        string? className;
        try
        {
            className = _tree.ReadIdentity(element.Node).ClassName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            className = null;
        }

        return element with { Properties = properties, ClassName = className, Cached = true };
    }

    /// <summary>The element's properties, or null when they cannot be read.</summary>
    private UiaNodeProperties? TryRead(IUiaNode node)
    {
        try
        {
            return _tree.Read(node);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// An element of the walk: with the properties, patterns and class name a cached read gave it (<see cref="Cached"/>), or
    /// with none of them when it is still to be read one at a time (<see cref="IUiaTree.Read"/> gives no patterns, so its
    /// Value pattern is then asked for).
    /// </summary>
    private sealed record Candidate(
        IUiaNode Node, UiaNodeProperties? Properties, UiaPatterns? Patterns, string? ClassName = null, bool Cached = false)
    {
        public Candidate(UiaElement element)
            : this(element.Node, element.Properties, element.Patterns, element.ClassName, Cached: true)
        {
        }
    }

    /// <summary>A shown page: its root, its address (null when it holds none) and what ranks it.</summary>
    private sealed record Page(
        Candidate Root, string? Url, bool IsDocument, bool ShownInAddressBar, bool TitleStartsWindowTitle, double Area)
    {
        /// <summary>Whether this page is more likely the tab's than <paramref name="other"/> (a tie keeps the first).</summary>
        public bool Outranks(Page other)
        {
            if (ShownInAddressBar != other.ShownInAddressBar)
            {
                return ShownInAddressBar;
            }

            if (TitleStartsWindowTitle != other.TitleStartsWindowTitle)
            {
                return TitleStartsWindowTitle;
            }

            if ((Url != null) != (other.Url != null))
            {
                return Url != null;
            }

            return Area > other.Area;
        }
    }
}

/// <summary>
/// What <see cref="BrowserUiaReader.ReadUrl"/> found: the page's address (<see cref="Url"/>, with
/// <see cref="BrowserErrorCode.None"/>), or why there is none.
/// </summary>
/// <param name="Url">The page's address; on a new tab page the new tab page's, when its Document gave it.</param>
/// <param name="DisplayUrl">The address bar's text when it is URL-like; null otherwise.</param>
/// <param name="UrlSource"><see cref="BrowserUrlSource.Document"/> or <see cref="BrowserUrlSource.AddressBar"/>; null on a
/// failure.</param>
/// <param name="IsEditing">Whether the address bar has the keyboard focus; null when none was read, or it cannot be told (its
/// focus cannot be read, or the browser is not the foreground window, whose address bar reports no focus).</param>
/// <param name="ErrorCode">None, or UrlBarNotFound, UrlExtractionFailed or NewTabPage.</param>
/// <param name="Error">Why there is no address; null with <see cref="BrowserErrorCode.None"/>.</param>
public sealed record BrowserUiaUrl(
    string? Url,
    string? DisplayUrl,
    string? UrlSource,
    bool? IsEditing,
    BrowserErrorCode ErrorCode = BrowserErrorCode.None,
    string? Error = null)
{
    public static BrowserUiaUrl Failed(BrowserErrorCode code, string error, string? url, string? displayUrl, bool? isEditing) =>
        new(url, displayUrl, null, isEditing, code, error);
}

/// <summary>What <see cref="BrowserUiaReader.ReadContent"/> found.</summary>
/// <param name="Found">Whether the window shows a page to read.</param>
/// <param name="Text">The page of text asked for (empty past the end of the text), or null when the page has no text or it
/// could not be read.</param>
/// <param name="Url">The page's address from its Document, or null when it holds none.</param>
/// <param name="Offset">Where <paramref name="Text"/> starts: the offset asked for, or the end of a shorter text.</param>
/// <param name="TotalLength">The whole text's length, when known (the last page was read).</param>
/// <param name="NextOffset">Where the next page starts, when more text follows.</param>
/// <param name="TextSkipped">True when the Text pattern page was cut (<see cref="UiaTextPage.Cut"/>): the rest of the
/// provider's character it ends in is skipped. Null otherwise.</param>
public sealed record BrowserUiaContent(
    bool Found, string? Text, string? Url, int Offset = 0, int? TotalLength = null, int? NextOffset = null, bool? TextSkipped = null);
