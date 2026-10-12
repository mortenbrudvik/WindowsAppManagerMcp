using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>read_table (roadmap M3.9); see the remarks of <see cref="ElementReadService"/>.</summary>
public sealed partial class ElementReadService
{
    /// <inheritdoc />
    public async Task<ElementTableResult> ReadTableAsync(ElementTableRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var startRow = Math.Max(0, request.StartRow);
        var maxRows = Math.Clamp(request.MaxRows, 1, MaxRowsLimit);
        var maxColumns = Math.Clamp(request.MaxColumns, 1, MaxColumnsLimit);

        var lookup = _registry.Lookup(request.Ref);
        if (!lookup.Success)
        {
            return TableFailure(0, lookup.Ref, startRow, lookup.Error!, lookup.ErrorCode, stopwatch, null);
        }

        var handle = lookup.WindowHandle;
        if (UiaTargets.PolicyRefusal(_windows, _policy, handle) is { } denied)
        {
            return TableFailure((long)handle, lookup.Ref, startRow, denied, UIElementErrorCode.TargetDeniedByPolicy, stopwatch, null);
        }

        var progress = new TableProgress(startRow, maxColumns);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        try
        {
            return await _worker.RunAsync(
                UiaTargets.TargetProcessId(_windows, handle),
                token => ReadTable(lookup.Ref, handle, maxRows, AccessibilityWarmup.BudgetFor(request.Timeout), progress, stopwatch, token),
                deadline.Token,
                StopGrace).ConfigureAwait(false);
        }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (UiaReadRefusedException ex)
        {
            // Earlier reads of the same application are still stuck; nothing was waited for
            return TableFailure((long)handle, lookup.Ref, startRow, ex.Message, UIElementErrorCode.OperationTimedOut, stopwatch, null);
        }
        catch (TimeoutException ex)
        {
            // The worker gave up on a call stuck in an application that does not answer: the whole rows read until then
            progress.Stop(ElementTableTruncation.Timeout);
            return progress.Source != null
                ? progress.Result((long)handle, stopwatch)
                : TableFailure((long)handle, progress.Ref ?? lookup.Ref, startRow, $"UI Automation did not answer in time: {ex.Message}",
                    UIElementErrorCode.OperationTimedOut, stopwatch, progress.Warmup.Value);
        }
    }

    /// <summary>The table read, on the worker thread.</summary>
    private ElementTableResult ReadTable(
        string elementRef, nint handle, int maxRows, TimeSpan warmupBudget, TableProgress progress, Stopwatch stopwatch,
        CancellationToken token)
    {
        ElementTableResult Failure(string error, UIElementErrorCode code) =>
            TableFailure((long)handle, progress.Ref ?? elementRef, progress.StartRow, error, code, stopwatch, progress.Warmup.Value);

        try
        {
            if (Prepare(elementRef, handle, warmupBudget, progress.Warmup, token, out var error, out var code) is not { } resolved)
            {
                return Failure(error!, code);
            }

            progress.Ref = resolved.Ref;
            progress.RefMoved = resolved.Moved;
            var node = resolved.Node;
            var grid = (resolved.Element.Patterns & UiaPatterns.Grid) != 0 ? _tree.GetPattern<IUiaGridPattern>(node) : null;
            if (grid != null)
            {
                ReadGrid(resolved.Element, grid, maxRows, progress, token);
            }
            else if (!ReadRows(node, resolved.Element.Properties.ControlType, maxRows, progress, token))
            {
                var controlType = resolved.Element.Properties.ControlType;
                return Failure(
                    $"The element is a {controlType} with neither the Grid pattern nor rows (DataItem, ListItem or TreeItem children), " +
                    "so it was not read as a table. Read its text with read_element_text, or find the table inside it with " +
                    "find_ui_elements (controlType 'DataGrid,Table,List').",
                    UIElementErrorCode.PatternNotSupported);
            }

            return progress.Result((long)handle, stopwatch);
        }
        catch (UiaElementUnavailableException ex)
        {
            // The table itself is gone (a cell that disappears is null): the rows read before are still returned, said to be all
            if (progress.RowCount > 0)
            {
                progress.Stop(ElementTableTruncation.Disappeared,
                    $"The table disappeared while it was read ({ex.Message}). The rows read before are returned; its ref is stale, so " +
                    "find the table again (find_ui_elements) before reading on.");
                return progress.Result((long)handle, stopwatch);
            }

            return Failure($"The table disappeared while it was read: {ex.Message}", UIElementErrorCode.ElementAccessFailed);
        }
        catch (TimeoutException ex)
        {
            if (progress.Source != null)
            {
                progress.Stop(ElementTableTruncation.Timeout);
                return progress.Result((long)handle, stopwatch);
            }

            return Failure($"UI Automation did not answer in time: {ex.Message}", UIElementErrorCode.OperationTimedOut);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (progress.Source != null)
            {
                progress.Stop(ElementTableTruncation.Timeout);
                return progress.Result((long)handle, stopwatch);
            }

            return Failure("The timeout passed before the table was read. Try again with a larger timeoutMs.",
                UIElementErrorCode.OperationTimedOut);
        }
        catch (Exception ex)
        {
            return Failure($"Error reading the table: {ex.Message}", UIElementErrorCode.OperationException);
        }
    }

    /// <summary>
    /// A grid: its row and column counts through the Grid pattern, its headers through the Table pattern or its header row, and
    /// the page's cells through GetItem where that only reads (<see cref="GetItemFrameworks"/>), else through the grid's
    /// realized rows. Header rows that are grid rows (an HTML table's) are not data rows: they are left out of the page and of
    /// the row count.
    /// </summary>
    private void ReadGrid(UiaElement element, IUiaGridPattern grid, int maxRows, TableProgress progress, CancellationToken token)
    {
        var rowCount = Math.Max(0, grid.RowCount);
        var columnCount = Math.Max(0, grid.ColumnCount);
        var columns = Math.Min(columnCount, progress.MaxColumns);
        var byItem = element.Properties.FrameworkId is { } framework && GetItemFrameworks.Contains(framework);
        var headers = ColumnHeadersOf(element, columns, token);
        var headerRows = Math.Min(rowCount, headers.Rows ?? (byItem ? HeaderRowsByItem(grid, headers, rowCount, columns) : 0));
        var dataRows = rowCount - headerRows;
        progress.Begin(ElementTableSource.Grid, dataRows, columnCount);
        progress.SetHeaders(headers.Names, columnCount);

        var end = (int)Math.Min((long)progress.StartRow + maxRows, dataRows);
        if (byItem)
        {
            ReadGridByItem(grid, headerRows, columns, end, progress, token);
        }
        else
        {
            ReadRealizedRows(element.Node, headers, headerRows, columns, end, progress, token);
        }

        progress.Finish(rowsRemain: end < dataRows);
    }

    /// <summary>The page's cells asked for by row and column (GridPattern.GetItem), of a grid whose GetItem only reads.</summary>
    private void ReadGridByItem(IUiaGridPattern grid, int headerRows, int columns, int end, TableProgress progress, CancellationToken token)
    {
        for (var row = progress.StartRow; row < end; row++)
        {
            var cells = new string?[columns];
            for (var column = 0; column < columns; column++)
            {
                token.ThrowIfCancellationRequested();
                var item = ItemAt(grid, headerRows + row, column);
                cells[column] = item == null ? null : CellText(TryRead(() => _tree.ReadElement(item)), token);
            }

            if (!progress.TryAddRow(cells))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The page's rows of a grid whose GetItem may scroll, read through the tree: its row children are the rows it has realized
    /// (a WPF DataGrid has only those around what it shows in the tree), each placed by its first cell's GridItem.Row, which
    /// only reads (a WPF cell's row is its item's index). The page ends before the first of its rows that is not among them
    /// (NotRealized): read_table never scrolls, so the agent scrolls with element_action and reads on.
    /// </summary>
    private void ReadRealizedRows(
        IUiaNode node, GridHeaders headers, int headerRows, int columns, int end, TableProgress progress, CancellationToken token)
    {
        var children = _tree.GetChildren(node, token);
        var cached = children.Count <= CachedRowsLimit ? TryRead(() => _tree.GetChildElements(node, token)) : null;
        var count = cached?.Count ?? children.Count;
        var next = progress.StartRow;
        var position = 0;
        for (var i = 0; i < count && next < end; i++)
        {
            token.ThrowIfCancellationRequested();
            var child = cached != null ? cached[i] : TryRead(() => _tree.ReadElement(children[i]));
            if (child == null || !RowTypes.Contains(child.Properties.ControlType))
            {
                continue;
            }

            // Where the row sits, from its first cells (a row header, a HeaderItem, is no grid item): a row before the page, or
            // a header row whose cells sit in the grid's header rows, is passed without reading its cells
            var rowChildren = TryRead(() => _tree.GetChildren(child.Node, token)) ?? [];
            var gridRow = RowOf(rowChildren) ?? RowOf(child.Node);
            if (gridRow - headerRows < next)
            {
                position++;
                continue;
            }

            var all = CellsOf(child.Node, rowChildren, token);
            if (all.Count > 0 && all.All(headers.Contains))
            {
                // A header row whose cells do not say where they sit (an HTML table's header cells): headers, not data
                continue;
            }

            // A row header is not a column: GetItem's column 0 is the first cell after it
            var cells = all.Where(cell => cell.Properties.ControlType != "HeaderItem").ToList();
            var row = (gridRow ?? headerRows + position) - headerRows;
            position++;
            if (row < next)
            {
                continue;
            }

            if (row > next)
            {
                // A row of the page that the grid has not realized: the page never skips it
                break;
            }

            string?[] texts = cells.Count == 0
                ? [CellText(child, token)]
                : cells.Take(columns).Select(cell => CellText(cell, token)).ToArray();
            if (!progress.TryAddRow(texts))
            {
                return;
            }

            next++;
        }

        if (next < end)
        {
            progress.Stop(ElementTableTruncation.NotRealized,
                $"Rows from {next} on are not realized: the grid creates its rows only around what it shows, and read_table never " +
                "scrolls (it reads, it does not act). Bring them into view with element_action (action 'scroll' on the grid's ref, " +
                $"or 'realize' on a row found with find_ui_elements searchVirtualized), then call read_table again with startRow {next}.");
        }
    }

    /// <summary>
    /// The grid's column headers through the Table pattern, else from a Header among its first children. Headers that are grid
    /// items (an HTML table's th cells, through Chromium) say where they sit, so each column gets the headers above it, top
    /// first ("Q1 / Revenue"), and their rows are the grid's header rows; Chromium lists a header that spans columns once per
    /// column. Other headers are taken one per column, in order.
    /// </summary>
    private GridHeaders ColumnHeadersOf(UiaElement grid, int columns, CancellationToken token)
    {
        if ((grid.Patterns & UiaPatterns.Table) != 0 &&
            TryRead(() => _tree.GetPattern<IUiaTablePattern>(grid.Node)?.GetColumnHeaders()) is { Count: > 0 } nodes)
        {
            // The first header tells whether they say where they sit (a header that is no grid item costs one read)
            var positioned = PositionOf(nodes[0]) != null;
            var read = nodes.Take(positioned ? columns * MaxHeaderRows : columns)
                .Select(header => (Element: TryRead(() => _tree.ReadElement(header)), Position: positioned ? PositionOf(header) : null))
                .ToList();
            var ids = read.Select(h => h.Element?.RuntimeId).OfType<IReadOnlyList<int>>().Select(RuntimeIdKey).ToHashSet();
            if (!positioned || read.Any(h => h.Element == null || h.Position == null))
            {
                var inOrder = read.Take(columns).Select(h => (string?)(h.Element?.Properties.Name ?? string.Empty)).ToList();
                return new GridHeaders(inOrder, ids, null);
            }

            var distinct = read
                .Select(h => (Name: h.Element!.Properties.Name, Position: h.Position!))
                .DistinctBy(h => h.Position)
                .ToList();
            var names = Enumerable.Range(0, columns)
                .Select(column => (string?)string.Join(" / ", distinct
                    .Where(h => h.Position.CoversColumn(column) && !string.IsNullOrEmpty(h.Name))
                    .OrderBy(h => h.Position.Row)
                    .Select(h => h.Name)))
                .ToList();

            // The grid's leading rows that hold nothing but these headers
            var rows = 0;
            while (columns > 0 && rows < MaxHeaderRows &&
                   Enumerable.Range(0, columns).All(column => distinct.Any(h => h.Position.Covers(rows, column))))
            {
                rows++;
            }

            return new GridHeaders(names, ids, rows);
        }

        if (HeaderRowOf(grid.Node, token) is { Count: > 0 } headerRow)
        {
            return new GridHeaders(
                headerRow.Take(columns).Select(h => (string?)(h.Properties.Name ?? string.Empty)).ToList(),
                headerRow.Select(h => h.RuntimeId).OfType<IReadOnlyList<int>>().Select(RuntimeIdKey).ToHashSet(),
                null);
        }

        return GridHeaders.None;
    }

    /// <summary>
    /// How many of the grid's leading rows are its column headers, for a grid read through GetItem whose headers do not say
    /// where they sit: the rows whose cells (the first columns) are the Table pattern's header elements themselves.
    /// </summary>
    private int HeaderRowsByItem(IUiaGridPattern grid, GridHeaders headers, int rowCount, int columns)
    {
        if (headers.Ids.Count == 0)
        {
            return 0;
        }

        var rows = 0;
        while (rows < Math.Min(rowCount, MaxHeaderRows) && columns > 0 &&
               Enumerable.Range(0, columns).All(column => ItemAt(grid, rows, column) is { } item &&
                                                          TryRead(() => _tree.GetRuntimeId(item)) is { } id &&
                                                          headers.Ids.Contains(RuntimeIdKey(id))))
        {
            rows++;
        }

        return rows;
    }

    /// <summary>The grid row of the first of a row's first cells that is a grid item, or null when none says.</summary>
    private int? RowOf(IReadOnlyList<IUiaNode> cells)
    {
        foreach (var cell in cells.Take(RowIndexCells))
        {
            if (RowOf(cell) is { } row)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The grid row of a grid item (its GridItem pattern's Row), or null when it is none or the read fails.</summary>
    private int? RowOf(IUiaNode node)
    {
        try
        {
            return _tree.GetPattern<IUiaGridItemPattern>(node)?.Row;
        }
        catch (Exception ex) when (ex is not TimeoutException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Where a grid item sits (its GridItem pattern), or null when it is none or the read fails.</summary>
    private GridPosition? PositionOf(IUiaNode node) =>
        TryRead(() => _tree.GetPattern<IUiaGridItemPattern>(node) is { } item
            ? new GridPosition(item.Row, item.Column, Math.Max(1, item.RowSpan), Math.Max(1, item.ColumnSpan))
            : null);

    private static string RuntimeIdKey(IReadOnlyList<int> runtimeId) => string.Join(".", runtimeId);

    /// <summary>Where a grid item sits: its first row and column, and how many it spans.</summary>
    private sealed record GridPosition(int Row, int Column, int RowSpan, int ColumnSpan)
    {
        public bool CoversColumn(int column) => column >= Column && column < Column + ColumnSpan;

        public bool Covers(int row, int column) => row >= Row && row < Row + RowSpan && CoversColumn(column);
    }

    /// <summary>
    /// A grid's column headers: the text of each of the first columns (null when it has none), the runtime ids of the header
    /// elements, and how many of the grid's leading rows they are (null when the headers do not say where they sit).
    /// </summary>
    private sealed record GridHeaders(IReadOnlyList<string?>? Names, IReadOnlySet<string> Ids, int? Rows)
    {
        public static GridHeaders None { get; } = new(null, new HashSet<string>(), null);

        /// <summary>True for a column header: one of the header elements, or a HeaderItem.</summary>
        public bool Contains(UiaElement cell) =>
            cell.Properties.ControlType == "HeaderItem" || (cell.RuntimeId is { } id && Ids.Contains(RuntimeIdKey(id)));
    }

    /// <summary>
    /// The page's rows from the element's row children; false when the element has no rows and is no list or table (then
    /// nothing is set).
    /// </summary>
    private bool ReadRows(IUiaNode node, string controlType, int maxRows, TableProgress progress, CancellationToken token)
    {
        var children = _tree.GetChildren(node, token);
        IReadOnlyList<UiaElement>? cached = null;
        if (children.Count <= CachedRowsLimit)
        {
            cached = TryRead(() => _tree.GetChildElements(node, token));
        }

        UiaElement? Child(int index) => cached != null ? cached[index] : TryRead(() => _tree.ReadElement(children[index]));

        List<string?>? headers = null;
        var headerCount = 0;
        var rowIndex = 0;
        var columnsSeen = 0;
        var end = (long)progress.StartRow + maxRows;
        var begun = false;
        var stoppedEarly = false;
        var count = cached?.Count ?? children.Count;

        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (Child(i) is not { } child)
            {
                continue;
            }

            var type = child.Properties.ControlType;
            if (type == "Header" && headers == null && rowIndex == 0)
            {
                var items = CellsOf(child, token);
                headers = items.Take(progress.MaxColumns).Select(item => (string?)(item.Properties.Name ?? string.Empty)).ToList();
                headerCount = items.Count;
                continue;
            }

            if (!RowTypes.Contains(type))
            {
                continue;
            }

            var inPage = rowIndex >= progress.StartRow && rowIndex < end;
            var mayBeHeaderRow = headers == null && rowIndex == 0;
            if (!inPage && !mayBeHeaderRow)
            {
                if (rowIndex >= end && cached == null)
                {
                    // Read one at a time: one more row is enough to know that rows remain; counting them all is not worth it
                    stoppedEarly = true;
                    break;
                }

                rowIndex++;
                continue;
            }

            var cells = CellsOf(child, token);
            if (mayBeHeaderRow && cells.Count > 0 && cells.All(c => c.Properties.ControlType == "HeaderItem"))
            {
                headers = cells.Take(progress.MaxColumns).Select(c => (string?)(c.Properties.Name ?? string.Empty)).ToList();
                headerCount = cells.Count;
                continue;
            }

            if (!inPage)
            {
                rowIndex++;
                continue;
            }

            if (!begun)
            {
                progress.Begin(ElementTableSource.Rows, null, null);
                progress.SetHeaders(headers, headerCount);
                begun = true;
            }

            string?[] texts = cells.Count == 0
                ? [CellText(child, token)]
                : cells.Take(progress.MaxColumns).Select(cell => CellText(cell, token)).ToArray();
            columnsSeen = Math.Max(columnsSeen, Math.Max(1, cells.Count));
            progress.SeeColumns(Math.Max(columnsSeen, headerCount));
            if (!progress.TryAddRow(texts))
            {
                return true;
            }

            rowIndex++;
        }

        if (!begun)
        {
            if (rowIndex == 0 && headers == null && !TableTypes.Contains(controlType))
            {
                return false;
            }

            progress.Begin(ElementTableSource.Rows, null, null);
            progress.SetHeaders(headers, headerCount);
            progress.SeeColumns(headerCount);
        }

        if (!stoppedEarly)
        {
            progress.SetTotalRows(rowIndex);
        }

        progress.Finish(rowsRemain: stoppedEarly || rowIndex > end);
        return true;
    }

    /// <summary>The headers from a Header among the grid's first children, or null when there is none.</summary>
    private List<UiaElement>? HeaderRowOf(IUiaNode grid, CancellationToken token)
    {
        var children = TryRead(() => _tree.GetChildren(grid, token));
        foreach (var child in children?.Take(HeaderSearchChildren) ?? [])
        {
            if (TryRead(() => _tree.ReadElement(child)) is { Properties.ControlType: "Header" } header)
            {
                return CellsOf(header, token).ToList();
            }
        }

        return null;
    }

    /// <summary>
    /// The cells of a row or header: its children, read in one cached call when there are few, else the first ones one by one,
    /// but for nested tree items. An expanded tree item's child items are rows of their own, not its cells (a treegrid row's
    /// cells are DataItems, which stay).
    /// </summary>
    private IReadOnlyList<UiaElement> CellsOf(UiaElement row, CancellationToken token) =>
        CellsOf(row.Node, TryRead(() => _tree.GetChildren(row.Node, token)) ?? [], token);

    /// <summary>The cells of a row whose <paramref name="children"/> were listed already (see the other overload).</summary>
    private IReadOnlyList<UiaElement> CellsOf(IUiaNode row, IReadOnlyList<IUiaNode> children, CancellationToken token)
    {
        if (children.Count == 0)
        {
            return [];
        }

        var cells = children.Count <= CachedCellsLimit && TryRead(() => _tree.GetChildElements(row, token)) is { } cached
            ? cached
            : children.Take(MaxColumnsLimit).Select(child => TryRead(() => _tree.ReadElement(child))).OfType<UiaElement>().ToList();
        return cells.Any(cell => cell.Properties.ControlType == "TreeItem")
            ? cells.Where(cell => cell.Properties.ControlType != "TreeItem").ToList()
            : cells;
    }

    /// <summary>
    /// A cell's text: its value when it has the Value pattern (never a password field's: null), else its name, else the names
    /// of its few children. The value comes first because the name of a cell that has one often describes the cell instead
    /// of holding its text: "Amount Row 1, Not sorted." in a WinForms DataGridView, "Item: ..., Column Display Index: 0" in a
    /// WPF DataGrid, the column's name in Explorer's details rows. Null for a cell that could not be read or disappeared.
    /// </summary>
    private string? CellText(UiaElement? cell, CancellationToken token)
    {
        if (cell == null)
        {
            return null;
        }

        if ((cell.Patterns & UiaPatterns.Value) != 0)
        {
            if (TryRead(() => _tree.ReadIdentity(cell.Node)) is not { IsPassword: false })
            {
                // A password field, or one whose flag cannot be read: its value is never read
                return null;
            }

            string? value;
            try
            {
                value = TryValue(cell.Node);
            }
            catch (UiaElementUnavailableException)
            {
                // The cell is gone: null, as a cell the table no longer has (the next GetItem or row read finds a table that is gone)
                return null;
            }

            if (value is { Length: > 0 })
            {
                return value;
            }
        }

        if (cell.Properties.Name is { Length: > 0 } name)
        {
            return name;
        }

        var children = TryRead(() => _tree.GetChildren(cell.Node, token));
        if (children is { Count: > 0 and <= CellChildrenLimit } &&
            TryRead(() => _tree.GetChildElements(cell.Node, token)) is { } texts)
        {
            return string.Join(" ", texts.Select(t => t.Properties.Name).Where(n => !string.IsNullOrEmpty(n)));
        }

        return string.Empty;
    }

    /// <summary>
    /// The grid's cell at a row and column, or null when it has none there or the provider fails; a grid that is gone throws
    /// (<see cref="UiaElementUnavailableException"/>), which ends the read with the rows read before.
    /// </summary>
    private static IUiaNode? ItemAt(IUiaGridPattern grid, int row, int column)
    {
        try
        {
            return grid.GetItem(row, column);
        }
        catch (Exception ex) when (ex is not TimeoutException and not OperationCanceledException and not UiaElementUnavailableException)
        {
            return null;
        }
    }

    /// <summary>A read whose failure (other than a timeout, which ends the table read) gives null.</summary>
    private static T? TryRead<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not TimeoutException and not OperationCanceledException)
        {
            return null;
        }
    }

    private static ElementTableResult TableFailure(
        long handle, string? elementRef, int startRow, string error, UIElementErrorCode code, Stopwatch stopwatch,
        AccessibilityWarmupInfo? warmup) =>
        new(false, elementRef, handle, null, null, null, startRow, null, null, false, Elapsed(stopwatch), Error: error, ErrorCode: code,
            AccessibilityWarmup: warmup);

    /// <summary>
    /// What a table read has read so far, shared with the caller so that the rows read before the worker abandons a stuck
    /// call are still returned. Whole rows only; every member is thread safe.
    /// </summary>
    private sealed class TableProgress(int startRow, int maxColumns)
    {
        /// <summary>The bytes of a null cell in the result's JSON, with its comma.</summary>
        private const int NullCellBytes = 5;

        private readonly object _gate = new();
        private readonly List<IReadOnlyList<string?>> _rows = [];
        private string? _ref;
        private bool _refMoved;
        private ElementTableSource? _source;
        private IReadOnlyList<string?>? _headers;
        private int? _totalRows;
        private int? _totalColumns;
        private int _bytes;
        private bool _cellsTruncated;
        private bool _rowsRemain;
        private ElementTableTruncation? _stop;
        private string? _warning;

        public int StartRow { get; } = startRow;

        public int MaxColumns { get; } = maxColumns;

        public WarmupSlot Warmup { get; } = new(null);

        public string? Ref
        {
            get { lock (_gate) return _ref; }
            set { lock (_gate) _ref = value; }
        }

        /// <summary>The ref was moved to a re-created element (<see cref="ResolvedElement.Moved"/>): the result says so.</summary>
        public bool RefMoved
        {
            get { lock (_gate) return _refMoved; }
            set { lock (_gate) _refMoved = value; }
        }

        public ElementTableSource? Source
        {
            get { lock (_gate) return _source; }
        }

        public void Begin(ElementTableSource source, int? totalRows, int? totalColumns)
        {
            lock (_gate)
            {
                _source = source;
                _totalRows = totalRows;
                _totalColumns = totalColumns;
            }
        }

        public void SetHeaders(IReadOnlyList<string?>? headers, int headerCount)
        {
            lock (_gate)
            {
                _headers = headers is { Count: > 0 } ? Fit(headers.Select(Cap).ToList(), MaxTableTextBytes / 2) : null;
                _bytes += _headers?.Sum(CellBytes) ?? 0;
                if (headerCount > 0 && _totalColumns == null)
                {
                    _totalColumns = headerCount;
                }
            }
        }

        public void SeeColumns(int columns)
        {
            lock (_gate)
            {
                _totalColumns = Math.Max(_totalColumns ?? 0, columns);
            }
        }

        public void SetTotalRows(int rows)
        {
            lock (_gate)
            {
                _totalRows = rows;
            }
        }

        /// <summary>
        /// Adds a whole row; false (and stopped with Size) when it would pass the size limit. The page's first row is always
        /// added, its cells cut to what is left when it alone would pass the limit, so that every page moves the read on.
        /// </summary>
        public bool TryAddRow(IReadOnlyList<string?> cells)
        {
            var capped = cells.Select(Cap).ToList();
            var bytes = capped.Sum(CellBytes);
            lock (_gate)
            {
                if (_bytes + bytes > MaxTableTextBytes)
                {
                    if (_rows.Count > 0 || _stop != null)
                    {
                        _stop ??= ElementTableTruncation.Size;
                        return false;
                    }

                    capped = Fit(capped, MaxTableTextBytes - _bytes);
                    bytes = capped.Sum(CellBytes);
                }

                _bytes += bytes;
                _rows.Add(capped);
                return true;
            }
        }

        public int RowCount
        {
            get { lock (_gate) return _rows.Count; }
        }

        /// <summary>Ends the page for <paramref name="reason"/> (the first reason given counts), with what to tell the agent.</summary>
        public void Stop(ElementTableTruncation reason, string? warning = null)
        {
            lock (_gate)
            {
                if (_stop == null)
                {
                    _stop = reason;
                    _warning = warning;
                }
            }
        }

        public void Finish(bool rowsRemain)
        {
            lock (_gate)
            {
                _rowsRemain = rowsRemain;
            }
        }

        public ElementTableResult Result(long handle, Stopwatch stopwatch)
        {
            lock (_gate)
            {
                var rowsRemain = _stop != null || _rowsRemain;
                ElementTableTruncation? reason = _stop ??
                                                 (_rowsRemain ? ElementTableTruncation.MaxRows :
                                                     _totalColumns > MaxColumns ? ElementTableTruncation.MaxColumns : null);
                var moved = _refMoved ? ElementRefs.MovedWarning(_ref!) : null;
                var warning = moved == null ? _warning : _warning == null ? moved : moved + " " + _warning;
                return new ElementTableResult(
                    Success: true,
                    Ref: _ref,
                    WindowHandle: handle,
                    Source: _source,
                    Headers: _headers,
                    Rows: _rows.ToList(),
                    StartRow: StartRow,
                    TotalRows: _totalRows,
                    TotalColumns: _totalColumns,
                    Truncated: reason != null,
                    ElapsedMilliseconds: Elapsed(stopwatch),
                    TruncationReason: reason,
                    NextStartRow: rowsRemain ? StartRow + _rows.Count : null,
                    CellsTruncated: _cellsTruncated ? true : null,
                    AccessibilityWarmup: Warmup.Value,
                    RefMoved: _refMoved ? true : null,
                    Warning: warning);
            }
        }

        /// <summary>The text cut to <see cref="MaxCellLength"/>, noting the cut. Called under the lock or before it is taken.</summary>
        private string? Cap(string? text)
        {
            if (text is not { Length: > MaxCellLength })
            {
                return text;
            }

            var end = char.IsHighSurrogate(text[MaxCellLength - 1]) ? MaxCellLength - 1 : MaxCellLength;
            _cellsTruncated = true;
            return text[..end];
        }

        /// <summary>
        /// The cells cut so that together they take at most <paramref name="budget"/> bytes, each to an equal share, noting a
        /// cut. Called under the lock.
        /// </summary>
        private List<string?> Fit(List<string?> cells, int budget)
        {
            if (cells.Sum(CellBytes) <= budget)
            {
                return cells;
            }

            var share = Math.Max(0, budget / cells.Count - NullCellBytes);
            return cells.Select(cell =>
            {
                if (cell == null || FittingLength(cell, cell.Length, share) is var end && end == cell.Length)
                {
                    return cell;
                }

                _cellsTruncated = true;
                return cell[..end];
            }).ToList();
        }

        /// <summary>The bytes a header or cell takes in the result's JSON: its text with its quotes and comma, or null and its comma.</summary>
        private static int CellBytes(string? cell) => cell == null ? NullCellBytes : ToolJson.StringBytes(cell) + 3;
    }
}
