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

        var progress = new TableProgress(startRow, maxColumns, UiaTargets.ForeignElementsOf(_windows, _policy, handle));
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
            if (Prepare(elementRef, handle, warmupBudget, progress.Warmup, token, out _, out var error, out var code) is not { } resolved)
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
                    $"The element is a {controlType} with neither the Grid pattern nor rows (DataItem, ListItem or TreeItem children, " +
                    "also inside groups, or Custom children holding DataItem cells), " +
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
    /// the row count. Each row's row header comes with it (roadmap M3.F15): through the Table pattern for a grid read through
    /// GetItem, from the row itself for one read through its rows.
    /// <para>A grid read through its realized rows that also has the ItemContainer pattern virtualizes its items, and its
    /// RowCount may count only the rows it has realized (roadmap M3.F36): Explorer's Items View reports 42 of a folder's 300
    /// files. Every page of such a grid says so, and a page that starts at or past that count is not taken for the end of the
    /// list: its realized rows there are read (a scrolled list's rows carry their own row), and where it has none the page
    /// ends NotRealized, as any row it has not realized ends a page. A grid that has realized no row is taken to be empty.</para>
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

        // GetItem reaches every row, so a grid read through it counts them all; one read through its realized rows may not
        var virtualizing = !byItem && (element.Patterns & UiaPatterns.ItemContainer) != 0;
        var pastCount = virtualizing && dataRows > 0 && progress.StartRow >= dataRows;
        if (virtualizing)
        {
            progress.Note(
                $"totalRows ({dataRows}) is the Grid pattern's row count, and this list virtualizes its items (ItemContainer " +
                "pattern), so it may count only the rows the list has realized: the list may hold more. To reach them, realize an " +
                "item (find_ui_elements searchVirtualized, then element_action 'realize') or element_action 'scroll' the list, " +
                "then call read_table again.");
        }

        var end = (int)Math.Min((long)progress.StartRow + maxRows, pastCount ? int.MaxValue : dataRows);
        if (byItem)
        {
            ReadGridByItem(grid, RowHeadersOf(element, rowCount, columnCount, headerRows, progress, token), headerRows, columns, end,
                progress, token);
        }
        else
        {
            ReadRealizedRows(element.Node, headers, headerRows, columns, end, progress, token);
        }

        // Past the count of a virtualizing list, whether rows follow is not known: the page says they may
        progress.Finish(rowsRemain: pastCount || end < dataRows);
    }

    /// <summary>
    /// The page's cells asked for by row and column (GridPattern.GetItem), of a grid whose GetItem only reads, each row with its
    /// row header when <paramref name="rowHeaders"/> gives one.
    /// </summary>
    private void ReadGridByItem(
        IUiaGridPattern grid, Func<int, string?>? rowHeaders, int headerRows, int columns, int end, TableProgress progress,
        CancellationToken token)
    {
        for (var row = progress.StartRow; row < end; row++)
        {
            var cells = new string?[columns];
            for (var column = 0; column < columns; column++)
            {
                token.ThrowIfCancellationRequested();
                var item = ItemAt(grid, headerRows + row, column);
                cells[column] = item == null ? null : CellText(TryRead(() => _tree.ReadElement(item)), progress, token);
            }

            if (!progress.TryAddRow(cells, rowHeaders?.Invoke(row)))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The page's rows of a grid whose GetItem may scroll, read through the tree: its row children are the rows it has realized
    /// (a WPF DataGrid has only those around what it shows in the tree), each placed by its first cell's GridItem.Row, which
    /// only reads (a WPF cell's row is its item's index), those of a grouped grid inside its groups (<see cref="RowCandidates"/>).
    /// The page ends before the first of its rows that is not among them (NotRealized): read_table never scrolls, so the agent
    /// scrolls with element_action and reads on. A HeaderItem in a row is its row header (a WPF DataGridRowHeader).
    /// </summary>
    private void ReadRealizedRows(
        IUiaNode node, GridHeaders headers, int headerRows, int columns, int end, TableProgress progress, CancellationToken token)
    {
        var next = progress.StartRow;
        var position = 0;
        foreach (var (child, _, _) in next < end ? RowCandidates(node, token) : [])
        {
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
            var rowHeader = all.FirstOrDefault(cell => cell.Properties.ControlType == "HeaderItem");
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
                ? [CellText(child, progress, token)]
                : cells.Take(columns).Select(cell => CellText(cell, progress, token)).ToArray();
            if (!progress.TryAddRow(texts, rowHeader == null ? null : CellText(rowHeader, progress, token)))
            {
                return;
            }

            if (++next >= end)
            {
                break;
            }
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

    /// <summary>
    /// The text of each data row's row header (by its index) through the Table pattern, for a grid read through GetItem
    /// (roadmap M3.F15); null when the grid has none, or when they are cells of the grid (an HTML table's th in a row), which
    /// its rows hold already. Only the page's headers are read: one per grid row in order, from the grid's first row when there
    /// is one per row, else from its first data row. A row without one, or whose header has no text, gets null.
    /// </summary>
    private Func<int, string?>? RowHeadersOf(
        UiaElement grid, int rowCount, int columnCount, int headerRows, TableProgress progress, CancellationToken token)
    {
        if ((grid.Patterns & UiaPatterns.Table) == 0 ||
            TryRead(() => _tree.GetPattern<IUiaTablePattern>(grid.Node)?.GetRowHeaders()) is not { Count: > 0 } nodes ||
            PositionOf(nodes[0]) is { Column: >= 0 } position && position.Column < columnCount)
        {
            return null;
        }

        var first = nodes.Count >= rowCount ? headerRows : 0;
        return row => first + row < nodes.Count ? CellText(TryRead(() => _tree.ReadElement(nodes[first + row])), progress, token) : null;
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
    /// The page's rows from the element's row children, those of a grouped list inside its groups (<see cref="RowCandidates"/>)
    /// and numbered across them; false when the element has no rows and is no list or table (then nothing is set).
    /// <para>A WinForms DataGridView has neither the Grid nor the Table pattern, and its rows are Custom children (roadmap
    /// M3.F39): a Custom "Top Row" of Header items, then a Custom "Row n" per row of a Header (its row header, when the grid
    /// shows them) and a DataItem per cell. So the element's first <see cref="HeaderSearchChildren"/> Custom children are
    /// looked into: a first one of nothing but Header items is the header row (<see cref="HeaderRowTexts"/>), and once one
    /// holds a DataItem, every Custom child is a row. A Header that starts a row is its row header, no column; it gives the
    /// row's rowHeaders entry only through its value (<see cref="RowHeaderText"/>).</para>
    /// </summary>
    private bool ReadRows(IUiaNode node, string controlType, int maxRows, TableProgress progress, CancellationToken token)
    {
        List<string?>? headers = null;
        var headerCount = 0;

        // A header row of Header items, until a row tells whether its leading one is the corner above the row headers
        IReadOnlyList<UiaElement>? headerRow = null;
        bool? rowHeaderColumn = null;
        var customRows = false;
        var customProbes = 0;
        var rowIndex = 0;
        var columnsSeen = 0;
        var end = (long)progress.StartRow + maxRows;
        var begun = false;
        var stoppedEarly = false;

        foreach (var (candidate, cached, grouped) in RowCandidates(node, token))
        {
            if (candidate is not { } child)
            {
                continue;
            }

            // The column headers are the list's own Header, never one inside a group
            var type = child.Properties.ControlType;
            if (type == "Header" && !grouped && headers == null && headerRow == null && rowIndex == 0)
            {
                var items = CellsOf(child, token);
                headers = items.Take(progress.MaxColumns).Select(item => (string?)(item.Properties.Name ?? string.Empty)).ToList();
                headerCount = items.Count;
                continue;
            }

            IReadOnlyList<UiaElement>? probed = null;
            if (type == "Custom" && !customRows)
            {
                // Whether the Custom children are a WinForms DataGridView's header row and rows (roadmap M3.F39)
                if (grouped || rowIndex > 0 || customProbes++ >= HeaderSearchChildren)
                {
                    continue;
                }

                probed = CellsOf(child, token);
                if (headers == null && headerRow == null && probed.Count > 0 && probed.All(c => c.Properties.ControlType == "Header"))
                {
                    headerRow = probed;
                    continue;
                }

                if (!probed.Any(c => c.Properties.ControlType == "DataItem"))
                {
                    continue;
                }

                customRows = true;
                rowHeaderColumn = probed[0].Properties.ControlType == "Header";
            }
            else if (!RowTypes.Contains(type) && !(customRows && type == "Custom"))
            {
                continue;
            }

            var inPage = rowIndex >= progress.StartRow && rowIndex < end;
            var mayBeHeaderRow = headers == null && headerRow == null && rowIndex == 0;
            if (!inPage && !mayBeHeaderRow)
            {
                if (rowIndex >= end && !cached)
                {
                    // Read one at a time: one more row is enough to know that rows remain; counting them all is not worth it
                    stoppedEarly = true;
                    break;
                }

                rowIndex++;
                continue;
            }

            var cells = probed ?? CellsOf(child, token);
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

            // A Header that starts a row is its row header (a WinForms DataGridView's), no column
            var rowHeader = cells.Count > 0 && cells[0].Properties.ControlType == "Header" ? cells[0] : null;
            if (rowHeader != null)
            {
                cells = cells.Skip(1).ToList();
            }

            rowHeaderColumn ??= rowHeader != null;
            if (!begun)
            {
                if (headerRow != null)
                {
                    (headers, headerCount) = HeaderRowTexts(headerRow, rowHeaderColumn, progress, token);
                }

                progress.Begin(ElementTableSource.Rows, null, null);
                progress.SetHeaders(headers, headerCount);
                begun = true;
            }

            string?[] texts = cells.Count == 0
                ? [CellText(child, progress, token)]
                : cells.Take(progress.MaxColumns).Select(cell => CellText(cell, progress, token)).ToArray();
            columnsSeen = Math.Max(columnsSeen, Math.Max(1, cells.Count));
            progress.SeeColumns(Math.Max(columnsSeen, headerCount));
            if (!progress.TryAddRow(texts, rowHeader == null ? null : RowHeaderText(rowHeader, progress)))
            {
                return true;
            }

            rowIndex++;
        }

        if (!begun)
        {
            if (headerRow != null)
            {
                (headers, headerCount) = HeaderRowTexts(headerRow, rowHeaderColumn, progress, token);
            }

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

    /// <summary>
    /// The column headers of a header row of Header items (a WinForms DataGridView's top row, roadmap M3.F39), each its value,
    /// else its name, and how many there are. When the grid shows row headers, its top row starts with the corner cell above
    /// them, which heads no column: the leading Header is left out when the data rows start with a Header
    /// (<paramref name="rowHeaderColumn"/>), and, when no row was read to tell (an empty grid), when it holds no value while
    /// the next one does (a column header holds its text as its value). The corner's name ("Top Left Header Cell") is in the
    /// user's language and never decides.
    /// </summary>
    private (List<string?> Names, int Count) HeaderRowTexts(
        IReadOnlyList<UiaElement> headerRow, bool? rowHeaderColumn, TableProgress progress, CancellationToken token)
    {
        var corner = rowHeaderColumn ?? (headerRow.Count > 1 && ValueText(headerRow[0], progress) is not { Length: > 0 } &&
                                         ValueText(headerRow[1], progress) is { Length: > 0 });
        var columns = corner ? headerRow.Skip(1).ToList() : headerRow;
        return (columns.Take(progress.MaxColumns).Select(header => CellText(header, progress, token)).ToList(), columns.Count);
    }

    /// <summary>
    /// The text of a row header that starts a row of a row walk: its value, or null when it has none. Never its name: a
    /// WinForms DataGridView names each row header "Row n" (n from 0) whatever it shows, and does not expose what it shows,
    /// so its rows get no rowHeaders (roadmap M3.F39).
    /// </summary>
    private string? RowHeaderText(UiaElement header, TableProgress progress) => ValueText(header, progress);

    /// <summary>
    /// The element's children, each read, with the children of its Group children in their place (roadmap M3.F15): Explorer's
    /// grouped views (a Group per date or type, without the Grid pattern) and WPF's grouped grids (a GroupItem per group) hold
    /// their rows in groups, down to <see cref="MaxGroupDepth"/> levels of them. Each level's children are read in one cached
    /// call when there are at most <see cref="CachedRowsLimit"/>, else one at a time as they are reached; Cached says which, so
    /// that a walk can stop where reading on would cost a call per row, and Grouped says the child sits in a group. A child that
    /// cannot be read is null; the element's own children that cannot be listed end the read, a group's are skipped.
    /// </summary>
    private IEnumerable<(UiaElement? Element, bool Cached, bool Grouped)> RowCandidates(IUiaNode node, CancellationToken token, int depth = 0)
    {
        var children = depth == 0 ? _tree.GetChildren(node, token) : TryRead(() => _tree.GetChildren(node, token)) ?? [];
        var cached = children.Count <= CachedRowsLimit ? TryRead(() => _tree.GetChildElements(node, token)) : null;
        var count = cached?.Count ?? children.Count;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var child = cached != null ? cached[i] : TryRead(() => _tree.ReadElement(children[i]));
            if (child is { Properties.ControlType: "Group" } && depth < MaxGroupDepth)
            {
                foreach (var inner in RowCandidates(child.Node, token, depth + 1))
                {
                    yield return inner;
                }

                continue;
            }

            yield return (child, cached != null, depth > 0);
        }
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
    /// WPF DataGrid, the column's name in Explorer's details rows. So a cell whose Value pattern fails gives its name, and the
    /// page's warning says so (<see cref="TableProgress.NoteValueFailure"/>). Null for a cell that could not be read or
    /// disappeared, and for one of another process that the target policy refuses (<see cref="ForeignElements"/>: checked by
    /// its own window, when it has one, and by the process its identity names, where that is read).
    /// </summary>
    private string? CellText(UiaElement? cell, TableProgress progress, CancellationToken token)
    {
        if (cell == null)
        {
            return null;
        }

        if (progress.Foreign.LeavesOutWindow(cell.NativeWindowHandle))
        {
            return null;
        }

        if ((cell.Patterns & UiaPatterns.Value) != 0)
        {
            if (ValueOf(cell, progress) is not { } value)
            {
                return null;
            }

            if (value.Length > 0)
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

    /// <summary>The value of a cell or header that has the Value pattern (see <see cref="ValueOf"/>), else null.</summary>
    private string? ValueText(UiaElement cell, TableProgress progress) =>
        (cell.Patterns & UiaPatterns.Value) != 0 ? ValueOf(cell, progress) : null;

    /// <summary>
    /// The value of a cell with the Value pattern, empty when it has none or the read fails (noted for the page's warning).
    /// Null for a password field, or one whose flag cannot be read, whose value is never read; for a cell of another process
    /// than the window's that the target policy refuses (its identity names its process; noted too); and for a cell that is
    /// gone: null, as a cell the table no longer has (the next GetItem or row read finds a table that is gone).
    /// </summary>
    private string? ValueOf(UiaElement cell, TableProgress progress)
    {
        if (TryRead(() => _tree.ReadIdentity(cell.Node)) is not { IsPassword: false } identity)
        {
            return null;
        }

        if (progress.Foreign.LeavesOut(identity.ProcessId))
        {
            return null;
        }

        var failures = new List<string>(1);
        try
        {
            var value = TryValue(cell.Node, failures);
            if (failures.Count > 0)
            {
                progress.NoteValueFailure(failures[0]);
            }

            return value ?? string.Empty;
        }
        catch (UiaElementUnavailableException)
        {
            return null;
        }
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
    private sealed class TableProgress(int startRow, int maxColumns, ForeignElements foreign)
    {
        /// <summary>The bytes of a null cell in the result's JSON, with its comma.</summary>
        private const int NullCellBytes = 5;

        private int _valueFailures;
        private string? _firstValueFailure;

        /// <summary>The target policy's check of the cells of another process than the window's (see <see cref="CellText"/>).</summary>
        public ForeignElements Foreign { get; } = foreign;

        /// <summary>
        /// A cell's or header's Value pattern failed (<paramref name="failure"/> says how), so its name or its children's names
        /// were taken in its place: the page's warning says how many and the first failure.
        /// </summary>
        public void NoteValueFailure(string failure)
        {
            lock (_gate)
            {
                _valueFailures++;
                _firstValueFailure ??= failure;
            }
        }

        private readonly object _gate = new();
        private readonly List<IReadOnlyList<string?>> _rows = [];
        private readonly List<string?> _rowHeaders = [];
        private bool _hasRowHeaders;
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
        private string? _note;

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
        /// Adds a whole row, with its row header (null or empty: none); false (and stopped with Size) when it would pass the
        /// size limit. The page's first row is always added, its cells and row header cut to what is left when it alone would
        /// pass the limit, so that every page moves the read on. Once a row has a row header, every row's header counts in the
        /// size, a null one too, also for the rows added before it.
        /// </summary>
        public bool TryAddRow(IReadOnlyList<string?> cells, string? rowHeader = null)
        {
            var capped = cells.Select(Cap).ToList();
            var header = rowHeader is { Length: > 0 } ? Cap(rowHeader) : null;
            lock (_gate)
            {
                var counted = _hasRowHeaders || header != null;
                var earlier = header != null && !_hasRowHeaders ? _rows.Count * NullCellBytes : 0;
                var bytes = capped.Sum(CellBytes) + (counted ? CellBytes(header) : 0) + earlier;
                if (_bytes + bytes > MaxTableTextBytes)
                {
                    if (_rows.Count > 0 || _stop != null)
                    {
                        _stop ??= ElementTableTruncation.Size;
                        return false;
                    }

                    var fitted = Fit(header == null ? capped : [.. capped, header], MaxTableTextBytes - _bytes);
                    capped = header == null ? fitted : fitted.GetRange(0, fitted.Count - 1);
                    header = header == null ? null : fitted[^1];
                    bytes = fitted.Sum(CellBytes);
                }

                _bytes += bytes;
                _rows.Add(capped);
                _rowHeaders.Add(header);
                _hasRowHeaders |= header != null;
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

        /// <summary>What every page of this table says, whatever ends it (a virtualizing list's row count, roadmap M3.F36).</summary>
        public void Note(string note)
        {
            lock (_gate)
            {
                _note = note;
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

                // A page that timed out before its first row moves the read on by nothing: no nextStartRow, so that an agent
                // that pages on it does not read the same page for ever (roadmap M3.F15)
                var noRow = _stop == ElementTableTruncation.Timeout && _rows.Count == 0;
                var timedOut = noRow
                    ? $"The timeout passed before the first row of the page was read, so no row was returned. Call read_table " +
                      $"again with startRow {StartRow} and a larger timeoutMs."
                    : null;
                var moved = _refMoved ? ElementRefs.MovedWarning(_ref!) : null;
                var valueFailed = _valueFailures == 0
                    ? null
                    : $"{(_valueFailures == 1 ? "1 cell's" : $"{_valueFailures} cells'")} Value pattern failed (first: " +
                      $"{_firstValueFailure}), so {(_valueFailures == 1 ? "its name was" : "their names were")} returned in its " +
                      "place: such a name is often a description of the cell (\"Amount Row 1, Not sorted.\") rather than its text. " +
                      "Read the page again; if it keeps failing, read those cells with read_element_text.";
                var leftOut = Foreign.LeftOutWarning(null, "those cells are null");
                var warning = string.Join(" ", new[] { moved, _warning, timedOut, valueFailed, leftOut, _note }.OfType<string>());
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
                    NextStartRow: rowsRemain && !noRow ? StartRow + _rows.Count : null,
                    CellsTruncated: _cellsTruncated ? true : null,
                    AccessibilityWarmup: Warmup.Value,
                    RefMoved: _refMoved ? true : null,
                    Warning: warning.Length > 0 ? warning : null,
                    RowHeaders: _hasRowHeaders ? _rowHeaders.ToList() : null);
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
