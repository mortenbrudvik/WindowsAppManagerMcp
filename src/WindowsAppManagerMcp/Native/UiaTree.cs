using System.Windows.Automation;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// <see cref="IUiaTree"/> over the managed UI Automation client (System.Windows.Automation). It only
/// reads: it finds elements and reads properties, and never invokes a pattern or sets focus.
/// </summary>
/// <remarks>
/// <para>Each property read is one cross-process call; nothing is cached yet (M3.1).</para>
/// <para>The managed client cannot name the control types added in Windows 8 (AppBar,
/// SemanticZoom) and cannot read their raw ids, so <see cref="UiaControlTypes.GetName"/> falls back
/// to the localized control type. The UIA3 adapter planned in M3.2 can read the ids.</para>
/// </remarks>
public sealed class UiaTree : IUiaTree
{
    // Patterns that act on the element. Scroll, Window and Transform are left out: they act on a
    // container or the window, not on a control an agent would pick as a target.
    private static readonly (AutomationProperty Property, string Name)[] ActionPatternProperties =
    [
        (AutomationElement.IsInvokePatternAvailableProperty, "Invoke"),
        (AutomationElement.IsTogglePatternAvailableProperty, "Toggle"),
        (AutomationElement.IsValuePatternAvailableProperty, "Value"),
        (AutomationElement.IsSelectionItemPatternAvailableProperty, "SelectionItem"),
        (AutomationElement.IsExpandCollapsePatternAvailableProperty, "ExpandCollapse"),
        (AutomationElement.IsRangeValuePatternAvailableProperty, "RangeValue"),
    ];

    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            return AutomationElement.RootElement != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IUiaNode? FromHandle(nint windowHandle)
    {
        try
        {
            return Wrap(AutomationElement.FromHandle(windowHandle));
        }
        catch (ElementNotAvailableException)
        {
            // Not a window, or it closed
            return null;
        }
    }

    /// <inheritdoc />
    public IUiaNode? FromPoint(int x, int y) =>
        Translate(() => Wrap(AutomationElement.FromPoint(new System.Windows.Point(x, y))));

    /// <inheritdoc />
    public IUiaNode? GetFocusedElement() => Translate(() => Wrap(AutomationElement.FocusedElement));

    /// <inheritdoc />
    public IUiaNode? GetParent(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate(() =>
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(element);
            if (parent == null || Automation.Compare(parent, AutomationElement.RootElement))
            {
                return null;
            }

            return Wrap(parent);
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<IUiaNode> GetChildren(IUiaNode node, CancellationToken cancellationToken)
    {
        var element = Unwrap(node);
        cancellationToken.ThrowIfCancellationRequested();

        // One FindAll call reads all children in the control view (the default cache request's view),
        // so a child that disappears meanwhile cannot end the list early, as it could with a sibling walk.
        return Translate(() => element.FindAll(TreeScope.Children, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Select(child => (IUiaNode)new Node(child))
            .ToList());
    }

    /// <inheritdoc />
    public UiaNodeProperties Read(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate(() =>
        {
            var current = element.Current;
            var controlType = current.ControlType;
            var rect = current.BoundingRectangle;

            return new UiaNodeProperties(
                Name: current.Name,
                ControlType: UiaControlTypes.GetName(
                    controlType,
                    controlType == null ? TryGetLocalizedControlType(current) : null),
                AutomationId: current.AutomationId,
                Bounds: rect.IsEmpty ? UiaRect.Empty : new UiaRect(rect.X, rect.Y, rect.Width, rect.Height),
                IsEnabled: current.IsEnabled,
                IsOffscreen: current.IsOffscreen);
        });
    }

    /// <inheritdoc />
    public UiaNodeInteraction ReadInteraction(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate(() =>
        {
            var current = element.Current;
            var patterns = ActionPatternProperties
                .Where(p => element.GetCurrentPropertyValue(p.Property) is true)
                .Select(p => p.Name)
                .ToList();

            return new UiaNodeInteraction(
                IsKeyboardFocusable: current.IsKeyboardFocusable,
                HasKeyboardFocus: current.HasKeyboardFocus,
                ActionPatterns: patterns);
        });
    }

    /// <inheritdoc />
    public UiaNodeIdentity ReadIdentity(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate(() =>
        {
            var current = element.Current;
            return new UiaNodeIdentity(
                ClassName: current.ClassName,
                FrameworkId: current.FrameworkId,
                ProcessId: current.ProcessId,
                NativeWindowHandle: current.NativeWindowHandle,
                IsPassword: current.IsPassword);
        });
    }

    /// <inheritdoc />
    public string? ReadValue(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate(() =>
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern
                ? valuePattern.Current.Value
                : null);
    }

    private static string? TryGetLocalizedControlType(AutomationElement.AutomationElementInformation current)
    {
        try
        {
            return current.LocalizedControlType;
        }
        catch (Exception)
        {
            // The control type name is optional; never lose the element over it
            return null;
        }
    }

    private static Node? Wrap(AutomationElement? element) => element == null ? null : new Node(element);

    private static AutomationElement Unwrap(IUiaNode node) =>
        node is Node own
            ? own.Element
            : throw new ArgumentException($"The node was not created by {nameof(UiaTree)}.", nameof(node));

    /// <summary>
    /// Runs a read and reports a vanished element as <see cref="UiaElementUnavailableException"/>,
    /// the seam's exception, so services do not depend on System.Windows.Automation.
    /// </summary>
    private static T Translate<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (ElementNotAvailableException ex)
        {
            throw new UiaElementUnavailableException(ex.Message, ex);
        }
    }

    private sealed class Node(AutomationElement element) : IUiaNode
    {
        public AutomationElement Element { get; } = element;
    }
}
