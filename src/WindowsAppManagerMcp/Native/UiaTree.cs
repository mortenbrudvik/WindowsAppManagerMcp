using WindowsAppManagerMcp.Models;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// <see cref="IUiaTree"/> over the managed UI Automation client (System.Windows.Automation), the "UIA2" engine:
/// the fallback behind WAM_UIA_ENGINE=uia2 (roadmap M3.2; <see cref="Uia3Tree"/> is the default). Its own methods
/// only read; its pattern objects act, for the action tools.
/// </summary>
/// <remarks>
/// <para>The single-node reads (Read, ReadIdentity, ...) cost one cross-process call per property; the walk reads
/// (<see cref="GetChildElements"/>, <see cref="FindAll"/>, <see cref="ReadSubtree"/>) fetch the walk's properties
/// with each level in one call under an activated CacheRequest (roadmap M3.1).</para>
/// <para>The entry points (IsAvailable, FromHandle, FromPoint, GetFocusedElement) first pass
/// <see cref="UiaClientStartup"/>, because the client's first call in a process fails when several
/// threads make it at once.</para>
/// <para>The managed client has no per-call timeouts, and it cannot name the control types added in Windows 8
/// (AppBar, SemanticZoom) or read their raw ids, so <see cref="UiaControlTypes.GetName(ControlType?, string?)"/>
/// falls back to the localized control type and conditions cannot compare them. It has no LegacyIAccessible
/// pattern.</para>
/// </remarks>
public sealed class UiaTree : IUiaTree
{
    // The walk's properties (UiaElement), read with each element by the cached reads
    private static readonly AutomationProperty[] WalkProperties =
    [
        AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.LocalizedControlTypeProperty,
        AutomationElement.AutomationIdProperty, AutomationElement.ClassNameProperty, AutomationElement.BoundingRectangleProperty,
        AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.IsKeyboardFocusableProperty,
        AutomationElement.RuntimeIdProperty, AutomationElement.FrameworkIdProperty
    ];

    private static readonly (AutomationProperty Property, UiaPatterns Pattern)[] PatternAvailability =
    [
        (AutomationElement.IsInvokePatternAvailableProperty, UiaPatterns.Invoke),
        (AutomationElement.IsTogglePatternAvailableProperty, UiaPatterns.Toggle),
        (AutomationElement.IsValuePatternAvailableProperty, UiaPatterns.Value),
        (AutomationElement.IsSelectionItemPatternAvailableProperty, UiaPatterns.SelectionItem),
        (AutomationElement.IsExpandCollapsePatternAvailableProperty, UiaPatterns.ExpandCollapse),
        (AutomationElement.IsRangeValuePatternAvailableProperty, UiaPatterns.RangeValue),
        (AutomationElement.IsScrollItemPatternAvailableProperty, UiaPatterns.ScrollItem),
        (AutomationElement.IsScrollPatternAvailableProperty, UiaPatterns.Scroll),
        (AutomationElement.IsWindowPatternAvailableProperty, UiaPatterns.Window),
        (AutomationElement.IsTextPatternAvailableProperty, UiaPatterns.Text),
        (AutomationElement.IsGridPatternAvailableProperty, UiaPatterns.Grid),
        (AutomationElement.IsTablePatternAvailableProperty, UiaPatterns.Table),
        (AutomationElement.IsItemContainerPatternAvailableProperty, UiaPatterns.ItemContainer),
        (AutomationElement.IsVirtualizedItemPatternAvailableProperty, UiaPatterns.VirtualizedItem),
    ];

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

    private readonly UiaClientStartup _startup;

    public UiaTree()
        : this(UiaClientStartup.Shared)
    {
    }

    /// <summary>Creates the adapter with its own start gate (tests).</summary>
    public UiaTree(UiaClientStartup startup)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
    }

    /// <inheritdoc />
    public UiaEngine Engine => UiaEngine.Uia2;

    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            _startup.EnsureStarted();
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
        _startup.EnsureStarted();
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
    public IUiaNode? FromPoint(int x, int y)
    {
        _startup.EnsureStarted();
        return Translate(() => Wrap(AutomationElement.FromPoint(new System.Windows.Point(x, y))));
    }

    /// <inheritdoc />
    public IUiaNode? GetFocusedElement()
    {
        _startup.EnsureStarted();
        return Translate(() => Wrap(AutomationElement.FocusedElement));
    }

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
                IsOffscreen: current.IsOffscreen,
                FrameworkId: current.FrameworkId);
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
    public bool IsInteractable(IUiaNode node)
    {
        var element = Unwrap(node);

        // Focusable first: edits, buttons and most list items answer with the first read
        return Translate(() =>
            element.Current.IsKeyboardFocusable ||
            ActionPatternProperties.Any(p => element.GetCurrentPropertyValue(p.Property) is true));
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
    public UiaElement ReadElement(IUiaNode node)
    {
        var element = Unwrap(node);

        // GetUpdatedCache always asks the provider: an element that is gone throws ElementNotAvailableException
        return Translate(() => ToElement(element.GetUpdatedCache(WalkRequest(TreeScope.Element))));
    }

    /// <inheritdoc />
    public UiaPoint? GetClickablePoint(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate<UiaPoint?>(() =>
            element.TryGetClickablePoint(out var point) && double.IsFinite(point.X) && double.IsFinite(point.Y)
                ? new UiaPoint((int)Math.Round(point.X), (int)Math.Round(point.Y))
                : null);
    }

    /// <inheritdoc />
    public UiaNodeDescription ReadDescription(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate(() =>
        {
            var current = element.Current;
            return new UiaNodeDescription(
                HelpText: current.HelpText,
                AccessKey: current.AccessKey,
                AcceleratorKey: current.AcceleratorKey,
                LocalizedControlType: current.LocalizedControlType);
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

    /// <inheritdoc />
    public bool? ReadIsModal(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate<bool?>(() =>
            element.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) && pattern is WindowPattern window
                ? window.Current.IsModal
                : null);
    }

    /// <inheritdoc />
    public IReadOnlyList<UiaElement> GetChildElements(IUiaNode node, CancellationToken cancellationToken)
    {
        var element = Unwrap(node);
        cancellationToken.ThrowIfCancellationRequested();
        return Translate(() =>
        {
            using (WalkRequest(TreeScope.Element).Activate())
            {
                return Elements(element.FindAll(TreeScope.Children, Condition.TrueCondition));
            }
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<UiaElement> FindAll(IUiaNode root, UiaScope scope, UiaCondition condition, CancellationToken cancellationToken)
    {
        var element = Unwrap(root);
        ArgumentNullException.ThrowIfNull(condition);
        var search = ToCondition(condition);
        cancellationToken.ThrowIfCancellationRequested();
        return Translate(() =>
        {
            // The active request's TreeFilter (the control view) is the view FindAll searches
            using (WalkRequest(TreeScope.Element).Activate())
            {
                return Elements(element.FindAll(ToTreeScope(scope), search));
            }
        });
    }

    /// <inheritdoc />
    public UiaElementTree ReadSubtree(IUiaNode root, int maxElements)
    {
        var element = Unwrap(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxElements, 1);
        return Translate(() =>
        {
            var cachedRoot = element.GetUpdatedCache(WalkRequest(TreeScope.Subtree));

            // Breadth first, so a cut keeps the levels nearest the root
            var rootChildren = new List<UiaElementTree>();
            var tree = new UiaElementTree(ToElement(cachedRoot), rootChildren);
            var count = 1;
            var pending = new Queue<(AutomationElement Element, List<UiaElementTree> Children)>();
            pending.Enqueue((cachedRoot, rootChildren));
            while (pending.Count > 0 && count < maxElements)
            {
                var (parent, children) = pending.Dequeue();
                foreach (AutomationElement child in parent.CachedChildren)
                {
                    if (count >= maxElements)
                    {
                        break;
                    }

                    var grandchildren = new List<UiaElementTree>();
                    children.Add(new UiaElementTree(ToElement(child), grandchildren));
                    count++;
                    pending.Enqueue((child, grandchildren));
                }
            }

            return tree;
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<int>? GetRuntimeId(IUiaNode node)
    {
        var element = Unwrap(node);
        return Translate(() => element.GetRuntimeId());
    }

    /// <inheritdoc />
    public bool AreSameElement(IUiaNode first, IUiaNode second)
    {
        var a = Unwrap(first);
        var b = Unwrap(second);
        return Translate(() => Automation.Compare(a, b));
    }

    /// <inheritdoc />
    public TPattern? GetPattern<TPattern>(IUiaNode node) where TPattern : class, IUiaPattern
    {
        var element = Unwrap(node);
        var pattern = typeof(TPattern).Name switch
        {
            nameof(IUiaInvokePattern) => System.Windows.Automation.InvokePattern.Pattern,
            nameof(IUiaTogglePattern) => System.Windows.Automation.TogglePattern.Pattern,
            nameof(IUiaValuePattern) => System.Windows.Automation.ValuePattern.Pattern,
            nameof(IUiaExpandCollapsePattern) => System.Windows.Automation.ExpandCollapsePattern.Pattern,
            nameof(IUiaSelectionItemPattern) => System.Windows.Automation.SelectionItemPattern.Pattern,
            nameof(IUiaScrollItemPattern) => System.Windows.Automation.ScrollItemPattern.Pattern,
            nameof(IUiaScrollPattern) => System.Windows.Automation.ScrollPattern.Pattern,
            nameof(IUiaRangeValuePattern) => System.Windows.Automation.RangeValuePattern.Pattern,
            nameof(IUiaWindowPattern) => System.Windows.Automation.WindowPattern.Pattern,
            nameof(IUiaTextPattern) => System.Windows.Automation.TextPattern.Pattern,
            nameof(IUiaGridPattern) => System.Windows.Automation.GridPattern.Pattern,
            nameof(IUiaTablePattern) => System.Windows.Automation.TablePattern.Pattern,
            nameof(IUiaItemContainerPattern) => System.Windows.Automation.ItemContainerPattern.Pattern,
            nameof(IUiaVirtualizedItemPattern) => System.Windows.Automation.VirtualizedItemPattern.Pattern,
            // No LegacyIAccessible in the managed client
            _ => null
        };
        if (pattern == null)
        {
            return null;
        }

        return Translate(() => element.TryGetCurrentPattern(pattern, out var found) ? WrapPattern(typeof(TPattern), found) as TPattern : null);
    }

    /// <summary>
    /// A cache request for the walk's properties and the pattern availability, in the control view, with live
    /// elements (Full mode, so the elements can be read further). A new one per call: Activate pushes it onto the
    /// calling thread's stack, and concurrent calls must not share it.
    /// </summary>
    private static CacheRequest WalkRequest(TreeScope scope)
    {
        var request = new CacheRequest
        {
            TreeFilter = Automation.ControlViewCondition,
            TreeScope = scope,
            AutomationElementMode = AutomationElementMode.Full
        };
        foreach (var property in WalkProperties)
        {
            request.Add(property);
        }

        foreach (var (property, _) in PatternAvailability)
        {
            request.Add(property);
        }

        return request;
    }

    private static List<UiaElement> Elements(AutomationElementCollection collection) =>
        collection.Cast<AutomationElement>().Select(ToElement).ToList();

    /// <summary>An element whose walk properties were cached by the call that returned it.</summary>
    private static UiaElement ToElement(AutomationElement element)
    {
        var cached = element.Cached;
        var controlType = cached.ControlType;
        var rect = cached.BoundingRectangle;
        var patterns = UiaPatterns.None;
        foreach (var (property, pattern) in PatternAvailability)
        {
            if (element.GetCachedPropertyValue(property) is true)
            {
                patterns |= pattern;
            }
        }

        var properties = new UiaNodeProperties(
            Name: cached.Name,
            ControlType: UiaControlTypes.GetName(controlType, controlType == null ? cached.LocalizedControlType : null),
            AutomationId: cached.AutomationId,
            Bounds: rect.IsEmpty ? UiaRect.Empty : new UiaRect(rect.X, rect.Y, rect.Width, rect.Height),
            IsEnabled: cached.IsEnabled,
            IsOffscreen: cached.IsOffscreen,
            FrameworkId: cached.FrameworkId);

        return new UiaElement(
            new Node(element),
            properties,
            ClassName: cached.ClassName,
            IsKeyboardFocusable: cached.IsKeyboardFocusable,
            Patterns: patterns,
            RuntimeId: element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[],
            // The managed client knows no id it has no ControlType for
            ControlTypeId: controlType?.Id);
    }

    private static TreeScope ToTreeScope(UiaScope scope) => scope switch
    {
        UiaScope.Children => TreeScope.Children,
        UiaScope.Descendants => TreeScope.Descendants,
        UiaScope.Subtree => TreeScope.Subtree,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope.")
    };

    /// <summary>The managed condition for a seam condition.</summary>
    private static Condition ToCondition(UiaCondition condition) => condition switch
    {
        UiaTrueCondition => Condition.TrueCondition,
        UiaPropertyCondition property => new PropertyCondition(
            ToProperty(property.Property),
            PropertyValue(property.Property, property.Value),
            property.IgnoreCase ? PropertyConditionFlags.IgnoreCase : PropertyConditionFlags.None),
        UiaAndCondition { Conditions.Count: 1 } single => ToCondition(single.Conditions[0]),
        UiaAndCondition and => new AndCondition(and.Conditions.Select(ToCondition).ToArray()),
        UiaOrCondition { Conditions.Count: 1 } single => ToCondition(single.Conditions[0]),
        UiaOrCondition or => new OrCondition(or.Conditions.Select(ToCondition).ToArray()),
        UiaNotCondition not => new NotCondition(ToCondition(not.Condition)),
        _ => throw new ArgumentException($"Unknown condition {condition.GetType().Name}.", nameof(condition))
    };

    private static AutomationProperty ToProperty(UiaProperty property) => property switch
    {
        UiaProperty.Name => AutomationElement.NameProperty,
        UiaProperty.AutomationId => AutomationElement.AutomationIdProperty,
        UiaProperty.ClassName => AutomationElement.ClassNameProperty,
        UiaProperty.ControlType => AutomationElement.ControlTypeProperty,
        UiaProperty.FrameworkId => AutomationElement.FrameworkIdProperty,
        UiaProperty.ProcessId => AutomationElement.ProcessIdProperty,
        UiaProperty.IsEnabled => AutomationElement.IsEnabledProperty,
        UiaProperty.IsOffscreen => AutomationElement.IsOffscreenProperty,
        UiaProperty.IsKeyboardFocusable => AutomationElement.IsKeyboardFocusableProperty,
        UiaProperty.RuntimeId => AutomationElement.RuntimeIdProperty,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Not a property a condition can compare.")
    };

    /// <summary>
    /// A condition value as the managed client compares it: a control type name becomes its ControlType, a runtime id an
    /// int array.
    /// </summary>
    private static object PropertyValue(UiaProperty property, object value)
    {
        if (property == UiaProperty.RuntimeId)
        {
            // The managed client compares a runtime id given as an int array
            return ((IReadOnlyList<int>)value).ToArray();
        }

        if (property != UiaProperty.ControlType)
        {
            return value;
        }

        var id = UiaControlTypes.GetId((string)value) ??
                 throw new ArgumentException($"'{value}' is not a control type name.", nameof(value));
        return ControlType.LookupById(id) ??
               throw new UiaConditionNotSupportedException(
                   $"The managed UI Automation client (WAM_UIA_ENGINE=uia2) cannot search for {value} elements; the UIA3 engine can.");
    }

    /// <summary>The seam's pattern object for a managed pattern, chosen by the requested type (a TablePattern is also a GridPattern).</summary>
    private static object? WrapPattern(Type requested, object pattern) => requested.Name switch
    {
        nameof(IUiaInvokePattern) => new InvokePatternAdapter((System.Windows.Automation.InvokePattern)pattern),
        nameof(IUiaTogglePattern) => new TogglePatternAdapter((System.Windows.Automation.TogglePattern)pattern),
        nameof(IUiaValuePattern) => new ValuePatternAdapter((System.Windows.Automation.ValuePattern)pattern),
        nameof(IUiaExpandCollapsePattern) => new ExpandCollapsePatternAdapter((System.Windows.Automation.ExpandCollapsePattern)pattern),
        nameof(IUiaSelectionItemPattern) => new SelectionItemPatternAdapter((System.Windows.Automation.SelectionItemPattern)pattern),
        nameof(IUiaScrollItemPattern) => new ScrollItemPatternAdapter((System.Windows.Automation.ScrollItemPattern)pattern),
        nameof(IUiaScrollPattern) => new ScrollPatternAdapter((System.Windows.Automation.ScrollPattern)pattern),
        nameof(IUiaRangeValuePattern) => new RangeValuePatternAdapter((System.Windows.Automation.RangeValuePattern)pattern),
        nameof(IUiaWindowPattern) => new WindowPatternAdapter((System.Windows.Automation.WindowPattern)pattern),
        nameof(IUiaTextPattern) => new TextPatternAdapter((System.Windows.Automation.TextPattern)pattern),
        nameof(IUiaGridPattern) => new GridPatternAdapter((System.Windows.Automation.GridPattern)pattern),
        nameof(IUiaTablePattern) => new TablePatternAdapter((System.Windows.Automation.TablePattern)pattern),
        nameof(IUiaItemContainerPattern) => new ItemContainerPatternAdapter((System.Windows.Automation.ItemContainerPattern)pattern),
        nameof(IUiaVirtualizedItemPattern) => new VirtualizedItemPatternAdapter((System.Windows.Automation.VirtualizedItemPattern)pattern),
        _ => null
    };

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
    /// the seam's exception, so services do not depend on System.Windows.Automation. The managed client passes most
    /// UI Automation HRESULTs on as a <see cref="COMException"/> (UIA_E_NOTSUPPORTED, RPC_E_DISCONNECTED, ...): those are
    /// translated as the UIA3 engine translates them (<see cref="UiaComErrors.Translate"/>), so both engines raise the
    /// same seam exceptions.
    /// </summary>
    public static T Translate<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (ElementNotAvailableException ex)
        {
            throw new UiaElementUnavailableException(ex.Message, ex);
        }
        catch (COMException ex) when (UiaComErrors.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    /// <inheritdoc />
    /// <remarks>The managed client has no AutoSetFocus switch; its pattern actions may move focus as the provider does.</remarks>
    public void SetFocus(IUiaNode node)
    {
        var element = Unwrap(node);
        Translate(element.SetFocus);
    }

    /// <summary>Runs an action of a pattern and reports a vanished element as <see cref="UiaElementUnavailableException"/>.</summary>
    private static void Translate(Action act) => Translate<object?>(() =>
    {
        act();
        return null;
    });

    private sealed class InvokePatternAdapter(System.Windows.Automation.InvokePattern pattern) : IUiaInvokePattern
    {
        public void Invoke() => Translate(pattern.Invoke);
    }

    private sealed class TogglePatternAdapter(System.Windows.Automation.TogglePattern pattern) : IUiaTogglePattern
    {
        public UiaToggleState ToggleState => Translate(() => (UiaToggleState)(int)pattern.Current.ToggleState);

        public void Toggle() => Translate(pattern.Toggle);
    }

    private sealed class ValuePatternAdapter(System.Windows.Automation.ValuePattern pattern) : IUiaValuePattern
    {
        public string? Value => Translate(() => pattern.Current.Value);

        public bool IsReadOnly => Translate(() => pattern.Current.IsReadOnly);

        public void SetValue(string value) => Translate(() => pattern.SetValue(value));
    }

    private sealed class ExpandCollapsePatternAdapter(System.Windows.Automation.ExpandCollapsePattern pattern) : IUiaExpandCollapsePattern
    {
        public UiaExpandCollapseState ExpandCollapseState =>
            Translate(() => (UiaExpandCollapseState)(int)pattern.Current.ExpandCollapseState);

        public void Expand() => Translate(pattern.Expand);

        public void Collapse() => Translate(pattern.Collapse);
    }

    private sealed class SelectionItemPatternAdapter(System.Windows.Automation.SelectionItemPattern pattern) : IUiaSelectionItemPattern
    {
        public bool IsSelected => Translate(() => pattern.Current.IsSelected);

        public void Select() => Translate(pattern.Select);

        public void AddToSelection() => Translate(pattern.AddToSelection);

        public void RemoveFromSelection() => Translate(pattern.RemoveFromSelection);
    }

    private sealed class ScrollItemPatternAdapter(System.Windows.Automation.ScrollItemPattern pattern) : IUiaScrollItemPattern
    {
        public void ScrollIntoView() => Translate(pattern.ScrollIntoView);
    }

    private sealed class ScrollPatternAdapter(System.Windows.Automation.ScrollPattern pattern) : IUiaScrollPattern
    {
        public UiaScrollState State => Translate(() =>
        {
            var current = pattern.Current;
            return new UiaScrollState(
                current.HorizontalScrollPercent,
                current.VerticalScrollPercent,
                current.HorizontalViewSize,
                current.VerticalViewSize,
                current.HorizontallyScrollable,
                current.VerticallyScrollable);
        });

        public void Scroll(UiaScrollAmount horizontal, UiaScrollAmount vertical) =>
            Translate(() => pattern.Scroll((ScrollAmount)(int)horizontal, (ScrollAmount)(int)vertical));

        public void SetScrollPercent(double horizontalPercent, double verticalPercent) =>
            Translate(() => pattern.SetScrollPercent(horizontalPercent, verticalPercent));
    }

    private sealed class RangeValuePatternAdapter(System.Windows.Automation.RangeValuePattern pattern) : IUiaRangeValuePattern
    {
        public UiaRangeValueState State => Translate(() =>
        {
            var current = pattern.Current;
            return new UiaRangeValueState(
                current.Value, current.Minimum, current.Maximum, current.SmallChange, current.LargeChange, current.IsReadOnly);
        });

        public void SetValue(double value) => Translate(() => pattern.SetValue(value));
    }

    private sealed class WindowPatternAdapter(System.Windows.Automation.WindowPattern pattern) : IUiaWindowPattern
    {
        public UiaWindowState State => Translate(() =>
        {
            var current = pattern.Current;
            return new UiaWindowState(
                current.IsModal,
                current.IsTopmost,
                current.CanMaximize,
                current.CanMinimize,
                (UiaWindowVisualState)(int)current.WindowVisualState,
                (UiaWindowInteractionState)(int)current.WindowInteractionState);
        });

        public void SetVisualState(UiaWindowVisualState state) =>
            Translate(() => pattern.SetWindowVisualState((WindowVisualState)(int)state));

        public void Close() => Translate(pattern.Close);

        public bool WaitForInputIdle(int milliseconds) => Translate(() => pattern.WaitForInputIdle(milliseconds));
    }

    private sealed class TextPatternAdapter(System.Windows.Automation.TextPattern pattern) : IUiaTextPattern
    {
        public string GetText(int maxLength) => Translate(() => pattern.DocumentRange.GetText(maxLength) ?? string.Empty);

        public string GetSelectedText(int maxLength) => Translate(() =>
        {
            var text = string.Join("\n", pattern.GetSelection().Select(range => range.GetText(maxLength) ?? string.Empty));
            return maxLength >= 0 && text.Length > maxLength ? text[..maxLength] : text;
        });

        public UiaTextPage GetTextPage(int offset, int maxLength) => Translate(() =>
        {
            // DocumentRange is a new range each time: moving its endpoints changes nothing in the document
            var range = pattern.DocumentRange;
            var start = offset > 0
                ? range.MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
                    System.Windows.Automation.Text.TextUnit.Character, offset)
                : 0;
            range.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End, range,
                System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
            var length = range.MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.End,
                System.Windows.Automation.Text.TextUnit.Character, Math.Max(1, maxLength));
            var text = length > 0 ? range.GetText(-1) ?? string.Empty : string.Empty;

            // A full page: one more character after it tells whether text follows
            var hasMore = length >= maxLength &&
                          range.Clone().MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.End,
                              System.Windows.Automation.Text.TextUnit.Character, 1) > 0;
            return new UiaTextPage(text, start, length, hasMore);
        });
    }

    private sealed class GridPatternAdapter(System.Windows.Automation.GridPattern pattern) : IUiaGridPattern
    {
        public int RowCount => Translate(() => pattern.Current.RowCount);

        public int ColumnCount => Translate(() => pattern.Current.ColumnCount);

        public IUiaNode? GetItem(int row, int column) => Translate(() => (IUiaNode?)Wrap(pattern.GetItem(row, column)));
    }

    private sealed class TablePatternAdapter(System.Windows.Automation.TablePattern pattern) : IUiaTablePattern
    {
        public UiaRowOrColumnMajor RowOrColumnMajor => Translate(() => (UiaRowOrColumnMajor)(int)pattern.Current.RowOrColumnMajor);

        public IReadOnlyList<IUiaNode> GetColumnHeaders() =>
            Translate(() => pattern.Current.GetColumnHeaders().Select(header => (IUiaNode)new Node(header)).ToList());

        public IReadOnlyList<IUiaNode> GetRowHeaders() =>
            Translate(() => pattern.Current.GetRowHeaders().Select(header => (IUiaNode)new Node(header)).ToList());
    }

    private sealed class ItemContainerPatternAdapter(System.Windows.Automation.ItemContainerPattern pattern) : IUiaItemContainerPattern
    {
        public IUiaNode? FindItemByProperty(IUiaNode? startAfter, UiaProperty? property, object? value)
        {
            var start = startAfter == null ? null : Unwrap(startAfter);
            var automationProperty = property is { } p ? ToProperty(p) : null;
            var compared = property is { } q && value != null ? PropertyValue(q, value) : value;
            return Translate(() => (IUiaNode?)Wrap(pattern.FindItemByProperty(start, automationProperty, compared)));
        }
    }

    private sealed class VirtualizedItemPatternAdapter(System.Windows.Automation.VirtualizedItemPattern pattern) : IUiaVirtualizedItemPattern
    {
        public void Realize() => Translate(pattern.Realize);
    }

    private sealed class Node(AutomationElement element) : IUiaNode
    {
        public AutomationElement Element { get; } = element;
    }
}
