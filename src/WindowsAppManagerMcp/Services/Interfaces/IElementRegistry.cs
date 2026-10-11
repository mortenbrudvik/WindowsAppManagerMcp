using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The element registry (roadmap M3.4): gives the elements the read tools return a ref, such as "w12e5", and turns a ref
/// back into the live element for the calls that act on it, wait for it or read it (M3.6 element_action, M3.8
/// wait_for_element, M3.9 the text and table reads, M3.11 the marks of a capture).
/// </summary>
/// <remarks>
/// <para><b>Refs.</b> "w&lt;window&gt;e&lt;element&gt;": the window number is given to a window the first time one of its
/// elements is registered, and the element number counts up within it. A ref is never given to another element in the
/// same server session: the same element (the same runtime id under the same window, with the same control type,
/// automation id and class) keeps its ref across reads, and a new element gets a new number. Window numbers start at a
/// random value per session, so a ref an agent kept from before a server restart practically never names an element of
/// the new session (it is stale instead). Refs are case-insensitive and opaque: only their form is checked
/// (<see cref="Services.ElementRefs.IsWellFormed"/>).</para>
/// <para><b>Lifetime.</b> An entry holds the element, its runtime id, its window, the selector that found it (find_ui_elements'
/// identity criteria) or none (a tree walk, the focused element, the element at a point), its fingerprint (name, control
/// type, automation id, class, index path), the generation of the read that last saw it and when it was last used. The
/// registry keeps the <see cref="Services.ElementRegistry.DefaultCapacity"/> most recently used entries; the others expire.
/// A window's entries are dropped when the window is gone (IsWindow fails, or its handle now belongs to another process).</para>
/// <para><b>Resolution</b> (<see cref="Resolve"/>), in order: (1) a live read of the stored element (one call that asks the
/// provider; a runtime id alone is answered from memory and cannot tell that the element is gone); if it is alive with the
/// same runtime id, that is the element. (2) A search of the window for the runtime id, accepted when the element found has
/// the stored control type, automation id and class. (3) The stored selector, or one made from the fingerprint (the
/// automation id, else the name, with control type and class), run again and each result checked against the fingerprint
/// (with the name too when there is no automation id): one result is the element, re-created, and the ref moves to it;
/// several are <see cref="UIElementErrorCode.AmbiguousElement"/>, none <see cref="UIElementErrorCode.ElementStale"/>. A ref
/// never silently resolves to a different element. Both searches are <see cref="Services.ElementSearch"/>es, bounded by the
/// caller's deadline.</para>
/// <para><b>Threads.</b> Every member is thread safe. <see cref="Register"/> and <see cref="Lookup"/> make no UI Automation
/// call. <see cref="Resolve"/> does: call it inside a read on an <see cref="IUiaWorker"/> thread (after the window's
/// hung check), or use <see cref="ResolveAsync"/>, which does both.</para>
/// </remarks>
public interface IElementRegistry
{
    /// <summary>
    /// A new read generation: each read that registers elements takes one, and its entries remember it, so a later caller
    /// can tell which read last saw an element (M3.11 ties a capture's marks to it). Also drops the entries of windows that
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
public sealed record ElementOrigin(long Generation, Services.ElementCriteria? Selector = null, IReadOnlyList<int>? IndexPath = null);

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

    /// <summary>The element was re-created: the stored selector found exactly one element with its fingerprint.</summary>
    Selector
}

/// <summary>A ref's live element.</summary>
/// <param name="Ref">The element's ref: the one asked for, or, when the element found again already had a ref of its own,
/// that one (the asked-for ref is then dropped).</param>
/// <param name="WindowHandle">The window the element belongs to.</param>
/// <param name="Node">The element, for further reads and pattern calls through the same tree.</param>
/// <param name="Element">Its properties, read during the resolution.</param>
public sealed record ResolvedElement(string Ref, nint WindowHandle, IUiaNode Node, UiaElement Element, ElementResolutionPath Via);

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
public sealed record ElementLookup(
    string Ref,
    nint WindowHandle,
    ElementFingerprint? Fingerprint,
    long Generation,
    UIElementErrorCode ErrorCode = UIElementErrorCode.None,
    string? Error = null)
{
    public bool Success => ErrorCode == UIElementErrorCode.None;
}
