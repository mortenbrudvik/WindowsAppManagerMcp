using System.Globalization;
using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>The form of element refs (roadmap M3.4): "w&lt;window&gt;e&lt;element&gt;", for example "w4120035e17".</summary>
public static partial class ElementRefs
{
    /// <summary>The description of a ref parameter, for the tools that take one.</summary>
    public const string Example = "w4120035e17";

    /// <summary>True when <paramref name="value"/> has the form of a ref (any letter case, no spaces).</summary>
    public static bool IsWellFormed(string? value) => value != null && RefPattern().IsMatch(value);

    /// <summary>The ref in lower case, or null when it is not well formed.</summary>
    public static string? Normalize(string? value) => IsWellFormed(value) ? value!.ToLowerInvariant() : null;

    /// <summary>The ref of element <paramref name="element"/> of window <paramref name="window"/>.</summary>
    public static string Format(int window, int element) =>
        string.Create(CultureInfo.InvariantCulture, $"w{window}e{element}");

    /// <summary>The window and element numbers of a well-formed ref.</summary>
    public static bool TryParse(string? value, out int window, out int element)
    {
        window = element = 0;
        var match = value == null ? null : RefPattern().Match(value);
        return match is { Success: true } &&
               int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out window) &&
               int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out element);
    }

    [GeneratedRegex("^[wW]([1-9][0-9]{0,8})[eE]([1-9][0-9]{0,8})$", RegexOptions.CultureInvariant)]
    private static partial Regex RefPattern();
}

/// <summary>The element registry (roadmap M3.4); see <see cref="IElementRegistry"/>.</summary>
public sealed class ElementRegistry : IElementRegistry
{
    /// <summary>
    /// How many refs the registry keeps: the least recently used expire first. Twice the most elements one read returns
    /// (get_ui_elements' and capture_with_elements' maxElements limit), so one large read cannot expire the refs an agent
    /// got just before it.
    /// </summary>
    public const int DefaultCapacity = 10_000;

    /// <summary>
    /// The production registry's first window number is drawn from [<see cref="FirstWindowNumberMin"/>,
    /// <see cref="FirstWindowNumberMax"/>): eight million starting points, so a ref kept from before a server restart names
    /// a window of the new session only when the new session happens to number that window too (about 1 in 80,000 per
    /// remembered window for a session that numbers 100 windows). Seven digits leave room for a million windows.
    /// </summary>
    public const int FirstWindowNumberMin = 1_000_000;

    /// <inheritdoc cref="FirstWindowNumberMin"/>
    public const int FirstWindowNumberMax = 9_000_000;

    /// <summary>A resolution keeps this many matches of its searches: enough to tell one from several.</summary>
    private const int ResolutionKeep = 8;

    private readonly object _gate = new();
    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly INativeWindowWrapper _windows;
    private readonly TimeProvider _clock;
    private readonly int _capacity;

    // The live window records by handle. Window numbers are issued in order from _firstWindowSeq, so a number below
    // _nextWindowSeq was issued; nothing is kept for a window once it is dropped
    private readonly Dictionary<nint, WindowRecord> _windowsByHandle = [];
    private readonly Dictionary<string, Entry> _byRef = new(StringComparer.Ordinal);

    // Most recently used first
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _firstWindowSeq;
    private int _nextWindowSeq;
    private long _generation;

    /// <summary>
    /// The production registry: window numbers start at a random value in
    /// [<see cref="FirstWindowNumberMin"/>, <see cref="FirstWindowNumberMax"/>).
    /// </summary>
    public ElementRegistry(IUiaTree tree, IUiaWorker worker, INativeWindowWrapper windows)
        : this(tree, worker, windows, DefaultCapacity, Random.Shared.Next(FirstWindowNumberMin, FirstWindowNumberMax), TimeProvider.System)
    {
    }

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs <see cref="ResolveAsync"/>'s resolution under a deadline.</param>
    /// <param name="windows">Tells whether a window still exists and which process it belongs to.</param>
    /// <param name="capacity">How many refs to keep.</param>
    /// <param name="firstWindowNumber">The number of the first window (tests pass 1).</param>
    /// <param name="clock">The clock of the entries' last use.</param>
    public ElementRegistry(IUiaTree tree, IUiaWorker worker, INativeWindowWrapper windows, int capacity, int firstWindowNumber, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstWindowNumber, 1);
        _tree = tree;
        _worker = worker;
        _windows = windows;
        _capacity = capacity;
        _firstWindowSeq = _nextWindowSeq = firstWindowNumber;
        _clock = clock;
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byRef.Count;
            }
        }
    }

    /// <inheritdoc />
    public long NextGeneration()
    {
        List<WindowRecord> records;
        lock (_gate)
        {
            records = _windowsByHandle.Values.ToList();
        }

        // Outside the lock: the checks are Win32 reads
        var gone = records.Where(r => !IsSameWindow(r)).ToList();
        lock (_gate)
        {
            foreach (var record in gone)
            {
                DropWindow(record);
            }

            return ++_generation;
        }
    }

    /// <inheritdoc />
    public string? Register(nint windowHandle, UiaElement element, ElementOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(origin);
        if (element.RuntimeId is not { Count: > 0 } runtimeId || windowHandle == nint.Zero)
        {
            return null;
        }

        var identity = WindowIdentityOf(windowHandle);
        if (identity == null)
        {
            return null;
        }

        var key = RuntimeKey(runtimeId);
        var fingerprint = Fingerprint(element, origin.IndexPath);
        lock (_gate)
        {
            var record = WindowRecordFor(windowHandle, identity.Value);
            if (record.Entries.TryGetValue(key, out var existing))
            {
                if (SameIdentity(existing.Fingerprint, fingerprint))
                {
                    existing.Node = element.Node;
                    existing.Fingerprint = Merge(existing.Fingerprint, fingerprint);
                    existing.Selector = origin.Unique switch
                    {
                        true when origin.Selector != null => origin.Selector,
                        false => null,
                        _ => existing.Selector
                    };
                    existing.Generation = Math.Max(existing.Generation, origin.Generation);
                    Touch(existing);
                    return existing.Ref;
                }

                // Another element has the runtime id of one that is gone (or a recycled list item shows another row): the
                // old ref must not name it
                Remove(existing);
            }

            var entry = new Entry(
                ElementRefs.Format(record.Seq, ++record.LastElement), record, key, runtimeId.ToArray(), element.Node, fingerprint)
            {
                Selector = origin.Unique == true ? origin.Selector : null,
                Generation = origin.Generation
            };
            record.Entries[key] = entry;
            _byRef[entry.Ref] = entry;
            entry.LruNode = _lru.AddFirst(entry);
            entry.LastUsed = _clock.GetUtcNow();
            while (_byRef.Count > _capacity && _lru.Last is { } oldest)
            {
                Remove(oldest.Value);
            }

            return entry.Ref;
        }
    }

    /// <inheritdoc />
    public ElementLookup Lookup(string elementRef)
    {
        var normalized = ElementRefs.Normalize(elementRef);
        if (normalized == null)
        {
            return new ElementLookup(elementRef ?? string.Empty, 0, null, 0, UIElementErrorCode.ElementStale,
                $"'{elementRef}' is not an element ref: refs look like '{ElementRefs.Example}' and come from find_ui_elements, " +
                "get_ui_elements, get_focused_element, get_element_at_point or capture_with_elements.");
        }

        Entry? entry;
        lock (_gate)
        {
            if (!_byRef.TryGetValue(normalized, out entry))
            {
                return new ElementLookup(normalized, 0, null, 0, UIElementErrorCode.ElementStale, UnknownRefMessage(normalized));
            }
        }

        if (!IsSameWindow(entry.Window))
        {
            lock (_gate)
            {
                DropWindow(entry.Window);
            }

            return new ElementLookup(normalized, 0, null, 0, UIElementErrorCode.ElementStale,
                $"Element ref '{normalized}' is stale: its window closed. Find the element again with find_ui_elements.");
        }

        lock (_gate)
        {
            return new ElementLookup(normalized, entry.Window.Handle, entry.Fingerprint, entry.Generation);
        }
    }

    /// <inheritdoc />
    public async Task<ElementResolution> ResolveAsync(string elementRef, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var lookup = Lookup(elementRef);
        if (!lookup.Success)
        {
            return ElementResolution.Failed(lookup.ErrorCode, lookup.Error!);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await _worker.RunAsync(
                UiaTargets.TargetProcessId(_windows, lookup.WindowHandle),
                token => UiaTargets.NotRespondingReason(_windows, lookup.WindowHandle, "its element was not read") is { } notResponding
                    ? ElementResolution.Failed(UIElementErrorCode.NotResponding, notResponding)
                    : Resolve(lookup.Ref, token),
                deadline.Token,
                TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return ElementResolution.Failed(UIElementErrorCode.OperationTimedOut,
                $"Element ref '{lookup.Ref}' could not be resolved in time: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public ElementResolution Resolve(string elementRef, CancellationToken cancellationToken)
    {
        var lookup = Lookup(elementRef);
        if (!lookup.Success)
        {
            return ElementResolution.Failed(lookup.ErrorCode, lookup.Error!);
        }

        Entry? entry;
        lock (_gate)
        {
            entry = _byRef.GetValueOrDefault(lookup.Ref);
        }

        if (entry == null)
        {
            return ElementResolution.Failed(UIElementErrorCode.ElementStale, UnknownRefMessage(lookup.Ref));
        }

        try
        {
            return ResolveEntry(entry, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            return ElementResolution.Failed(UIElementErrorCode.OperationTimedOut,
                $"Element ref '{entry.Ref}' could not be resolved in time: UI Automation did not answer, or the deadline passed.");
        }
        catch (Exception ex)
        {
            return ElementResolution.Failed(UIElementErrorCode.OperationException,
                $"Element ref '{entry.Ref}' could not be resolved: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private ElementResolution ResolveEntry(Entry entry, CancellationToken cancellationToken)
    {
        IUiaNode node;
        ElementFingerprint fingerprint;
        ElementCriteria? selector;
        lock (_gate)
        {
            node = entry.Node;
            fingerprint = entry.Fingerprint;
            selector = entry.Selector;
        }

        // 1. The stored element, if it is still there: a live read asks the provider (a runtime id alone is answered from
        //    memory). Accepted only with the stored runtime id and identity: a proxy that now answers for another element,
        //    a recycled list item that shows another row, or a read without a runtime id proves nothing
        try
        {
            var live = _tree.ReadElement(node);
            if (live.RuntimeId is { Count: > 0 } liveId && liveId.SequenceEqual(entry.RuntimeId) &&
                SameIdentity(fingerprint, Fingerprint(live, null)))
            {
                return Resolved(entry, node, live, ElementResolutionPath.Live);
            }
        }
        catch (UiaElementUnavailableException)
        {
            // Gone: look for it again
        }

        var window = _tree.FromHandle(entry.Window.Handle);
        if (window == null)
        {
            return ElementResolution.Failed(UIElementErrorCode.ElementStale,
                $"Element ref '{entry.Ref}' is stale: its window can no longer be read. Find the element again with find_ui_elements.");
        }

        // 2. The same runtime id in the window (an engine can hand out a new proxy for the same element), if it is still
        //    the same control
        var byId = Search(
            window,
            new ElementCriteria(),
            UiaCondition.PropertyEquals(UiaProperty.RuntimeId, entry.RuntimeId),
            e => SameIdentity(fingerprint, Fingerprint(e, null)),
            cancellationToken);
        if (byId.MatchCount == 1)
        {
            return Resolved(entry, byId.Matches[0].Node, byId.Matches[0], ElementResolutionPath.RuntimeId);
        }

        // 3. Re-created: the stored selector, each result checked against the fingerprint. Only a selector the issuing read
        //    proved to fit this one element of the window is stored (see Register); without one, a look-alike may be a
        //    sibling (another row's Delete button), so the ref is not moved to it
        if (selector == null)
        {
            if (byId.Stop == ElementSearchStop.Timeout)
            {
                return ElementResolution.Failed(UIElementErrorCode.OperationTimedOut,
                    $"Element ref '{entry.Ref}': its element ({Describe(fingerprint)}) did not answer, and the search for it did not " +
                    "finish in time; the ref was not dropped. Try again with more time, or find the element with find_ui_elements.");
            }

            return ElementResolution.Failed(UIElementErrorCode.ElementStale,
                $"Element ref '{entry.Ref}' is stale: its element ({Describe(fingerprint)}) is gone" +
                (byId.Complete ? "" : " (the search of the window did not finish)") +
                ", and the ref is not moved to a look-alike, because the read that issued it did not prove that its " +
                "description fits only this element. Find the element again with find_ui_elements.");
        }

        var (condition, filter) = selector.Compile();
        var found = Search(window, selector, condition, e => (filter == null || filter(e)) && SameElement(fingerprint, e), cancellationToken);
        switch (found.MatchCount)
        {
            case 1 when found.Complete:
                return Rebind(entry, found.Matches[0]);
            case > 1:
                return ElementResolution.Failed(UIElementErrorCode.AmbiguousElement,
                    $"Element ref '{entry.Ref}' is ambiguous: its element ({Describe(fingerprint)}) is gone, and " +
                    $"{(found.Complete ? "" : "at least ")}{found.MatchCount} elements now fit its description, so the ref is " +
                    "not moved to any of them. Find the element you mean with find_ui_elements (narrow it with withinRef, " +
                    "className or controlType) and use its ref.",
                    found.MatchCount);
            case 1:
                return ElementResolution.Failed(UIElementErrorCode.OperationTimedOut,
                    $"Element ref '{entry.Ref}': its element ({Describe(fingerprint)}) was re-created, but the search of the window " +
                    "did not finish, so it is not certain that only one element fits; the ref was not moved. Try again, or " +
                    "find the element again with find_ui_elements.");
            case 0 when found.Stop == ElementSearchStop.Timeout:
                return ElementResolution.Failed(UIElementErrorCode.OperationTimedOut,
                    $"Element ref '{entry.Ref}': its element ({Describe(fingerprint)}) is gone, and the search for it again did " +
                    "not finish in time; the ref was not moved. Try again with more time, or find the element with find_ui_elements.");
            default:
                return ElementResolution.Failed(UIElementErrorCode.ElementStale,
                    $"Element ref '{entry.Ref}' is stale: its element ({Describe(fingerprint)}) is gone" +
                    (found.Complete ? "" : " (the search of the window did not finish)") +
                    ". Find the element again with find_ui_elements.");
        }
    }

    /// <summary>
    /// A search of the window during a resolution. A control type the engine cannot search for (the managed client and
    /// AppBar or SemanticZoom) is tested on the server instead.
    /// </summary>
    private ElementSearchOutcome Search(
        IUiaNode window, ElementCriteria criteria, UiaCondition condition, Func<UiaElement, bool> filter, CancellationToken cancellationToken)
    {
        try
        {
            return new ElementSearch(_tree, condition, filter, ResolutionKeep).Run(window, cancellationToken);
        }
        catch (UiaConditionNotSupportedException) when (criteria.ControlTypes is { Count: > 0 })
        {
            var (serverCondition, serverFilter) = criteria.Compile(controlTypesOnServer: true);
            return new ElementSearch(_tree, serverCondition, e => (serverFilter == null || serverFilter(e)) && filter(e), ResolutionKeep)
                .Run(window, cancellationToken);
        }
    }

    private ElementResolution Resolved(Entry entry, IUiaNode node, UiaElement element, ElementResolutionPath via)
    {
        lock (_gate)
        {
            if (!_byRef.ContainsKey(entry.Ref))
            {
                return ElementResolution.Failed(UIElementErrorCode.ElementStale, UnknownRefMessage(entry.Ref));
            }

            entry.Node = node;
            entry.Fingerprint = Merge(entry.Fingerprint, Fingerprint(element, null));
            Touch(entry);
            return new ElementResolution(new ResolvedElement(entry.Ref, entry.Window.Handle, node, element, via));
        }
    }

    /// <summary>
    /// Moves a ref to its re-created element. When that element already has a ref of its own, the old ref is dropped and
    /// the element's own ref is returned, so one element never has two refs.
    /// </summary>
    private ElementResolution Rebind(Entry entry, UiaElement element)
    {
        lock (_gate)
        {
            if (!_byRef.ContainsKey(entry.Ref) || element.RuntimeId is not { Count: > 0 } runtimeId)
            {
                return ElementResolution.Failed(UIElementErrorCode.ElementStale, UnknownRefMessage(entry.Ref));
            }

            var key = RuntimeKey(runtimeId);
            if (entry.Window.Entries.TryGetValue(key, out var owner) && !ReferenceEquals(owner, entry))
            {
                Remove(entry);
                owner.Node = element.Node;
                Touch(owner);
                return new ElementResolution(new ResolvedElement(
                    owner.Ref, owner.Window.Handle, element.Node, element, ElementResolutionPath.Selector));
            }

            entry.Window.Entries.Remove(entry.Key);
            entry.Key = key;
            entry.RuntimeId = runtimeId.ToArray();
            entry.Window.Entries[key] = entry;
            entry.Node = element.Node;
            entry.Fingerprint = Merge(entry.Fingerprint, Fingerprint(element, null));
            Touch(entry);
            return new ElementResolution(new ResolvedElement(
                entry.Ref, entry.Window.Handle, element.Node, element, ElementResolutionPath.Selector));
        }
    }

    private string UnknownRefMessage(string elementRef)
    {
        var issued = false;
        if (ElementRefs.TryParse(elementRef, out var window, out var element))
        {
            lock (_gate)
            {
                // A window still open also knows how far its element numbers went
                issued = window >= _firstWindowSeq && window < _nextWindowSeq &&
                         !_windowsByHandle.Values.Any(r => r.Seq == window && element > r.LastElement);
            }
        }

        return issued
            ? $"Element ref '{elementRef}' is stale: its element is gone, its window closed, or the ref expired (the server " +
              $"keeps the {_capacity} most recently used refs). Find the element again with find_ui_elements."
            : $"Element ref '{elementRef}' was not issued by this server session (refs do not survive a server restart). " +
              "Find the element again with find_ui_elements.";
    }

    /// <summary>
    /// The window's record, created when the window has none (or its handle now names a window of another process or
    /// class).
    /// </summary>
    private WindowRecord WindowRecordFor(nint handle, WindowIdentity identity)
    {
        if (_windowsByHandle.TryGetValue(handle, out var record))
        {
            if (record.Identity == identity)
            {
                return record;
            }

            DropWindow(record);
        }

        record = new WindowRecord(_nextWindowSeq++, handle, identity);
        _windowsByHandle[handle] = record;
        return record;
    }

    /// <summary>The window's process and class, or null when the handle is no window.</summary>
    private WindowIdentity? WindowIdentityOf(nint handle)
    {
        try
        {
            if (!_windows.IsWindow(handle))
            {
                return null;
            }

            var processId = _windows.GetWindowThreadProcessId(handle);
            return processId == 0 ? null : new WindowIdentity(processId, _windows.GetClassName(handle) ?? string.Empty);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the record's window still exists with the same process and class: its handle was not reused by another
    /// window. A reuse by a window of the same process and class is not told apart; it is rare, since a handle value comes
    /// back only after its slot's 16-bit reuse counter wraps.
    /// </summary>
    private bool IsSameWindow(WindowRecord record) => WindowIdentityOf(record.Handle) == record.Identity;

    /// <summary>Drops a window and its refs. Under the lock.</summary>
    private void DropWindow(WindowRecord record)
    {
        foreach (var entry in record.Entries.Values.ToList())
        {
            Remove(entry);
        }

        if (_windowsByHandle.TryGetValue(record.Handle, out var current) && ReferenceEquals(current, record))
        {
            _windowsByHandle.Remove(record.Handle);
        }
    }

    /// <summary>Drops one ref. Under the lock.</summary>
    private void Remove(Entry entry)
    {
        _byRef.Remove(entry.Ref);
        if (entry.Window.Entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
        {
            entry.Window.Entries.Remove(entry.Key);
        }

        if (entry.LruNode?.List != null)
        {
            _lru.Remove(entry.LruNode);
        }
    }

    /// <summary>Marks an entry as just used. Under the lock.</summary>
    private void Touch(Entry entry)
    {
        entry.LastUsed = _clock.GetUtcNow();
        if (entry.LruNode?.List != null)
        {
            _lru.Remove(entry.LruNode);
            _lru.AddFirst(entry.LruNode);
        }
    }

    private static string RuntimeKey(IReadOnlyList<int> runtimeId) =>
        string.Join('.', runtimeId.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Who an element is, as read now.</summary>
    internal static ElementFingerprint Fingerprint(UiaElement element, IReadOnlyList<int>? indexPath) =>
        new(
            EmptyToNull(element.Properties.Name),
            element.Properties.ControlType,
            EmptyToNull(element.Properties.AutomationId),
            EmptyToNull(element.ClassName),
            indexPath?.ToArray());

    /// <summary>A newer read of the same element: its name and index path win; a class it did not read is kept.</summary>
    private static ElementFingerprint Merge(ElementFingerprint stored, ElementFingerprint read) =>
        stored with
        {
            Name = read.Name,
            ClassName = read.ClassName ?? stored.ClassName,
            IndexPath = read.IndexPath ?? stored.IndexPath
        };

    /// <summary>
    /// Whether an element read now can be the stored one: the same control type and automation id, and the same class when
    /// both reads have one. The name is not compared, since labels change ("Play" becomes "Pause"), except for a list,
    /// tree or grid item without an automation id (<see cref="NameIsIdentity"/>).
    /// </summary>
    internal static bool SameIdentity(ElementFingerprint stored, ElementFingerprint read) =>
        string.Equals(stored.ControlType, read.ControlType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(stored.AutomationId ?? string.Empty, read.AutomationId ?? string.Empty, StringComparison.Ordinal) &&
        (stored.ClassName == null || read.ClassName == null ||
         string.Equals(stored.ClassName, read.ClassName, StringComparison.Ordinal)) &&
        (!NameIsIdentity(stored) || string.Equals(stored.Name ?? string.Empty, read.Name ?? string.Empty, StringComparison.Ordinal));

    /// <summary>
    /// True for a ListItem, TreeItem or DataItem without an automation id: its name says which item it is. A virtualizing
    /// list (WinUI, WPF with recycling, many web lists) reuses an item's element, and its runtime id, for another row when
    /// it scrolls, so the same runtime id with another name is another item.
    /// </summary>
    internal static bool NameIsIdentity(ElementFingerprint fingerprint) =>
        fingerprint.AutomationId == null &&
        (fingerprint.ControlType.Equals("ListItem", StringComparison.OrdinalIgnoreCase) ||
         fingerprint.ControlType.Equals("TreeItem", StringComparison.OrdinalIgnoreCase) ||
         fingerprint.ControlType.Equals("DataItem", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether an element found by a selector is the stored element re-created: the same identity, and, when it has no
    /// automation id to say who it is, the same name.
    /// </summary>
    internal static bool SameElement(ElementFingerprint stored, UiaElement element)
    {
        var read = Fingerprint(element, null);
        return SameIdentity(stored, read) &&
               (stored.AutomationId != null || string.Equals(stored.Name ?? string.Empty, read.Name ?? string.Empty, StringComparison.Ordinal));
    }

    private static string Describe(ElementFingerprint fingerprint)
    {
        var parts = new List<string> { fingerprint.ControlType };
        if (fingerprint.Name != null)
        {
            parts.Add($"'{fingerprint.Name}'");
        }

        if (fingerprint.AutomationId != null)
        {
            parts.Add($"automationId '{fingerprint.AutomationId}'");
        }

        if (fingerprint.IndexPath is { Count: > 0 } path)
        {
            parts.Add($"at {string.Join('/', path)}");
        }

        return string.Join(' ', parts);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>What tells a window apart from a later window that reuses its handle.</summary>
    private readonly record struct WindowIdentity(uint ProcessId, string ClassName);

    /// <summary>A window whose elements have refs: its number, and the handle, process and class it was seen with.</summary>
    private sealed class WindowRecord(int seq, nint handle, WindowIdentity identity)
    {
        public int Seq { get; } = seq;
        public nint Handle { get; } = handle;
        public WindowIdentity Identity { get; } = identity;

        /// <summary>The number of the last element given a ref; numbers are never given twice.</summary>
        public int LastElement { get; set; }

        /// <summary>The entries by runtime id.</summary>
        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One ref. Its mutable fields change only under the registry's lock.</summary>
    private sealed class Entry(string elementRef, WindowRecord window, string key, int[] runtimeId, IUiaNode node, ElementFingerprint fingerprint)
    {
        public string Ref { get; } = elementRef;
        public WindowRecord Window { get; } = window;
        public string Key { get; set; } = key;
        public int[] RuntimeId { get; set; } = runtimeId;
        public IUiaNode Node { get; set; } = node;
        public ElementFingerprint Fingerprint { get; set; } = fingerprint;
        public ElementCriteria? Selector { get; set; }
        public long Generation { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public LinkedListNode<Entry>? LruNode { get; set; }
    }
}
