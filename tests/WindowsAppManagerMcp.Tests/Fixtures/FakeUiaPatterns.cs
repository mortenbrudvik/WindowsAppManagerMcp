namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Pattern objects for <see cref="FakeUiaNode.WithPattern{TPattern}"/>: their state is what the test sets, and their
/// actions only count calls. Nothing acts on a real element.
/// </summary>
public sealed class FakeTogglePattern(UiaToggleState state) : IUiaTogglePattern
{
    public UiaToggleState ToggleState { get; set; } = state;

    public int Toggles { get; private set; }

    public void Toggle() => Toggles++;
}

/// <inheritdoc cref="FakeTogglePattern"/>
public sealed class FakeExpandCollapsePattern(UiaExpandCollapseState state) : IUiaExpandCollapsePattern
{
    public UiaExpandCollapseState ExpandCollapseState { get; set; } = state;

    public void Expand() => ExpandCollapseState = UiaExpandCollapseState.Expanded;

    public void Collapse() => ExpandCollapseState = UiaExpandCollapseState.Collapsed;
}

/// <inheritdoc cref="FakeTogglePattern"/>
public sealed class FakeSelectionItemPattern(bool isSelected) : IUiaSelectionItemPattern
{
    public bool IsSelected { get; set; } = isSelected;

    public void Select() => IsSelected = true;

    public void AddToSelection() => IsSelected = true;

    public void RemoveFromSelection() => IsSelected = false;
}

/// <summary>
/// A Text pattern over a document string (roadmap M3.9). A page is cut from the document by character, as a provider whose
/// character unit is one UTF-16 code unit does; <see cref="LineBreakIsOneCharacter"/> makes "\r\n" one character, as some
/// providers count it. Every call is counted; <see cref="Failure"/> makes every call throw.
/// </summary>
public sealed class FakeTextPattern(string document) : IUiaTextPattern
{
    public string Document { get; set; } = document;

    /// <summary>Count "\r\n" as one character, as some providers do.</summary>
    public bool LineBreakIsOneCharacter { get; init; }

    /// <summary>Thrown by every call when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Runs before every page is returned.</summary>
    public Action? OnGetTextPage { get; set; }

    /// <summary>How many times any method was called.</summary>
    public int Calls { get; private set; }

    public string GetText(int maxLength)
    {
        Calls++;
        ThrowIfFailing();
        return maxLength >= 0 && Document.Length > maxLength ? Document[..maxLength] : Document;
    }

    public string GetSelectedText(int maxLength)
    {
        Calls++;
        ThrowIfFailing();
        return string.Empty;
    }

    public UiaTextPage GetTextPage(int offset, int maxLength)
    {
        Calls++;
        ThrowIfFailing();
        OnGetTextPage?.Invoke();
        var units = Units();
        var start = Math.Min(offset, units.Count);
        var length = Math.Min(maxLength, units.Count - start);
        return new UiaTextPage(string.Concat(units.Skip(start).Take(length)), start, length, start + length < units.Count);
    }

    /// <summary>The document split into the provider's characters.</summary>
    private List<string> Units()
    {
        var units = new List<string>(Document.Length);
        for (var i = 0; i < Document.Length; i++)
        {
            if (LineBreakIsOneCharacter && Document[i] == '\r' && i + 1 < Document.Length && Document[i + 1] == '\n')
            {
                units.Add("\r\n");
                i++;
            }
            else
            {
                units.Add(Document[i].ToString());
            }
        }

        return units;
    }

    private void ThrowIfFailing()
    {
        if (Failure != null)
        {
            throw Failure;
        }
    }
}

/// <summary>A Grid pattern over a rectangle of cells (roadmap M3.9): a cell is the node the test put there, or null.</summary>
public sealed class FakeGridPattern(IReadOnlyList<IReadOnlyList<FakeUiaNode?>> cells, int? columnCount = null) : IUiaGridPattern
{
    private readonly List<(int Row, int Column)> _items = [];

    public int RowCount => cells.Count;

    public int ColumnCount => columnCount ?? (cells.Count == 0 ? 0 : cells.Max(row => row.Count));

    /// <summary>Every GetItem call, in order.</summary>
    public IReadOnlyList<(int Row, int Column)> Items => _items;

    public IUiaNode? GetItem(int row, int column)
    {
        _items.Add((row, column));
        if (row < 0 || row >= cells.Count || column < 0 || column >= ColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row), "No such cell.");
        }

        return column < cells[row].Count ? cells[row][column] : null;
    }
}

/// <summary>A Table pattern with column headers (roadmap M3.9).</summary>
public sealed class FakeTablePattern(params FakeUiaNode[] columnHeaders) : IUiaTablePattern
{
    public UiaRowOrColumnMajor RowOrColumnMajor => UiaRowOrColumnMajor.RowMajor;

    /// <summary>Thrown by <see cref="GetColumnHeaders"/> when set.</summary>
    public Exception? HeadersException { get; set; }

    public IReadOnlyList<IUiaNode> GetColumnHeaders() =>
        HeadersException != null ? throw HeadersException : columnHeaders;

    public IReadOnlyList<IUiaNode> GetRowHeaders() => [];
}
