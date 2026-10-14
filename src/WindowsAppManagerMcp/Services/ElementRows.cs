using System.Globalization;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The list, tree or grid row an element is inside (M3 review): its nearest ListItem, TreeItem or DataItem ancestor
/// (<see cref="ElementRow"/>). A virtualizing list (WinUI, WPF with recycling, many web lists) reuses a row's elements for
/// another item when it scrolls or its data changes, and a control inside the row (the row's Delete button) keeps its
/// runtime id, type, automation id and class. So the element registry records the row when the element is read
/// (<see cref="ElementOrigin.Row"/>: a tree walk knows it; find_ui_elements, the focused element, the element at a point
/// and the popups' elements read it with <see cref="Finder"/>), and reads it
/// again when it resolves the ref (<see cref="Changed"/>): a row that now shows another item makes the ref stale.
/// </summary>
/// <remarks>The row is looked for at most <see cref="MaxLevels"/> levels up, and not past a list, tree, grid, table or
/// window: an element above them is in no row of theirs. An element that is itself an item has no row: its own name and
/// runtime id say which item it is (<see cref="ElementRegistry.NameIsIdentity"/>).</remarks>
public static class ElementRows
{
    /// <summary>How many levels above an element its row is looked for.</summary>
    public const int MaxLevels = 8;

    /// <summary>True for a ListItem, TreeItem or DataItem: a row of a list, tree or grid.</summary>
    public static bool IsItem(string? controlType) =>
        controlType != null &&
        (controlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase) ||
         controlType.Equals("TreeItem", StringComparison.OrdinalIgnoreCase) ||
         controlType.Equals("DataItem", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for an element above any row an element below it could be in: a list, tree, grid or table (whose rows are its
    /// children), or a window.
    /// </summary>
    public static bool EndsTheSearch(string? controlType) =>
        controlType != null &&
        (controlType.Equals("List", StringComparison.OrdinalIgnoreCase) ||
         controlType.Equals("Tree", StringComparison.OrdinalIgnoreCase) ||
         controlType.Equals("DataGrid", StringComparison.OrdinalIgnoreCase) ||
         controlType.Equals("Table", StringComparison.OrdinalIgnoreCase) ||
         controlType.Equals("Window", StringComparison.OrdinalIgnoreCase));

    /// <summary>The row of an item, as the row of the elements below it (zero levels up), or null without a runtime id.</summary>
    public static ElementRow? OfItem(IReadOnlyList<int>? runtimeId, UiaNodeProperties properties) =>
        runtimeId is { Count: > 0 } id
            ? new ElementRow(id.ToArray(), EmptyToNull(properties.Name), EmptyToNull(properties.AutomationId), 0)
            : null;

    /// <summary>
    /// For a tree walk: the row of a child of an element whose row (counted from that element) is <paramref name="parentRow"/>,
    /// or null when it has none within <see cref="MaxLevels"/>.
    /// </summary>
    public static ElementRow? Below(ElementRow? parentRow) =>
        parentRow is { Levels: < MaxLevels } row ? row with { Levels = row.Levels + 1 } : null;

    /// <summary>
    /// Why the element at <paramref name="node"/> is no longer in <paramref name="row"/>, or null when it still is: the
    /// element <see cref="ElementRow.Levels"/> levels up is the same item (its runtime id, and its name too when it has no
    /// automation id). One call per level and one read of the item, under <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="reCreated">For an element that was re-created (the row may be re-created too): the item's name and
    /// automation id are compared, not its runtime id.</param>
    /// <param name="now">The row as read now when it is the same item (its runtime id may be new), else null.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="UiaElementUnavailableException">The element or an ancestor is gone.</exception>
    public static string? Changed(
        IUiaTree tree, IUiaNode node, ElementRow row, bool reCreated, CancellationToken cancellationToken, out ElementRow? now)
    {
        now = null;
        var current = node;
        for (var level = 0; level < row.Levels; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tree.GetParent(current) is not { } parent)
            {
                return $"its row ({Describe(row)}) is gone";
            }

            current = parent;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var item = tree.ReadElement(current);
        var name = EmptyToNull(item.Properties.Name);
        var automationId = EmptyToNull(item.Properties.AutomationId);
        if (!IsItem(item.Properties.ControlType))
        {
            return $"its row ({Describe(row)}) is gone";
        }

        var sameItem = string.Equals(automationId, row.AutomationId, StringComparison.Ordinal) &&
                       (row.AutomationId != null || string.Equals(name, row.Name, StringComparison.Ordinal)) &&
                       (reCreated || (item.RuntimeId is { Count: > 0 } id && id.SequenceEqual(row.RuntimeId)));
        if (!sameItem)
        {
            return $"its row now shows another item ({item.Properties.ControlType} '{name}', not {Describe(row)})";
        }

        now = OfItem(item.RuntimeId, item.Properties) is { } read ? read with { Levels = row.Levels } : row;
        return null;
    }

    /// <summary>
    /// True when two reads of one element saw it in different rows (both known): another item's runtime id, automation id,
    /// or name for an item without an automation id.
    /// </summary>
    public static bool AreDifferent(ElementRow? stored, ElementRow? read) =>
        stored != null && read != null &&
        (!stored.RuntimeId.SequenceEqual(read.RuntimeId) ||
         !string.Equals(stored.AutomationId, read.AutomationId, StringComparison.Ordinal) ||
         (stored.AutomationId == null && !string.Equals(stored.Name, read.Name, StringComparison.Ordinal)));

    /// <summary>The row for messages: "'Invoice 1041'" or "automationId 'Row7'".</summary>
    public static string Describe(ElementRow row) =>
        row.AutomationId != null ? $"automationId '{row.AutomationId}'" : $"'{row.Name}'";

    /// <summary>
    /// Finds the rows of the matches of one search, reading each ancestor once: matches in one list share their ancestors
    /// above the row, and siblings share all of them.
    /// </summary>
    public static Finder For(IUiaTree tree) => new(tree);

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Key(IReadOnlyList<int> runtimeId) =>
        string.Join('.', runtimeId.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The rows of a search's matches (see <see cref="For"/>).</summary>
    public sealed class Finder
    {
        private readonly IUiaTree _tree;

        // The ancestors read, by runtime id: the row at or above each (Levels counted from it), or null for none
        private readonly Dictionary<string, ElementRow?> _known = new(StringComparer.Ordinal);

        internal Finder(IUiaTree tree) => _tree = tree;

        /// <summary>
        /// The row of <paramref name="element"/>, or null when it is an item itself or has no row within
        /// <see cref="MaxLevels"/>. Per level, the parent and its runtime id, and a read of an ancestor not read before.
        /// </summary>
        /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
        public ElementRow? Of(UiaElement element, CancellationToken cancellationToken)
        {
            if (IsItem(element.Properties.ControlType))
            {
                return null;
            }

            var walked = new List<(string Key, int Level)>();
            var current = element.Node;

            // The row however far up a known ancestor said it is; the element's own row only within MaxLevels
            ElementRow? row = null;
            var known = false;
            for (var level = 1; level <= MaxLevels; level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_tree.GetParent(current) is not { } parent)
                {
                    known = true;
                    break;
                }

                // The runtime id first (the engines know it from finding the parent): an ancestor read for another match is
                // not read again
                var key = _tree.GetRuntimeId(parent) is { Count: > 0 } id ? Key(id) : null;
                if (key != null && _known.TryGetValue(key, out var above))
                {
                    row = above is { } found ? found with { Levels = found.Levels + level } : null;
                    known = true;
                    break;
                }

                var read = _tree.ReadElement(parent);
                key ??= read.RuntimeId is { Count: > 0 } readId ? Key(readId) : null;

                if (IsItem(read.Properties.ControlType))
                {
                    row = OfItem(read.RuntimeId, read.Properties) is { } item ? item with { Levels = level } : null;
                    known = true;
                    if (key != null)
                    {
                        walked.Add((key, level));
                    }

                    break;
                }

                if (key != null)
                {
                    walked.Add((key, level));
                }

                if (EndsTheSearch(read.Properties.ControlType))
                {
                    known = true;
                    break;
                }

                current = parent;
            }

            // Only an answer the levels did not cut short holds for the other matches below these ancestors
            if (known)
            {
                foreach (var (key, level) in walked)
                {
                    _known[key] = row is { } r ? r with { Levels = r.Levels - level } : null;
                }
            }

            return row is { Levels: <= MaxLevels } ? row : null;
        }
    }
}
