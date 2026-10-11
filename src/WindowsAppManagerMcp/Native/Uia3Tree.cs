using Interop.UIAutomationClient;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// <see cref="IUiaTree"/> over the UI Automation COM client (IUIAutomation, "UIA3"), the default engine (roadmap M3.2).
/// </summary>
/// <remarks>
/// <para>What it adds over the managed client (<see cref="UiaTree"/>):</para>
/// <list type="bullet">
/// <item>Hard per-call deadlines: IUIAutomation2 ConnectionTimeout (<see cref="ConnectionTimeoutMs"/>) and
/// TransactionTimeout (<see cref="TransactionTimeoutMs"/>). A provider that does not answer makes the call fail with a
/// <see cref="TimeoutException"/> instead of blocking it for as long as the provider takes, so a hung application
/// costs a worker thread for seconds, not until it recovers.</item>
/// <item>Raw control type ids: 50039 (SemanticZoom) and 50040 (AppBar) are named from the id, in every display
/// language, and conditions can compare them.</item>
/// <item>Cached reads: <see cref="Read"/>, <see cref="ReadIdentity"/> and the interaction reads fetch all their
/// properties in one call (BuildUpdatedCache), and <see cref="GetChildElements"/> a whole level with its properties
/// (FindAllBuildCache), instead of one call per property.</item>
/// <item>LegacyIAccessible (the MSAA bridge, DoDefaultAction).</item>
/// </list>
/// <para>The client object is created on first use, behind a lock: the first UI Automation call of a process
/// failed with E_FAIL in the managed client when threads made it at once (<see cref="UiaClientStartup"/>), so
/// creation is serialized here too. Its objects are free-threaded, so the walk threads of
/// <see cref="Services.UiaWorker"/> (all MTA) share them.</para>
/// <para>The search view is the control view: the cache requests' TreeFilter and the find condition are both
/// the client's ControlViewCondition, as the managed client's default cache request uses.</para>
/// <para>An element without a bounding rectangle is reported by the COM client as (0, 0, 0, 0); it becomes
/// <see cref="UiaRect.Empty"/>, so it has no bounds in either engine.</para>
/// </remarks>
public sealed class Uia3Tree : IUiaTree
{
    /// <summary>How long the client waits to connect to a provider (IUIAutomation2.ConnectionTimeout).</summary>
    public const uint ConnectionTimeoutMs = 1000;

    /// <summary>How long one call may wait for a provider to answer (IUIAutomation2.TransactionTimeout).</summary>
    public const uint TransactionTimeoutMs = 5000;

    private static readonly object ComCreationLock = new();

    private readonly Func<IUIAutomation2> _createClient;
    private readonly object _startLock = new();
    private volatile Client? _client;

    /// <summary>The production adapter: creates the CUIAutomation8 client on first use.</summary>
    public Uia3Tree()
        : this(CreateComClient)
    {
    }

    /// <summary>Creates the adapter over the client <paramref name="createClient"/> returns (tests pass a mock).</summary>
    public Uia3Tree(Func<IUIAutomation2> createClient)
    {
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
    }

    /// <inheritdoc />
    public UiaEngine Engine => UiaEngine.Uia3;

    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            return Started().Root != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public IUiaNode? FromHandle(nint windowHandle)
    {
        var client = Started();
        try
        {
            return UiaComErrors.Run(() => Wrap(client.Automation.ElementFromHandle(windowHandle)));
        }
        catch (Exception ex) when (ex is UiaElementUnavailableException or ArgumentException)
        {
            // Not a window (E_INVALIDARG), or it closed
            return null;
        }
    }

    /// <inheritdoc />
    public IUiaNode? FromPoint(int x, int y)
    {
        var client = Started();
        return UiaComErrors.Run(() => Wrap(client.Automation.ElementFromPoint(new tagPOINT { x = x, y = y })));
    }

    /// <inheritdoc />
    public IUiaNode? GetFocusedElement()
    {
        var client = Started();
        return UiaComErrors.Run(() => Wrap(client.Automation.GetFocusedElement()));
    }

    /// <inheritdoc />
    public IUiaNode? GetParent(IUiaNode node)
    {
        var element = Unwrap(node);
        var client = Started();
        return UiaComErrors.Run(() =>
        {
            var parent = client.ControlViewWalker.GetParentElement(element);
            return parent == null || client.Automation.CompareElements(parent, client.Root) != 0 ? null : Wrap(parent);
        });
    }

    /// <inheritdoc />
    public IReadOnlyList<IUiaNode> GetChildren(IUiaNode node, CancellationToken cancellationToken)
    {
        var element = Unwrap(node);
        var client = Started();
        cancellationToken.ThrowIfCancellationRequested();

        // One call for all children, so a child that disappears meanwhile cannot end the list early
        return UiaComErrors.Run(() => Nodes(element.FindAll(TreeScope.TreeScope_Children, client.ControlView)));
    }

    /// <inheritdoc />
    public UiaNodeProperties Read(IUiaNode node)
    {
        var element = Unwrap(node);
        var client = Started();
        return UiaComErrors.Run(() => ToElement(element.BuildUpdatedCache(client.WalkRequest)).Properties);
    }

    /// <inheritdoc />
    public UiaNodeInteraction ReadInteraction(IUiaNode node)
    {
        var element = Unwrap(node);
        var client = Started();
        return UiaComErrors.Run(() =>
        {
            var cached = element.BuildUpdatedCache(client.InteractionRequest);
            return new UiaNodeInteraction(
                IsKeyboardFocusable: CachedBool(cached, UiaIds.IsKeyboardFocusableProperty),
                HasKeyboardFocus: CachedBool(cached, UiaIds.HasKeyboardFocusProperty),
                ActionPatterns: UiaPatternNames.Actions(CachedPatterns(cached)));
        });
    }

    /// <inheritdoc />
    public bool IsInteractable(IUiaNode node)
    {
        var element = Unwrap(node);
        var client = Started();

        // One call reads focusability and every action pattern's availability
        return UiaComErrors.Run(() =>
        {
            var cached = element.BuildUpdatedCache(client.InteractionRequest);
            return CachedBool(cached, UiaIds.IsKeyboardFocusableProperty) ||
                   (CachedPatterns(cached) & UiaPatterns.Actions) != 0;
        });
    }

    /// <inheritdoc />
    public UiaNodeIdentity ReadIdentity(IUiaNode node)
    {
        var element = Unwrap(node);
        var client = Started();
        return UiaComErrors.Run(() =>
        {
            var cached = element.BuildUpdatedCache(client.IdentityRequest);
            return new UiaNodeIdentity(
                ClassName: CachedString(cached, UiaIds.ClassNameProperty),
                FrameworkId: CachedString(cached, UiaIds.FrameworkIdProperty),
                ProcessId: CachedInt(cached, UiaIds.ProcessIdProperty) ?? 0,
                NativeWindowHandle: CachedInt(cached, UiaIds.NativeWindowHandleProperty) ?? 0,
                IsPassword: CachedBool(cached, UiaIds.IsPasswordProperty));
        });
    }

    /// <inheritdoc />
    public string? ReadValue(IUiaNode node) => GetPattern<IUiaValuePattern>(node)?.Value;

    /// <inheritdoc />
    public bool? ReadIsModal(IUiaNode node)
    {
        var element = Unwrap(node);
        Started();

        // Only IsModal: the pattern's other properties are further calls, any of which a provider can fail
        return UiaComErrors.Run<bool?>(() =>
            element.GetCurrentPattern(UiaIds.WindowPattern) is IUIAutomationWindowPattern window
                ? window.CurrentIsModal != 0
                : null);
    }

    /// <inheritdoc />
    public IReadOnlyList<UiaElement> GetChildElements(IUiaNode node, CancellationToken cancellationToken)
    {
        var element = Unwrap(node);
        var client = Started();
        cancellationToken.ThrowIfCancellationRequested();
        return UiaComErrors.Run(() =>
            Elements(element.FindAllBuildCache(TreeScope.TreeScope_Children, client.ControlView, client.WalkRequest)));
    }

    /// <inheritdoc />
    public IReadOnlyList<UiaElement> FindAll(IUiaNode root, UiaScope scope, UiaCondition condition, CancellationToken cancellationToken)
    {
        var element = Unwrap(root);
        ArgumentNullException.ThrowIfNull(condition);
        var client = Started();
        cancellationToken.ThrowIfCancellationRequested();
        return UiaComErrors.Run(() =>
        {
            var search = client.Automation.CreateAndCondition(client.ControlView, ToCondition(client.Automation, condition));
            return Elements(element.FindAllBuildCache(ToTreeScope(scope), search, client.WalkRequest));
        });
    }

    /// <inheritdoc />
    public UiaElementTree ReadSubtree(IUiaNode root, int maxElements)
    {
        var element = Unwrap(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxElements, 1);
        var client = Started();
        return UiaComErrors.Run(() =>
        {
            var cachedRoot = element.BuildUpdatedCache(client.SubtreeRequest);

            // Breadth first, so a cut keeps the levels nearest the root
            var rootChildren = new List<UiaElementTree>();
            var tree = new UiaElementTree(ToElement(cachedRoot), rootChildren);
            var count = 1;
            var pending = new Queue<(IUIAutomationElement Element, List<UiaElementTree> Children)>();
            pending.Enqueue((cachedRoot, rootChildren));
            while (pending.Count > 0 && count < maxElements)
            {
                var (parent, children) = pending.Dequeue();
                var cachedChildren = parent.GetCachedChildren();
                var length = cachedChildren?.Length ?? 0;
                for (var i = 0; i < length && count < maxElements; i++)
                {
                    var child = cachedChildren!.GetElement(i);
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
        Started();
        return UiaComErrors.Run(() => element.GetRuntimeId());
    }

    /// <inheritdoc />
    public bool AreSameElement(IUiaNode first, IUiaNode second)
    {
        var a = Unwrap(first);
        var b = Unwrap(second);
        var client = Started();
        return UiaComErrors.Run(() => client.Automation.CompareElements(a, b) != 0);
    }

    /// <inheritdoc />
    public TPattern? GetPattern<TPattern>(IUiaNode node) where TPattern : class, IUiaPattern
    {
        var element = Unwrap(node);
        Started();
        if (UiaIds.PatternId(typeof(TPattern)) is not { } patternId)
        {
            return null;
        }

        var pattern = UiaComErrors.Run(() => element.GetCurrentPattern(patternId));
        return pattern == null ? null : WrapPattern(typeof(TPattern), pattern) as TPattern;
    }

    /// <summary>
    /// Creates the CUIAutomation8 client, one thread at a time, in the multithreaded apartment: the worker threads that
    /// use it are MTA threads, and a client created on an STA thread would belong to that thread's apartment.
    /// </summary>
    private static IUIAutomation2 CreateComClient()
    {
        lock (ComCreationLock)
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            {
                return (IUIAutomation2)new CUIAutomation8Class();
            }

            IUIAutomation2? client = null;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    client = (IUIAutomation2)new CUIAutomation8Class();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            })
            {
                IsBackground = true,
                Name = "UI Automation client"
            };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();
            return client ?? throw error ?? new InvalidOperationException("The UI Automation client was not created.");
        }
    }

    /// <summary>The client, created, configured and given its first call on first use.</summary>
    private Client Started()
    {
        if (_client is { } started)
        {
            return started;
        }

        lock (_startLock)
        {
            if (_client == null)
            {
                var automation = _createClient();
                automation.ConnectionTimeout = ConnectionTimeoutMs;
                automation.TransactionTimeout = TransactionTimeoutMs;
                _client = UiaComErrors.Run(() => new Client(automation));
            }

            return _client;
        }
    }

    private static Node? Wrap(IUIAutomationElement? element) => element == null ? null : new Node(element);

    private static IUIAutomationElement Unwrap(IUiaNode node) =>
        node is Node own
            ? own.Element
            : throw new ArgumentException($"The node was not created by {nameof(Uia3Tree)}.", nameof(node));

    private static List<IUiaNode> Nodes(IUIAutomationElementArray? array)
    {
        var nodes = new List<IUiaNode>(array?.Length ?? 0);
        for (var i = 0; i < (array?.Length ?? 0); i++)
        {
            nodes.Add(new Node(array!.GetElement(i)));
        }

        return nodes;
    }

    private static List<UiaElement> Elements(IUIAutomationElementArray? array)
    {
        var elements = new List<UiaElement>(array?.Length ?? 0);
        for (var i = 0; i < (array?.Length ?? 0); i++)
        {
            elements.Add(ToElement(array!.GetElement(i)));
        }

        return elements;
    }

    /// <summary>An element whose walk properties were cached by the call that returned it.</summary>
    internal static UiaElement ToElement(IUIAutomationElement element)
    {
        var controlTypeId = CachedInt(element, UiaIds.ControlTypeProperty);
        var localized = CachedString(element, UiaIds.LocalizedControlTypeProperty);
        var controlType = controlTypeId is { } id
            ? UiaControlTypes.GetName(id, localized)
            : UiaControlTypes.GetName(null, localized);

        var properties = new UiaNodeProperties(
            Name: CachedString(element, UiaIds.NameProperty),
            ControlType: controlType,
            AutomationId: CachedString(element, UiaIds.AutomationIdProperty),
            Bounds: ToRect(element.GetCachedPropertyValue(UiaIds.BoundingRectangleProperty)),
            IsEnabled: CachedBool(element, UiaIds.IsEnabledProperty),
            IsOffscreen: CachedBool(element, UiaIds.IsOffscreenProperty));

        return new UiaElement(
            new Node(element),
            properties,
            ClassName: CachedString(element, UiaIds.ClassNameProperty),
            IsKeyboardFocusable: CachedBool(element, UiaIds.IsKeyboardFocusableProperty),
            Patterns: CachedPatterns(element),
            RuntimeId: element.GetCachedPropertyValue(UiaIds.RuntimeIdProperty) as int[],
            ControlTypeId: controlTypeId);
    }

    /// <summary>
    /// A BoundingRectangle value: four doubles (left, top, width, height). The COM client reports (0, 0, 0, 0) for an
    /// element without a rectangle, and a value it could not read as the "not supported" object; both become
    /// <see cref="UiaRect.Empty"/>.
    /// </summary>
    internal static UiaRect ToRect(object? value) =>
        value is double[] { Length: 4 } r && !(r[0] == 0 && r[1] == 0 && r[2] == 0 && r[3] == 0)
            ? new UiaRect(r[0], r[1], r[2], r[3])
            : UiaRect.Empty;

    private static UiaPatterns CachedPatterns(IUIAutomationElement element)
    {
        var patterns = UiaPatterns.None;
        foreach (var (property, pattern) in UiaIds.PatternAvailability)
        {
            if (element.GetCachedPropertyValue(property) is true)
            {
                patterns |= pattern;
            }
        }

        return patterns;
    }

    // A property the provider does not support reads as the client's "not supported" object, which none of these match
    private static string? CachedString(IUIAutomationElement element, int property) =>
        element.GetCachedPropertyValue(property) as string;

    private static bool CachedBool(IUIAutomationElement element, int property) =>
        element.GetCachedPropertyValue(property) is true;

    private static int? CachedInt(IUIAutomationElement element, int property) =>
        element.GetCachedPropertyValue(property) is int value ? value : null;

    private static TreeScope ToTreeScope(UiaScope scope) => scope switch
    {
        UiaScope.Children => TreeScope.TreeScope_Children,
        UiaScope.Descendants => TreeScope.TreeScope_Descendants,
        UiaScope.Subtree => TreeScope.TreeScope_Subtree,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope.")
    };

    /// <summary>The COM condition for a seam condition.</summary>
    internal static IUIAutomationCondition ToCondition(IUIAutomation2 automation, UiaCondition condition) => condition switch
    {
        UiaTrueCondition => automation.CreateTrueCondition(),
        UiaPropertyCondition property => property.IgnoreCase
            ? automation.CreatePropertyConditionEx(
                UiaIds.PropertyId(property.Property), PropertyValue(property.Property, property.Value),
                PropertyConditionFlags.PropertyConditionFlags_IgnoreCase)
            : automation.CreatePropertyCondition(UiaIds.PropertyId(property.Property), PropertyValue(property.Property, property.Value)),
        UiaAndCondition and => and.Conditions.Select(c => ToCondition(automation, c)).Aggregate(automation.CreateAndCondition),
        UiaOrCondition or => or.Conditions.Select(c => ToCondition(automation, c)).Aggregate(automation.CreateOrCondition),
        UiaNotCondition not => automation.CreateNotCondition(ToCondition(automation, not.Condition)),
        _ => throw new ArgumentException($"Unknown condition {condition.GetType().Name}.", nameof(condition))
    };

    /// <summary>A condition value as the COM client compares it: a control type name becomes its id.</summary>
    private static object PropertyValue(UiaProperty property, object value) =>
        property == UiaProperty.ControlType
            ? UiaControlTypes.GetId((string)value) ??
              throw new ArgumentException($"'{value}' is not a control type name.", nameof(value))
            : value;

    /// <summary>
    /// The wrapper of the pattern <paramref name="type"/> asks for, over the COM pattern object, or null when the object
    /// is not of that pattern. Chosen by the requested type, not by what the object answers QueryInterface for: a table
    /// pattern object is often a grid pattern object too.
    /// </summary>
    private static IUiaPattern? WrapPattern(Type type, object pattern) => type switch
    {
        _ when type == typeof(IUiaInvokePattern) => pattern is IUIAutomationInvokePattern p ? new InvokePattern(p) : null,
        _ when type == typeof(IUiaTogglePattern) => pattern is IUIAutomationTogglePattern p ? new TogglePattern(p) : null,
        _ when type == typeof(IUiaValuePattern) => pattern is IUIAutomationValuePattern p ? new ValuePattern(p) : null,
        _ when type == typeof(IUiaExpandCollapsePattern) =>
            pattern is IUIAutomationExpandCollapsePattern p ? new ExpandCollapsePattern(p) : null,
        _ when type == typeof(IUiaSelectionItemPattern) =>
            pattern is IUIAutomationSelectionItemPattern p ? new SelectionItemPattern(p) : null,
        _ when type == typeof(IUiaScrollItemPattern) => pattern is IUIAutomationScrollItemPattern p ? new ScrollItemPattern(p) : null,
        _ when type == typeof(IUiaScrollPattern) => pattern is IUIAutomationScrollPattern p ? new ScrollPattern(p) : null,
        _ when type == typeof(IUiaRangeValuePattern) => pattern is IUIAutomationRangeValuePattern p ? new RangeValuePattern(p) : null,
        _ when type == typeof(IUiaWindowPattern) => pattern is IUIAutomationWindowPattern p ? new WindowPattern(p) : null,
        _ when type == typeof(IUiaTextPattern) => pattern is IUIAutomationTextPattern p ? new TextPattern(p) : null,
        _ when type == typeof(IUiaGridPattern) => pattern is IUIAutomationGridPattern p ? new GridPattern(p) : null,
        _ when type == typeof(IUiaTablePattern) => pattern is IUIAutomationTablePattern p ? new TablePattern(p) : null,
        _ when type == typeof(IUiaItemContainerPattern) =>
            pattern is IUIAutomationItemContainerPattern p ? new ItemContainerPattern(p) : null,
        _ when type == typeof(IUiaVirtualizedItemPattern) =>
            pattern is IUIAutomationVirtualizedItemPattern p ? new VirtualizedItemPattern(p) : null,
        _ when type == typeof(IUiaLegacyIAccessiblePattern) =>
            pattern is IUIAutomationLegacyIAccessiblePattern p ? new LegacyIAccessiblePattern(p) : null,
        _ => null
    };

    /// <summary>The created client and the objects every call shares.</summary>
    private sealed class Client
    {
        public Client(IUIAutomation2 automation)
        {
            Automation = automation;

            // The first call: if UI Automation cannot be used, creating the client fails here
            Root = automation.GetRootElement();
            ControlView = automation.ControlViewCondition;
            ControlViewWalker = automation.ControlViewWalker;

            int[] walkProperties =
            [
                UiaIds.NameProperty, UiaIds.ControlTypeProperty, UiaIds.LocalizedControlTypeProperty,
                UiaIds.AutomationIdProperty, UiaIds.ClassNameProperty, UiaIds.BoundingRectangleProperty,
                UiaIds.IsEnabledProperty, UiaIds.IsOffscreenProperty, UiaIds.IsKeyboardFocusableProperty,
                UiaIds.RuntimeIdProperty, .. UiaIds.PatternAvailability.Select(p => p.Property)
            ];
            WalkRequest = Request(walkProperties);
            SubtreeRequest = Request(walkProperties);
            SubtreeRequest.TreeScope = TreeScope.TreeScope_Subtree;
            InteractionRequest = Request(
            [
                UiaIds.IsKeyboardFocusableProperty, UiaIds.HasKeyboardFocusProperty,
                .. UiaIds.PatternAvailability.Select(p => p.Property)
            ]);
            IdentityRequest = Request(
            [
                UiaIds.ClassNameProperty, UiaIds.FrameworkIdProperty, UiaIds.ProcessIdProperty,
                UiaIds.NativeWindowHandleProperty, UiaIds.IsPasswordProperty
            ]);
        }

        public IUIAutomation2 Automation { get; }

        public IUIAutomationElement Root { get; }

        public IUIAutomationCondition ControlView { get; }

        public IUIAutomationTreeWalker ControlViewWalker { get; }

        /// <summary>The walk's properties (UiaElement) of the element itself.</summary>
        public IUIAutomationCacheRequest WalkRequest { get; }

        /// <summary>The walk's properties of the element and its whole subtree.</summary>
        public IUIAutomationCacheRequest SubtreeRequest { get; }

        /// <summary>Focusability, focus and the pattern availability.</summary>
        public IUIAutomationCacheRequest InteractionRequest { get; }

        /// <summary>Class, framework, process, window handle and the password flag.</summary>
        public IUIAutomationCacheRequest IdentityRequest { get; }

        private IUIAutomationCacheRequest Request(IEnumerable<int> properties)
        {
            var request = Automation.CreateCacheRequest();
            foreach (var property in properties)
            {
                request.AddProperty(property);
            }

            request.TreeScope = TreeScope.TreeScope_Element;
            request.TreeFilter = ControlView;
            request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
            return request;
        }
    }

    private sealed class Node(IUIAutomationElement element) : IUiaNode
    {
        public IUIAutomationElement Element { get; } = element;
    }

    private sealed class InvokePattern(IUIAutomationInvokePattern pattern) : IUiaInvokePattern
    {
        public void Invoke() => UiaComErrors.Run(pattern.Invoke);
    }

    private sealed class TogglePattern(IUIAutomationTogglePattern pattern) : IUiaTogglePattern
    {
        public UiaToggleState ToggleState => UiaComErrors.Run(() => (UiaToggleState)(int)pattern.CurrentToggleState);

        public void Toggle() => UiaComErrors.Run(pattern.Toggle);
    }

    private sealed class ValuePattern(IUIAutomationValuePattern pattern) : IUiaValuePattern
    {
        public string? Value => UiaComErrors.Run(() => pattern.CurrentValue);

        public bool IsReadOnly => UiaComErrors.Run(() => pattern.CurrentIsReadOnly != 0);

        public void SetValue(string value) => UiaComErrors.Run(() => pattern.SetValue(value));
    }

    private sealed class ExpandCollapsePattern(IUIAutomationExpandCollapsePattern pattern) : IUiaExpandCollapsePattern
    {
        public UiaExpandCollapseState ExpandCollapseState =>
            UiaComErrors.Run(() => (UiaExpandCollapseState)(int)pattern.CurrentExpandCollapseState);

        public void Expand() => UiaComErrors.Run(pattern.Expand);

        public void Collapse() => UiaComErrors.Run(pattern.Collapse);
    }

    private sealed class SelectionItemPattern(IUIAutomationSelectionItemPattern pattern) : IUiaSelectionItemPattern
    {
        public bool IsSelected => UiaComErrors.Run(() => pattern.CurrentIsSelected != 0);

        public void Select() => UiaComErrors.Run(pattern.Select);

        public void AddToSelection() => UiaComErrors.Run(pattern.AddToSelection);

        public void RemoveFromSelection() => UiaComErrors.Run(pattern.RemoveFromSelection);
    }

    private sealed class ScrollItemPattern(IUIAutomationScrollItemPattern pattern) : IUiaScrollItemPattern
    {
        public void ScrollIntoView() => UiaComErrors.Run(pattern.ScrollIntoView);
    }

    private sealed class ScrollPattern(IUIAutomationScrollPattern pattern) : IUiaScrollPattern
    {
        public UiaScrollState State => UiaComErrors.Run(() => new UiaScrollState(
            pattern.CurrentHorizontalScrollPercent,
            pattern.CurrentVerticalScrollPercent,
            pattern.CurrentHorizontalViewSize,
            pattern.CurrentVerticalViewSize,
            pattern.CurrentHorizontallyScrollable != 0,
            pattern.CurrentVerticallyScrollable != 0));

        public void Scroll(UiaScrollAmount horizontal, UiaScrollAmount vertical) =>
            UiaComErrors.Run(() => pattern.Scroll((ScrollAmount)(int)horizontal, (ScrollAmount)(int)vertical));

        public void SetScrollPercent(double horizontalPercent, double verticalPercent) =>
            UiaComErrors.Run(() => pattern.SetScrollPercent(horizontalPercent, verticalPercent));
    }

    private sealed class RangeValuePattern(IUIAutomationRangeValuePattern pattern) : IUiaRangeValuePattern
    {
        public UiaRangeValueState State => UiaComErrors.Run(() => new UiaRangeValueState(
            pattern.CurrentValue,
            pattern.CurrentMinimum,
            pattern.CurrentMaximum,
            pattern.CurrentSmallChange,
            pattern.CurrentLargeChange,
            pattern.CurrentIsReadOnly != 0));

        public void SetValue(double value) => UiaComErrors.Run(() => pattern.SetValue(value));
    }

    private sealed class WindowPattern(IUIAutomationWindowPattern pattern) : IUiaWindowPattern
    {
        public UiaWindowState State => UiaComErrors.Run(() => new UiaWindowState(
            pattern.CurrentIsModal != 0,
            pattern.CurrentIsTopmost != 0,
            pattern.CurrentCanMaximize != 0,
            pattern.CurrentCanMinimize != 0,
            (UiaWindowVisualState)(int)pattern.CurrentWindowVisualState,
            (UiaWindowInteractionState)(int)pattern.CurrentWindowInteractionState));

        public void SetVisualState(UiaWindowVisualState state) =>
            UiaComErrors.Run(() => pattern.SetWindowVisualState((WindowVisualState)(int)state));

        public void Close() => UiaComErrors.Run(pattern.Close);

        public bool WaitForInputIdle(int milliseconds) => UiaComErrors.Run(() => pattern.WaitForInputIdle(milliseconds) != 0);
    }

    private sealed class TextPattern(IUIAutomationTextPattern pattern) : IUiaTextPattern
    {
        public string GetText(int maxLength) => UiaComErrors.Run(() => pattern.DocumentRange.GetText(maxLength) ?? string.Empty);

        public string GetSelectedText(int maxLength) => UiaComErrors.Run(() =>
        {
            var ranges = pattern.GetSelection();
            var texts = new List<string>();
            for (var i = 0; i < (ranges?.Length ?? 0); i++)
            {
                texts.Add(ranges!.GetElement(i).GetText(maxLength) ?? string.Empty);
            }

            var text = string.Join("\n", texts);
            return maxLength >= 0 && text.Length > maxLength ? text[..maxLength] : text;
        });
    }

    private sealed class GridPattern(IUIAutomationGridPattern pattern) : IUiaGridPattern
    {
        public int RowCount => UiaComErrors.Run(() => pattern.CurrentRowCount);

        public int ColumnCount => UiaComErrors.Run(() => pattern.CurrentColumnCount);

        public IUiaNode? GetItem(int row, int column) => UiaComErrors.Run(() => Wrap(pattern.GetItem(row, column)));
    }

    private sealed class TablePattern(IUIAutomationTablePattern pattern) : IUiaTablePattern
    {
        public UiaRowOrColumnMajor RowOrColumnMajor =>
            UiaComErrors.Run(() => (UiaRowOrColumnMajor)(int)pattern.CurrentRowOrColumnMajor);

        public IReadOnlyList<IUiaNode> GetColumnHeaders() => UiaComErrors.Run(() => Nodes(pattern.GetCurrentColumnHeaders()));

        public IReadOnlyList<IUiaNode> GetRowHeaders() => UiaComErrors.Run(() => Nodes(pattern.GetCurrentRowHeaders()));
    }

    private sealed class ItemContainerPattern(IUIAutomationItemContainerPattern pattern) : IUiaItemContainerPattern
    {
        public IUiaNode? FindItemByProperty(IUiaNode? startAfter, UiaProperty? property, object? value)
        {
            var start = startAfter == null ? null : Unwrap(startAfter);
            var propertyId = property is { } p ? UiaIds.PropertyId(p) : 0;
            var compared = property is { } q && value != null ? PropertyValue(q, value) : value;
            return UiaComErrors.Run(() => Wrap(pattern.FindItemByProperty(start!, propertyId, compared!)));
        }
    }

    private sealed class VirtualizedItemPattern(IUIAutomationVirtualizedItemPattern pattern) : IUiaVirtualizedItemPattern
    {
        public void Realize() => UiaComErrors.Run(pattern.Realize);
    }

    private sealed class LegacyIAccessiblePattern(IUIAutomationLegacyIAccessiblePattern pattern) : IUiaLegacyIAccessiblePattern
    {
        public UiaLegacyAccessibleState State => UiaComErrors.Run(() => new UiaLegacyAccessibleState(
            pattern.CurrentName,
            pattern.CurrentValue,
            pattern.CurrentDescription,
            pattern.CurrentDefaultAction,
            pattern.CurrentRole,
            pattern.CurrentState));

        public void DoDefaultAction() => UiaComErrors.Run(pattern.DoDefaultAction);
    }
}
