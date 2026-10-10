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
/// <item>maxDepth counts levels below the window: its children are level 1, and nothing below
/// level maxDepth is read.</item>
/// <item>The controlTypes, size and interactable filters decide only whether an element itself is
/// returned. An element that fails one is left out, and the elements inside it that pass take its
/// place in its parent's list. Only an offscreen element (visibleOnly) leaves out its whole subtree.</item>
/// <item>An element that cannot be read is skipped and counted, and the walk goes on with its
/// siblings. An element whose interaction properties cannot be read (interactableOnly) is left out
/// and counted too, but the elements inside it are still walked. A timeout ends the walk and returns
/// what was read so far. So does a walk that is abandoned because a UI Automation call did not return
/// (a hung provider): the elements it had read until then are returned.</item>
/// </list>
/// </remarks>
public class UIElementService : IUIElementService
{
    private const int MaxAllowedDepth = 10;
    private const int DefaultMaxDepth = 5;

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
    /// <param name="windows">Identifies the application whose window is read (for a UWP app its own
    /// process, see <see cref="UwpFrames"/>), so a hung application blocks only reads of itself (see
    /// <see cref="IUiaWorker"/>). Without it all reads share one count.</param>
    public UIElementService(IUiaTree tree, IUiaWorker worker, INativeWindowWrapper? windows = null)
    {
        _tree = tree;
        _worker = worker;
        _windows = windows;
    }

    /// <inheritdoc />
    public bool IsAvailable() => _tree.IsAvailable();

    /// <inheritdoc />
    public UIElementResult GetUIElements(
        nint windowHandle,
        int maxDepth = DefaultMaxDepth,
        UIElementFilter? filter = null,
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

        var progress = new WalkProgress();
        try
        {
            return _worker.Run(
                TargetProcessId(windowHandle),
                token => ReadTree(windowHandle, maxDepth, filter, controlTypes, stopwatch, progress, token),
                cancellationToken,
                StopGrace);
        }
        catch (TimeoutException ex)
        {
            // The worker gave up on a read stuck in an application that does not answer. The walk thread is
            // still blocked in a UI Automation call; what it had read until then is returned all the same
            return progress.Walk?.PartialResult(windowHandle, stopwatch) ??
                   CreateErrorResult(windowHandle, stopwatch, ex.Message, UIElementErrorCode.OperationTimedOut);
        }
    }

    /// <summary>
    /// The walk of one call, once it has started: the caller reads its partial tree when the worker abandons it.
    /// </summary>
    private sealed class WalkProgress
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
        WalkProgress progress,
        CancellationToken cancellationToken)
    {
        try
        {
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
            var walk = new Walk(_tree, maxDepth, filter, controlTypes, windowTitle, cancellationToken);
            progress.Walk = walk;

            // Errors reading the window's own children propagate: an empty tree would look like a
            // window without controls
            var elements = walk.ReadChildren(window, level: 1, isWindow: true);

            if (walk.TimedOut && walk.ElementCount == 0)
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
                : walk.SkippedElementCount > 0 ? UIElementTruncationReason.ElementsSkipped : (UIElementTruncationReason?)null;

            return new UIElementResult(
                Success: true,
                WindowHandle: (long)windowHandle,
                WindowTitle: windowTitle,
                Elements: elements,
                TotalElementCount: walk.ElementCount,
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
    /// The state of one tree walk. The walk runs on the worker thread; when the worker abandons it, the caller
    /// takes its partial tree (<see cref="PartialResult"/>) while the walk thread may still be blocked in a UI
    /// Automation call, and may go on later. So the lists of the tree and the counts change only under
    /// <see cref="_gate"/>, which is never held during a UI Automation call, and the partial tree is a copy.
    /// </summary>
    private sealed class Walk(
        IUiaTree tree,
        int maxDepth,
        UIElementFilter? filter,
        HashSet<string>? controlTypes,
        string? windowTitle,
        CancellationToken cancellationToken)
    {
        private readonly object _gate = new();

        /// <summary>The elements whose children are being read, outermost first, with what was read inside them.</summary>
        private readonly List<PendingElement> _pending = [];

        /// <summary>The window's children read so far (level 1).</summary>
        private List<UIElementInfo>? _top;

        private int _elementCount;
        private int _skippedElementCount;
        private int _readCount;
        private int _depthReached;

        /// <summary>Elements returned, nested ones included.</summary>
        public int ElementCount
        {
            get
            {
                lock (_gate)
                {
                    return _elementCount;
                }
            }
        }

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
        public bool TimedOut { get; private set; }

        /// <summary>
        /// The tree read so far, for a walk the worker abandoned: the finished elements, and the elements whose
        /// children were being read with the children read until then. Success with truncated=true; null when no
        /// element passed the filters yet. Runs on the caller's thread.
        /// </summary>
        public UIElementResult? PartialResult(nint windowHandle, Stopwatch stopwatch)
        {
            List<UIElementInfo> elements;
            int skipped, depthReached;
            lock (_gate)
            {
                if (_top == null)
                {
                    return null;
                }

                // From the innermost element being read outwards: each takes the partial one inside it as its
                // last child (or, when it is filtered out, those children take its place)
                List<UIElementInfo>? inner = null;
                for (var i = _pending.Count - 1; i >= 0; i--)
                {
                    var pending = _pending[i];
                    var children = new List<UIElementInfo>(pending.Children);
                    children.AddRange(inner ?? []);
                    inner = pending.Included ? [Element(pending.Properties, children)] : children;
                }

                elements = [.. _top, .. inner ?? []];
                skipped = _skippedElementCount;
                depthReached = _depthReached;
            }

            var count = Count(elements);
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
                    SkippedElementCount: skipped,
                    DepthReached: depthReached,
                    Truncated: true,
                    TruncationReason: UIElementTruncationReason.Timeout);

            static int Count(IReadOnlyList<UIElementInfo>? list) => list?.Sum(e => 1 + Count(e.Children)) ?? 0;
        }

        /// <summary>
        /// Reads the children of <paramref name="parent"/>, which are at <paramref name="level"/>, and
        /// returns the elements to list under it.
        /// </summary>
        public List<UIElementInfo> ReadChildren(IUiaNode parent, int level, bool isWindow = false)
        {
            var output = new List<UIElementInfo>();
            if (isWindow)
            {
                lock (_gate)
                {
                    _top = output;
                }
            }

            ReadChildrenInto(parent, level, output, isWindow);
            return output;
        }

        /// <summary>Reads the children of <paramref name="parent"/> into <paramref name="output"/>.</summary>
        private void ReadChildrenInto(IUiaNode parent, int level, List<UIElementInfo> output, bool isWindow = false)
        {
            if (StopRequested())
            {
                return;
            }

            IReadOnlyList<IUiaNode> children;
            try
            {
                children = tree.GetChildren(parent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TimedOut = true;
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
                if (StopRequested())
                {
                    break;
                }

                Visit(child, level, output);
            }
        }

        /// <summary>
        /// Adds <paramref name="node"/> with its returned children to <paramref name="output"/> when it
        /// passes the filters; otherwise adds the children that pass in its place.
        /// </summary>
        private void Visit(IUiaNode node, int level, List<UIElementInfo> output)
        {
            UiaNodeProperties properties;
            bool included;
            try
            {
                properties = tree.Read(node);
                lock (_gate)
                {
                    _readCount++;
                    _depthReached = Math.Max(_depthReached, level);
                }

                if (filter?.VisibleOnly == true && properties.IsOffscreen)
                {
                    // An offscreen element's subtree is offscreen too
                    return;
                }

                included = PassesFilters(node, properties);
            }
            catch (Exception)
            {
                // The element disappeared or its provider failed (ElementNotAvailable, COMException,
                // TimeoutException, ...). Skip it; its siblings are still walked.
                Count(ref _skippedElementCount);
                return;
            }

            var children = new List<UIElementInfo>();
            var readsChildren = level < maxDepth && !StopRequested();
            if (readsChildren)
            {
                // While its children are read, the element is pending: a partial tree shows it with them
                lock (_gate)
                {
                    _pending.Add(new PendingElement(properties, included, children));
                }

                ReadChildrenInto(node, level + 1, children);
            }

            // Finished: from the pending elements into its parent's list, in one step
            lock (_gate)
            {
                if (readsChildren)
                {
                    _pending.RemoveAt(_pending.Count - 1);
                }

                if (!included)
                {
                    output.AddRange(children);
                    return;
                }

                _elementCount++;
                output.Add(Element(properties, children));
            }
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

        private void Count(ref int counter)
        {
            lock (_gate)
            {
                counter++;
            }
        }

        /// <summary>An element whose children are being read, and the elements read inside it so far.</summary>
        private sealed record PendingElement(UiaNodeProperties Properties, bool Included, List<UIElementInfo> Children);

        /// <summary>
        /// The controlTypes, size and interactable filters, cheapest first: the interactable check
        /// reads more properties, so it runs only for elements that pass the others. Throws only when
        /// the element disappeared.
        /// </summary>
        private bool PassesFilters(IUiaNode node, UiaNodeProperties properties)
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

                try
                {
                    return tree.IsInteractable(node);
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
                TimedOut = true;
            }

            return TimedOut;
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
