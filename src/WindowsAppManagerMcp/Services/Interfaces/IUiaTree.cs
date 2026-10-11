using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Read-only access to the UI Automation tree: find elements and read their properties. The
/// services walk the tree through this seam, so their traversal and filter logic can be unit tested
/// with an in-memory tree. The production adapter (Native/UiaTree) wraps System.Windows.Automation.
/// </summary>
/// <remarks>
/// <para>Every call can cross into another process and block until that process answers, so callers
/// run them under a deadline (see <see cref="IUiaWorker"/>).</para>
/// <para>Errors: a node that disappeared, or whose window is gone, throws
/// <see cref="UiaElementUnavailableException"/>. A provider that does not answer in time can throw
/// <see cref="TimeoutException"/>. Other failures surface as the provider's exception.</para>
/// </remarks>
public interface IUiaTree
{
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
    /// disappears during the read cannot end the list early.
    /// </summary>
    IReadOnlyList<IUiaNode> GetChildren(IUiaNode node, CancellationToken cancellationToken);

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
    /// The node's value through the Value pattern, or null when it has none. Never call this for a
    /// password field (<see cref="UiaNodeIdentity.IsPassword"/>).
    /// </summary>
    string? ReadValue(IUiaNode node);

    /// <summary>
    /// Whether the node is a modal window (its Window pattern's IsModal), such as a dialog drawn inside its app's window;
    /// null when the node has no Window pattern. Only reads.
    /// </summary>
    bool? ReadIsModal(IUiaNode node);
}

/// <summary>An element of the UI Automation tree. Only the <see cref="IUiaTree"/> that returned it can read it.</summary>
public interface IUiaNode
{
}

/// <summary>
/// The properties of a node that every tree walk reads.
/// </summary>
/// <param name="ControlType">The control type name, for example "Button". Types the managed UI Automation
/// client cannot name (AppBar, SemanticZoom) come from the localized control type, and "Unknown" when even
/// that is missing.</param>
/// <param name="Bounds">The bounding rectangle in physical screen pixels, as UI Automation reports it. An
/// element without geometry reports an empty rectangle (infinite position, negative infinite size).</param>
public record UiaNodeProperties(
    string? Name,
    string ControlType,
    string? AutomationId,
    UiaRect Bounds,
    bool IsEnabled,
    bool IsOffscreen);

/// <summary>Keyboard focus and the action patterns of a node.</summary>
/// <param name="ActionPatterns">The supported patterns that act on the element, by name: Invoke, Toggle,
/// Value, SelectionItem, ExpandCollapse and RangeValue. Empty when it supports none.</param>
public record UiaNodeInteraction(
    bool IsKeyboardFocusable,
    bool HasKeyboardFocus,
    IReadOnlyList<string> ActionPatterns);

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
