using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Access to the UI Automation tree: find elements, read their properties (one at a time, or many in one cached
/// call) and reach their control patterns. The services use UI Automation only through this seam, so their
/// traversal and filter logic can be unit tested with an in-memory tree. Two engines implement it (roadmap M3.2):
/// Native/Uia3Tree over the COM client (IUIAutomation, the default) and Native/UiaTree over the managed client
/// (System.Windows.Automation); WAM_UIA_ENGINE chooses (UiaEngineOptions).
/// </summary>
/// <remarks>
/// <para>Every call can cross into another process and block until that process answers, so callers
/// run them under a deadline (see <see cref="IUiaWorker"/>). The UIA3 engine also gives every call a
/// connection and a transaction timeout of its own.</para>
/// <para>Errors: a node that disappeared, or whose window is gone, throws
/// <see cref="UiaElementUnavailableException"/>. A provider that does not answer in time can throw
/// <see cref="TimeoutException"/>. Other failures surface as the provider's exception.</para>
/// <para>The tree's own methods only read, except <see cref="SetFocus"/>. Acting is done through the pattern objects of
/// <see cref="GetPattern{TPattern}"/> (see <see cref="IUiaPattern"/>).</para>
/// </remarks>
public interface IUiaTree
{
    /// <summary>The engine behind this tree.</summary>
    UiaEngine Engine { get; }

    /// <summary>True when UI Automation can be used on this system.</summary>
    bool IsAvailable();

    /// <summary>The node of a window, or null when the handle is not (or no longer) a window.</summary>
    IUiaNode? FromHandle(nint windowHandle);

    /// <summary>
    /// The node at a point in physical virtual-screen pixels (the same space as click coordinates),
    /// or null when there is none.
    /// </summary>
    IUiaNode? FromPoint(int x, int y);

    /// <summary>The node that has keyboard focus, or null when there is none.</summary>
    IUiaNode? GetFocusedElement();

    /// <summary>
    /// The parent of a node in the control view, or null when the node is a top-level element (its
    /// parent is the desktop root) or is the root itself.
    /// </summary>
    IUiaNode? GetParent(IUiaNode node);

    /// <summary>
    /// The children of a node in the control view, in tree order, read in one call so a child that
    /// disappears during the read cannot end the list early. Only references: no property is read, so it is the cheapest
    /// way to tell how wide a level is before its properties are read, but the call still grows with the level's width
    /// (one reference per child). To tell only whether there is a child, use <see cref="HasChildren"/>.
    /// </summary>
    IReadOnlyList<IUiaNode> GetChildren(IUiaNode node, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a node has a child in the control view: a tree walker's first child, so at most one element is read, however
    /// wide the level.
    /// </summary>
    bool HasChildren(IUiaNode node);

    /// <summary>The properties every tree walk needs.</summary>
    UiaNodeProperties Read(IUiaNode node);

    /// <summary>Keyboard focus and the action patterns a node supports.</summary>
    UiaNodeInteraction ReadInteraction(IUiaNode node);

    /// <summary>
    /// True when the node is keyboard-focusable or supports an action pattern (those listed in
    /// <see cref="UiaNodeInteraction.ActionPatterns"/>). It stops at the first property that answers
    /// the question, so it usually costs fewer cross-process calls than <see cref="ReadInteraction"/>,
    /// and it never reads keyboard focus.
    /// </summary>
    bool IsInteractable(IUiaNode node);

    /// <summary>Who provides a node: class, framework, process, window handle, password flag.</summary>
    UiaNodeIdentity ReadIdentity(IUiaNode node);

    /// <summary>
    /// The node with the properties a walk reads (<see cref="UiaElement"/>: its runtime id, class and patterns included),
    /// read now in one cross-process call. Unlike <see cref="GetRuntimeId"/>, which an engine may answer from what it
    /// read before, this always asks the provider, so it tells whether the element still exists (roadmap M3.4: the element
    /// registry's liveness check): an element that is gone throws <see cref="UiaElementUnavailableException"/>.
    /// </summary>
    UiaElement ReadElement(IUiaNode node);

    /// <summary>
    /// An item its list has not realized, read property by property (roadmap M3.10). What a list's ItemContainer pattern
    /// returns for such an item is often a placeholder that serves only some properties: WPF's ItemAutomationPeer, for an item
    /// without a container, answers its control type, name, automation id, class, runtime id and patterns, but fails its
    /// bounding rectangle, IsOffscreen, IsEnabled, keyboard focus, IsPassword and description with "element not available",
    /// and so fails <see cref="ReadElement"/>'s one cached read as a whole. Here a property the provider fails is left at its
    /// default: no bounds, offscreen, enabled (not known to be disabled), not keyboard-focusable, and no name, automation id,
    /// class, framework or patterns; the element is then marked <see cref="UiaElement.Virtualized"/>. Only reads. Call it for
    /// an element <see cref="ReadElement"/> reported unavailable that may be such an item: one a list's ItemContainer pattern
    /// returned, or the element of a ref given for one.
    /// </summary>
    /// <exception cref="UiaElementUnavailableException">The provider does not serve its runtime id or its control type, which
    /// every placeholder still knows: the element is gone (or could not be named).</exception>
    UiaVirtualizedItem ReadVirtualizedItem(IUiaNode node);

    /// <summary>
    /// A point on the node that a click would hit (UI Automation's clickable point), in physical screen pixels, or null
    /// when the provider reports none (it is offscreen, covered, or the provider does not compute one).
    /// </summary>
    UiaPoint? GetClickablePoint(IUiaNode node);

    /// <summary>The node's help text, access and accelerator keys and localized control type.</summary>
    UiaNodeDescription ReadDescription(IUiaNode node);

    /// <summary>
    /// The node's value through the Value pattern, or null when it has none. Never call this for a
    /// password field (<see cref="UiaNodeIdentity.IsPassword"/>).
    /// </summary>
    string? ReadValue(IUiaNode node);

    /// <summary>
    /// Whether the node is a modal window (its Window pattern's IsModal), such as a dialog drawn inside its app's window;
    /// null when the node has no Window pattern. Only reads.
    /// </summary>
    bool? ReadIsModal(IUiaNode node);

    /// <summary>
    /// The children of a node in the control view, in tree order, each with the properties a walk needs
    /// (<see cref="UiaElement"/>), all read in one cross-process call with a cache request. Faster than
    /// <see cref="GetChildren"/> and a <see cref="Read"/> per child, but a provider that fails for one child can fail
    /// the whole call: read the children one at a time then. Its cost grows with the number of children and cannot be
    /// stopped once started, so call it only for a level known to be small (list it with <see cref="GetChildren"/>
    /// first, as the tree walk does).
    /// </summary>
    IReadOnlyList<UiaElement> GetChildElements(IUiaNode node, CancellationToken cancellationToken);

    /// <summary>
    /// The elements in <paramref name="scope"/> of <paramref name="root"/> in the control view that match
    /// <paramref name="condition"/>, in tree order, with their properties, searched in the provider's process in one
    /// call. Nothing limits its cost: every element in the scope is tested and every match read with all the walk's
    /// properties, in one call that cannot be stopped once started. Search a small root, or the children of one;
    /// <see cref="Services.ElementSearch"/> splits a large root into parts and stops between them (roadmap M3.4).
    /// </summary>
    /// <exception cref="UiaConditionNotSupportedException">The engine cannot express the condition (the managed client
    /// has no id for the AppBar and SemanticZoom control types).</exception>
    /// <exception cref="NotSupportedException">The provider does not support the search (UIA_E_NOTSUPPORTED).</exception>
    IReadOnlyList<UiaElement> FindAll(IUiaNode root, UiaScope scope, UiaCondition condition, CancellationToken cancellationToken);

    /// <summary>
    /// A node and its subtree in the control view with their properties, fetched in one call (BuildUpdatedCache with a
    /// subtree scope). Only for small subtrees such as a menu or a dialog: the call cannot be stopped once started, and
    /// it reads the whole subtree. <paramref name="maxElements"/> limits only what is returned (breadth first), not what
    /// is read, so it bounds the result's size, not the call's cost.
    /// </summary>
    UiaElementTree ReadSubtree(IUiaNode root, int maxElements);

    /// <summary>
    /// The node's runtime id, read now: unique among the elements on the desktop while the element exists (an element
    /// that is gone throws <see cref="UiaElementUnavailableException"/>), or null when the provider reports none.
    /// </summary>
    IReadOnlyList<int>? GetRuntimeId(IUiaNode node);

    /// <summary>True when two nodes are the same element (UI Automation compares their runtime ids).</summary>
    bool AreSameElement(IUiaNode first, IUiaNode second);

    /// <summary>
    /// The node's control pattern of type <typeparamref name="TPattern"/> (for example
    /// <see cref="IUiaInvokePattern"/>), or null when the element does not support it or the engine has no such
    /// pattern (the managed client has no LegacyIAccessible).
    /// </summary>
    TPattern? GetPattern<TPattern>(IUiaNode node) where TPattern : class, IUiaPattern;

    /// <summary>
    /// Gives the node keyboard focus (UI Automation SetFocus). The one member of the tree that acts: it changes the
    /// application's focus and can bring its window to the front. Only element_action calls it, inside the input gate and
    /// after the target checks (roadmap M3.6), as it calls the pattern actions.
    /// </summary>
    /// <exception cref="InvalidOperationException">The element cannot take focus (it is not focusable or not enabled).</exception>
    void SetFocus(IUiaNode node);
}

/// <summary>An element of the UI Automation tree. Only the <see cref="IUiaTree"/> that returned it can read it.</summary>
public interface IUiaNode
{
}

/// <summary>
/// The properties of a node that every tree walk reads.
/// </summary>
/// <param name="ControlType">The control type name, for example "Button". The UIA3 engine names it from the raw id
/// (AppBar and SemanticZoom included); the managed client cannot name those two, so with it they come from the
/// localized control type. "Unknown" when neither gives a name.</param>
/// <param name="Bounds">The bounding rectangle in physical screen pixels, as UI Automation reports it. An
/// element without geometry has <see cref="UiaRect.Empty"/> (infinite position, negative infinite size) in both engines.</param>
/// <param name="FrameworkId">The UI framework that provides the element, for example "Win32", "WPF", "XAML" or "Chrome"
/// (roadmap M3.3), or null when the provider reports none.</param>
public record UiaNodeProperties(
    string? Name,
    string ControlType,
    string? AutomationId,
    UiaRect Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    string? FrameworkId = null);

/// <summary>Keyboard focus and the action patterns of a node.</summary>
/// <param name="ActionPatterns">The supported patterns that act on the element, by name: Invoke, Toggle,
/// Value, SelectionItem, ExpandCollapse and RangeValue. Empty when it supports none.</param>
public record UiaNodeInteraction(
    bool IsKeyboardFocusable,
    bool HasKeyboardFocus,
    IReadOnlyList<string> ActionPatterns);

/// <summary>How a node describes itself to assistive technology (<see cref="IUiaTree.ReadDescription"/>).</summary>
/// <param name="HelpText">The help text (often a tooltip), or null.</param>
/// <param name="AccessKey">The access key, for example "Alt+F", or null.</param>
/// <param name="AcceleratorKey">The shortcut, for example "Ctrl+S", or null.</param>
/// <param name="LocalizedControlType">The control type in the display language, for example "button", or null.</param>
public record UiaNodeDescription(
    string? HelpText,
    string? AccessKey,
    string? AcceleratorKey,
    string? LocalizedControlType);

/// <summary>A point in physical screen pixels.</summary>
public readonly record struct UiaPoint(int X, int Y);

/// <summary>Who provides a node.</summary>
/// <param name="NativeWindowHandle">The window handle of the element, or 0 when the element is not a window
/// of its own (most elements inside an application are not).</param>
public record UiaNodeIdentity(
    string? ClassName,
    string? FrameworkId,
    int ProcessId,
    nint NativeWindowHandle,
    bool IsPassword);

/// <summary>
/// A bounding rectangle as UI Automation reports it, in physical screen pixels. It can be empty or
/// not finite; <see cref="ToBounds"/> turns it into the integer bounds the tools return.
/// </summary>
public readonly record struct UiaRect(double X, double Y, double Width, double Height)
{
    /// <summary>The empty rectangle UI Automation reports for an element without geometry.</summary>
    public static UiaRect Empty { get; } =
        new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    /// <summary>
    /// The smallest integer rectangle that covers this one: the left and top edges are rounded down
    /// and the right and bottom edges up, so negative coordinates on monitors left of or above the
    /// primary one round the right way. Null when the rectangle is empty, negative or not finite,
    /// which means the element has no position on screen.
    /// </summary>
    public BoundsDto? ToBounds()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            Width < 0 || Height < 0)
        {
            return null;
        }

        var left = Math.Floor(X);
        var top = Math.Floor(Y);
        var right = Math.Ceiling(X + Width);
        var bottom = Math.Ceiling(Y + Height);

        if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue ||
            right - left > int.MaxValue || bottom - top > int.MaxValue)
        {
            return null;
        }

        return new BoundsDto((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}

/// <summary>
/// The engine cannot express a search condition (<see cref="IUiaTree.FindAll"/>): the managed client
/// (WAM_UIA_ENGINE=uia2) has no id for the AppBar and SemanticZoom control types. The caller can test that criterion on
/// the server instead. Not a <see cref="NotSupportedException"/>: that is a provider that does not support a call
/// (UIA_E_NOTSUPPORTED), which fails only the part of a search it was asked for.
/// </summary>
public class UiaConditionNotSupportedException : Exception
{
    public UiaConditionNotSupportedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A node read through <see cref="IUiaTree"/> is no longer available: the element disappeared, or
/// its window closed, while it was being read.
/// </summary>
public class UiaElementUnavailableException : Exception
{
    public UiaElementUnavailableException(string message)
        : base(message)
    {
    }

    public UiaElementUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
