namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// A UI Automation control pattern of one node, from <see cref="IUiaTree.GetPattern{TPattern}"/>. Each pattern reads
/// its state live (one cross-process call per read) and its methods act on the element in its application.
/// </summary>
/// <remarks>
/// <para>Methods that act (Invoke, Toggle, SetValue, Expand, Select, Scroll, Close, Realize, DoDefaultAction, ...)
/// change the target application. Only the action tools may call them, behind the input guard (roadmap M3.6); the
/// read tools never do. NativeCallGuardTests keeps the UI Automation calls behind them inside the engine adapters, and
/// the E2E guard classifies every member.</para>
/// <para>Some actions block until the application returns from them: a Win32 Invoke that opens a modal dialog returns
/// only when the dialog closes. The UIA3 engine's transaction timeout bounds them; callers still run them on a thread
/// they can abandon.</para>
/// <para>Errors are those of <see cref="IUiaTree"/>; under both engines, a call the provider does not support
/// (UIA_E_NOTSUPPORTED) throws <see cref="NotSupportedException"/>, and an element that is not enabled, or a call the
/// provider refuses (UIA_E_INVALIDOPERATION), throws <see cref="InvalidOperationException"/>.</para>
/// </remarks>
public interface IUiaPattern
{
}

/// <summary>InvokePattern: a button, menu item or link.</summary>
public interface IUiaInvokePattern : IUiaPattern
{
    void Invoke();
}

/// <summary>TogglePattern: a check box or toggle button.</summary>
public interface IUiaTogglePattern : IUiaPattern
{
    UiaToggleState ToggleState { get; }

    void Toggle();
}

/// <summary>ValuePattern: an edit box or a control with a text value.</summary>
public interface IUiaValuePattern : IUiaPattern
{
    /// <summary>The value. Never read it for a password field.</summary>
    string? Value { get; }

    bool IsReadOnly { get; }

    void SetValue(string value);
}

/// <summary>ExpandCollapsePattern: a combo box, tree item, menu or split button.</summary>
public interface IUiaExpandCollapsePattern : IUiaPattern
{
    UiaExpandCollapseState ExpandCollapseState { get; }

    void Expand();

    void Collapse();
}

/// <summary>SelectionItemPattern: a list item, tab item or radio button.</summary>
public interface IUiaSelectionItemPattern : IUiaPattern
{
    bool IsSelected { get; }

    void Select();

    void AddToSelection();

    void RemoveFromSelection();
}

/// <summary>ScrollItemPattern: an item its container can scroll into view.</summary>
public interface IUiaScrollItemPattern : IUiaPattern
{
    void ScrollIntoView();
}

/// <summary>ScrollPattern: a scrollable container.</summary>
public interface IUiaScrollPattern : IUiaPattern
{
    UiaScrollState State { get; }

    void Scroll(UiaScrollAmount horizontal, UiaScrollAmount vertical);

    /// <summary>Scrolls to a position in percent (0-100); -1 leaves an axis where it is.</summary>
    void SetScrollPercent(double horizontalPercent, double verticalPercent);
}

/// <summary>RangeValuePattern: a slider, spinner or progress bar.</summary>
public interface IUiaRangeValuePattern : IUiaPattern
{
    UiaRangeValueState State { get; }

    void SetValue(double value);
}

/// <summary>WindowPattern: a window, including a dialog drawn inside its application's window.</summary>
public interface IUiaWindowPattern : IUiaPattern
{
    UiaWindowState State { get; }

    void SetVisualState(UiaWindowVisualState state);

    void Close();

    /// <summary>Waits until the window's thread is idle and ready for input, or the time passes. False when it passed.</summary>
    bool WaitForInputIdle(int milliseconds);
}

/// <summary>TextPattern: a document or rich edit control.</summary>
public interface IUiaTextPattern : IUiaPattern
{
    /// <summary>The document's text, at most <paramref name="maxLength"/> characters (-1: all of it).</summary>
    string GetText(int maxLength);

    /// <summary>The selected text, ranges joined by newlines, at most <paramref name="maxLength"/> characters.</summary>
    string GetSelectedText(int maxLength);

    /// <summary>
    /// A page of the document's text (roadmap M3.9): a range over the document whose start is moved
    /// <paramref name="offset"/> characters forward, collapsed to its start, and whose end is then moved
    /// <paramref name="maxLength"/> characters forward (MoveEndpointByUnit with TextUnit.Character, so the provider's own
    /// character unit counts, never the whole document read). Only reads: the range is the client's, not the selection.
    /// The page's text is at most <paramref name="maxLength"/> UTF-16 characters: where the provider's characters are
    /// longer (a line break counted as one), the range spans fewer of them (<see cref="Native.UiaTextPageReader"/>).
    /// </summary>
    /// <param name="offset">Where the page starts, in the provider's characters (0: the start of the document).</param>
    /// <param name="maxLength">The most characters the page spans and holds (at least 1).</param>
    UiaTextPage GetTextPage(int offset, int maxLength);
}

/// <summary>A page of a document's text (<see cref="IUiaTextPattern.GetTextPage"/>).</summary>
/// <param name="Text">The page's text: the text of the characters it spans, which can be more UTF-16 characters than
/// <paramref name="Length"/> when the provider counts a line break or another sequence as one character.</param>
/// <param name="Start">Where the page starts, in the provider's characters: the offset asked for, or less when the document
/// is shorter (then the page is empty and <paramref name="Start"/> is the document's length).</param>
/// <param name="Length">How many of the provider's characters the page spans.</param>
/// <param name="HasMore">True when text follows the page.</param>
/// <param name="Cut">True when <paramref name="Text"/> is only the start of the text the page spans: one of the provider's
/// characters was longer than the page could hold. The rest of it is skipped (the next page starts after it).</param>
public sealed record UiaTextPage(string Text, int Start, int Length, bool HasMore, bool Cut = false);

/// <summary>GridPattern: a grid of cells.</summary>
public interface IUiaGridPattern : IUiaPattern
{
    int RowCount { get; }

    int ColumnCount { get; }

    /// <summary>
    /// The cell at a row and column (both zero-based), or null when there is none. Not a pure read: the provider may bring
    /// the cell into view first. WPF's DataGrid scrolls to it and realizes its row (ScrollIntoView, then UpdateLayout), WPF's
    /// GridView brings the row into view, and the Windows Community Toolkit DataGrid scrolls too; read_table calls it only for
    /// the frameworks whose GetItem only looks the cell up (ElementReadService), and the E2E guard counts it as an action.
    /// </summary>
    IUiaNode? GetItem(int row, int column);
}

/// <summary>
/// GridItemPattern: a cell of a grid, and where it sits. Its properties only read (a WPF DataGrid cell's row is its item's
/// index in the grid's items), so read_table places the rows it reads through the tree by them.
/// </summary>
public interface IUiaGridItemPattern : IUiaPattern
{
    /// <summary>The zero-based row of the cell (the first, when it spans several).</summary>
    int Row { get; }

    /// <summary>The zero-based column of the cell (the first, when it spans several).</summary>
    int Column { get; }

    /// <summary>How many rows the cell spans (1 for most cells).</summary>
    int RowSpan { get; }

    /// <summary>How many columns the cell spans (1 for most cells).</summary>
    int ColumnSpan { get; }
}

/// <summary>TablePattern: a grid with headers.</summary>
public interface IUiaTablePattern : IUiaPattern
{
    UiaRowOrColumnMajor RowOrColumnMajor { get; }

    IReadOnlyList<IUiaNode> GetColumnHeaders();

    IReadOnlyList<IUiaNode> GetRowHeaders();
}

/// <summary>ItemContainerPattern: a container whose items may be virtualized (not in the tree until realized).</summary>
public interface IUiaItemContainerPattern : IUiaPattern
{
    /// <summary>
    /// The first item after <paramref name="startAfter"/> (null: from the start) whose <paramref name="property"/>
    /// equals <paramref name="value"/>, realized or not; null when there is none. A null property and value find the
    /// next item.
    /// </summary>
    IUiaNode? FindItemByProperty(IUiaNode? startAfter, UiaProperty? property, object? value);
}

/// <summary>VirtualizedItemPattern: an item that is not in the tree until it is realized.</summary>
public interface IUiaVirtualizedItemPattern : IUiaPattern
{
    void Realize();
}

/// <summary>LegacyIAccessiblePattern: the MSAA bridge of controls that support no other pattern. UIA3 engine only.</summary>
public interface IUiaLegacyIAccessiblePattern : IUiaPattern
{
    /// <summary>
    /// The name of the element's MSAA default action (accDefaultAction, such as "Press"), or null when it has none. The one
    /// property read: MSAA's other properties include the element's value (accValue), which for a password field can be
    /// the password.
    /// </summary>
    string? DefaultAction { get; }

    void DoDefaultAction();
}

public enum UiaToggleState
{
    Off = 0,
    On = 1,
    Indeterminate = 2
}

public enum UiaExpandCollapseState
{
    Collapsed = 0,
    Expanded = 1,
    PartiallyExpanded = 2,
    LeafNode = 3
}

public enum UiaScrollAmount
{
    LargeDecrement = 0,
    SmallDecrement = 1,
    NoAmount = 2,
    LargeIncrement = 3,
    SmallIncrement = 4
}

public enum UiaWindowVisualState
{
    Normal = 0,
    Maximized = 1,
    Minimized = 2
}

public enum UiaWindowInteractionState
{
    Running = 0,
    Closing = 1,
    ReadyForUserInteraction = 2,
    BlockedByModalWindow = 3,
    NotResponding = 4
}

public enum UiaRowOrColumnMajor
{
    RowMajor = 0,
    ColumnMajor = 1,
    Indeterminate = 2
}

/// <summary>A scrollable container's position (percentages 0-100, -1 when an axis cannot scroll).</summary>
public sealed record UiaScrollState(
    double HorizontalPercent,
    double VerticalPercent,
    double HorizontalViewSize,
    double VerticalViewSize,
    bool HorizontallyScrollable,
    bool VerticallyScrollable);

/// <summary>A range control's value and limits.</summary>
public sealed record UiaRangeValueState(
    double Value,
    double Minimum,
    double Maximum,
    double SmallChange,
    double LargeChange,
    bool IsReadOnly);

/// <summary>A window's state through its Window pattern.</summary>
public sealed record UiaWindowState(
    bool IsModal,
    bool IsTopmost,
    bool CanMaximize,
    bool CanMinimize,
    UiaWindowVisualState VisualState,
    UiaWindowInteractionState InteractionState);
