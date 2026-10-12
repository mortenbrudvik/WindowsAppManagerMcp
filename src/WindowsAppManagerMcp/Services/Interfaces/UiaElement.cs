using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// A node with the properties a tree walk needs, all read in the one cross-process call that found it (a UI
/// Automation cache request): what <see cref="IUiaTree.GetChildElements"/>, <see cref="IUiaTree.FindAll"/> and
/// <see cref="IUiaTree.ReadSubtree"/> return. Nothing here is read again later, so the values are a snapshot of the
/// moment of that call.
/// </summary>
/// <param name="Node">The node, for further reads (its children, a pattern) through the same tree.</param>
/// <param name="Properties">Name, control type, automation id, bounds, enabled and offscreen.</param>
/// <param name="ClassName">The provider's class name, or null.</param>
/// <param name="IsKeyboardFocusable">Whether the element can take keyboard focus.</param>
/// <param name="Patterns">The control patterns the element supports (its Is*PatternAvailable properties).</param>
/// <param name="RuntimeId">The element's runtime id, unique among the elements on the desktop while it exists, or
/// null when the provider did not report one.</param>
/// <param name="ControlTypeId">The raw control type id (50000-50040, UIA_*ControlTypeId), or null when the engine
/// could not read it: the managed UI Automation client (UIA2) reads no id it has no name for, such as 50039
/// SemanticZoom and 50040 AppBar.</param>
/// <param name="NativeWindowHandle">The window the element is the UI Automation element of, or 0 for an element without a
/// window of its own (most controls of WPF, WinUI and web pages). The HWND proxy lists the windows a top-level window owns
/// (its dialogs and popups) among that window's children, with their own handles: see <see cref="Services.OwnedWindows"/>.</param>
public sealed record UiaElement(
    IUiaNode Node,
    UiaNodeProperties Properties,
    string? ClassName,
    bool IsKeyboardFocusable,
    UiaPatterns Patterns,
    IReadOnlyList<int>? RuntimeId,
    int? ControlTypeId,
    nint NativeWindowHandle = 0)
{
    /// <summary>
    /// Keyboard-focusable, or supports an action pattern (<see cref="UiaPatterns.Actions"/>): what interactableOnly
    /// keeps, together with IsEnabled.
    /// </summary>
    public bool IsInteractable => IsKeyboardFocusable || (Patterns & UiaPatterns.Actions) != 0;
}

/// <summary>A node read with its cached subtree (<see cref="IUiaTree.ReadSubtree"/>).</summary>
/// <param name="Element">The node and its properties.</param>
/// <param name="Children">Its children in the control view, in tree order, each with its own children.</param>
public sealed record UiaElementTree(UiaElement Element, IReadOnlyList<UiaElementTree> Children);

/// <summary>
/// The control patterns a node supports, read from its Is*PatternAvailable properties. Each value has a pattern
/// object behind it (<see cref="IUiaTree.GetPattern{TPattern}"/>).
/// </summary>
[Flags]
public enum UiaPatterns
{
    None = 0,
    Invoke = 1 << 0,
    Toggle = 1 << 1,
    Value = 1 << 2,
    SelectionItem = 1 << 3,
    ExpandCollapse = 1 << 4,
    RangeValue = 1 << 5,
    ScrollItem = 1 << 6,
    Scroll = 1 << 7,
    Window = 1 << 8,
    Text = 1 << 9,
    Grid = 1 << 10,
    Table = 1 << 11,
    ItemContainer = 1 << 12,
    VirtualizedItem = 1 << 13,

    /// <summary>The MSAA bridge (DoDefaultAction). Only the UIA3 engine reads it; the managed client has no such pattern.</summary>
    LegacyIAccessible = 1 << 14,

    /// <summary>
    /// The patterns that act on the element itself, which make an element interactable: Invoke, Toggle, Value,
    /// SelectionItem, ExpandCollapse and RangeValue. Scroll, Window and the containers act on a container or the
    /// window, and LegacyIAccessible is on nearly every element of the UIA3 engine, so they do not count.
    /// </summary>
    Actions = Invoke | Toggle | Value | SelectionItem | ExpandCollapse | RangeValue
}

/// <summary>Names of the action patterns, in the order the tools have always reported them.</summary>
public static class UiaPatternNames
{
    private static readonly (UiaPatterns Pattern, string Name)[] ActionNames =
    [
        (UiaPatterns.Invoke, "Invoke"),
        (UiaPatterns.Toggle, "Toggle"),
        (UiaPatterns.Value, "Value"),
        (UiaPatterns.SelectionItem, "SelectionItem"),
        (UiaPatterns.ExpandCollapse, "ExpandCollapse"),
        (UiaPatterns.RangeValue, "RangeValue"),
    ];

    /// <summary>The action patterns among <paramref name="patterns"/> by name: Invoke, Toggle, Value, SelectionItem,
    /// ExpandCollapse and RangeValue, in that order.</summary>
    public static IReadOnlyList<string> Actions(UiaPatterns patterns) =>
        ActionNames.Where(p => (patterns & p.Pattern) != 0).Select(p => p.Name).ToList();

    /// <summary>
    /// The patterns among <paramref name="patterns"/> by name, the action patterns first in the order of
    /// <see cref="Actions"/>, then ScrollItem, Scroll, Window, Text, Grid, Table, ItemContainer and VirtualizedItem.
    /// LegacyIAccessible is left out: the UIA3 engine reports it on nearly every element, so it says nothing.
    /// </summary>
    public static IReadOnlyList<string> All(UiaPatterns patterns) =>
        Enum.GetValues<UiaPatterns>()
            .Where(p => p is not (UiaPatterns.None or UiaPatterns.Actions or UiaPatterns.LegacyIAccessible) && (patterns & p) != 0)
            .Select(p => p.ToString())
            .ToList();
}

/// <summary>How far <see cref="IUiaTree.FindAll"/> searches from its root, in the control view.</summary>
public enum UiaScope
{
    /// <summary>The root's children.</summary>
    Children,

    /// <summary>Everything below the root.</summary>
    Descendants,

    /// <summary>The root and everything below it.</summary>
    Subtree
}

/// <summary>The properties a <see cref="UiaCondition"/> can compare.</summary>
public enum UiaProperty
{
    /// <summary>A string.</summary>
    Name,

    /// <summary>A string.</summary>
    AutomationId,

    /// <summary>A string.</summary>
    ClassName,

    /// <summary>A control type name as UiaControlTypes.Names lists it, for example "Button" (compared by id).</summary>
    ControlType,

    /// <summary>A string, for example "Win32", "WPF" or "Chrome".</summary>
    FrameworkId,

    /// <summary>An int.</summary>
    ProcessId,

    /// <summary>A bool.</summary>
    IsEnabled,

    /// <summary>A bool.</summary>
    IsOffscreen,

    /// <summary>A bool.</summary>
    IsKeyboardFocusable,

    /// <summary>
    /// A runtime id: an int array (an <c>IReadOnlyList&lt;int&gt;</c>), compared element by element (roadmap M3.4: the
    /// element registry finds an element again by it).
    /// </summary>
    RuntimeId
}

/// <summary>
/// A search condition for <see cref="IUiaTree.FindAll"/>, translated by each engine into its own UI Automation
/// condition, so the search runs in the provider's process in one call.
/// </summary>
public abstract record UiaCondition
{
    /// <summary>Matches every element.</summary>
    public static UiaCondition True { get; } = new UiaTrueCondition();

    /// <summary>
    /// Matches elements whose <paramref name="property"/> equals <paramref name="value"/>. Strings can be compared
    /// ignoring case.
    /// </summary>
    /// <exception cref="ArgumentException">The value has the wrong type for the property (see <see cref="UiaProperty"/>).</exception>
    public static UiaCondition PropertyEquals(UiaProperty property, object value, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var valid = property switch
        {
            UiaProperty.Name or UiaProperty.AutomationId or UiaProperty.ClassName or UiaProperty.ControlType
                or UiaProperty.FrameworkId => value is string,
            UiaProperty.ProcessId => value is int,
            UiaProperty.IsEnabled or UiaProperty.IsOffscreen or UiaProperty.IsKeyboardFocusable => value is bool,
            UiaProperty.RuntimeId => value is IReadOnlyList<int> { Count: > 0 },
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException($"{value.GetType().Name} is not a value of {property}.", nameof(value));
        }

        if (ignoreCase && value is not string)
        {
            throw new ArgumentException("Only string properties can be compared ignoring case.", nameof(ignoreCase));
        }

        // A runtime id is kept as an int array of its own: the caller's list cannot change under the condition, and both
        // engines pass an int[] to UI Automation
        return new UiaPropertyCondition(
            property, value is IReadOnlyList<int> runtimeId ? runtimeId.ToArray() : value, ignoreCase);
    }

    /// <summary>Matches elements that match every condition.</summary>
    public static UiaCondition And(params UiaCondition[] conditions) => new UiaAndCondition(Validated(conditions));

    /// <summary>Matches elements that match any condition.</summary>
    public static UiaCondition Or(params UiaCondition[] conditions) => new UiaOrCondition(Validated(conditions));

    /// <summary>Matches elements that do not match <paramref name="condition"/>.</summary>
    public static UiaCondition Not(UiaCondition condition) =>
        new UiaNotCondition(condition ?? throw new ArgumentNullException(nameof(condition)));

    private static IReadOnlyList<UiaCondition> Validated(UiaCondition[] conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (conditions.Length == 0 || conditions.Any(c => c == null))
        {
            throw new ArgumentException("At least one condition, and no null ones, are needed.", nameof(conditions));
        }

        return conditions.ToArray();
    }
}

/// <summary>Matches every element (<see cref="UiaCondition.True"/>).</summary>
public sealed record UiaTrueCondition : UiaCondition;

/// <summary>A property comparison (<see cref="UiaCondition.PropertyEquals"/>).</summary>
public sealed record UiaPropertyCondition(UiaProperty Property, object Value, bool IgnoreCase) : UiaCondition;

/// <summary>All of the conditions (<see cref="UiaCondition.And"/>).</summary>
public sealed record UiaAndCondition(IReadOnlyList<UiaCondition> Conditions) : UiaCondition;

/// <summary>Any of the conditions (<see cref="UiaCondition.Or"/>).</summary>
public sealed record UiaOrCondition(IReadOnlyList<UiaCondition> Conditions) : UiaCondition;

/// <summary>Not the condition (<see cref="UiaCondition.Not"/>).</summary>
public sealed record UiaNotCondition(UiaCondition Condition) : UiaCondition;

