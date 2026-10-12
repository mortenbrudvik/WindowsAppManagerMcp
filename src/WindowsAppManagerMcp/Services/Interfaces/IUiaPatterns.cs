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
/// <para>Errors are those of <see cref="IUiaTree"/>; an element that is not enabled, or a call the provider does not
/// support, throws <see cref="InvalidOperationException"/>.</para>
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
    /// </summary>
    /// <param name="offset">Where the page starts, in the provider's characters (0: the start of the document).</param>
    /// <param name="maxLength">The most characters the page spans (at least 1).</param>
    UiaTextPage GetTextPage(int offset, int maxLength);
}

/// <summary>A page of a document's text (<see cref="IUiaTextPattern.GetTextPage"/>).</summary>
/// <param name="Text">The page's text. The provider may return more UTF-16 characters than <paramref name="Length"/> when it
/// counts a line break or another sequence as one character.</param>
/// <param name="Start">Where the page starts, in the provider's characters: the offset asked for, or less when the document
/// is shorter (then the page is empty and <paramref name="Start"/> is the document's length).</param>
/// <param name="Length">How many of the provider's characters the page spans.</param>
/// <param name="HasMore">True when text follows the page.</param>
public sealed record UiaTextPage(string Text, int Start, int Length, bool HasMore);

/// <summary>GridPattern: a grid of cells.</summary>
public interface IUiaGridPattern : IUiaPattern
{
    int RowCount { get; }

    int ColumnCount { get; }

    /// <summary>The cell at a row and column (both zero-based), or null when there is none.</summary>
    IUiaNode? GetItem(int row, int column);
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
    UiaLegacyAccessibleState State { get; }

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

/// <summary>What MSAA reports for an element (LegacyIAccessiblePattern).</summary>
/// <param name="Role">The MSAA role (ROLE_SYSTEM_*).</param>
/// <param name="State">The MSAA state flags (STATE_SYSTEM_*).</param>
public sealed record UiaLegacyAccessibleState(
    string? Name,
    string? Value,
    string? Description,
    string? DefaultAction,
    uint Role,
    uint State);
