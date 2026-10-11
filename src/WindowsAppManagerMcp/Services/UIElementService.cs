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
/// <item>A window that does not respond is not read (NotResponding): Windows reports it hung, or it does not answer a
/// message within <see cref="RespondTimeoutMs"/>. Every UI Automation call to it would block until it recovers.</item>
/// <item>The walk is breadth first (roadmap M3.1): all of a level before the next, so a walk cut short by the timeout
/// or by maxElements keeps the levels nearest the window. The children of an element are read in one cross-process
/// call with their properties (a cache request: <see cref="IUiaTree.GetChildElements"/>); when that call fails, they
/// are read one at a time, so one failing element costs only itself.</item>
/// <item>maxDepth counts levels below the window: its children are level 1, and nothing below
/// level maxDepth is read.</item>
/// <item>The controlTypes, size and interactable filters decide only whether an element itself is
/// returned. An element that fails one is left out, and the elements inside it that pass take its
/// place in its parent's list. Only an offscreen element (visibleOnly) leaves out its whole subtree.</item>
/// <item>maxElements stops the walk once that many elements are returned (truncationReason MaxElements).</item>
/// <item>An element that cannot be read is skipped and counted, and the walk goes on with its
/// siblings. An element whose interaction properties cannot be read (interactableOnly) is left out
/// and counted too, but the elements inside it are still walked. A timeout ends the walk and returns
/// what was read so far; a level already fetched when it passes is kept whole, because reading it cost no further
/// call. So does a walk that is abandoned because a UI Automation call did not return (a hung provider): the
/// elements it had read until then are returned.</item>
/// </list>
/// </remarks>
public class UIElementService : IUIElementService
{
    private const int MaxAllowedDepth = 10;
    private const int DefaultMaxDepth = 5;

    /// <summary>A walk reports its progress after every this many elements read.</summary>
    public const int ProgressInterval = 100;

    /// <summary>How long the hung-window check waits for the window to answer a message.</summary>
    public const uint RespondTimeoutMs = 500;

    /// <summary>
    /// How long to keep waiting after the timeout for the walk to stop and hand back its partial
    /// tree. A walk still stuck in a UI Automation call after that is abandoned.
    /// </summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly INativeWindowWrapper? _windows;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the walk under a deadline.</param>
    /// <param name="windows">Checks that the window responds before it is read, and identifies the application
    /// whose window is read (for a UWP app its own process, see <see cref="UwpFrames"/>), so a hung application blocks
    /// only reads of itself (see <see cref="IUiaWorker"/>). Without it all reads share one count and nothing is
    /// checked first.</param>
    public UIElementService(IUiaTree tree, IUiaWorker worker, INativeWindowWrapper? windows = null)
    {
        _tree = tree;
        _worker = worker;
        _windows = windows;
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

        return result with { WindowState = ReadWindowState(windowHandle) };
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

    /// <summary>
    /// Why the window must not be read, or null when it responds (or cannot be checked): Windows reports its thread
    /// hung (IsHungAppWindow), or it does not answer a no-op message within <see cref="RespondTimeoutMs"/>
    /// (SendMessageTimeout WM_NULL with SMTO_ABORTIFHUNG). A handle that is no window is left to the UI Automation
    /// lookup, which reports WindowNotFound.
    /// </summary>
    private string? NotRespondingReason(nint windowHandle)
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

            if (_windows.IsHungAppWindow(windowHandle))
            {
                return "The window is not responding (Windows reports it hung), so its UI elements were not read: every UI " +
                       "Automation call to it would block until it recovers. Wait, then check it again with find_windows or " +
                       "get_window_info (isResponding).";
            }

            if (!_windows.RespondsWithin(windowHandle, RespondTimeoutMs))
            {
                // It may have closed meanwhile; then the lookup below reports that
                if (!_windows.IsWindow(windowHandle))
                {
                    return null;
                }

                return $"The window did not answer a message within {RespondTimeoutMs} ms, so its UI elements were not read: " +
                       "the application is busy or hung. Wait, then try again.";
            }

            return null;
        }
        catch (Exception)
        {
            // The check is a courtesy: the walk's own deadline still bounds the read
            return null;
        }
    }

    /// <summary>
    /// The walk of one call, once it has started: the caller reads its partial tree when the worker abandons it.
    /// </summary>
    private sealed class WalkStarted
    {
        private Walk? _walk;

        public Walk? Walk
        {
            get => Volatile.Read(ref _walk);
            set => Volatile.Write(ref _walk, value);
        }
    }

    /// <summary>
    /// The process of the app that the window shows, or 0 when it is not known. For a UWP frame window
    /// that is the app's own process, not the ApplicationFrameHost that owns the frames of every UWP app.
    /// </summary>
    private int TargetProcessId(nint windowHandle)
    {
        try
        {
            return _windows == null ? 0 : (int)UwpFrames.AppProcessId(_windows, windowHandle);
        }
        catch (Exception)
        {
            // Only the stuck-read count depends on it
            return 0;
        }
    }

    /// <summary>
    /// Validates the controlTypes filter. Returns an error message for unknown names, or null with
    /// the canonical names in <paramref name="controlTypes"/> (null when the filter is not set).
    /// </summary>
    private static string? ParseControlTypes(string[]? names, out HashSet<string>? controlTypes)
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

            var windowTitle = TryReadName(window);
            var walk = new Walk(_tree, maxDepth, filter, controlTypes, windowTitle, progress, cancellationToken);
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
                TruncationReason: truncationReason);
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

    private string? TryReadName(IUiaNode node)
    {
        try
        {
            return _tree.Read(node).Name;
        }
        catch (Exception)
        {
            // The window title is optional
            return null;
        }
    }

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
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        private readonly object _gate = new();

        /// <summary>The window: its children are the elements of level 1.</summary>
        private readonly WalkNode _root = new(null, Included: false);

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

        /// <summary>The walk stopped at maxElements with elements left to read.</summary>
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
                    // Elements are left whose children were not read
                    _leftUnread = true;
                    return;
                }

                if (StopRequested())
                {
                    return;
                }

                ReadChildren(next.Parent, next.Node, next.Level);
            }
        }

        /// <summary>The returned tree so far and how many elements it holds. Runs on either thread.</summary>
        public (List<UIElementInfo> Elements, int Count) Snapshot()
        {
            lock (_gate)
            {
                var elements = Materialize(_root.Children);
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
                    TruncationReason: UIElementTruncationReason.Timeout);
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
                children = FetchChildren(node, isWindow);
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

            foreach (var child in children)
            {
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

                Visit(child, level, parent);
            }
        }

        /// <summary>
        /// The children of a node: with their properties in one call, or, when that call fails, as nodes to read one at
        /// a time. A timeout, or the cancellation of the walk, is not retried; neither is the window's own failure when
        /// its children cannot be read either way.
        /// </summary>
        private IReadOnlyList<Candidate> FetchChildren(IUiaNode node, bool isWindow)
        {
            try
            {
                return tree.GetChildElements(node, cancellationToken).Select(e => new Candidate(e.Node, e)).ToList();
            }
            catch (Exception ex) when (ex is not (TimeoutException or OperationCanceledException))
            {
                // A provider failed for the fetch as a whole (often for one child): read the children one at a time
            }

            if (StopRequested())
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return tree.GetChildren(node, cancellationToken).Select(n => new Candidate(n, null)).ToList();
        }

        /// <summary>
        /// Adds <paramref name="candidate"/> to <paramref name="parent"/>'s list unless it is offscreen (visibleOnly) or
        /// cannot be read, and queues it for its children while the levels go on.
        /// </summary>
        private void Visit(Candidate candidate, int level, WalkNode parent)
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

            var node = new WalkNode(properties, included);
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
        /// The returned elements of a list of walk nodes: an element left out by the filters is replaced by the
        /// returned elements inside it. Called under the gate.
        /// </summary>
        private static List<UIElementInfo> Materialize(List<WalkNode> nodes)
        {
            var output = new List<UIElementInfo>();
            foreach (var node in nodes)
            {
                var children = Materialize(node.Children);
                if (node.Included)
                {
                    output.Add(Element(node.Properties!, children));
                }
                else
                {
                    output.AddRange(children);
                }
            }

            return output;
        }

        private static UIElementInfo Element(UiaNodeProperties properties, List<UIElementInfo> children) =>
            new(
                Name: string.IsNullOrEmpty(properties.Name) ? null : properties.Name,
                ControlType: properties.ControlType,
                AutomationId: string.IsNullOrEmpty(properties.AutomationId) ? null : properties.AutomationId,
                Bounds: properties.Bounds.ToBounds(),
                IsEnabled: properties.IsEnabled,
                IsOffscreen: properties.IsOffscreen,
                Children: children.Count > 0 ? children : null);

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
        /// The controlTypes, size and interactable filters, cheapest first. A fetched element's interaction
        /// properties came with it; an element read on its own reads them only when it passes the others. Throws only
        /// when the element disappeared.
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

            return true;
        }

        private bool StopRequested()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _timedOut = true;
            }

            return _timedOut || _capReached;
        }

        /// <summary>A child to visit: its node, and its properties when they came with it.</summary>
        private sealed record Candidate(IUiaNode Node, UiaElement? Cached);

        /// <summary>An element read during the walk, returned or not, with the elements read inside it.</summary>
        private sealed record WalkNode(UiaNodeProperties? Properties, bool Included)
        {
            public List<WalkNode> Children { get; } = [];
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
