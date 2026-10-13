namespace WindowsAppManagerMcp.Models;

/// <summary>Where read_element_text got an element's text (roadmap M3.9).</summary>
public enum ElementTextSource
{
    /// <summary>The Text pattern's document, read a page at a time.</summary>
    Text,

    /// <summary>The Value pattern's value, read in full and then cut to the page.</summary>
    Value,

    /// <summary>The element's name (its label or text), when it has neither a Text nor a Value pattern with text.</summary>
    Name,

    /// <summary>A password field: nothing was read, and the text is left out.</summary>
    Redacted
}

/// <summary>Where read_table got a table's cells (roadmap M3.9).</summary>
public enum ElementTableSource
{
    /// <summary>
    /// The Grid pattern: its row and column counts, and each cell of the page by GetItem where that only reads (Win32, WinForms,
    /// Chromium), else the grid's realized rows; headers from the Table pattern or the grid's header row.
    /// </summary>
    Grid,

    /// <summary>The element's row children (DataItem, ListItem, TreeItem) and their children as cells.</summary>
    Rows
}

/// <summary>Why read_table did not return the whole table (roadmap M3.9). The first that applies is reported.</summary>
public enum ElementTableTruncation
{
    /// <summary>More rows follow the page; read on from <c>nextStartRow</c>.</summary>
    MaxRows = 1,

    /// <summary>The table has more columns than <c>maxColumns</c>.</summary>
    MaxColumns = 2,

    /// <summary>The timeout passed before the page was read; the rows read until then are returned.</summary>
    Timeout = 3,

    /// <summary>The page's text reached the size limit of one result; read on from <c>nextStartRow</c>.</summary>
    Size = 4,

    /// <summary>
    /// The table disappeared while the page was read (its window closed, or the application replaced it); the rows read until
    /// then are returned. Its ref is stale: find the table again before reading on.
    /// </summary>
    Disappeared = 5,

    /// <summary>
    /// The next row of the page is not realized: the grid creates the elements of its rows only around what it shows, and
    /// read_table never scrolls (its GetItem would scroll the grid). The realized rows before it are returned; scroll the
    /// grid with element_action (or realize a row) and read again from <c>nextStartRow</c>.
    /// </summary>
    NotRealized = 6
}

/// <summary>
/// What read_element_text reads (roadmap M3.9): the element of <paramref name="Ref"/>, or the one element that
/// <paramref name="Query"/> (find_ui_elements' request) finds.
/// </summary>
/// <param name="Ref">An element ref, or null when <paramref name="Query"/> names the element.</param>
/// <param name="Query">find_ui_elements' search, run first; it must find exactly one element.</param>
/// <param name="Offset">Where to start, in characters (0 or more).</param>
/// <param name="MaxLength">The most characters to return (clamped to 1-<see cref="Services.ElementReadService.MaxTextLength"/>).</param>
/// <param name="Timeout">How long the search and the read may take together.</param>
public sealed record ElementTextRequest(
    string? Ref,
    ElementQuery? Query,
    int Offset,
    int MaxLength,
    TimeSpan Timeout);

/// <summary>What read_table reads (roadmap M3.9).</summary>
/// <param name="Ref">The table's element ref.</param>
/// <param name="StartRow">The first row to return (zero-based, headers not counted).</param>
/// <param name="MaxRows">The most rows to return (clamped to 1-<see cref="Services.ElementReadService.MaxRowsLimit"/>).</param>
/// <param name="MaxColumns">The most columns to return (clamped to 1-<see cref="Services.ElementReadService.MaxColumnsLimit"/>).</param>
/// <param name="Timeout">How long the read may take.</param>
public sealed record ElementTableRequest(
    string Ref,
    int StartRow,
    int MaxRows,
    int MaxColumns,
    TimeSpan Timeout);

/// <summary>Result of read_element_text (roadmap M3.9).</summary>
/// <param name="Ref">The element read: the ref asked for, the ref it has now when it was found again under a ref of its own,
/// or the ref of the element the criteria found.</param>
/// <param name="ControlType">The element's control type.</param>
/// <param name="Text">The page of text; null for a password field and on failure.</param>
/// <param name="Source">Where the text came from: Text, Value, Name, or Redacted for a password field.</param>
/// <param name="Offset">Where the page starts (the offset asked for, or the end of a shorter text).</param>
/// <param name="TotalLength">The text's whole length, when known: always for Value and Name, for Text once the last page was
/// read.</param>
/// <param name="Truncated">True when more text follows the page.</param>
/// <param name="NextOffset">The offset of the next page, when more text follows.</param>
/// <param name="IsPassword">True for a password field (nothing was read); left out otherwise.</param>
/// <param name="Candidates">For AmbiguousElement from the criteria: the elements they found (up to 5), each with its ref.</param>
/// <param name="AccessibilityWarmup">Set when the window shows web content and this call waited for it (roadmap M3.3).</param>
/// <param name="TextSkipped">True when the page ends inside one of the provider's characters that was longer than the page
/// could hold (a line break counted as one character, with maxLength 1): the rest of that character is not returned, and
/// nextOffset is after it. Left out otherwise.</param>
/// <param name="RefMoved">True when the ref read was moved to a re-created element that alone fits its description (the
/// element it was given for is gone), with <paramref name="Warning"/>; left out otherwise.</param>
/// <param name="Warning">Why the text needs a check although it was read: the ref moved.</param>
public sealed record ElementTextResult(
    bool Success,
    string? Ref,
    long WindowHandle,
    string? ControlType,
    string? Text,
    ElementTextSource? Source,
    int Offset,
    int? TotalLength,
    bool Truncated,
    int? NextOffset,
    double ElapsedMilliseconds,
    bool? IsPassword = null,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    IReadOnlyList<FoundElement>? Candidates = null,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    bool? TextSkipped = null,
    bool? RefMoved = null,
    string? Warning = null
);

/// <summary>Result of read_table (roadmap M3.9).</summary>
/// <param name="Ref">The table read: the ref asked for, or the ref it has now when it was found again under a ref of its own.</param>
/// <param name="Source">Grid (the Grid pattern) or Rows (a walk of its row children).</param>
/// <param name="Headers">The column headers (the first maxColumns), or null when the table has none.</param>
/// <param name="Rows">The rows read, each with its first maxColumns cells; a cell is null when the table has no element there,
/// when it disappeared while it was read, or when it is a password field.</param>
/// <param name="StartRow">The index of the first row returned.</param>
/// <param name="TotalRows">How many rows the table has, when known (the Grid pattern's row count without its header rows, or
/// every row child read).</param>
/// <param name="TotalColumns">How many columns the table has: the Grid pattern's column count, or the most cells or headers seen.</param>
/// <param name="Truncated">True when the result does not hold every row from <paramref name="StartRow"/> on, or every column.</param>
/// <param name="NextStartRow">The startRow of the next page, when rows were left out.</param>
/// <param name="CellsTruncated">True when a header or cell was cut to the cell length limit; left out otherwise.</param>
/// <param name="RefMoved">True when the ref was moved to a re-created element that alone fits its description (the element
/// it was given for is gone), with <paramref name="Warning"/>; left out otherwise.</param>
/// <param name="Warning">Why the table needs a check although it was read (the ref moved), and why the page ended early and
/// what to do (NotRealized: scroll the grid with element_action and read on; Disappeared: find the table again); left out
/// otherwise.</param>
public sealed record ElementTableResult(
    bool Success,
    string? Ref,
    long WindowHandle,
    ElementTableSource? Source,
    IReadOnlyList<string?>? Headers,
    IReadOnlyList<IReadOnlyList<string?>>? Rows,
    int StartRow,
    int? TotalRows,
    int? TotalColumns,
    bool Truncated,
    double ElapsedMilliseconds,
    ElementTableTruncation? TruncationReason = null,
    int? NextStartRow = null,
    bool? CellsTruncated = null,
    string? Error = null,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    AccessibilityWarmupInfo? AccessibilityWarmup = null,
    bool? RefMoved = null,
    string? Warning = null
);
