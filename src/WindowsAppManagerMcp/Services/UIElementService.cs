using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for retrieving UI element trees from windows using Windows UI Automation. The tree is
/// read through <see cref="IUiaTree"/>, on an <see cref="IUiaWorker"/> thread.
/// </summary>
/// <remarks>
/// Walk rules:
/// <list type="bullet">
/// <item>A window of an app the user's target policy denies is not read (TargetDeniedByPolicy, see
/// <see cref="UiaTargets.ReadVerdict"/>): checked before the walk, the warm-up and the hung-window check.</item>
/// <item>A window that does not respond is not read (NotResponding): Windows reports it hung, or it does not answer a
/// message within <see cref="RespondTimeoutMs"/>. Every UI Automation call to it would block until it recovers.</item>
/// <item>The walk is breadth first (roadmap M3.1): all of a level before the next, so a walk cut short by the timeout
/// or by maxElements keeps the levels nearest the window. The children of an element are first listed without their
/// properties (<see cref="IUiaTree.GetChildren"/>, one cheap call). A level of at most <see cref="CachedLevelLimit"/>
/// children is then read in one more cross-process call with their properties (a cache request:
/// <see cref="IUiaTree.GetChildElements"/>); a wider level, or one whose cached read fails, is read one element at a
/// time, so the walk stops in the middle of it at maxElements or the timeout, and one failing element costs only
/// itself.</item>
/// <item>maxDepth counts levels below the window: its children are level 1, and nothing below
/// level maxDepth is read.</item>
/// <item>The controlTypes, size, interactable and actionable filters decide only whether an element itself is
/// returned. An element that fails one is left out, and the elements inside it that pass take its
/// place in its parent's list. Only an offscreen element (visibleOnly) leaves out its whole subtree.</item>
/// <item>maxElements stops the walk once that many elements are returned. truncationReason MaxElements says something
/// was left unread: an element of the level being read, or the children of an element whose children were still to
/// be read. To tell, the elements still waiting are listed (one cheap call each) until one has a child; when that
/// cannot be told (the timeout, a failing call), it is assumed.</item>
/// <item>An element that cannot be read is skipped and counted, and the walk goes on with its
/// siblings. An element whose interaction properties cannot be read (interactableOnly) is left out
/// and counted too, but the elements inside it are still walked. A timeout ends the walk and returns
/// what was read so far; a level already fetched when it passes is kept whole, because reading it cost no further
/// call. So does a walk that is abandoned because a UI Automation call did not return (a hung provider): the
/// elements it had read until then are returned.</item>
/// <item>A window that shows web content (Chromium, Electron, WebView2) is warmed up first (<see cref="IAccessibilityWarmup"/>,
/// roadmap M3.3): its page's accessibility tree is asked for and waited for, within a share of the timeout, and the result
/// says what came of it (accessibilityWarmup) instead of returning a silently thin tree.</item>
/// <item>Each element names its UI framework only where it changes from the returned element it is inside (or, at the top,
/// from the window's), so a web page inside a native frame shows where it starts without repeating it on every element.</item>
/// </list>
/// </remarks>
public class UIElementService : IUIElementService
{
    private const int MaxAllowedDepth = 10;
    private const int DefaultMaxDepth = 5;

    /// <summary>
    /// The widest level read in one call with its properties. A wider level is read one element at a time, so that
    /// maxElements and the timeout stop the walk in it: one call for thousands of children with all their properties
    /// can outlast the deadline, and its elements would all be lost.
    /// </summary>
    public const int CachedLevelLimit = 200;

    /// <summary>A walk reports its progress after every this many elements read.</summary>
    public const int ProgressInterval = 100;

    /// <summary>How long the hung-window check waits for the window to answer a message.</summary>
    public const uint RespondTimeoutMs = UiaTargets.RespondTimeoutMs;

    /// <summary>
    /// How long to keep waiting after the timeout for the walk to stop and hand back its partial
    /// tree. A walk still stuck in a UI Automation call after that is abandoned.
    /// </summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly INativeWindowWrapper? _windows;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly IElementRegistry? _registry;
    private readonly ITargetPolicy? _policy;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the walk under a deadline.</param>
    /// <param name="windows">Checks that the window responds before it is read, and identifies the application
    /// whose window is read (for a UWP app its own process, see <see cref="UwpFrames"/>), so a hung application blocks
    /// only reads of itself (see <see cref="IUiaWorker"/>). Without it all reads share one count and nothing is
    /// checked first.</param>
    /// <param name="warmup">Warms up the web content of Chromium, Electron and WebView2 windows before they are read
    /// (roadmap M3.3). The server always passes it; without it no window is warmed up.</param>
    /// <param name="registry">Gives every returned element its ref (roadmap M3.4). Without it the elements have none.</param>
    /// <param name="policy">The user's target policy, which refuses reads of a window of an app it denies. The server always
    /// passes it; without it (or without <paramref name="windows"/>) no window is refused.</param>
    public UIElementService(
        IUiaTree tree,
        IUiaWorker worker,
        INativeWindowWrapper? windows = null,
        IAccessibilityWarmup? warmup = null,
        IElementRegistry? registry = null,
        ITargetPolicy? policy = null)
    {
        _tree = tree;
        _worker = worker;
        _windows = windows;
        _warmup = warmup;
        _registry = registry;
        _policy = policy;
    }

    /// <inheritdoc />
    public bool IsAvailable() => _tree.IsAvailable();

    /// <inheritdoc />
    public async Task<UIElementResult> GetUIElementsAsync(
        nint windowHandle,
        int maxDepth = DefaultMaxDepth,
        UIElementFilter? filter = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (windowHandle == nint.Zero)
        {
            return CreateErrorResult(
                windowHandle,
                stopwatch,
                "Invalid window handle (zero)",
                UIElementErrorCode.InvalidHandle);
        }

        maxDepth = Math.Clamp(maxDepth, 1, MaxAllowedDepth);

        if (ParseControlTypes(filter?.ControlTypes, out var controlTypes) is { } invalidControlTypes)
        {
            return CreateErrorResult(windowHandle, stopwatch, invalidControlTypes, UIElementErrorCode.InvalidControlType);
        }

        // Before the worker: no warm-up and no UI Automation call reaches a window of an app the user denies
        if (UiaTargets.PolicyRefusal(_windows, _policy, windowHandle) is { } denied)
        {
            return CreateErrorResult(windowHandle, stopwatch, denied, UIElementErrorCode.TargetDeniedByPolicy);
        }

        var started = new WalkStarted();
        UIElementResult result;
        try
        {
            result = await _worker.RunAsync(
                TargetProcessId(windowHandle),
                token => ReadTree(windowHandle, maxDepth, filter, controlTypes, stopwatch, started, progress, token),
                cancellationToken,
                StopGrace).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // The worker gave up on a read stuck in an application that does not answer. The walk thread is
            // still blocked in a UI Automation call; what it had read until then is returned all the same
            result = started.Walk?.PartialResult(windowHandle, stopwatch) ??
                     CreateErrorResult(windowHandle, stopwatch, ex.Message, UIElementErrorCode.OperationTimedOut);
        }

        return result with { WindowState = ReadWindowState(windowHandle), AccessibilityWarmup = started.Warmup };
    }

    /// <summary>
    /// The state of the window's top-level window: Minimized when it, or the window itself, is minimized (its elements
    /// are offscreen), Maximized or Normal otherwise. Null without a window wrapper, or when the handle is no window.
    /// These are reads Windows answers without the window's thread, so a hung application does not block them.
    /// </summary>
    private WindowState? ReadWindowState(nint windowHandle)
    {
        if (_windows == null)
        {
            return null;
        }

        try
        {
            if (!_windows.IsWindow(windowHandle))
            {
                return null;
            }

            var root = _windows.GetAncestor(windowHandle, WindowAncestor.Root);
            if (root == nint.Zero)
            {
                root = windowHandle;
            }

            if (_windows.IsIconic(root) || _windows.IsIconic(windowHandle))
            {
                return WindowState.Minimized;
            }

            return _windows.IsZoomed(root) ? WindowState.Maximized : WindowState.Normal;
        }
        catch (Exception)
        {
            // Only the reported state depends on it
            return null;
        }
    }

    /// <summary>Why the window must not be read, or null when it responds (see <see cref="UiaTargets.NotRespondingReason"/>).</summary>
    private string? NotRespondingReason(nint windowHandle) => UiaTargets.NotRespondingReason(_windows, windowHandle);

    /// <summary>
    /// The walk of one call, once it has started: the caller reads its partial tree when the worker abandons it.
    /// </summary>
    private sealed class WalkStarted
    {
        private Walk? _walk;
        private AccessibilityWarmupInfo? _warmup;

        public Walk? Walk
        {
            get => Volatile.Read(ref _walk);
            set => Volatile.Write(ref _walk, value);
        }

        /// <summary>What the warm-up of the window's web content did, when it ran (roadmap M3.3).</summary>
        public AccessibilityWarmupInfo? Warmup
        {
            get => Volatile.Read(ref _warmup);
            set => Volatile.Write(ref _warmup, value);
        }
    }

    /// <summary>The process a stuck read of the window is counted against (see <see cref="UiaTargets.TargetProcessId"/>).</summary>
    private int TargetProcessId(nint windowHandle) => UiaTargets.TargetProcessId(_windows, windowHandle);

    /// <summary>
    /// Validates the controlTypes filter. Returns an error message for unknown names, or null with
    /// the canonical names in <paramref name="controlTypes"/> (null when the filter is not set).
    /// </summary>
    internal static string? ParseControlTypes(IReadOnlyList<string>? names, out HashSet<string>? controlTypes)
    {
        controlTypes = null;
        var requested = names?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (requested is not { Count: > 0 })
        {
            return null;
        }

        var unknown = requested.Where(n => UiaControlTypes.Canonicalize(n) == null).ToList();
        if (unknown.Count > 0)
        {
            return $"Unknown control type{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", unknown.Select(n => $"'{n.Trim()}'"))}. " +
                   $"Valid names: {string.Join(", ", UiaControlTypes.Names)}.";
        }

        controlTypes = new HashSet<string>(
            requested.Select(n => UiaControlTypes.Canonicalize(n)!),
            StringComparer.OrdinalIgnoreCase);
        return null;
    }

    /// <summary>Reads the tree on the worker thread.</summary>
    private UIElementResult ReadTree(
        nint windowHandle,
        int maxDepth,
        UIElementFilter? filter,
        HashSet<string>? controlTypes,
        Stopwatch stopwatch,
        WalkStarted started,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (NotRespondingReason(windowHandle) is { } notResponding)
            {
                return CreateErrorResult(windowHandle, stopwatch, notResponding, UIElementErrorCode.NotResponding);
            }

            if (!_tree.IsAvailable())
            {
                return CreateErrorResult(
                    windowHandle,
                    stopwatch,
                    "UI Automation is not available on this system",
                    UIElementErrorCode.AutomationUnavailable);
            }

            IUiaNode? window;
            try
            {
                window = _tree.FromHandle(windowHandle);
            }
            catch (UiaElementUnavailableException)
            {
                window = null;
            }

            if (window == null)
            {
                return CreateErrorResult(
                    windowHandle,
                    stopwatch,
                    "Window not found or handle is invalid",
                    UIElementErrorCode.WindowNotFound);
            }

            // A window with web content builds its page's tree only once asked: ask, and wait for it a moment
            started.Warmup = _warmup?.WarmUp(
                windowHandle, filter?.AccessibilityWarmupBudget ?? AccessibilityWarmup.MaxWait, cancellationToken);

            var (windowTitle, windowFramework) = TryReadWindow(window);
            var refs = _registry == null ? null : new WalkRefs(_registry, windowHandle, _registry.NextGeneration());
            var walk = new Walk(
                _tree, maxDepth, filter, controlTypes, windowTitle, windowFramework, refs, progress, cancellationToken);
            started.Walk = walk;

            // Errors reading the window's own children propagate: an empty tree would look like a
            // window without controls
            walk.Run(window);

            var (elements, count) = walk.Snapshot();
            if (walk.TimedOut && count == 0)
            {
                var message = walk.ReadCount == 0
                    ? "The timeout passed before any element was read. Try again with a larger timeoutMs; " +
                      "if it keeps happening, the application may not be responding."
                    : $"The timeout passed before any element matching the filters was found ({walk.ReadCount} " +
                      $"element{(walk.ReadCount == 1 ? " was" : "s were")} read). Try again with a larger timeoutMs, " +
                      "a smaller maxDepth or looser filters.";
                return CreateErrorResult(windowHandle, stopwatch, message, UIElementErrorCode.OperationTimedOut, windowTitle);
            }

            var truncationReason = walk.TimedOut
                ? UIElementTruncationReason.Timeout
                : walk.StoppedAtMaxElements
                    ? UIElementTruncationReason.MaxElements
                    : walk.SkippedElementCount > 0 ? UIElementTruncationReason.ElementsSkipped : (UIElementTruncationReason?)null;

            return new UIElementResult(
                Success: true,
                WindowHandle: (long)windowHandle,
                WindowTitle: windowTitle,
                Elements: elements,
                TotalElementCount: count,
                Depth: maxDepth,
                ElapsedMilliseconds: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                SkippedElementCount: walk.SkippedElementCount,
                DepthReached: walk.DepthReached,
                Truncated: truncationReason != null,
                TruncationReason: truncationReason,
                FrameworkId: windowFramework);
        }
        catch (UiaElementUnavailableException ex)
        {
            return CreateErrorResult(
                windowHandle,
                stopwatch,
                $"Element is no longer available: {ex.Message}",
                UIElementErrorCode.ElementAccessFailed);
        }
        catch (TimeoutException ex)
        {
            return CreateErrorResult(
                windowHandle,
                stopwatch,
                $"UI Automation did not answer in time: {ex.Message}",
                UIElementErrorCode.OperationTimedOut);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(
                windowHandle,
                stopwatch,
                $"Error accessing UI elements: {ex.Message}",
                UIElementErrorCode.OperationException);
        }
    }

    /// <summary>The window's name and UI framework, or nulls when they cannot be read.</summary>
    private (string? Name, string? FrameworkId) TryReadWindow(IUiaNode node)
    {
        try
        {
            var properties = _tree.Read(node);
            return (properties.Name, EmptyToNull(properties.FrameworkId));
        }
        catch (Exception)
        {
            // The window title is optional
            return (null, null);
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// The state of one breadth-first tree walk. The walk runs on the worker thread; when the worker abandons it, the
    /// caller takes its partial tree (<see cref="PartialResult"/>) while the walk thread may still be blocked in a UI
    /// Automation call, and may go on later. So the tree and the counts change only under <see cref="_gate"/>, which is
    /// never held during a UI Automation call, and the partial tree is a copy.
    /// </summary>
    /// <remarks>
    /// Every element read is a <see cref="WalkNode"/> in its parent's list, in tree order, whether it is returned or
    /// not; an element the filters leave out keeps its place, and the elements inside it that are returned take that
    /// place when the tree is copied out (<see cref="Materialize"/>). So the tree is consistent at any moment, however
    /// far the levels below have been read.
    /// </remarks>
    private sealed class Walk(
        IUiaTree tree,
        int maxDepth,
        UIElementFilter? filter,
        HashSet<string>? controlTypes,
        string? windowTitle,
        string? windowFramework,
        WalkRefs? refs,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        private readonly object _gate = new();

        /// <summary>The window: its children are the elements of level 1.</summary>
        private readonly WalkNode _root = new(null, Included: false, Ref: null, IndexPath: []);

        /// <summary>The elements whose children are still to be read, in breadth-first order, with their level's number.</summary>
        private readonly Queue<(WalkNode Parent, IUiaNode Node, int Level)> _pending = new();

        private int _elementCount;
        private int _skippedElementCount;
        private int _readCount;
        private int _depthReached;
        private volatile bool _timedOut;
        private volatile bool _capReached;
        private volatile bool _leftUnread;

        /// <summary>Elements, or lists of children, that could not be read.</summary>
        public int SkippedElementCount
        {
            get
            {
                lock (_gate)
                {
                    return _skippedElementCount;
                }
            }
        }

        /// <summary>Elements whose properties were read, returned or not.</summary>
        public int ReadCount
        {
            get
            {
                lock (_gate)
                {
                    return _readCount;
                }
            }
        }

        /// <summary>The deepest level an element was read at (the window's children are level 1).</summary>
        public int DepthReached
        {
            get
            {
                lock (_gate)
                {
                    return _depthReached;
                }
            }
        }

        /// <summary>The walk stopped early because the timeout passed.</summary>
        public bool TimedOut => _timedOut;

        /// <summary>The walk stopped at maxElements with elements left unread (or, when that could not be told, perhaps).</summary>
        public bool StoppedAtMaxElements => _capReached && _leftUnread;

        /// <summary>Walks the window's subtree, level by level.</summary>
        public void Run(IUiaNode window)
        {
            ReadChildren(_root, window, level: 1, isWindow: true);
            while (true)
            {
                (WalkNode Parent, IUiaNode Node, int Level) next;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out next))
                    {
                        return;
                    }
                }

                if (_capReached)
                {
                    // Elements are left whose children were not read; whether there are any is still to be seen
                    // (a level cut in the middle has already said so)
                    _leftUnread = _leftUnread || AnyChildLeft(next.Node);
                    return;
                }

                if (StopRequested())
                {
                    return;
                }

                ReadChildren(next.Parent, next.Node, next.Level);
            }
        }

        /// <summary>
        /// Whether <paramref name="first"/> or an element still waiting has a child: the elements maxElements left unread.
        /// One call per element, until the first with a child. When the walk is asked to stop, or a call fails, it is
        /// not known, and assumed.
        /// </summary>
        private bool AnyChildLeft(IUiaNode first)
        {
            var node = first;
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return true;
                }

                try
                {
                    if (tree.GetChildren(node, cancellationToken).Count > 0)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    return true;
                }

                lock (_gate)
                {
                    if (!_pending.TryDequeue(out var next))
                    {
                        return false;
                    }

                    node = next.Node;
                }
            }
        }

        /// <summary>The returned tree so far and how many elements it holds. Runs on either thread.</summary>
        public (List<UIElementInfo> Elements, int Count) Snapshot()
        {
            lock (_gate)
            {
                var elements = Materialize(_root.Children, windowFramework);
                return (elements, Count(elements));
            }

            static int Count(IReadOnlyList<UIElementInfo>? list) => list?.Sum(e => 1 + Count(e.Children)) ?? 0;
        }

        /// <summary>
        /// The tree read so far, for a walk the worker abandoned: success with truncated=true, or null when no element
        /// passed the filters yet. Runs on the caller's thread.
        /// </summary>
        public UIElementResult? PartialResult(nint windowHandle, Stopwatch stopwatch)
        {
            var (elements, count) = Snapshot();
            return count == 0
                ? null
                : new UIElementResult(
                    Success: true,
                    WindowHandle: (long)windowHandle,
                    WindowTitle: windowTitle,
                    Elements: elements,
                    TotalElementCount: count,
                    Depth: maxDepth,
                    ElapsedMilliseconds: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                    SkippedElementCount: SkippedElementCount,
                    DepthReached: DepthReached,
                    Truncated: true,
                    TruncationReason: UIElementTruncationReason.Timeout,
                    FrameworkId: windowFramework);
        }

        /// <summary>
        /// Reads the children of <paramref name="node"/>, which are at <paramref name="level"/>, into
        /// <paramref name="parent"/>, and queues those whose own children are to be read.
        /// </summary>
        private void ReadChildren(WalkNode parent, IUiaNode node, int level, bool isWindow = false)
        {
            if (StopRequested())
            {
                return;
            }

            IReadOnlyList<Candidate> children;
            try
            {
                children = FetchChildren(node);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _timedOut = true;
                return;
            }
            catch (Exception) when (!isWindow)
            {
                // This element's children could not be read; the element itself is still returned
                Count(ref _skippedElementCount);
                return;
            }

            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                if (_capReached)
                {
                    _leftUnread = true;
                    return;
                }

                // Elements fetched with their properties cost no further call, so a level already read is kept whole
                // when the timeout passes; elements read one at a time stop with it
                if (child.Cached == null && StopRequested())
                {
                    return;
                }

                Visit(child, level, parent, [.. parent.IndexPath, index]);
            }
        }

        /// <summary>
        /// The children of a node: listed in one call without their properties, then, for a level of at most
        /// <see cref="CachedLevelLimit"/>, read in one more call with them. A wider level, or one whose cached read fails
        /// (a provider that fails one child can fail it all), comes as nodes to read one at a time. A cached read that
        /// times out, or the cancellation of the walk, is not retried one at a time: each read would wait again.
        /// </summary>
        private IReadOnlyList<Candidate> FetchChildren(IUiaNode node)
        {
            var children = tree.GetChildren(node, cancellationToken);
            if (children.Count == 0)
            {
                return [];
            }

            if (children.Count <= CachedLevelLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return tree.GetChildElements(node, cancellationToken).Select(e => new Candidate(e.Node, e)).ToList();
                }
                catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException))
                {
                    // A provider failed for the read as a whole (often for one child): read the children one at a time
                }
            }

            return children.Select(n => new Candidate(n, null)).ToList();
        }

        /// <summary>
        /// Adds <paramref name="candidate"/> to <paramref name="parent"/>'s list unless it is offscreen (visibleOnly) or
        /// cannot be read, and queues it for its children while the levels go on.
        /// </summary>
        private void Visit(Candidate candidate, int level, WalkNode parent, int[] indexPath)
        {
            UiaNodeProperties properties;
            bool included;
            try
            {
                properties = candidate.Cached?.Properties ?? tree.Read(candidate.Node);
                int read, returned, depthReached;
                lock (_gate)
                {
                    read = ++_readCount;
                    _depthReached = depthReached = Math.Max(_depthReached, level);
                    returned = _elementCount;
                }

                ReportProgress(read, returned, depthReached);

                if (filter?.VisibleOnly == true && properties.IsOffscreen)
                {
                    // An offscreen element's subtree is offscreen too
                    return;
                }

                included = PassesFilters(candidate, properties);
            }
            catch (Exception)
            {
                // The element disappeared or its provider failed (ElementNotAvailable, COMException,
                // TimeoutException, ...). Skip it; its siblings are still walked.
                Count(ref _skippedElementCount);
                return;
            }

            // The list row the element is in (the walk knows its ancestors), and the row of the elements below it
            var row = ElementRows.IsItem(properties.ControlType) ? null : ElementRows.Below(parent.Row);
            var rowBelow = ElementRows.IsItem(properties.ControlType)
                ? ElementRows.OfItem(RuntimeIdOf(candidate), properties)
                : ElementRows.EndsTheSearch(properties.ControlType) ? null : row;

            // Registered outside the walk's lock: the registry has its own, and makes no UI Automation call
            var elementRef = included ? refs?.Register(tree, candidate, properties, indexPath, row) : null;
            var node = new WalkNode(properties, included, elementRef, indexPath) { Row = rowBelow };
            lock (_gate)
            {
                parent.Children.Add(node);
                if (included)
                {
                    _elementCount++;
                    if (filter?.MaxElements is { } max && _elementCount >= max)
                    {
                        _capReached = true;
                    }
                }

                if (level < maxDepth)
                {
                    _pending.Enqueue((node, candidate.Node, level + 1));
                }
            }
        }

        /// <summary>
        /// An item's runtime id, for the row of the elements below it: from its level's read, else as the engine knows it
        /// (no further call for an element it found); null when it cannot be read.
        /// </summary>
        private IReadOnlyList<int>? RuntimeIdOf(Candidate candidate)
        {
            try
            {
                return candidate.Cached?.RuntimeId ?? tree.GetRuntimeId(candidate.Node);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The returned elements of a list of walk nodes: an element left out by the filters is replaced by the
        /// returned elements inside it. An element names its framework only where it differs from
        /// <paramref name="enclosingFramework"/>, that of the returned element it is inside (or the window's). Called
        /// under the gate.
        /// </summary>
        private static List<UIElementInfo> Materialize(List<WalkNode> nodes, string? enclosingFramework)
        {
            var output = new List<UIElementInfo>();
            foreach (var node in nodes)
            {
                if (!node.Included)
                {
                    output.AddRange(Materialize(node.Children, enclosingFramework));
                    continue;
                }

                var properties = node.Properties!;
                var framework = EmptyToNull(properties.FrameworkId);
                var children = Materialize(node.Children, framework ?? enclosingFramework);
                output.Add(Element(
                    properties,
                    children,
                    framework != null && !string.Equals(framework, enclosingFramework, StringComparison.Ordinal) ? framework : null,
                    node.Ref));
            }

            return output;
        }

        private static UIElementInfo Element(
            UiaNodeProperties properties, List<UIElementInfo> children, string? frameworkId, string? elementRef) =>
            new(
                Name: string.IsNullOrEmpty(properties.Name) ? null : properties.Name,
                ControlType: properties.ControlType,
                AutomationId: string.IsNullOrEmpty(properties.AutomationId) ? null : properties.AutomationId,
                Bounds: properties.Bounds.ToBounds(),
                IsEnabled: properties.IsEnabled,
                IsOffscreen: properties.IsOffscreen,
                Children: children.Count > 0 ? children : null,
                FrameworkId: frameworkId,
                Ref: elementRef);

        /// <summary>
        /// Reports every <see cref="ProgressInterval"/> elements read. Never once the walk is asked to stop: the caller
        /// may have returned already, and a progress notification must not follow the call's result.
        /// </summary>
        private void ReportProgress(int read, int returned, int depthReached)
        {
            if (progress == null || read % ProgressInterval != 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            progress.Report(new OperationProgress(
                read,
                Total: null,
                $"Read {read} UI elements so far ({returned} returned), down to level {depthReached} of {maxDepth}"));
        }

        private void Count(ref int counter)
        {
            lock (_gate)
            {
                counter++;
            }
        }

        /// <summary>
        /// The controlTypes, size, interactable and actionable filters, cheapest first; an element must pass every
        /// filter that is set. A fetched element's interaction properties came with it; an element read on its own
        /// reads them (focus and patterns for interactable, a fresh element read for actionable) only when it passes
        /// the filters before. Throws only when the element disappeared.
        /// </summary>
        private bool PassesFilters(Candidate candidate, UiaNodeProperties properties)
        {
            if (controlTypes != null && !controlTypes.Contains(properties.ControlType))
            {
                return false;
            }

            if (filter?.MinWidth is not null || filter?.MinHeight is not null)
            {
                // An element without bounds has no size to compare
                var bounds = properties.Bounds.ToBounds();
                if (bounds == null ||
                    (filter.MinWidth is { } minWidth && bounds.Width < minWidth) ||
                    (filter.MinHeight is { } minHeight && bounds.Height < minHeight))
                {
                    return false;
                }
            }

            if (filter?.InteractableOnly == true)
            {
                if (!properties.IsEnabled)
                {
                    return false;
                }

                if (!IsInteractable(candidate))
                {
                    return false;
                }
            }

            if (filter?.ActionableOnly == true && !IsActionable(candidate))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether the element can take focus or supports an action pattern: from the properties fetched with its level,
        /// or from a fresh read when it was read on its own. An element whose properties cannot be read counts as
        /// skipped and is left out; its subtree is still walked.
        /// </summary>
        private bool IsInteractable(Candidate candidate)
        {
            if (candidate.Cached is { } cached)
            {
                return cached.IsInteractable;
            }

            try
            {
                return tree.IsInteractable(candidate.Node);
            }
            catch (Exception ex) when (ex is not UiaElementUnavailableException)
            {
                // Only this element's own properties failed (a provider that rejects a pattern
                // query, for example): leave the element out, but keep walking the elements
                // inside it. A vanished element is skipped with its subtree, which is gone too.
                Count(ref _skippedElementCount);
                return false;
            }
        }

        /// <summary>
        /// Whether the element supports an action pattern: from the patterns fetched with its level, or from one fresh read
        /// of the element (a cross-process call) when it was read on its own. An element whose patterns cannot be read is
        /// left out and counted, and the elements inside it are still walked; one that disappeared throws.
        /// </summary>
        private bool IsActionable(Candidate candidate)
        {
            if (candidate.Cached is { } cached)
            {
                return (cached.Patterns & UiaPatterns.Actions) != 0;
            }

            try
            {
                return (tree.ReadElement(candidate.Node).Patterns & UiaPatterns.Actions) != 0;
            }
            catch (Exception ex) when (ex is not UiaElementUnavailableException)
            {
                Count(ref _skippedElementCount);
                return false;
            }
        }

        private bool StopRequested()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _timedOut = true;
            }

            return _timedOut || _capReached;
        }

        /// <summary>
        /// An element read during the walk, returned or not, with the elements read inside it, its ref when it is
        /// returned, and its index path (its index among its parent's children at each level below the window).
        /// </summary>
        private sealed record WalkNode(UiaNodeProperties? Properties, bool Included, string? Ref, int[] IndexPath)
        {
            public List<WalkNode> Children { get; } = [];

            /// <summary>
            /// The list row of the elements below this one (<see cref="ElementRows"/>), its levels counted from this element
            /// (0 when it is the item itself), or null when they are in none.
            /// </summary>
            public ElementRow? Row { get; init; }
        }
    }

    /// <summary>A child to visit: its node, and its properties when they came with it.</summary>
    private sealed record Candidate(IUiaNode Node, UiaElement? Cached);

    /// <summary>
    /// Gives the elements a walk returns their refs (roadmap M3.4). An element fetched with its level brings its runtime id
    /// and class along; one read on its own gets its runtime id from <see cref="IUiaTree.GetRuntimeId"/> (which the
    /// engines answer from what they read when they found it) and is registered without a class.
    /// </summary>
    private sealed class WalkRefs(IElementRegistry registry, nint windowHandle, long generation)
    {
        public string? Register(IUiaTree tree, Candidate candidate, UiaNodeProperties properties, int[] indexPath, ElementRow? row)
        {
            try
            {
                var element = candidate.Cached ?? new UiaElement(
                    candidate.Node, properties, ClassName: null, IsKeyboardFocusable: false, UiaPatterns.None,
                    tree.GetRuntimeId(candidate.Node), ControlTypeId: null);
                return registry.Register(windowHandle, element, new ElementOrigin(generation, IndexPath: indexPath, Row: row));
            }
            catch (Exception)
            {
                // The element is still returned, without a ref
                return null;
            }
        }
    }

    private static UIElementResult CreateErrorResult(
        nint windowHandle,
        Stopwatch stopwatch,
        string error,
        UIElementErrorCode errorCode,
        string? windowTitle = null)
    {
        return new UIElementResult(
            Success: false,
            WindowHandle: (long)windowHandle,
            WindowTitle: windowTitle,
            Elements: null,
            TotalElementCount: 0,
            Depth: 0,
            ElapsedMilliseconds: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            Error: error,
            ErrorCode: errorCode);
    }
}
