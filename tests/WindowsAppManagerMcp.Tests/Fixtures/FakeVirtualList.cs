namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A virtualized list for the M3.10 tests (roadmap M3.10), as WPF's VirtualizingStackPanel shows one to UI Automation: a List
/// node with the ItemContainer and Scroll patterns whose tree holds only the realized items, while
/// <see cref="FindItemByProperty"/> finds every item, realized or not. An item that is not realized is a placeholder
/// (<see cref="FakeUiaNode.Unrealized"/>): offscreen, without bounds, with the VirtualizedItem and ScrollItem patterns, and, as
/// WPF's ItemAutomationPeer, it fails every read of its bounds, IsOffscreen, IsEnabled, focus, IsPassword or description, so
/// only <see cref="FakeUiaTree.ReadVirtualizedItem"/> reads it. Its Realize makes it real and puts it in the list's children
/// (still offscreen, as a provider whose Realize does not scroll), and ScrollIntoView brings it on screen. Nothing here is a
/// real element.
/// </summary>
public sealed class FakeVirtualList : IUiaItemContainerPattern
{
    private readonly List<FakeUiaNode> _items = [];
    private readonly List<string> _probes = [];

    /// <param name="count">How many items the list holds.</param>
    /// <param name="realized">How many of the first items are realized (in the tree and on screen).</param>
    /// <param name="name">The name of item <c>i</c>.</param>
    /// <param name="processId">The process the list and its items report.</param>
    /// <param name="automationId">The automation id of item <c>i</c>, or null for none.</param>
    public FakeVirtualList(int count, int realized, Func<int, string> name, int processId = 0, Func<int, string?>? automationId = null)
    {
        List = new FakeUiaNode("List", "Files")
        {
            AutomationId = "FileList", ProcessId = processId, OtherPatterns = UiaPatterns.ItemContainer | UiaPatterns.Scroll
        }.WithPattern<IUiaItemContainerPattern>(this);
        for (var i = 0; i < count; i++)
        {
            var isRealized = i < realized;
            var item = new FakeUiaNode("ListItem", name(i))
            {
                AutomationId = automationId?.Invoke(i),
                ProcessId = processId,
                Unrealized = !isRealized,
                IsOffscreen = !isRealized,
                Bounds = isRealized ? new UiaRect(10, 10 + i * 20, 200, 20) : UiaRect.Empty,
                OtherPatterns = UiaPatterns.VirtualizedItem | UiaPatterns.ScrollItem
            };
            var virtualized = new FakeVirtualizedItemPattern();
            virtualized.OnRealize = () => Realize(item);
            var scrollItem = new FakeScrollItemPattern { OnScrollIntoView = () => item.IsOffscreen = false };
            item.WithPattern<IUiaVirtualizedItemPattern>(virtualized).WithPattern<IUiaScrollItemPattern>(scrollItem);
            _items.Add(item);
            if (isRealized)
            {
                List.With(item);
            }
        }
    }

    /// <summary>The list's node.</summary>
    public FakeUiaNode List { get; }

    /// <summary>Every item, realized or not, in list order.</summary>
    public IReadOnlyList<FakeUiaNode> Items => _items;

    /// <summary>Every FindItemByProperty call, as "property=value after Name" (or "after start").</summary>
    public IReadOnlyList<string> Probes => _probes;

    /// <summary>Thrown by <see cref="FindItemByProperty"/> when set (a provider that fails).</summary>
    public Exception? FindException { get; set; }

    /// <summary>How many FindItemByProperty calls answer before <see cref="FindException"/> is thrown (0: from the first).</summary>
    public int FailFromProbe { get; set; }

    /// <summary>Runs on every FindItemByProperty call with its number (1 for the first), before it answers.</summary>
    public Action<int>? OnProbe { get; set; }

    /// <summary>The VirtualizedItem pattern of item <paramref name="index"/>.</summary>
    public FakeVirtualizedItemPattern VirtualizedOf(int index) =>
        (FakeVirtualizedItemPattern)_items[index].PatternObjects[typeof(IUiaVirtualizedItemPattern)];

    /// <summary>The ScrollItem pattern of item <paramref name="index"/>.</summary>
    public FakeScrollItemPattern ScrollItemOf(int index) =>
        (FakeScrollItemPattern)_items[index].PatternObjects[typeof(IUiaScrollItemPattern)];

    /// <summary>Whether item <paramref name="index"/> is in the list's children (realized).</summary>
    public bool IsRealized(int index) => List.Children.Contains(_items[index]);

    public IUiaNode? FindItemByProperty(IUiaNode? startAfter, UiaProperty? property, object? value)
    {
        _probes.Add($"{property}={value} after {(startAfter is FakeUiaNode after ? after.Name : "start")}");
        OnProbe?.Invoke(_probes.Count);
        if (FindException != null && _probes.Count > FailFromProbe)
        {
            throw FindException;
        }

        var start = startAfter == null ? 0 : _items.IndexOf((FakeUiaNode)startAfter) + 1;
        for (var i = start; i < _items.Count; i++)
        {
            var item = _items[i];
            var actual = property switch
            {
                null => null,
                UiaProperty.Name => item.Name,
                UiaProperty.AutomationId => item.AutomationId,
                _ => throw new NotSupportedException($"The fake list compares Name and AutomationId only, not {property}.")
            };

            // As WPF compares: the whole value, case-sensitively
            if (property == null || string.Equals(actual, value as string, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private void Realize(FakeUiaNode item)
    {
        item.Unrealized = false;
        if (!List.Children.Contains(item))
        {
            List.With(item);
        }
    }
}
