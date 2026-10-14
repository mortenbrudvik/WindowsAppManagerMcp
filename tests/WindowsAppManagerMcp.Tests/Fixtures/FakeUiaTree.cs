using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An in-memory <see cref="IUiaTree"/> for unit tests: no real window or UI Automation provider is
/// read. Build a tree from <see cref="FakeUiaNode"/>s, register windows by handle, and inject
/// failures, delays and call hooks per node.
/// </summary>
public sealed class FakeUiaTree : IUiaTree
{
    private readonly ConcurrentDictionary<nint, FakeUiaNode> _windows = new();
    private readonly ConcurrentQueue<string> _reads = new();
    private readonly ConcurrentQueue<string> _cachedFetches = new();
    private readonly ConcurrentQueue<string> _childListings = new();
    private readonly ConcurrentQueue<string> _childProbes = new();
    private readonly ConcurrentQueue<string> _finds = new();
    private readonly ConcurrentDictionary<int, FakeUiaHandler> _handlers = new();
    private readonly ConcurrentQueue<(FakeUiaHandler, int)> _removedHandlers = new();
    private int _nextHandler;

    /// <summary>The engine the fake claims to be.</summary>
    public UiaEngine Engine { get; set; } = UiaEngine.Uia3;

    /// <summary>What <see cref="IsAvailable"/> returns.</summary>
    public bool Available { get; set; } = true;

    /// <summary>What <see cref="GetFocusedElement"/> returns.</summary>
    public FakeUiaNode? Focused { get; set; }

    /// <summary>Thrown by <see cref="GetFocusedElement"/> when set.</summary>
    public Exception? FocusedException { get; set; }

    /// <summary>What <see cref="FromPoint"/> returns for a point; null by default.</summary>
    public Func<int, int, FakeUiaNode?> AtPoint { get; set; } = (_, _) => null;

    /// <summary>Runs when any search starts, with its root and condition (it can throw, as a provider would).</summary>
    public Action<FakeUiaNode, UiaCondition>? OnFindAll { get; set; }

    /// <summary>
    /// How the fake's provider tests a substring condition (<see cref="UiaCondition.PropertyContains"/>, roadmap M3.F12), or
    /// null to test it as UI Automation documents PropertyConditionFlags_MatchSubstring: the text anywhere in the property,
    /// ignoring case when the condition asks. A test sets it for a provider that ignores the flag or misapplies it.
    /// </summary>
    public Func<FakeUiaNode, UiaPropertyContainsCondition, bool>? SubstringTest { get; set; }

    /// <summary>
    /// When true, a search whose condition has a substring in it throws <see cref="UiaConditionNotSupportedException"/>
    /// (after <see cref="OnFindAll"/>), as the UIA3 engine does where Windows refuses PropertyConditionFlags_MatchSubstring
    /// (before Windows 10 1809) and the managed engine always does.
    /// </summary>
    public bool RefusesSubstrings { get; set; }

    /// <summary>Thrown by <see cref="FromHandle"/> when set.</summary>
    public Exception? FromHandleException { get; set; }

    /// <summary>Every property read, as "Read:Name", "Interaction:Name", "Interactable:Name", ... in call order.</summary>
    public IReadOnlyList<string> Reads => _reads.ToList();

    /// <summary>The names of the nodes whose basic properties were read.</summary>
    public IReadOnlyList<string> ReadNodes => Reads.Where(r => r.StartsWith("Read:")).Select(r => r[5..]).ToList();

    /// <summary>Registers <paramref name="window"/> as the node of <paramref name="handle"/>.</summary>
    public FakeUiaTree WithWindow(nint handle, FakeUiaNode window)
    {
        _windows[handle] = window;
        return this;
    }

    public bool IsAvailable() => Available;

    /// <summary>Thrown by <see cref="FromHandle"/> for one window when it returns an exception for that handle.</summary>
    public Func<nint, Exception?>? FromHandleFailure { get; set; }

    public IUiaNode? FromHandle(nint windowHandle)
    {
        if ((FromHandleException ?? FromHandleFailure?.Invoke(windowHandle)) is { } failure)
        {
            throw failure;
        }

        return _windows.TryGetValue(windowHandle, out var window) ? window : null;
    }

    public IUiaNode? FromPoint(int x, int y) => AtPoint(x, y);

    public IUiaNode? GetFocusedElement()
    {
        if (FocusedException != null)
        {
            throw FocusedException;
        }

        return Focused;
    }

    public IUiaNode? GetParent(IUiaNode node)
    {
        var fake = Fake(node);
        ThrowIfSet(fake.ParentException);
        return fake.Parent;
    }

    public IReadOnlyList<IUiaNode> GetChildren(IUiaNode node, CancellationToken cancellationToken)
    {
        var fake = Fake(node);
        _childListings.Enqueue(fake.Name ?? fake.ControlType);
        fake.OnGetChildren?.Invoke();
        ThrowIfSet(fake.ChildrenException);
        return fake.Children.ToList();
    }

    /// <summary>Whether the node has a child: logged in <see cref="ChildProbes"/>; fails like <see cref="GetChildren"/>.</summary>
    public bool HasChildren(IUiaNode node)
    {
        var fake = Fake(node);
        _childProbes.Enqueue(fake.Name ?? fake.ControlType);
        ThrowIfSet(fake.ChildrenException);
        return fake.Children.Count > 0;
    }

    public UiaNodeProperties Read(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Read:{fake.Name}");
        fake.OnRead?.Invoke();
        ThrowIfSet(fake.ReadException);
        ThrowIfUnrealized(fake);
        return new UiaNodeProperties(fake.Name, fake.ControlType, fake.AutomationId, fake.Bounds, fake.IsEnabled, fake.IsOffscreen, fake.FrameworkId);
    }

    public UiaNodeInteraction ReadInteraction(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Interaction:{fake.Name}");
        ThrowIfSet(fake.InteractionException);
        ThrowIfUnrealized(fake);
        return new UiaNodeInteraction(fake.IsKeyboardFocusable, fake.HasKeyboardFocus, fake.ActionPatterns);
    }

    public bool IsInteractable(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Interactable:{fake.Name}");
        ThrowIfSet(fake.InteractionException);
        ThrowIfUnrealized(fake);
        return fake.IsKeyboardFocusable || fake.ActionPatterns.Count > 0;
    }

    public UiaNodeIdentity ReadIdentity(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Identity:{fake.Name}");
        ThrowIfSet(fake.IdentityException);
        ThrowIfUnrealized(fake);
        return new UiaNodeIdentity(fake.ClassName, fake.FrameworkId, fake.ProcessId, fake.NativeWindowHandle, fake.IsPassword);
    }

    /// <summary>A fresh read of the walk's properties: logged as "Element:Name"; fails like <see cref="Read"/>.</summary>
    public UiaElement ReadElement(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Element:{fake.Name}");
        fake.OnRead?.Invoke();
        ThrowIfSet(fake.ReadException);
        ThrowIfUnrealized(fake);
        return Cached(fake);
    }

    /// <summary>
    /// The property-by-property read of an item its list has not realized: logged as "VirtualizedItem:Name"; fails like
    /// <see cref="ReadElement"/> for a node that vanished (its <see cref="FakeUiaNode.ReadException"/>), and for one without a
    /// runtime id. Of an <see cref="FakeUiaNode.Unrealized"/> node it reads what WPF's placeholder serves (control type, name,
    /// automation id, class, framework, runtime id, process and patterns) and leaves the rest at the engines' defaults (no
    /// bounds, offscreen, enabled, not focusable, password unknown), marked virtualized; any other node reads in full.
    /// </summary>
    public UiaVirtualizedItem ReadVirtualizedItem(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"VirtualizedItem:{fake.Name}");
        ThrowIfSet(fake.ReadException);
        if (fake.RuntimeId is not { Count: > 0 })
        {
            throw Vanished("element's runtime id");
        }

        if (!fake.Unrealized)
        {
            return new UiaVirtualizedItem(Cached(fake), fake.ProcessId, fake.IsPassword);
        }

        var element = new UiaElement(
            fake,
            new UiaNodeProperties(fake.Name, fake.ControlType, fake.AutomationId, UiaRect.Empty, IsEnabled: true, IsOffscreen: true, fake.FrameworkId),
            fake.ClassName,
            IsKeyboardFocusable: false,
            fake.Patterns,
            fake.RuntimeId,
            UiaControlTypes.GetId(fake.ControlType),
            fake.NativeWindowHandle,
            Virtualized: true);
        return new UiaVirtualizedItem(element, fake.ProcessId, IsPassword: null);
    }

    public UiaPoint? GetClickablePoint(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Clickable:{fake.Name}");
        ThrowIfSet(fake.ClickablePointException);
        ThrowIfUnrealized(fake);
        return fake.ClickablePoint;
    }

    public UiaNodeDescription ReadDescription(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Description:{fake.Name}");
        ThrowIfSet(fake.ReadException);
        ThrowIfUnrealized(fake);
        return new UiaNodeDescription(fake.HelpText, fake.AccessKey, fake.AcceleratorKey, fake.LocalizedControlType);
    }

    public string? ReadValue(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"Value:{fake.Name}");
        ThrowIfSet(fake.ValueException);
        return fake.Value;
    }

    public bool? ReadIsModal(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"IsModal:{fake.Name}");
        ThrowIfSet(fake.ReadException);
        return fake.IsModal;
    }

    /// <summary>
    /// The children with their properties in one "call", as a cache request reads them: each child's properties are
    /// read ("Read:Name") and its <see cref="FakeUiaNode.OnRead"/> runs, in order (the node's
    /// <see cref="FakeUiaNode.OnGetChildren"/> runs only for <see cref="GetChildren"/>, which the walk calls first, so it
    /// runs once per level). The call is atomic like the real one
    /// (it does not stop when the token is cancelled during it), and it fails as a whole when the node's
    /// <see cref="FakeUiaNode.CachedChildrenException"/> or <see cref="FakeUiaNode.ChildrenException"/> is set, or when
    /// any child's <see cref="FakeUiaNode.ReadException"/> or <see cref="FakeUiaNode.InteractionException"/> is: one
    /// failing provider fails the fetch, and the walk then reads the children one at a time.
    /// </summary>
    public IReadOnlyList<UiaElement> GetChildElements(IUiaNode node, CancellationToken cancellationToken)
    {
        var fake = Fake(node);
        cancellationToken.ThrowIfCancellationRequested();
        _cachedFetches.Enqueue(fake.Name ?? fake.ControlType);
        ThrowIfSet(fake.CachedChildrenException);
        ThrowIfSet(fake.ChildrenException);
        return fake.Children.Select(ReadCached).ToList();
    }

    public IReadOnlyList<UiaElement> FindAll(IUiaNode root, UiaScope scope, UiaCondition condition, CancellationToken cancellationToken)
    {
        var fake = Fake(root);
        cancellationToken.ThrowIfCancellationRequested();
        _finds.Enqueue($"{fake.Name ?? fake.ControlType}:{scope}");
        OnFindAll?.Invoke(fake, condition);
        fake.OnFindAll?.Invoke(condition);
        if (RefusesSubstrings && HasSubstring(condition))
        {
            throw new UiaConditionNotSupportedException("This engine cannot search for a substring (PropertyConditionFlags_MatchSubstring).");
        }

        ThrowIfSet(fake.FindAllException);
        IEnumerable<FakeUiaNode> candidates = scope switch
        {
            UiaScope.Children => fake.Children,
            UiaScope.Descendants => Descendants(fake),
            UiaScope.Subtree => Descendants(fake).Prepend(fake),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        return candidates.Where(n => Matches(n, condition, SubstringTest)).Select(ReadCached).ToList();
    }

    /// <summary>Whether the condition has a substring condition anywhere in it.</summary>
    public static bool HasSubstring(UiaCondition condition) => condition switch
    {
        UiaPropertyContainsCondition => true,
        UiaAndCondition and => and.Conditions.Any(HasSubstring),
        UiaOrCondition or => or.Conditions.Any(HasSubstring),
        UiaNotCondition not => HasSubstring(not.Condition),
        _ => false
    };

    public UiaElementTree ReadSubtree(IUiaNode root, int maxElements)
    {
        var fake = Fake(root);
        var count = 1;
        var rootChildren = new List<UiaElementTree>();
        var tree = new UiaElementTree(ReadCached(fake), rootChildren);
        var pending = new Queue<(FakeUiaNode Node, List<UiaElementTree> Children)>();
        pending.Enqueue((fake, rootChildren));
        while (pending.Count > 0 && count < maxElements)
        {
            var (parent, children) = pending.Dequeue();
            foreach (var child in parent.Children.TakeWhile(_ => count < maxElements))
            {
                var grandchildren = new List<UiaElementTree>();
                children.Add(new UiaElementTree(ReadCached(child), grandchildren));
                count++;
                pending.Enqueue((child, grandchildren));
            }
        }

        return tree;
    }

    public IReadOnlyList<int>? GetRuntimeId(IUiaNode node)
    {
        var fake = Fake(node);
        ThrowIfSet(fake.ReadException);
        return fake.RuntimeId;
    }

    public bool AreSameElement(IUiaNode first, IUiaNode second) => ReferenceEquals(Fake(first), Fake(second));

    public TPattern? GetPattern<TPattern>(IUiaNode node) where TPattern : class, IUiaPattern
    {
        var fake = Fake(node);
        _reads.Enqueue($"Pattern:{typeof(TPattern).Name}:{fake.Name}");
        return fake.PatternObjects.TryGetValue(typeof(TPattern), out var pattern) ? (TPattern)pattern : null;
    }

    /// <summary>
    /// The one acting member of the tree: logged as "SetFocus:Name"; fails with the node's
    /// <see cref="FakeUiaNode.SetFocusException"/>; otherwise, when <see cref="SetFocusMovesFocus"/> (the default), the node becomes
    /// <see cref="Focused"/> and has keyboard focus.
    /// </summary>
    public void SetFocus(IUiaNode node)
    {
        var fake = Fake(node);
        _reads.Enqueue($"SetFocus:{fake.Name}");
        ThrowIfSet(fake.SetFocusException);
        ThrowIfUnrealized(fake);
        if (SetFocusMovesFocus)
        {
            if (Focused != null)
            {
                Focused.HasKeyboardFocus = false;
            }

            Focused = fake;
            fake.HasKeyboardFocus = true;
        }
    }

    /// <summary>Whether <see cref="SetFocus"/> moves focus to its node (false: an application that ignores it).</summary>
    public bool SetFocusMovesFocus { get; set; } = true;

    /// <summary>
    /// Runs before a handler is added (roadmap M3.F22), with its kind ("Structure" or "Property") and node: it can block, as
    /// a hung provider does, or throw, as a provider that refuses does.
    /// </summary>
    public Action<string, FakeUiaNode>? OnAddHandler { get; set; }

    /// <summary>Runs before a handler is removed, with its kind: it can block or throw.</summary>
    public Action<string>? OnRemoveHandler { get; set; }

    /// <summary>The event handlers added and not removed.</summary>
    public IReadOnlyList<FakeUiaHandler> Handlers => _handlers.Values.OrderBy(h => h.Id).ToList();

    /// <summary>Every handler removed, with the managed thread that removed it, in order.</summary>
    public IReadOnlyList<(FakeUiaHandler Handler, int Thread)> RemovedHandlers => _removedHandlers.ToList();

    /// <summary>A structure change below a watched node: every structure handler is called, on the caller's thread.</summary>
    public void RaiseStructureChanged()
    {
        foreach (var handler in Handlers.Where(h => h.Kind == "Structure"))
        {
            handler.Changed();
        }
    }

    /// <summary>A change of <paramref name="property"/>: every property handler that watches it is called, on the caller's thread.</summary>
    public void RaisePropertyChanged(UiaWatchedProperty property)
    {
        foreach (var handler in Handlers.Where(h => h.Kind == "Property" && h.Properties.Contains(property)))
        {
            handler.Changed();
        }
    }

    public IDisposable AddStructureChangedHandler(IUiaNode node, Action changed) => AddHandler("Structure", Fake(node), [], changed);

    public IDisposable AddPropertyChangedHandler(IUiaNode node, IReadOnlyCollection<UiaWatchedProperty> properties, Action changed) =>
        AddHandler("Property", Fake(node), properties, changed);

    private IDisposable AddHandler(string kind, FakeUiaNode node, IReadOnlyCollection<UiaWatchedProperty> properties, Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        OnAddHandler?.Invoke(kind, node);
        var handler = new FakeUiaHandler(Interlocked.Increment(ref _nextHandler), kind, node, properties, changed,
            Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState());
        _handlers[handler.Id] = handler;
        return new HandlerRegistration(() =>
        {
            OnRemoveHandler?.Invoke(kind);
            _handlers.TryRemove(handler.Id, out _);
            _removedHandlers.Enqueue((handler, Environment.CurrentManagedThreadId));
        });
    }

    private sealed class HandlerRegistration(Action remove) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                remove();
            }
        }
    }

    /// <summary>Every <see cref="FindAll"/> call as "RootName:Scope", in call order.</summary>
    public IReadOnlyList<string> Finds => _finds.ToList();

    /// <summary>The nodes whose children were fetched with <see cref="GetChildElements"/>, by name, in call order.</summary>
    public IReadOnlyList<string> CachedFetches => _cachedFetches.ToList();

    /// <summary>The nodes whose children were listed with <see cref="GetChildren"/>, by name, in call order.</summary>
    public IReadOnlyList<string> ChildListings => _childListings.ToList();

    /// <summary>The nodes asked <see cref="HasChildren"/>, by name, in call order.</summary>
    public IReadOnlyList<string> ChildProbes => _childProbes.ToList();

    private UiaElement ReadCached(FakeUiaNode node)
    {
        _reads.Enqueue($"Read:{node.Name}");
        node.OnRead?.Invoke();
        ThrowIfSet(node.ReadException);
        ThrowIfSet(node.InteractionException);
        ThrowIfUnrealized(node);
        return Cached(node);
    }

    private static UiaElement Cached(FakeUiaNode node) =>
        new UiaElement(
            node,
            new UiaNodeProperties(node.Name, node.ControlType, node.AutomationId, node.Bounds, node.IsEnabled, node.IsOffscreen, node.FrameworkId),
            node.ClassName,
            node.IsKeyboardFocusable,
            node.Patterns,
            node.RuntimeId,
            UiaControlTypes.GetId(node.ControlType),
            node.NativeWindowHandle);

    private static IEnumerable<FakeUiaNode> Descendants(FakeUiaNode node) =>
        node.Children.SelectMany(child => Descendants(child).Prepend(child));

    /// <summary>
    /// Evaluates a condition as a provider would; a substring condition with <paramref name="substringTest"/> when one is
    /// given (<see cref="SubstringTest"/>).
    /// </summary>
    public static bool Matches(
        FakeUiaNode node, UiaCondition condition, Func<FakeUiaNode, UiaPropertyContainsCondition, bool>? substringTest = null) =>
        condition switch
        {
            UiaTrueCondition => true,
            UiaAndCondition and => and.Conditions.All(c => Matches(node, c, substringTest)),
            UiaOrCondition or => or.Conditions.Any(c => Matches(node, c, substringTest)),
            UiaNotCondition not => !Matches(node, not.Condition, substringTest),
            UiaPropertyCondition property => PropertyMatches(node, property),
            UiaPropertyContainsCondition contains => substringTest?.Invoke(node, contains) ?? ContainsMatches(node, contains),
            _ => throw new ArgumentException("Unknown condition", nameof(condition))
        };

    private static bool ContainsMatches(FakeUiaNode node, UiaPropertyContainsCondition condition)
    {
        var actual = condition.Property switch
        {
            UiaProperty.Name => node.Name,
            UiaProperty.AutomationId => node.AutomationId,
            UiaProperty.ClassName => node.ClassName,
            UiaProperty.FrameworkId => node.FrameworkId,
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
        return actual != null &&
               actual.Contains(condition.Value, condition.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PropertyMatches(FakeUiaNode node, UiaPropertyCondition condition)
    {
        object? actual = condition.Property switch
        {
            UiaProperty.Name => node.Name,
            UiaProperty.AutomationId => node.AutomationId,
            UiaProperty.ClassName => node.ClassName,
            UiaProperty.ControlType => node.ControlType,
            UiaProperty.FrameworkId => node.FrameworkId,
            UiaProperty.ProcessId => node.ProcessId,
            UiaProperty.IsEnabled => node.IsEnabled,
            UiaProperty.IsOffscreen => node.IsOffscreen,
            UiaProperty.IsKeyboardFocusable => node.IsKeyboardFocusable,
            UiaProperty.RuntimeId => node.RuntimeId,
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
        if (condition.Property == UiaProperty.RuntimeId)
        {
            return actual is IReadOnlyList<int> runtimeId && runtimeId.SequenceEqual((IReadOnlyList<int>)condition.Value);
        }

        return actual is string text && condition.Value is string expected
            ? string.Equals(text, expected,
                condition.IgnoreCase || condition.Property == UiaProperty.ControlType ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            : Equals(actual, condition.Value);
    }

    /// <summary>
    /// A read that includes a property an <see cref="FakeUiaNode.Unrealized"/> placeholder does not serve fails, as WPF's
    /// ItemAutomationPeer fails it with ElementNotAvailableException (ThrowElementNotAvailableException).
    /// </summary>
    private static void ThrowIfUnrealized(FakeUiaNode node)
    {
        if (node.Unrealized)
        {
            throw new UiaElementUnavailableException(
                "The element is not available: it is an item its list has not realized, which serves only some properties.");
        }
    }

    private static void ThrowIfSet(Exception? exception)
    {
        if (exception != null)
        {
            throw exception;
        }
    }

    private static FakeUiaNode Fake(IUiaNode node) =>
        node as FakeUiaNode ?? throw new ArgumentException("Not a FakeUiaNode", nameof(node));

    /// <summary>The exception a real tree throws for an element that disappeared.</summary>
    public static UiaElementUnavailableException Vanished(string what = "element") =>
        new($"The {what} is no longer available.");
}

/// <summary>An event handler added to a <see cref="FakeUiaTree"/>, with the managed thread (and its apartment) that added it.</summary>
public sealed record FakeUiaHandler(
    int Id, string Kind, FakeUiaNode Node, IReadOnlyCollection<UiaWatchedProperty> Properties, Action Changed, int Thread, ApartmentState Apartment);

/// <summary>A node of <see cref="FakeUiaTree"/>. Every property can be set with an object initializer.</summary>
public sealed class FakeUiaNode : IUiaNode
{
    private static int _nextRuntimeId;

    private readonly List<FakeUiaNode> _children = [];
    private readonly Dictionary<Type, object> _patterns = [];

    public FakeUiaNode(string controlType, string? name = null)
    {
        ControlType = controlType;
        Name = name;
    }

    public string ControlType { get; init; }
    public string? Name { get; set; }
    public string? AutomationId { get; init; }
    public UiaRect Bounds { get; init; } = new(0, 0, 100, 30);
    public bool IsEnabled { get; set; } = true;
    public bool IsOffscreen { get; set; }

    public bool IsKeyboardFocusable { get; init; }
    public bool HasKeyboardFocus { get; set; }
    public IReadOnlyList<string> ActionPatterns { get; init; } = [];

    public string? ClassName { get; init; }

    /// <summary>
    /// A placeholder of an item its list has not realized, as WPF's ItemAutomationPeer is for an item without a container: it
    /// serves its control type, name, automation id, class, runtime id, process and patterns, but every read that includes
    /// its bounds, IsOffscreen, IsEnabled, keyboard focus, IsPassword, description or clickable point fails with
    /// <see cref="UiaElementUnavailableException"/> (Read, ReadElement, ReadInteraction, IsInteractable, ReadIdentity,
    /// ReadDescription, GetClickablePoint, SetFocus and the cached reads). Only <see cref="FakeUiaTree.ReadVirtualizedItem"/>
    /// reads it. <see cref="FakeVirtualList"/> sets it for the items it has not realized.
    /// </summary>
    public bool Unrealized { get; set; }
    public string? FrameworkId { get; init; }
    public int ProcessId { get; set; }
    public nint NativeWindowHandle { get; init; }
    public bool IsPassword { get; init; }
    public string? Value { get; set; }

    /// <summary>Thrown by <see cref="FakeUiaTree.ReadValue"/> for this node, as a value read of an element that vanished.</summary>
    public Exception? ValueException { get; set; }

    /// <summary>The Window pattern's IsModal, or null when the node has no Window pattern.</summary>
    public bool? IsModal { get; init; }

    /// <summary>What <see cref="FakeUiaTree.GetClickablePoint"/> returns.</summary>
    public UiaPoint? ClickablePoint { get; set; }

    public Exception? ClickablePointException { get; set; }

    public string? HelpText { get; init; }
    public string? AccessKey { get; init; }
    public string? AcceleratorKey { get; init; }
    public string? LocalizedControlType { get; init; }

    /// <summary>Thrown by <see cref="FakeUiaTree.FindAll"/> searches rooted at this node.</summary>
    public Exception? FindAllException { get; set; }

    /// <summary>Runs when a search rooted at this node starts, with its condition.</summary>
    public Action<UiaCondition>? OnFindAll { get; set; }

    public Exception? ReadException { get; set; }

    /// <summary>Thrown by <see cref="FakeUiaTree.SetFocus"/> for this node.</summary>
    public Exception? SetFocusException { get; set; }
    public Exception? InteractionException { get; set; }
    public Exception? IdentityException { get; set; }
    public Exception? ChildrenException { get; set; }
    public Exception? ParentException { get; set; }

    /// <summary>Fails only the cached fetch of this node's children (<see cref="FakeUiaTree.GetChildElements"/>).</summary>
    public Exception? CachedChildrenException { get; set; }

    /// <summary>A runtime id unique among the fake nodes of the test run.</summary>
    public IReadOnlyList<int>? RuntimeId { get; init; } = NewRuntimeId();

    /// <summary>A runtime id no other fake node has.</summary>
    public static IReadOnlyList<int> NewRuntimeId() => [42, Interlocked.Increment(ref _nextRuntimeId)];

    /// <summary>
    /// The patterns the node reports as available: its <see cref="ActionPatterns"/> by name, plus
    /// <see cref="OtherPatterns"/>.
    /// </summary>
    public UiaPatterns Patterns => ActionPatterns
        .Select(name => Enum.Parse<UiaPatterns>(name))
        .Aggregate(OtherPatterns, (all, pattern) => all | pattern);

    /// <summary>Available patterns that are not action patterns (Scroll, Window, Text, ...).</summary>
    public UiaPatterns OtherPatterns { get; init; }

    /// <summary>The pattern objects <see cref="FakeUiaTree.GetPattern{TPattern}"/> returns, by pattern interface.</summary>
    public IReadOnlyDictionary<Type, object> PatternObjects => _patterns;

    /// <summary>Gives the node a pattern object and returns the node.</summary>
    public FakeUiaNode WithPattern<TPattern>(TPattern pattern) where TPattern : class, IUiaPattern
    {
        _patterns[typeof(TPattern)] = pattern;
        return this;
    }

    /// <summary>Runs when the node's basic properties are read, before they are returned.</summary>
    public Action? OnRead { get; set; }

    /// <summary>Runs when the node's children are read, before they are returned.</summary>
    public Action? OnGetChildren { get; set; }

    public FakeUiaNode? Parent { get; private set; }

    public IReadOnlyList<FakeUiaNode> Children => _children;

    /// <summary>Appends children and returns this node, so trees can be written as nested expressions.</summary>
    public FakeUiaNode With(params FakeUiaNode[] children)
    {
        foreach (var child in children)
        {
            child.Parent = this;
            _children.Add(child);
        }

        return this;
    }

    /// <summary>
    /// The element disappears: it leaves its parent's children, so no search finds it, and every read of it fails as a
    /// read of a vanished element does.
    /// </summary>
    public void Vanish()
    {
        Parent?._children.Remove(this);
        ReadException = FakeUiaTree.Vanished();
    }

    /// <summary>Puts <paramref name="child"/> at <paramref name="index"/> of this node's children and returns this node.</summary>
    public FakeUiaNode Insert(int index, FakeUiaNode child)
    {
        child.Parent = this;
        _children.Insert(index, child);
        return this;
    }

    public override string ToString() => $"{ControlType} '{Name}'";
}
