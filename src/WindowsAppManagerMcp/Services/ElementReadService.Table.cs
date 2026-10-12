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
            var node = resolved.Node;
            var grid = (resolved.Element.Patterns & UiaPatterns.Grid) != 0 ? _tree.GetPattern<IUiaGridPattern>(node) : null;
            if (grid != null)
            {
                ReadGrid(node, grid, resolved.Element.Patterns, maxRows, progress, token);
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

    /// <summary>The page's cells through the Grid pattern, the headers through the Table pattern or the header row.</summary>
    private void ReadGrid(IUiaNode node, IUiaGridPattern grid, UiaPatterns patterns, int maxRows, TableProgress progress, CancellationToken token)
    {
        var rowCount = Math.Max(0, grid.RowCount);
        var columnCount = Math.Max(0, grid.ColumnCount);
        progress.Begin(ElementTableSource.Grid, rowCount, columnCount);
        var columns = Math.Min(columnCount, progress.MaxColumns);

        List<string?>? headers = null;
        var headerCount = 0;
        if ((patterns & UiaPatterns.Table) != 0 &&
            TryRead(() => _tree.GetPattern<IUiaTablePattern>(node)?.GetColumnHeaders()) is { Count: > 0 } columnHeaders)
        {
            headerCount = columnHeaders.Count;
            headers = columnHeaders.Take(progress.MaxColumns)
                .Select(header => (string?)(TryRead(() => _tree.ReadElement(header))?.Properties.Name ?? string.Empty))
                .ToList();
        }

        if (headers == null && HeaderRowOf(node, token) is { Count: > 0 } headerRow)
        {
            headerCount = headerRow.Count;
            headers = headerRow.Take(progress.MaxColumns).Select(h => (string?)(h.Properties.Name ?? string.Empty)).ToList();
        }

        progress.SetHeaders(headers, headerCount);

        var end = (int)Math.Min((long)progress.StartRow + maxRows, rowCount);
        for (var row = progress.StartRow; row < end; row++)
        {
            var cells = new string?[columns];
            for (var column = 0; column < columns; column++)
            {
                token.ThrowIfCancellationRequested();
                var item = ItemAt(grid, row, column);
                cells[column] = item == null ? null : CellText(TryRead(() => _tree.ReadElement(item)), token);
            }

            if (!progress.TryAddRow(cells))
            {
                return;
            }
        }

        progress.Finish(rowsRemain: end < rowCount);
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
    private IReadOnlyList<UiaElement> CellsOf(UiaElement row, CancellationToken token)
    {
        var children = TryRead(() => _tree.GetChildren(row.Node, token)) ?? [];
        if (children.Count == 0)
        {
            return [];
        }

        var cells = children.Count <= CachedCellsLimit && TryRead(() => _tree.GetChildElements(row.Node, token)) is { } cached
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
                    Warning: _warning);
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
