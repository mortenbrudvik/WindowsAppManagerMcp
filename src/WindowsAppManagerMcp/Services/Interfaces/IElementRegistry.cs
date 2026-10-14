using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The element registry (roadmap M3.4): gives the elements the read tools return a ref, such as "w4120035e17", and turns a ref
/// back into the live element for the calls that act on it, wait for it or read it (M3.6 element_action, M3.8
/// wait_for_element, M3.9 the text and table reads, M3.11 the marks of a capture).
/// </summary>
/// <remarks>
/// <para><b>Refs.</b> "w&lt;window&gt;e&lt;element&gt;": the window number is given to a window the first time one of its
/// elements is registered, and the element number counts up within it. A ref is never given to another element in the
/// same server session: the same element (the same runtime id under the same window, with the same identity, see below)
/// keeps its ref across reads, and a new element gets a new number. Window numbers start at a random value in eight
/// million per session (<see cref="Services.ElementRegistry.FirstWindowNumberMin"/>), so a ref an agent kept from before a
/// server restart is "not issued by this session" unless the new session happens to number the same window (about 1 in
/// 80,000 per remembered window for a session that numbers 100): a probability, not a guarantee, since nothing of the
/// old session survives. Refs are case-insensitive and opaque: only their form is checked
/// (<see cref="Services.ElementRefs.IsWellFormed"/>).</para>
/// <para><b>Identity.</b> The same control type and automation id, and the same class when both reads have one. The name
/// is not compared ("Play" becomes "Pause"), except for a ListItem, TreeItem or DataItem without an automation id, whose
/// name says which item it is: a virtualizing list reuses an item's element and runtime id for another row
/// (<see cref="Services.ElementRegistry.NameIsIdentity"/>). An element inside such a recycled item keeps its own identity
/// (a check box of the row), so it is also checked against its row (<see cref="ElementOrigin.Row"/>, see
/// <see cref="Services.ElementRows"/>): a read that sees it in another item gives it a new ref.</para>
/// <para><b>Lifetime.</b> An entry holds the element, its runtime id, its window, a selector (see below), its fingerprint
/// (name, control type, automation id, class, index path), the generation of the read that last saw it and when it was
/// last used. The registry keeps the <see cref="Services.ElementRegistry.DefaultCapacity"/> most recently used entries
/// (twice the largest read); the others expire. A window's entries are dropped when the window is gone (IsWindow fails,
/// or its handle now names a window of another process, class or thread).</para>
/// <para><b>Selector.</b> find_ui_elements' identity criteria, stored only when its search proved that they fit no other
/// element of the window with this element's fingerprint (<see cref="ElementOrigin.Unique"/>: a complete search of the
/// whole window, not below withinRef). A tree walk, the focused element and the element at a point store none.</para>
/// <para><b>Resolution</b> (<see cref="Resolve"/>), in order: (1) a live read of the stored element (one call that asks the
/// provider; a runtime id alone is answered from memory and cannot tell that the element is gone), accepted when it reads
/// the stored runtime id and identity. An item its list has not realized (find_ui_elements' searchVirtualized gives refs of
/// such items) is often a placeholder that fails that read as a whole for the properties it does not serve; it is then read
/// property by property (<see cref="IUiaTree.ReadVirtualizedItem"/>) and accepted while it is still such an item, with the
/// VirtualizedItem pattern and the stored runtime id and identity. (2) A search of the window for the runtime id, accepted when the element found has
/// the stored identity. In both, an element with a recorded row is accepted only while the item that many levels up is
/// that row (its runtime id, and its name when it has no automation id): one call per level and one read, else
/// ElementStale ("its row now shows another item"). (3) Only with a stored selector: the selector run again and each result checked against the
/// fingerprint (with the name too when there is no automation id): one result is the element, re-created, and the ref
/// moves to it (an element with a recorded row only when that result is in the same row: the item that many levels up has
/// the row's name and automation id; otherwise ElementStale) (<see cref="ResolvedElement.Moved"/>, until a read returns the element again); several are
/// <see cref="UIElementErrorCode.AmbiguousElement"/>, none <see cref="UIElementErrorCode.ElementStale"/>. Without a selector a gone element is ElementStale: a look-alike may be a
/// sibling (another row's Delete button). A ref never silently resolves to a different element it can tell apart. Both
/// searches are <see cref="Services.ElementSearch"/>es of the window's own tree (not the windows it owns, see
/// <see cref="Services.OwnedWindows"/>), bounded by the caller's deadline; a search that did not finish is
/// OperationTimedOut, never a verdict that moves the ref.</para>
/// <para><b>Threads.</b> Every member is thread safe. <see cref="Register"/> and <see cref="Lookup"/> make no UI Automation
/// call. <see cref="Resolve"/> does: call it inside a read on an <see cref="IUiaWorker"/> thread (after the window's
/// hung check), or use <see cref="ResolveAsync"/>, which does both.</para>
/// </remarks>
public interface IElementRegistry
{
    /// <summary>
    /// A new read generation: each read that registers elements takes one, and its entries remember it, so a later caller
    /// can tell which read last saw an element (kept for a future tie between reads, such as a capture and its marks). Also drops the entries of windows that
    /// are gone.
    /// </summary>
    long NextGeneration();

    /// <summary>
    /// The ref of an element read under a window: the ref it already has when the same element was registered before
    /// (its node, name, index path and generation are updated), otherwise a new one. Makes no UI Automation call.
    /// </summary>
    /// <param name="windowHandle">The window the element was read under (a resolution searches it).</param>
    /// <param name="element">The element as it was read; its runtime id identifies it.</param>
    /// <param name="origin">The read's generation, and the selector or index path when there is one.</param>
    /// <returns>The ref, or null when the element has no runtime id or the handle is no window.</returns>
    string? Register(nint windowHandle, UiaElement element, ElementOrigin origin);

    /// <summary>What the registry knows about a ref, without UI Automation: its window and fingerprint, or why it is unusable.</summary>
    ElementLookup Lookup(string elementRef);

    /// <summary>
    /// The live element of a ref, found again when it was re-created (see the remarks). Makes UI Automation calls: run it on
    /// a UI Automation worker thread. Never throws for a provider failure or a timeout: they come back as the result's code.
    /// </summary>
    /// <param name="elementRef">The ref.</param>
    /// <param name="cancellationToken">The caller's deadline: the searches stop when it is cancelled.</param>
    ElementResolution Resolve(string elementRef, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="Resolve"/> on a worker thread, after the hung check of the ref's window (NotResponding), under
    /// <paramref name="timeout"/> (OperationTimedOut).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ElementResolution> ResolveAsync(string elementRef, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>How many refs the registry holds.</summary>
    int Count { get; }
}

/// <summary>How a registered element was found.</summary>
/// <param name="Generation">The read's generation (<see cref="IElementRegistry.NextGeneration"/>).</param>
/// <param name="Selector">find_ui_elements' identity criteria (no visibleOnly or enabledOnly: they are states, not who the
/// element is), or null when the element was not found by a query.</param>
/// <param name="IndexPath">The element's index among its parent's children at each level below the window, when the read
/// knows it (a tree walk), or null.</param>
/// <param name="Unique">Whether the read proved that <paramref name="Selector"/> fits no other element of the window with
/// this element's fingerprint: true (the selector is kept, and a resolution may move the ref to the element re-created),
/// false (another element fits it: a kept selector is dropped) or null (not known: nothing changes).</param>
/// <param name="Row">The list, tree or grid item the element is inside, when the read knows it (a tree walk; find_ui_elements,
/// get_focused_element, get_element_at_point and get_active_popups read it: <see cref="Services.ElementRows"/>), or null.</param>
public sealed record ElementOrigin(
    long Generation,
    Services.ElementCriteria? Selector = null,
    IReadOnlyList<int>? IndexPath = null,
    bool? Unique = null,
    ElementRow? Row = null);

/// <summary>
/// The list, tree or grid item an element is inside: its nearest ListItem, TreeItem or DataItem ancestor as a read saw it
/// (see <see cref="Services.ElementRows"/>).
/// </summary>
/// <param name="RuntimeId">The item's runtime id.</param>
/// <param name="Name">The item's name, or null.</param>
/// <param name="AutomationId">The item's automation id, or null.</param>
/// <param name="Levels">How many levels above the element the item is (1: its parent; 0 for the item itself, in a tree
/// walk).</param>
public sealed record ElementRow(IReadOnlyList<int> RuntimeId, string? Name, string? AutomationId, int Levels);

/// <summary>Who an element is, as it was last read: what a resolution checks an element found again against.</summary>
/// <param name="ClassName">The class name, or null when the read did not get it (an element a walk read on its own); then
/// it is not compared.</param>
/// <param name="IndexPath">Where a tree walk found it, or null.</param>
public sealed record ElementFingerprint(
    string? Name,
    string ControlType,
    string? AutomationId,
    string? ClassName,
    IReadOnlyList<int>? IndexPath);

/// <summary>How a ref was resolved.</summary>
public enum ElementResolutionPath
{
    /// <summary>The stored element is alive.</summary>
    Live,

    /// <summary>The stored element was gone, but the window holds an element with its runtime id and identity.</summary>
    RuntimeId,

    /// <summary>The element was re-created: its proven-unique selector found exactly one element with its fingerprint.</summary>
    Selector
}

/// <summary>A ref's live element.</summary>
/// <param name="Ref">The element's ref: the one asked for, or, when the element found again already had a ref of its own,
/// that one (the asked-for ref is then dropped).</param>
/// <param name="WindowHandle">The window the element belongs to.</param>
/// <param name="Node">The element, for further reads and pattern calls through the same tree.</param>
/// <param name="Element">Its properties, read during the resolution.</param>
/// <param name="Via">How it was found: this resolution's path.</param>
/// <param name="Moved">The ref names a re-created element it was moved to (<see cref="ElementResolutionPath.Selector"/>, in
/// this resolution or an earlier one) that no read has returned since: the element the ref was given for is gone, and this
/// one only fits its description. The tools that take a ref report it (refMoved), and element_action does not act on it
/// until a read (find_ui_elements) returns the element again.</param>
public sealed record ResolvedElement(
    string Ref, nint WindowHandle, IUiaNode Node, UiaElement Element, ElementResolutionPath Via, bool Moved = false);

/// <summary>The outcome of <see cref="IElementRegistry.Resolve"/>.</summary>
/// <param name="Element">The live element, or null when the ref could not be resolved.</param>
/// <param name="ErrorCode">ElementStale, AmbiguousElement, WindowNotFound (the window is there but UI Automation cannot read
/// it), NotResponding, OperationTimedOut or OperationException; None on success.</param>
/// <param name="CandidateCount">For AmbiguousElement: how many elements fit the ref's description.</param>
public sealed record ElementResolution(
    ResolvedElement? Element,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    string? Error = null,
    int CandidateCount = 0)
{
    public bool Success => Element != null;

    public static ElementResolution Failed(UIElementErrorCode code, string error, int candidates = 0) =>
        new(null, code, error, candidates);
}

/// <summary>What <see cref="IElementRegistry.Lookup"/> knows about a ref.</summary>
/// <param name="Ref">The ref, normalized (lower case).</param>
/// <param name="WindowHandle">Its window, or 0 when the ref is unusable.</param>
/// <param name="Fingerprint">Its element as last read, or null when the ref is unusable.</param>
/// <param name="ErrorCode">None, or ElementStale with <paramref name="Error"/>.</param>
/// <param name="NotIssued">True when this server session never issued the ref (one from before a server restart, a made-up
/// one, or no ref at all): it then says nothing about any element. False for a ref that was issued, also one that is stale
/// because its window closed or it was dropped (its element gone, or the ref expired).</param>
public sealed record ElementLookup(
    string Ref,
    nint WindowHandle,
    ElementFingerprint? Fingerprint,
    long Generation,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    string? Error = null,
    bool NotIssued = false)
{
    public bool Success => ErrorCode == UIElementErrorCode.None;
}
