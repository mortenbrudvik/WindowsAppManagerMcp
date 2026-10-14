using System.Text;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The UI Automation reads of get_browser_url and get_browser_content (roadmap M3.F24): the address bar's text and the
/// page's text, read through <see cref="IUiaTree"/>, so both engines serve them and the unit tests run them over an
/// in-memory tree. They are the reads UIAutomationWrapper made over the managed client, moved onto the seam as they were:
/// the address bar is found by its English name, by Firefox's AutomationId or as a URL-like edit box, and the page as the
/// first Document, else the largest pane. Making the lookup independent of the display language is roadmap M5.7.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Call it on the UI Automation worker, inside the read the caller runs under its deadline (<see cref="IUiaWorker"/>):
/// every read can wait for the browser. Once the token is cancelled the reads stop with
/// <see cref="OperationCanceledException"/>.</item>
/// <item>Any other failure (a provider error, an element that disappeared) leaves out what could not be read, as the old
/// wrapper did: an element that cannot be read is skipped with what is below it, and a search that fails finds nothing.</item>
/// <item>The children of an element are listed in one call, and a level of at most
/// <see cref="UIElementService.CachedLevelLimit"/> children is then read in one more call with their properties, as the
/// tree walk reads them; a wider level, or one whose cached read fails, is read one element at a time.</item>
/// <item>The value of a password field is never read: an edit box's password flag is read first, and one whose flag
/// cannot be read is treated as a password field.</item>
/// </list>
/// </remarks>
public sealed class BrowserUiaReader
{
    /// <summary>How many levels below the window the search for the address bar by name reads.</summary>
    public const int MaxNameSearchDepth = 10;

    /// <summary>How many levels below the page's document the walk for its text reads.</summary>
    public const int MaxTextDepth = 20;

    private static readonly string[] ChromiumBrowsers = ["chrome", "edge", "brave", "vivaldi", "opera", "chromium"];

    private readonly IUiaTree _tree;

    /// <param name="tree">The UI Automation tree.</param>
    public BrowserUiaReader(IUiaTree tree)
    {
        _tree = tree;
    }

    /// <summary>
    /// The text of the browser's address bar, or null when no address bar was found or it could not be read. Chromium
    /// browsers: the element named "Address and search bar", else "Address bar", else the first edit box of an address
    /// bar's size whose value looks like a URL. Firefox: the element with the AutomationId "urlbar-input", else "urlbar",
    /// else the one named "Search or enter address". Another browser: Chromium's lookup, then Firefox's.
    /// </summary>
    /// <param name="window">The browser window's element.</param>
    /// <param name="browserType">The browser type, for example "chrome" or "firefox" (case ignored).</param>
    /// <param name="cancellationToken">The read's deadline.</param>
    public string? ReadUrl(IUiaNode window, string browserType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = browserType.ToLowerInvariant();
        if (type == "firefox")
        {
            return FirefoxUrl(window, cancellationToken);
        }

        return ChromiumBrowsers.Contains(type)
            ? ChromiumUrl(window, cancellationToken)
            : ChromiumUrl(window, cancellationToken) ?? FirefoxUrl(window, cancellationToken);
    }

    /// <summary>
    /// The text of the page the browser shows, or null when no content area was found or it has no text. The content area
    /// is the first Document below the window, else the largest pane over 200 by 200 pixels; its Text pattern gives the
    /// text in one call, else its text is collected from the names of its text, edit box and link elements and from the
    /// values that differ from them, one per line, at most <see cref="MaxTextDepth"/> levels down.
    /// </summary>
    /// <param name="window">The browser window's element.</param>
    /// <param name="maxLength">The most characters returned, or null for all of them. The walk stops once it has read
    /// that many, so a long page is not read to its end only to be cut.</param>
    /// <param name="cancellationToken">The read's deadline.</param>
    public string? ReadContent(IUiaNode window, int? maxLength, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var limit = maxLength is >= 0 ? maxLength : null;
        if (FindContentArea(window, cancellationToken) is not { } area)
        {
            return null;
        }

        string? content;
        try
        {
            content = _tree.GetPattern<IUiaTextPattern>(area.Node) is { } text
                ? text.GetText(limit ?? -1)
                : CollectText(area, limit, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return content != null && limit is int max && content.Length > max ? content[..max] : content;
    }

    /// <summary>The address bar of a Chromium browser, read.</summary>
    private string? ChromiumUrl(IUiaNode window, CancellationToken cancellationToken)
    {
        var urlBar = FindByNameContains(window, "Address and search bar", cancellationToken) ??
                     FindByNameContains(window, "Address bar", cancellationToken);
        return urlBar != null ? ValueOf(urlBar, cancellationToken) : UrlLikeEditValue(window, cancellationToken);
    }

    /// <summary>The address bar of Firefox, read.</summary>
    private string? FirefoxUrl(IUiaNode window, CancellationToken cancellationToken)
    {
        var urlBar = FindByAutomationId(window, "urlbar-input", cancellationToken) ??
                     FindByAutomationId(window, "urlbar", cancellationToken) ??
                     FindByNameContains(window, "Search or enter address", cancellationToken);
        return urlBar != null ? ValueOf(urlBar, cancellationToken) : null;
    }

    /// <summary>
    /// The first element, the window itself included, whose name contains <paramref name="text"/> (case ignored), depth
    /// first in tree order and at most <see cref="MaxNameSearchDepth"/> levels below the window.
    /// </summary>
    private Candidate? FindByNameContains(IUiaNode window, string text, CancellationToken cancellationToken)
    {
        if (TryRead(window) is not { } properties)
        {
            return null;
        }

        var root = new Candidate(window, properties, null);
        return NameContains(properties, text) ? root : SearchByName(root, 0, text, cancellationToken);
    }

    private Candidate? SearchByName(Candidate node, int depth, string text, CancellationToken cancellationToken)
    {
        if (depth >= MaxNameSearchDepth)
        {
            return null;
        }

        foreach (var child in Children(node.Node, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((child.Properties ?? TryRead(child.Node)) is not { } properties)
            {
                continue;
            }

            var read = child with { Properties = properties };
            if (NameContains(properties, text))
            {
                return read;
            }

            if (SearchByName(read, depth + 1, text, cancellationToken) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool NameContains(UiaNodeProperties properties, string text) =>
        !string.IsNullOrEmpty(properties.Name) && properties.Name.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first element below the window with the AutomationId, or null.</summary>
    private Candidate? FindByAutomationId(IUiaNode window, string automationId, CancellationToken cancellationToken)
    {
        try
        {
            var found = _tree.FindAll(window, UiaScope.Descendants,
                UiaCondition.PropertyEquals(UiaProperty.AutomationId, automationId), cancellationToken);
            return found.Count == 0 ? null : new Candidate(found[0]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The value of the first edit box below the window that is the size of an address bar (wider than 200 and narrower
    /// than 2000 pixels, higher than 15 and lower than 60) and whose value looks like a URL (it has "://" or a dot).
    /// </summary>
    private string? UrlLikeEditValue(IUiaNode window, CancellationToken cancellationToken)
    {
        IReadOnlyList<UiaElement> edits;
        try
        {
            edits = _tree.FindAll(window, UiaScope.Descendants,
                UiaCondition.PropertyEquals(UiaProperty.ControlType, "Edit"), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = edit.Properties.Bounds;
            if (bounds.Width is > 200 and < 2000 && bounds.Height is > 15 and < 60 &&
                ValueOf(new Candidate(edit), cancellationToken) is { } value &&
                (value.Contains("://", StringComparison.Ordinal) || value.Contains('.', StringComparison.Ordinal)))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// What an address bar holds: its Value pattern's value, else its Text pattern's text, else its name; null when it
    /// cannot be read. A password field's value and text are not read, so only its name can be returned.
    /// </summary>
    private string? ValueOf(Candidate element, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var properties = element.Properties ?? _tree.Read(element.Node);
            if (!MayBePassword(element.Node, properties))
            {
                if (_tree.GetPattern<IUiaValuePattern>(element.Node) is { } value)
                {
                    return value.Value;
                }

                if (_tree.GetPattern<IUiaTextPattern>(element.Node) is { } text)
                {
                    return text.GetText(-1);
                }
            }

            return properties.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first Document below the window in tree order, else its largest pane over 200 by 200 pixels; null when there is
    /// neither or the search fails.
    /// </summary>
    private Candidate? FindContentArea(IUiaNode window, CancellationToken cancellationToken)
    {
        try
        {
            var documents = _tree.FindAll(window, UiaScope.Descendants,
                UiaCondition.PropertyEquals(UiaProperty.ControlType, "Document"), cancellationToken);
            if (documents.Count > 0)
            {
                return new Candidate(documents[0]);
            }

            var panes = _tree.FindAll(window, UiaScope.Descendants,
                UiaCondition.PropertyEquals(UiaProperty.ControlType, "Pane"), cancellationToken);
            UiaElement? largest = null;
            var largestArea = 0.0;
            foreach (var pane in panes)
            {
                var bounds = pane.Properties.Bounds;
                var area = bounds.Width * bounds.Height;
                if (area > largestArea && bounds.Width > 200 && bounds.Height > 200)
                {
                    largestArea = area;
                    largest = pane;
                }
            }

            return largest == null ? null : new Candidate(largest);
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

        if ((element.Patterns is not { } patterns || (patterns & UiaPatterns.Value) != 0) &&
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
    /// <see cref="UIElementService.CachedLevelLimit"/>, read in one more call with their properties and patterns. A wider
    /// level, or one whose cached read fails, comes as elements to read one at a time; a level that cannot be listed, or
    /// whose provider timed out, as none.
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
    /// An element of the walk: with the properties and patterns a cached read gave it, or with neither when it is still to
    /// be read one at a time (<see cref="IUiaTree.Read"/> gives no patterns, so its Value pattern is then asked for).
    /// </summary>
    private sealed record Candidate(IUiaNode Node, UiaNodeProperties? Properties, UiaPatterns? Patterns)
    {
        public Candidate(UiaElement element)
            : this(element.Node, element.Properties, element.Patterns)
        {
        }
    }
}
