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
/// properties (<see cref="IUiaTree.GetChildren"/>, one call of references only). A level of at most <see cref="CachedLevelLimit"/>
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
/// be read. To tell, the elements still waiting are asked for a first child (<see cref="IUiaTree.HasChildren"/>, one
/// element at most each, however wide its level) until one has one, at most <see cref="MaxChildProbes"/> of them; when
/// that cannot be told (more are waiting, the timeout, a failing call), it is assumed.</item>
/// <item>An element that cannot be read is skipped and counted, and the walk goes on with its
/// siblings. An element whose interaction properties cannot be read (interactableOnly) is left out
/// and counted too, but the elements inside it are still walked. A timeout ends the walk and returns
/// what was read so far; a level already fetched when it passes is kept whole, because reading it cost no further
/// call. So does a walk that is abandoned because a UI Automation call did not return (a hung provider): the
/// elements it had read until then are returned.</item>
/// <item>A window that shows web content (Chromium, Electron, WebView2) is warmed up first (<see cref="IAccessibilityWarmup"/>,
/// roadmap M3.3): its page's accessibility tree is asked for and waited for, within a share of the timeout, and the result
/// says what came of it (accessibilityWarmup) instead of returning a silently thin tree.</item>
/// <item>The page of a render widget inside the window (a WebView2 pane, roadmap M3.F35) is listed under the element of the
/// render widget's parent window, ahead of that element's own children: Chromium's own views lie between the two, so the
/// page would otherwise be deeper than maxDepth reaches. The page's root (its Document, or the render widget's own element
/// when that holds the page's elements, see <see cref="IAccessibilityWarmup.FindPages"/>) is one level below that element,
/// and the page is walked like any other subtree: its elements count toward maxDepth and maxElements at those levels and
/// get refs of the window. Chromium's views are still walked after it; a page met again among them is not listed twice.
/// Only shown render widgets are listed, at most <see cref="AccessibilityWarmup.MaxRenderWidgets"/>, and a window without
/// one costs no further call.</item>
/// <item>A WebView2 page hosted as a visual (roadmap M3.F43: the WinUI 3 WebView2 control, which has no render widget) lies
/// about 8 levels below the root of Chromium's views (the first element of the Chrome framework), beyond maxDepth. Its
/// Document is listed in place of that root's children, which are Chromium's own views and nothing of the page: the
/// page's elements are then a level below the root. Searches still see Chromium's views.</item>
/// <item>An element met again (the same runtime id: a provider that lists a subtree under two parents, or under one of its
/// own descendants, as a WinUI 3 window lists its WebView2's page and XAML island; roadmap M3.F43) is listed only where the
/// walk first met it, breadth first: it and the elements inside it are left out the second time, so they are not listed
/// twice with the same refs and a loop ends. An element without a runtime id cannot be told from another, and is listed
/// each time.</item>
/// <item>Each element names its UI framework only where it changes from the returned element it is inside (or, at the top,
/// from the window's), so a web page inside a native frame shows where it starts without repeating it on every element.</item>
/// <item>A window the walked window owns (a dialog or popup, which the HWND proxy lists among the window's children with its
/// whole tree) is left out with everything inside it, as the searches leave it out (<see cref="OwnedWindows"/>, roadmap
/// M3.F9): it is a window of its own, read by its handle, and its elements get refs only there. The result names it
/// (<see cref="UIElementResult.OwnedWindows"/>). A level fetched with its properties is tested on them; of a level read one
/// element at a time, the first children are read one at a time while they are owned windows (the HWND proxy lists them
/// first, at most <see cref="ElementSearch.MaxOwnedWindows"/>). Without the Win32 reads nothing is left out.</item>
/// <item>The result says why a tree may not show what the window holds (<see cref="UIElementResult.Warnings"/>, roadmap
/// M3.F16): a window whose process runs at a higher integrity level than the server, which UI Automation cannot read
/// (TargetElevated), and a minimized window, whose elements are all offscreen (WindowMinimized). Both are read after the
/// walk, with Win32 and token reads that no window's thread answers; neither stops the walk.</item>
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

    /// <summary>
    /// How many of the elements still waiting a walk stopped by maxElements asks for a child; after that, something is
    /// assumed left unread.
    /// </summary>
    public const int MaxChildProbes = 32;

    /// <summary>How long the hung-window check waits for the window to answer a message.</summary>
    public const uint RespondTimeoutMs = UiaTargets.RespondTimeoutMs;

    /// <summary>
    /// How long to keep waiting after the timeout for the walk to stop and hand back its partial
    /// tree, unless the filter sets its own (<see cref="UIElementFilter.StopGrace"/>). A walk still stuck in a UI
    /// Automation call after that is abandoned.
    /// </summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly INativeWindowWrapper? _windows;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly IElementRegistry? _registry;
    private readonly ITargetPolicy? _policy;
    private readonly IProcessSecurityProbe? _security;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the walk under a deadline.</param>
    /// <param name="windows">Checks that the window responds before it is read, and identifies the application
    /// whose window is read (for a UWP app its own process, see <see cref="UwpFrames"/>), so a hung application blocks
    /// only reads of itself (see <see cref="IUiaWorker"/>), and tells the windows the window owns, which the walk leaves
    /// out (roadmap M3.F9). Without it all reads share one count, nothing is checked first and nothing is left out.</param>
    /// <param name="warmup">Warms up the web content of Chromium, Electron and WebView2 windows before they are read
    /// (roadmap M3.3), and finds the pages of their render widgets, which the walk lists under their hosts (roadmap
    /// M3.F35). The server always passes it; without it no window is warmed up, and a page is listed only where the walk
    /// reaches it.</param>
    /// <param name="registry">Gives every returned element its ref (roadmap M3.4). Without it the elements have none.</param>
    /// <param name="policy">The user's target policy, which refuses reads of a window of an app it denies. The server always
    /// passes it; without it (or without <paramref name="windows"/>) no window is refused.</param>
    /// <param name="security">Tells a window whose process runs at a higher integrity level than the server, whose tree UI
    /// Automation cannot read (roadmap M3.F16). The server always passes it; without it (or without
    /// <paramref name="windows"/>) no such warning is given.</param>
    public UIElementService(
        IUiaTree tree,
        IUiaWorker worker,
        INativeWindowWrapper? windows = null,
        IAccessibilityWarmup? warmup = null,
        IElementRegistry? registry = null,
        ITargetPolicy? policy = null,
        IProcessSecurityProbe? security = null)
    {
        _tree = tree;
        _worker = worker;
        _windows = windows;
        _warmup = warmup;
        _registry = registry;
        _policy = policy;
        _security = security;
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
                filter?.StopGrace ?? StopGrace).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            // The worker gave up on a read stuck in an application that does not answer. The walk thread is
            // still blocked in a UI Automation call; what it had read until then is returned all the same
            result = started.Walk?.PartialResult(windowHandle, stopwatch) ??
                     CreateErrorResult(windowHandle, stopwatch, ex.Message, UIElementErrorCode.OperationTimedOut);
        }

        var windowState = ReadWindowState(windowHandle, out var root);
        return result with
        {
            WindowState = windowState,
            AccessibilityWarmup = started.Warmup,
            Warnings = Warnings(windowHandle, result, windowState, root, filter)
        };
    }

    /// <summary>
    /// The state of the window's top-level window (<paramref name="root"/>, the window itself when it has none above it):
    /// Minimized when it, or the window itself, is minimized (its elements are offscreen), Maximized or Normal otherwise.
    /// Null without a window wrapper, or when the handle is no window. These are reads Windows answers without the window's
    /// thread, so a hung application does not block them.
    /// </summary>
    private WindowState? ReadWindowState(nint windowHandle, out nint root)
    {
        root = windowHandle;
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

            var ancestor = _windows.GetAncestor(windowHandle, WindowAncestor.Root);
            if (ancestor != nint.Zero)
            {
                root = ancestor;
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
    /// Why the tree may not show what the window holds (roadmap M3.F16, D-UIA-07), or null when nothing says so.
    /// TargetElevated first, whatever the walk returned or why it failed: restoring the window does not make its tree
    /// readable, and no number of elements tells a thin tree from a small window (what Windows itself reports about the
    /// window's child windows can still be read). Then WindowMinimized, for a walk that succeeded. Nothing for a window
    /// that was not found or not read.
    /// </summary>
    private IReadOnlyList<string>? Warnings(
        nint windowHandle, UIElementResult result, WindowState? windowState, nint root, UIElementFilter? filter)
    {
        if (result.ErrorCode is UIElementErrorCode.WindowNotFound or UIElementErrorCode.AutomationUnavailable
            or UIElementErrorCode.NotResponding)
        {
            return null;
        }

        var warnings = new List<string>();
        if (ElevationWarning(windowHandle) is { } elevated)
        {
            warnings.Add(elevated);
        }

        if (result.Success && windowState == WindowState.Minimized)
        {
            warnings.Add(MinimizedWarning(root, filter?.VisibleOnly == true));
        }

        return warnings.Count > 0 ? warnings : null;
    }

    /// <summary>
    /// The TargetElevated warning when the window's process, or for a UWP frame the app it shows (see
    /// <see cref="UiaTargets.TargetProcessId"/>), is known or likely to run at a higher integrity level than the server, as
    /// the input guard tells it (<see cref="ProcessIntegrity.IsMorePrivilegedThan"/>); null otherwise, and when it cannot be
    /// read. Token reads, cached per process, which no window's thread answers.
    /// </summary>
    private string? ElevationWarning(nint windowHandle)
    {
        if (_security == null || _windows == null)
        {
            return null;
        }

        try
        {
            var server = _security.ServerIntegrity;
            foreach (var processId in new[] { (int)_windows.GetWindowThreadProcessId(windowHandle), TargetProcessId(windowHandle) }
                         .Where(id => id > 0).Distinct())
            {
                var integrity = _security.GetIntegrity(processId);
                if (integrity.IsMorePrivilegedThan(server))
                {
                    return CaptureWarnings.Format(UIElementWarnings.TargetElevated,
                        $"Window {(long)windowHandle} belongs to a process that runs at a higher integrity level ({integrity.Level}) than " +
                        $"this server ({server.Level}), for example as administrator. Windows keeps this server's UI Automation out " +
                        "of it, so its tree comes back empty or with only what Windows itself reports about its windows: that does " +
                        "not mean it has no controls. Input to it is refused too (TargetElevated). Ask the user to do this step, " +
                        "or to run the server elevated.");
                }
            }
        }
        catch (Exception)
        {
            // Only the warning depends on it
        }

        return null;
    }

    /// <summary>
    /// The WindowMinimized warning for a window whose top-level window <paramref name="root"/> is minimized: with
    /// <paramref name="visibleOnly"/>, its elements were left out; without, they are returned offscreen.
    /// </summary>
    private static string MinimizedWarning(nint root, bool visibleOnly) => CaptureWarnings.Format(UIElementWarnings.WindowMinimized,
        $"The window is minimized (its top-level window, handle {(long)root}), so UI Automation reports all its elements " +
        (visibleOnly
            ? "offscreen and visibleOnly left them out: the tree does not show what the window holds. "
            : "offscreen: their bounds are not where they will be once it is restored, and element_action refuses them " +
              "(ElementOffscreen). ") +
        $"Restore it (set_window_state with handle {(long)root} and state normal, or focus_window) and read it again.");

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

            // Its pages lie below Chromium's own views, deeper than the walk reaches: they are listed under the elements of
            // their render widgets' parent windows (roadmap M3.F35), or, hosted as a visual, in place of the children of the
            // root of Chromium's views (roadmap M3.F43)
            var found = _warmup?.FindPages(windowHandle, cancellationToken) ?? [];
            var pages = found.Where(page => page.ViewsRoot == null).ToList() is { Count: > 0 } hostedByWindows
                ? hostedByWindows.GroupBy(page => page.Host)
                    .ToDictionary(hosted => hosted.Key, hosted => hosted.Select(page => page.Root).ToList())
                : null;
            var visualPages = found.Where(page => page.ViewsRoot != null).ToList() is { Count: > 0 } visual ? visual : null;

            var (windowTitle, windowFramework) = TryReadWindow(window);
            var refs = _registry == null ? null : new WalkRefs(_registry, windowHandle, _registry.NextGeneration());
            var walk = new Walk(
                _tree, maxDepth, filter, controlTypes, windowTitle, windowFramework, refs, OwnedWindows.Below(_windows, windowHandle),
                pages, visualPages, progress, cancellationToken);
            started.Walk = walk;

            // Errors reading the window's own children propagate: an empty tree would look like a
            // window without controls
            walk.Run(window, windowHandle);

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
                FrameworkId: windowFramework,
                OwnedWindows: walk.OwnedWindowHandles);
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
        Func<UiaElement, bool>? leaveOut,
        Dictionary<nint, List<IUiaNode>>? pageHosts,
        List<RenderWidgetPage>? visualPages,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        private readonly object _gate = new();

        /// <summary>The window: its children are the elements of level 1.</summary>
        private readonly WalkNode _root = new(null, Included: false, Ref: null, IndexPath: []);

        /// <summary>
        /// The elements whose children are still to be read, in breadth-first order, with their level's number and the pages
        /// listed first among those children (roadmap M3.F35), or in their place (roadmap M3.F43).
        /// </summary>
        private readonly Queue<(WalkNode Parent, IUiaNode Node, int Level, HostedPages? Pages)> _pending = new();

        /// <summary>The handles of the windows the window owns, left out of its children (<see cref="OwnedWindows"/>).</summary>
        private readonly List<long> _ownedWindows = [];

        /// <summary>
        /// The runtime ids of the elements met so far (roadmap M3.F43), so an element met again, such as a page listed under
        /// its host and met again among Chromium's views, is not listed twice. Used on the walk's thread only.
        /// </summary>
        private readonly HashSet<string> _met = new(StringComparer.Ordinal);

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

        /// <summary>The windows the window owns that were left out of its children, or null when there were none.</summary>
        public IReadOnlyList<long>? OwnedWindowHandles
        {
            get
            {
                lock (_gate)
                {
                    return _ownedWindows.Count > 0 ? _ownedWindows.ToList() : null;
                }
            }
        }

        /// <summary>The walk stopped early because the timeout passed.</summary>
        public bool TimedOut => _timedOut;

        /// <summary>The walk stopped at maxElements with elements left unread (or, when that could not be told, perhaps).</summary>
        public bool StoppedAtMaxElements => _capReached && _leftUnread;

        /// <summary>Walks the subtree of the window (<paramref name="windowHandle"/>), level by level.</summary>
        public void Run(IUiaNode window, nint windowHandle)
        {
            ReadChildren(_root, window, level: 1, isWindow: true, TakePages(windowHandle));
            while (true)
            {
                (WalkNode Parent, IUiaNode Node, int Level, HostedPages? Pages) next;
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
                    _leftUnread = _leftUnread || next.Pages != null || AnyChildLeft(next.Node);
                    return;
                }

                if (StopRequested())
                {
                    return;
                }

                ReadChildren(next.Parent, next.Node, next.Level, pages: next.Pages);
            }
        }

        /// <summary>The pages listed under the element of the window <paramref name="host"/>, once, or null when there are none.</summary>
        private HostedPages? TakePages(nint host) =>
            host != nint.Zero && pageHosts != null && pageHosts.Remove(host, out var hosted) ? new HostedPages(hosted, InPlace: false) : null;

        /// <summary>
        /// The pages hosted as a visual listed in place of the children of the root of Chromium's views whose runtime id is
        /// <paramref name="runtimeId"/> (roadmap M3.F43), once, or null when there are none.
        /// </summary>
        private HostedPages? TakeVisualPages(IReadOnlyList<int> runtimeId)
        {
            if (visualPages is not { Count: > 0 })
            {
                return null;
            }

            var hosted = visualPages.Where(page => page.ViewsRoot!.SequenceEqual(runtimeId)).ToList();
            if (hosted.Count == 0)
            {
                return null;
            }

            visualPages.RemoveAll(hosted.Contains);
            return new HostedPages(hosted.Select(page => page.Root).ToList(), InPlace: true);
        }

        /// <summary>
        /// Whether <paramref name="first"/> or an element still waiting has a child: the elements maxElements left unread.
        /// One call per element (a first child, never the whole level), until the first with a child. When the walk is
        /// asked to stop, a call fails, or more than <see cref="MaxChildProbes"/> elements are waiting, it is not known,
        /// and assumed.
        /// </summary>
        private bool AnyChildLeft(IUiaNode first)
        {
            var node = first;
            for (var probes = 0; ; probes++)
            {
                if (cancellationToken.IsCancellationRequested || probes >= MaxChildProbes)
                {
                    return true;
                }

                try
                {
                    if (tree.HasChildren(node))
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

                    if (next.Pages != null)
                    {
                        return true;
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
                    FrameworkId: windowFramework,
                    OwnedWindows: OwnedWindowHandles);
        }

        /// <summary>
        /// Reads the children of <paramref name="node"/>, which are at <paramref name="level"/>, into
        /// <paramref name="parent"/>, and queues those whose own children are to be read. The pages of render widgets
        /// hosted by the node's window (<paramref name="pages"/>, roadmap M3.F35) come first; a page hosted as a visual takes
        /// the place of the children (roadmap M3.F43), which are then not read.
        /// </summary>
        private void ReadChildren(WalkNode parent, IUiaNode node, int level, bool isWindow = false, HostedPages? pages = null)
        {
            if (StopRequested())
            {
                return;
            }

            IReadOnlyList<Candidate> children;
            try
            {
                children = pages is { InPlace: true } ? [] : FetchChildren(node);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _timedOut = true;
                return;
            }
            catch (Exception) when (!isWindow)
            {
                // This element's children could not be read; the element itself is still returned, with its pages
                Count(ref _skippedElementCount);
                if (pages == null)
                {
                    return;
                }

                children = [];
            }

            // A window the window owns is read only as its own window
            if (isWindow && leaveOut != null)
            {
                children = WithoutOwnedWindows(children);
            }

            if (pages != null)
            {
                children = [.. pages.Roots.Select(PageCandidate), .. children];
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
        /// A page's root as a child of its host: read with the properties a level's cached read brings (one call), or, when
        /// that fails, to be read like an element of a wide level, which skips it when it is gone.
        /// </summary>
        private Candidate PageCandidate(IUiaNode root)
        {
            try
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    return new Candidate(root, tree.ReadElement(root));
                }
            }
            catch (Exception)
            {
                // Read again, on its own, when it is visited
            }

            return new Candidate(root, null);
        }

        /// <summary>
        /// The pages listed first among the children of the element <paramref name="candidate"/> (roadmap M3.F35): those of
        /// the render widgets whose parent window is the element's. Its window handle comes from its level's read; of an
        /// element read on its own, only a Pane's (the control type of a hosted window) is read, and only while pages are
        /// still to be listed. Or the pages hosted as a visual listed in place of its children, when it is the root of
        /// Chromium's views they lie below (<paramref name="runtimeId"/>, roadmap M3.F43). Null when it hosts none.
        /// </summary>
        private HostedPages? PagesHostedBy(Candidate candidate, UiaNodeProperties properties, IReadOnlyList<int>? runtimeId)
        {
            if (runtimeId != null && TakeVisualPages(runtimeId) is { } visual)
            {
                return visual;
            }

            if (pageHosts is not { Count: > 0 })
            {
                return null;
            }

            if (candidate.Cached is { } cached)
            {
                return TakePages(cached.NativeWindowHandle);
            }

            if (!string.Equals(properties.ControlType, "Pane", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                return TakePages(tree.ReadIdentity(candidate.Node).NativeWindowHandle);
            }
            catch (Exception)
            {
                // Not known to host a page
                return null;
            }
        }

        /// <summary>
        /// Notes the element's runtime id (roadmap M3.F43): false when the walk met it before, so it is not listed again. An
        /// element without a runtime id is never taken for another.
        /// </summary>
        private bool FirstMeeting(IReadOnlyList<int>? runtimeId) =>
            runtimeId is not { Count: > 0 } || _met.Add(string.Join('.', runtimeId));

        /// <summary>
        /// The window's children without the windows it owns, whose handles are noted. A child fetched with its properties is
        /// tested on them. Of a level read one element at a time, the first children are read one at a time while they are
        /// owned windows (the HWND proxy lists them first, at most <see cref="ElementSearch.MaxOwnedWindows"/>); the first
        /// that is none keeps its read, so it is not read again, and one that cannot be read ends the reads and is visited as
        /// any other.
        /// </summary>
        private List<Candidate> WithoutOwnedWindows(IReadOnlyList<Candidate> children)
        {
            var kept = new List<Candidate>(children.Count);
            var reading = children.Count > 0 && children[0].Cached == null;
            var owned = 0;
            foreach (var child in children)
            {
                var element = child.Cached;
                if (reading)
                {
                    reading = false;
                    if (owned < ElementSearch.MaxOwnedWindows && !cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            element = tree.ReadElement(child.Node);
                        }
                        catch (Exception)
                        {
                            // Not known to be owned: walked with the window, and read again there
                        }
                    }
                }

                if (element != null && leaveOut!(element))
                {
                    owned++;
                    reading = child.Cached == null;
                    lock (_gate)
                    {
                        _ownedWindows.Add((long)element.NativeWindowHandle);
                    }

                    continue;
                }

                kept.Add(element == null ? child : child with { Cached = element });
            }

            return kept;
        }

        /// <summary>
        /// Adds <paramref name="candidate"/> to <paramref name="parent"/>'s list unless it is offscreen (visibleOnly) or
        /// cannot be read, and queues it for its children while the levels go on.
        /// </summary>
        private void Visit(Candidate candidate, int level, WalkNode parent, int[] indexPath)
        {
            UiaNodeProperties properties;
            IReadOnlyList<int>? runtimeId;
            bool included;
            try
            {
                properties = candidate.Cached?.Properties ?? tree.Read(candidate.Node);

                // An element met before (a page listed under its host and met again among Chromium's views, a subtree a
                // provider lists twice) is listed only the first time
                runtimeId = RuntimeIdOf(candidate);
                if (!FirstMeeting(runtimeId))
                {
                    return;
                }

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
                ? ElementRows.OfItem(runtimeId, properties)
                : ElementRows.EndsTheSearch(properties.ControlType) ? null : row;

            // Registered outside the walk's lock: the registry has its own, and makes no UI Automation call
            var elementRef = included ? refs?.Register(candidate, properties, runtimeId, indexPath, row) : null;
            var node = new WalkNode(properties, included, elementRef, indexPath) { Row = rowBelow };
            var hosted = level < maxDepth ? PagesHostedBy(candidate, properties, runtimeId) : null;
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
                    _pending.Enqueue((node, candidate.Node, level + 1, hosted));
                }
            }
        }

        /// <summary>
        /// An element's runtime id, to tell an element met again and for its ref and the row of the elements below it: from
        /// its level's read, else as the engine knows it (no further call for an element it found); null when it cannot be
        /// read.
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

    /// <summary>
    /// A child to visit: its node, and its properties when they came with it.
    /// </summary>
    private sealed record Candidate(IUiaNode Node, UiaElement? Cached);

    /// <summary>
    /// The pages listed among an element's children: first among them (a render widget's, roadmap M3.F35), or in their
    /// place (<paramref name="InPlace"/>: hosted as a visual, under the root of Chromium's views, roadmap M3.F43).
    /// </summary>
    private sealed record HostedPages(List<IUiaNode> Roots, bool InPlace);

    /// <summary>
    /// Gives the elements a walk returns their refs (roadmap M3.4). An element fetched with its level brings its runtime id
    /// and class along; one read on its own comes with the runtime id the walk read (<see cref="IUiaTree.GetRuntimeId"/>,
    /// which the engines answer from what they read when they found it) and is registered without a class.
    /// </summary>
    private sealed class WalkRefs(IElementRegistry registry, nint windowHandle, long generation)
    {
        public string? Register(
            Candidate candidate, UiaNodeProperties properties, IReadOnlyList<int>? runtimeId, int[] indexPath, ElementRow? row)
        {
            try
            {
                var element = candidate.Cached ?? new UiaElement(
                    candidate.Node, properties, ClassName: null, IsKeyboardFocusable: false, UiaPatterns.None,
                    runtimeId, ControlTypeId: null);
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
