using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>read_element_text and read_table (roadmap M3.9); see <see cref="IElementReadService"/>.</summary>
/// <remarks>
/// <para>Order of work: the ref's window from the registry (no UI Automation) and the target policy's check of it
/// (TargetDeniedByPolicy, <see cref="UiaTargets.ReadVerdict"/>; read_element_text's criteria are checked by the search, which
/// refuses the window the same way), then in one read on the worker thread the hung-window check (NotResponding), the
/// warm-up of a window with web content within a third of the timeout (roadmap M3.3), the ref resolved
/// (<see cref="IElementRegistry.Resolve"/>: ElementStale, AmbiguousElement), then the read.</para>
/// <para><b>Text.</b> The password flag is read first; a password field, or one whose flag cannot be read, is never read.
/// Then the sources in this order: the Text pattern's document, paged by
/// the provider (<see cref="IUiaTextPattern.GetTextPage"/>, so a page costs its own length and not the document's), the
/// Value pattern's value (read in full, then cut), and the name. A page holds at most maxLength characters and
/// <see cref="MaxTextBytes"/> bytes of JSON: a document page that does not fit is read again over fewer characters, so its
/// nextOffset skips nothing. An empty document falls through to the value and a failing
/// pattern to the next source, but an element with either pattern is never read by its name: an empty edit box is empty, not
/// its label. The Text pattern comes before the Value pattern (the
/// design had them the other way round) because a browser document's value is its URL, and because only the Text pattern
/// can page a long document without reading all of it for every page; where an edit has both, they hold the same text.
/// An element with the Text pattern also gives its selection with every page (roadmap M3.F15), cut to
/// <see cref="MaxSelectedTextLength"/> characters. Its text attributes (font, weight, color, ...) are not read: they vary along
/// a page (a range's attribute is "mixed" as soon as two runs differ), so reporting them would take a walk of the page's
/// format runs and a list as long as the text.</para>
/// <para><b>Tables.</b> Only reads: never scrolls, realizes or acts. Through the Grid pattern when the element has it: its row
/// and column counts, the headers from the Table pattern's column headers (paired with their columns by where they sit when
/// they are grid items, and their rows then left out of the data rows), else from a Header among the grid's first children.
/// The page's cells come through GetItem for the frameworks whose GetItem only looks the cell up
/// (<see cref="GetItemFrameworks"/>), else from the grid's realized rows, placed by their cells' GridItem row: a row that is
/// not realized ends the page (NotRealized). Otherwise a walk of the element's children: DataItem, ListItem and TreeItem
/// children are the rows (read in one cached call when there are at most <see cref="CachedRowsLimit"/> children), a Header
/// child or a first row of HeaderItems gives the headers, and a row's children but nested tree items are its cells (a row
/// without cells is one cell, its own text). Both walks take the rows inside Group children as rows in their place, numbered
/// across the groups (roadmap M3.F15). A grid's row headers come with its rows: through the Table pattern for a grid read
/// through GetItem, unless they are cells of the grid already, and from a HeaderItem in the row for one read through its
/// rows. A cell's text is its value when it has the
/// Value pattern (never a password field's: null), else its name, else the names of its children; a cell that disappears is
/// null. The read stops at the timeout (whole rows only), at <see cref="MaxTableTextBytes"/> bytes of JSON, and between
/// cells when the token is cancelled; the rows read until the worker abandons a stuck call, or until the table itself
/// disappears, are still returned. A page that timed out before its first row has no nextStartRow (roadmap M3.F15).</para>
/// </remarks>
public sealed partial class ElementReadService : IElementReadService
{
    /// <summary>The most characters one read_element_text call returns.</summary>
    public const int MaxTextLength = 50_000;

    /// <summary>
    /// The most bytes a read_element_text page takes in the result's JSON (<see cref="ToolJson.StringBytes"/>: UTF-8 with the
    /// escapes), so that a page costs at most about 15,000 tokens in any script: 60,000 ASCII characters, 30,000 Cyrillic,
    /// 20,000 CJK, 5,000 emoji. <see cref="MaxTextLength"/> still bounds the characters.
    /// </summary>
    public const int MaxTextBytes = 60_000;

    /// <summary>
    /// The most characters of the selection read_element_text returns beside its page (roadmap M3.F15): enough for a word, a
    /// sentence or a paragraph the user picked out; a longer one is cut (selectionTruncated), and its text is in the pages.
    /// </summary>
    public const int MaxSelectedTextLength = 1_000;

    /// <summary>The most rows one read_table call returns.</summary>
    public const int MaxRowsLimit = 200;

    /// <summary>The most columns one read_table call returns.</summary>
    public const int MaxColumnsLimit = 50;

    /// <summary>A header or cell longer than this is cut (cellsTruncated).</summary>
    public const int MaxCellLength = 500;

    /// <summary>
    /// The most bytes the headers and cells of one read_table call take in the result's JSON (<see cref="ToolJson.StringBytes"/>
    /// of each, with its quotes and comma); the rows after it are left out (Size). A page's first row is always returned, its
    /// cells cut to fit when it alone would pass the limit, and headers that would take more than half of it are cut too.
    /// </summary>
    public const int MaxTableTextBytes = 40_000;

    /// <summary>A table with at most this many children has its rows read in one cached call; a larger one row by row.</summary>
    public const int CachedRowsLimit = 1_000;

    /// <summary>A cell with at most this many children gives their names when it has no name or value of its own.</summary>
    private const int CellChildrenLimit = 8;

    /// <summary>A row with at most this many children has its cells read in one cached call.</summary>
    private const int CachedCellsLimit = 64;

    /// <summary>How many of a grid's first children are checked for a Header.</summary>
    private const int HeaderSearchChildren = 3;

    /// <summary>How many times a text page that does not fit one result is read again over fewer characters before it is cut.</summary>
    private const int TextPageFits = 3;

    /// <summary>The most rows a grid's column headers are taken to fill (a header row over a header row, as Q1 over Revenue).</summary>
    private const int MaxHeaderRows = 4;

    /// <summary>How many of a realized row's first cells are asked for their GridItem row before the row is placed by its position.</summary>
    private const int RowIndexCells = 3;

    /// <summary>How many levels of Group children a table's rows are looked for in (a grouping by two keys nests two).</summary>
    private const int MaxGroupDepth = 3;

    /// <summary>
    /// The UI frameworks (FrameworkId) whose GridPattern.GetItem only looks the cell up, checked against their sources: the
    /// Win32 list view, the WinForms DataGridView (Rows[row].Cells[column]) and Chromium (its table's cell ids). Elsewhere
    /// GetItem can scroll: WPF's DataGrid scrolls the cell into view and realizes its row (ScrollIntoView, then UpdateLayout),
    /// WPF's GridView brings the row into view, and the Windows Community Toolkit DataGrid (XAML) scrolls too. A read never
    /// scrolls, so every other grid, one of an unknown framework included, is read through its realized rows.
    /// </summary>
    private static readonly HashSet<string> GetItemFrameworks = new(StringComparer.OrdinalIgnoreCase) { "Win32", "WinForm", "Chrome" };

    /// <summary>How long to keep waiting after the timeout for a table read to stop and hand back its rows.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> RowTypes = new(StringComparer.Ordinal) { "DataItem", "ListItem", "TreeItem" };

    /// <summary>Elements that are lists or tables even when they hold no rows now (an empty list is no failure).</summary>
    private static readonly HashSet<string> TableTypes = new(StringComparer.Ordinal) { "DataGrid", "List", "Table", "Tree" };

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly IElementRegistry _registry;
    private readonly IElementSearchService _search;
    private readonly INativeWindowWrapper? _windows;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly ITargetPolicy? _policy;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs each read under its deadline.</param>
    /// <param name="registry">Resolves the refs.</param>
    /// <param name="search">find_ui_elements' search, for read_element_text's criteria.</param>
    /// <param name="windows">The hung-window check and the application a stuck read is counted against; without it neither
    /// is done.</param>
    /// <param name="warmup">Warms up the web content of Chromium, Electron and WebView2 windows (roadmap M3.3). The server always
    /// passes it.</param>
    /// <param name="policy">The user's target policy, which refuses reads of a window of an app it denies. The server always
    /// passes it; without it (or without <paramref name="windows"/>) no window is refused.</param>
    public ElementReadService(
        IUiaTree tree,
        IUiaWorker worker,
        IElementRegistry registry,
        IElementSearchService search,
        INativeWindowWrapper? windows = null,
        IAccessibilityWarmup? warmup = null,
        ITargetPolicy? policy = null)
    {
        _tree = tree;
        _worker = worker;
        _registry = registry;
        _search = search;
        _windows = windows;
        _warmup = warmup;
        _policy = policy;
    }

    #region Text

    /// <inheritdoc />
    public async Task<ElementTextResult> ReadTextAsync(ElementTextRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var offset = Math.Max(0, request.Offset);
        var maxLength = Math.Clamp(request.MaxLength, 1, MaxTextLength);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);

        var elementRef = request.Ref;
        AccessibilityWarmupInfo? searchWarmup = null;
        if (elementRef == null)
        {
            var query = request.Query ?? throw new ArgumentException("Give a ref or a query.", nameof(request));
            var found = await _search.FindAsync(query with { Detail = ElementDetail.Minimal, Timeout = request.Timeout }, deadline.Token)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            searchWarmup = found.AccessibilityWarmup;
            if (Unique(found, query, out var error, out var code) is not { } match)
            {
                return TextFailure((long)query.WindowHandle, null, error!, code, stopwatch, searchWarmup) with
                {
                    Candidates = code == UIElementErrorCode.AmbiguousElement ? found.Elements : null
                };
            }

            elementRef = match;
        }

        var lookup = _registry.Lookup(elementRef);
        if (!lookup.Success)
        {
            return TextFailure(0, lookup.Ref, lookup.Error!, lookup.ErrorCode, stopwatch, searchWarmup);
        }

        var handle = lookup.WindowHandle;
        if (UiaTargets.PolicyRefusal(_windows, _policy, handle) is { } denied)
        {
            return TextFailure((long)handle, lookup.Ref, denied, UIElementErrorCode.TargetDeniedByPolicy, stopwatch, searchWarmup);
        }

        var warmup = new WarmupSlot(searchWarmup);
        try
        {
            return await _worker.RunAsync(
                UiaTargets.TargetProcessId(_windows, handle),
                token => ReadText(lookup.Ref, handle, offset, maxLength, AccessibilityWarmup.BudgetFor(request.Timeout), warmup, stopwatch, token),
                deadline.Token,
                TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return TextFailure((long)handle, lookup.Ref, $"UI Automation did not answer in time: {ex.Message}",
                UIElementErrorCode.OperationTimedOut, stopwatch, warmup.Value);
        }
    }

    /// <summary>
    /// The ref of the one element the criteria found, or null with why not, with element_action's codes: several
    /// (AmbiguousElement, also from a search that did not finish: more search cannot make them one), a search that did not
    /// cover the whole window, so another match (or the only one) may be in the rest (OperationTimedOut), or none
    /// (ElementNotFound).
    /// </summary>
    private static string? Unique(FindElementsResult found, ElementQuery query, out string? error, out UIElementErrorCode code)
    {
        error = null;
        code = UIElementErrorCode.None;
        if (!found.Success)
        {
            error = found.Error ?? "The search failed.";
            code = found.ErrorCode;
            return null;
        }

        var unfinished = found.TruncationReason is ElementSearchTruncation.Timeout or ElementSearchTruncation.SearchLimit or
            ElementSearchTruncation.PartsSkipped or ElementSearchTruncation.WindowLimit;
        if (found.MatchCount > 1)
        {
            error = $"{(unfinished ? "At least " : string.Empty)}{found.MatchCount} elements match the criteria, so none was read: " +
                    "a read never picks one for you. Pass the ref of the one you mean (candidates lists them), or narrow the criteria.";
            code = UIElementErrorCode.AmbiguousElement;
            return null;
        }

        if (unfinished)
        {
            var covered = found.Scope is ElementSearchScope.WindowAndPopups or ElementSearchScope.Desktop
                ? "every window of its scope"
                : "the whole window";
            error = $"The search did not cover {covered} ({found.TruncationReason}), so " +
                    (found.MatchCount == 0
                        ? "the element may be in the part it did not search: it found none so far."
                        : "the one element it found may not be the only match.") +
                    " Nothing was read. Pass the element's ref, narrow the criteria, or give more time.";
            code = UIElementErrorCode.OperationTimedOut;
            return null;
        }

        if (found.MatchCount == 0)
        {
            error = "No element matches the criteria" + (query.VisibleOnly ? " among the visible elements" : string.Empty) +
                    ". Check them with find_ui_elements, or read an element by its ref.";
            code = UIElementErrorCode.ElementNotFound;
            return null;
        }

        if (found.Elements is not [{ Ref: { } elementRef }])
        {
            error = "The element found has no runtime id, so it cannot be named for the read. Nothing was read.";
            code = UIElementErrorCode.ElementAccessFailed;
            return null;
        }

        return elementRef;
    }

    /// <summary>The text read, on the worker thread.</summary>
    private ElementTextResult ReadText(
        string elementRef, nint handle, int offset, int maxLength, TimeSpan warmupBudget, WarmupSlot warmup, Stopwatch stopwatch,
        CancellationToken token)
    {
        ElementTextResult Failure(string error, UIElementErrorCode code, string? resolvedRef = null) =>
            TextFailure((long)handle, resolvedRef ?? elementRef, error, code, stopwatch, warmup.Value);

        try
        {
            if (Prepare(elementRef, handle, warmupBudget, warmup, token, out var error, out var code) is not { } resolved)
            {
                return Failure(error!, code);
            }

            var node = resolved.Node;
            var controlType = resolved.Element.Properties.ControlType;

            // A ref moved to a re-created element is read, and the result says so
            ElementTextResult Read(ElementTextResult result) =>
                resolved.Moved ? result with { RefMoved = true, Warning = ElementRefs.MovedWarning(resolved.Ref) } : result;
            ElementTextResult Page(string text, ElementTextSource source, int start, int? total, int? next) =>
                WithSelection(node, resolved.Element.Patterns, Read(new(true, resolved.Ref, (long)handle, controlType, text, source,
                    start, total, next != null, next, Elapsed(stopwatch), AccessibilityWarmup: warmup.Value)));

            bool isPassword;
            try
            {
                isPassword = _tree.ReadIdentity(node).IsPassword;
            }
            catch (UiaElementUnavailableException) when (resolved.Element.Virtualized)
            {
                // An item its list has not realized fails the identity read as a whole (WPF's placeholder fails IsPassword). Its
                // name is read, which every element's description carries; a value or text it may hold is not, while it does
                // not say whether it is a password field
                var said = _tree.ReadVirtualizedItem(node).IsPassword;
                if (said == null && (resolved.Element.Patterns & (UiaPatterns.Text | UiaPatterns.Value)) != 0)
                {
                    return Failure("The element is an item its list has not realized, which does not say whether it is a password " +
                                   "field, so its text was not read. Realize it first (element_action realize), then read it again.",
                        UIElementErrorCode.ElementAccessFailed, resolved.Ref);
                }

                isPassword = said == true;
            }
            catch (Exception ex) when (ex is not TimeoutException and not UiaElementUnavailableException)
            {
                // Unknown: the text may be a password
                return Failure($"Whether the element is a password field could not be read ({ex.Message}), so its text was not " +
                               "read.", UIElementErrorCode.ElementAccessFailed, resolved.Ref);
            }

            if (isPassword)
            {
                return Read(new ElementTextResult(true, resolved.Ref, (long)handle, controlType, null, ElementTextSource.Redacted, 0,
                    null, false, null, Elapsed(stopwatch), IsPassword: true, AccessibilityWarmup: warmup.Value));
            }

            // An empty document falls through to the value, and a pattern that fails to the next source; but an element with
            // a Text or Value pattern never falls through to its name: an empty edit box reads as empty, not as its label
            var patterns = resolved.Element.Patterns;
            var hasValue = (patterns & UiaPatterns.Value) != 0;
            var page = (patterns & UiaPatterns.Text) != 0 ? TryTextPage(node, offset, maxLength) : null;
            if (page != null && (!hasValue || page is not { Start: 0, Length: 0, HasMore: false }))
            {
                page = FitTextPage(node, page, offset, maxLength);
                int? next = page.HasMore ? page.Start + page.Length : null;
                return Page(page.Text, ElementTextSource.Text, page.Start, page.HasMore ? null : page.Start + page.Length, next) with
                {
                    TextSkipped = page.Cut ? true : null
                };
            }

            if (hasValue && TryValue(node) is { } value)
            {
                return Slice(value, ElementTextSource.Value);
            }

            if (page != null)
            {
                return Page(page.Text, ElementTextSource.Text, page.Start, page.Start + page.Length, null);
            }

            return Slice(resolved.Element.Properties.Name ?? string.Empty, ElementTextSource.Name);

            ElementTextResult Slice(string text, ElementTextSource source)
            {
                var start = Math.Min(offset, text.Length);
                var end = start + FittingLength(text.AsSpan(start), maxLength, MaxTextBytes);
                return Page(text[start..end], source, start, text.Length, end < text.Length ? end : null);
            }
        }
        catch (UiaElementUnavailableException ex)
        {
            return Failure($"The element disappeared while it was read: {ex.Message}", UIElementErrorCode.ElementAccessFailed);
        }
        catch (TimeoutException ex)
        {
            return Failure($"UI Automation did not answer in time: {ex.Message}", UIElementErrorCode.OperationTimedOut);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Failure("The timeout passed before the text was read. Try again with a larger timeoutMs.",
                UIElementErrorCode.OperationTimedOut);
        }
        catch (Exception ex)
        {
            return Failure($"Error reading the element's text: {ex.Message}", UIElementErrorCode.OperationException);
        }
    }

    /// <summary>
    /// The result with the element's selection (roadmap M3.F15) when it has the Text pattern and something is selected: at
    /// most <see cref="MaxSelectedTextLength"/> characters, never ending inside a surrogate pair. Only for a page read: a
    /// password field never gets here. The page was read, so a selection that cannot be read is left out, whatever failed.
    /// </summary>
    private ElementTextResult WithSelection(IUiaNode node, UiaPatterns patterns, ElementTextResult result)
    {
        if ((patterns & UiaPatterns.Text) == 0)
        {
            return result;
        }

        string selected;
        try
        {
            selected = _tree.GetPattern<IUiaTextPattern>(node)?.GetSelectedText(MaxSelectedTextLength + 1) ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result;
        }

        if (selected.Length == 0)
        {
            return result;
        }

        var end = FittingLength(selected, MaxSelectedTextLength, MaxTextBytes);
        return result with { SelectedText = selected[..end], SelectionTruncated = end < selected.Length ? true : null };
    }

    /// <summary>A page of the Text pattern's document; null when the element has no such pattern or it fails.</summary>
    private UiaTextPage? TryTextPage(IUiaNode node, int offset, int maxLength)
    {
        try
        {
            return _tree.GetPattern<IUiaTextPattern>(node)?.GetTextPage(offset, maxLength);
        }
        catch (Exception ex) when (ex is not TimeoutException and not UiaElementUnavailableException)
        {
            // A provider whose Text pattern fails: the value or the name may still have the text
            return null;
        }
    }

    /// <summary>
    /// The page made to fit one result: at most <paramref name="maxLength"/> characters and <see cref="MaxTextBytes"/> bytes of
    /// JSON. A page whose text does not fit is read again over fewer of the provider's characters, in proportion to the part
    /// that fits, so that its end (Start + Length, the next page's offset) is still just past its text and nothing is skipped.
    /// Only when that does not work (one of the provider's characters is longer than the page, or the document changes under
    /// the read) is the text cut, and the page says so (<see cref="UiaTextPage.Cut"/>: the rest of its range is skipped).
    /// </summary>
    private UiaTextPage FitTextPage(IUiaNode node, UiaTextPage page, int offset, int maxLength)
    {
        for (var tries = 0; tries < TextPageFits; tries++)
        {
            var fits = FittingLength(page.Text, maxLength, MaxTextBytes);
            if (fits == page.Text.Length || page.Length <= 1)
            {
                break;
            }

            var fewer = (int)Math.Clamp((long)page.Length * fits / page.Text.Length, 1, page.Length - 1);
            if (TryTextPage(node, offset, fewer) is not { } shorter)
            {
                break;
            }

            page = shorter;
        }

        var end = FittingLength(page.Text, maxLength, MaxTextBytes);
        return end == page.Text.Length ? page : page with { Text = page.Text[..end], Cut = true };
    }

    /// <summary>
    /// How many characters from the start of <paramref name="text"/> fit both limits: at most <paramref name="maxChars"/>
    /// characters that take at most <paramref name="maxBytes"/> bytes in the result's JSON (<see cref="ToolJson.StringBytes"/>),
    /// never ending between the two halves of a surrogate pair (unless that would leave nothing).
    /// </summary>
    private static int FittingLength(ReadOnlySpan<char> text, int maxChars, int maxBytes)
    {
        var end = Math.Min(text.Length, maxChars);
        var bytes = 0;
        for (var i = 0; i < end; i++)
        {
            bytes += ToolJson.CharBytes(text[i]);
            if (bytes > maxBytes)
            {
                end = i;
                break;
            }
        }

        if (end < text.Length && end > 1 && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return end;
    }

    private string? TryValue(IUiaNode node)
    {
        try
        {
            return _tree.ReadValue(node);
        }
        catch (Exception ex) when (ex is not TimeoutException and not UiaElementUnavailableException)
        {
            return null;
        }
    }

    private static ElementTextResult TextFailure(
        long handle, string? elementRef, string error, UIElementErrorCode code, Stopwatch stopwatch, AccessibilityWarmupInfo? warmup) =>
        new(false, elementRef, handle, null, null, null, 0, null, false, null, Elapsed(stopwatch), Error: error, ErrorCode: code,
            AccessibilityWarmup: warmup);

    #endregion

    /// <summary>
    /// The hung-window check, the warm-up and the ref's resolution, on the worker thread: the live element, or null with the
    /// error and its code.
    /// </summary>
    private ResolvedElement? Prepare(
        string elementRef, nint handle, TimeSpan warmupBudget, WarmupSlot warmup, CancellationToken token, out string? error,
        out UIElementErrorCode code)
    {
        error = null;
        code = UIElementErrorCode.None;
        if (UiaTargets.NotRespondingReason(_windows, handle, "its element was not read") is { } notResponding)
        {
            error = notResponding;
            code = UIElementErrorCode.NotResponding;
            return null;
        }

        if (!_tree.IsAvailable())
        {
            error = "UI Automation is not available on this system";
            code = UIElementErrorCode.AutomationUnavailable;
            return null;
        }

        // The ref's window was read before, so it is usually warmed up already: then this costs nothing
        warmup.Value ??= _warmup?.WarmUp(handle, warmupBudget, token);

        var resolution = _registry.Resolve(elementRef, token);
        if (resolution.Element is not { } resolved)
        {
            error = resolution.Error;
            code = resolution.ErrorCode;
            return null;
        }

        return resolved;
    }

    private static double Elapsed(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);

    /// <summary>What the warm-up did, kept where the caller can read it when the worker abandons the read.</summary>
    private sealed class WarmupSlot(AccessibilityWarmupInfo? initial)
    {
        private AccessibilityWarmupInfo? _value = initial;

        public AccessibilityWarmupInfo? Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }
    }
}
