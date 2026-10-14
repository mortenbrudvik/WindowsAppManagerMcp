using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The accessibility warm-up of windows that show web content (roadmap M3.3): Chromium browsers, Electron and CEF apps
/// (VS Code, Slack, Discord, Spotify) and WebView2 panes (the new Outlook, Teams). Chromium builds a page's accessibility
/// tree only after a UI Automation client asks for it, and then in the background, so the first read of such a window sees
/// an empty document.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Detection uses only Win32 reads that do not wait for the window's application: a window shows web content when
/// it, or a visible window inside it, has the class Chrome_RenderWidgetHostHWND, the window Chromium renders a page into
/// (a WebView2 pane is such a window of msedgewebview2.exe inside the host app's window). A Chromium window without one,
/// such as a menu or a popup of the browser frame (Chrome_WidgetWin_*), has no page to wait for and is left alone.</item>
/// <item>The warm-up asks UI Automation for each render widget's element, which makes Chromium turn on its accessibility
/// tree for the page, finds the page's Document there (the element itself, or within a few levels of panes below it), and
/// counts the Document's children. When there are none it checks again every 200-500 ms, until the count is above zero
/// and stopped changing, or the budget (at most <see cref="MaxWait"/>) or the caller's deadline ends the wait.</item>
/// <item>WebView2 content hosted as a visual (roadmap M3.F23: CoreWebView2CompositionController, which the WinUI 3 WebView2
/// control uses) has no render widget: the page is drawn into the host app's window, and its UI Automation elements are
/// part of that window's tree. Only when a window has no render widget and its process started an msedgewebview2.exe (one
/// process list, Win32 only) is the window's UI Automation tree searched, within <see cref="VisualDocumentSearchDepth"/>
/// levels of panes and groups and at most <see cref="VisualDocumentSearchListings"/> listings, for a Document whose
/// framework is Chrome. When one is found it is checked like a render widget's Document and reported as WebView2; when
/// none is, the window is read as before. So a window without web content costs one process list, and a window of an app
/// that hosts WebView2 without a page in it (a dialog of that app) a bounded search.</item>
/// <item>Only the window being read is touched: nothing global is set, and no other application is asked anything.</item>
/// <item>A window whose pages got web content is remembered with the render widgets it showed (at most
/// <see cref="CacheCapacity"/> windows, a dead window or one whose handle now belongs to another process is forgotten), so
/// later reads do not wait. A later read still lists the window's shown render widgets (Win32 only): one that was not
/// warmed up, such as a tab shown since, is warmed up then, because Chromium builds a hidden page's tree only once it is
/// shown. A page hosted as a visual is remembered by its window, so a later read of that window makes no process list or
/// search. A page that stayed empty is waited for again, at most <see cref="MaxWaitingAttempts"/> times; after that it is
/// only checked, so a page that is really empty does not cost every read a wait.</item>
/// <item>A walk of the window lists its render widgets' pages under their parent windows' elements (roadmap M3.F35:
/// Chromium's own views lie between those elements and the page, deeper than the walk reaches). <see cref="FindPages"/>
/// gives them each page's root, the element the warm-up checks: it reuses the render widgets the warm-up just found in the
/// window, so a window without web content costs the walk no further call.</item>
/// </list>
/// </remarks>
public sealed class AccessibilityWarmup : IAccessibilityWarmup
{
    /// <summary>The longest wait for a window's web content, whatever budget the caller gives.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>How many windows are remembered at most.</summary>
    public const int CacheCapacity = 256;

    /// <summary>How many warm-ups of a window that stayed empty wait; later ones only check.</summary>
    public const int MaxWaitingAttempts = 3;

    /// <summary>The class of the window Chromium renders a page into.</summary>
    public const string RenderWidgetHostClass = "Chrome_RenderWidgetHostHWND";

    /// <summary>The most child windows looked at to find the render widgets.</summary>
    public const int MaxChildWindows = 2000;

    /// <summary>The most render widgets (pages) of one window that are warmed up.</summary>
    public const int MaxRenderWidgets = 4;

    /// <summary>
    /// How many levels below a window's element the Document of web content hosted as a visual is looked for (roadmap
    /// M3.F23).
    /// </summary>
    public const int VisualDocumentSearchDepth = 6;

    /// <summary>The most levels listed while looking for the Document of web content hosted as a visual (roadmap M3.F23).</summary>
    public const int VisualDocumentSearchListings = 16;

    /// <summary>The framework of the UI Automation elements of a Chromium page.</summary>
    public const string ChromeFrameworkId = "Chrome";

    private const string WebView2ProcessName = "msedgewebview2";
    private const string DesktopWindowClass = "#32769";

    // The Document is the render widget's element or a few panes below it; the search reads at most this many levels
    private const int DocumentSearchDepth = 3;
    private const int DocumentSearchListings = 12;

    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(500)
    ];

    private static readonly IReadOnlySet<nint> NoWidgets = new HashSet<nint>();

    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase) { "Pane", "Group", "Custom", "Window" };

    private readonly IUiaTree _tree;
    private readonly INativeWindowWrapper _windows;
    private readonly INativeProcessWrapper _processes;
    private readonly TimeProvider _time;
    private readonly Action<TimeSpan, CancellationToken> _wait;
    private readonly object _gate = new();
    private readonly Dictionary<nint, Remembered> _remembered = [];
    private long _sequence;

    // The render widgets the latest warm-up found, for the walk that follows it (FindPages, roadmap M3.F35)
    private (nint Window, List<nint> Widgets)? _found;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="windows">Finds the render widgets and tells whether a remembered window is still alive.</param>
    /// <param name="processes">Lists the processes, to tell whether a window's process started a WebView2 (roadmap M3.F23).</param>
    /// <param name="time">Measures the wait; default the system clock.</param>
    /// <param name="wait">Waits between two checks, returning early when the token is cancelled; default a wait on the
    /// token's handle. Tests pass one that moves a manual clock.</param>
    public AccessibilityWarmup(
        IUiaTree tree,
        INativeWindowWrapper windows,
        INativeProcessWrapper processes,
        TimeProvider? time = null,
        Action<TimeSpan, CancellationToken>? wait = null)
    {
        _tree = tree;
        _windows = windows;
        _processes = processes;
        _time = time ?? TimeProvider.System;
        _wait = wait ?? ((delay, token) => token.WaitHandle.WaitOne(delay));
    }

    /// <summary>
    /// The warm-up budget of a call with <paramref name="timeout"/>: a third of it, at most <see cref="MaxWait"/>, so the
    /// read after it keeps most of the time.
    /// </summary>
    public static TimeSpan BudgetFor(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(MaxWait.Ticks, timeout.Ticks / 3));

    /// <summary>How many windows are remembered now.</summary>
    public int RememberedCount
    {
        get
        {
            lock (_gate)
            {
                return _remembered.Count;
            }
        }
    }

    /// <inheritdoc />
    public AccessibilityWarmupInfo? WarmUp(nint window, TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            return WarmUpWindow(window, budget, cancellationToken);
        }
        catch (Exception)
        {
            // A courtesy: the read that follows reports its own errors
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RenderWidgetPage> FindPages(nint window, CancellationToken cancellationToken)
    {
        try
        {
            if (window == nint.Zero || ClassOf(window) == DesktopWindowClass)
            {
                return [];
            }

            var pages = new List<RenderWidgetPage>();
            foreach (var widget in TakeFound(window) ?? FindRenderWidgets(window))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // A render widget read as the window is its own page
                if (widget == window)
                {
                    continue;
                }

                // One whose parent is gone has nowhere to be listed
                var host = _windows.GetAncestor(widget, WindowAncestor.Parent);
                if (host == nint.Zero)
                {
                    continue;
                }

                try
                {
                    if (FindDocument(widget, cancellationToken) is { } root)
                    {
                        pages.Add(new RenderWidgetPage(widget, host, root));
                    }
                }
                catch (TimeoutException)
                {
                    // Every further call to the provider would wait as long
                    break;
                }
                catch (Exception)
                {
                    // This page is left out; the others are still listed
                }
            }

            return pages;
        }
        catch (Exception)
        {
            // A courtesy: the walk lists the window's tree without its pages
            return [];
        }
    }

    /// <summary>
    /// The render widgets the latest warm-up found in <paramref name="window"/>, once: the walk that follows a warm-up lists
    /// their pages (roadmap M3.F35). Null when the latest warm-up was of another window, or its widgets were taken already.
    /// </summary>
    private List<nint>? TakeFound(nint window)
    {
        lock (_gate)
        {
            if (_found is not { } found || found.Window != window)
            {
                return null;
            }

            _found = null;
            return found.Widgets;
        }
    }

    private AccessibilityWarmupInfo? WarmUpWindow(nint window, TimeSpan budget, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _found = null;
        }

        // The desktop window's children are every application's windows: only one window's own pages are warmed up
        if (window == nint.Zero || !_windows.IsWindow(window) || ClassOf(window) == DesktopWindowClass)
        {
            return null;
        }

        var processId = _windows.GetWindowThreadProcessId(window);
        Remembered? known;
        lock (_gate)
        {
            if (_remembered.TryGetValue(window, out known) && known.ProcessId != processId)
            {
                // The handle now belongs to another window
                _remembered.Remove(window);
                known = null;
            }
        }

        var widgets = FindRenderWidgets(window);
        lock (_gate)
        {
            _found = (window, widgets);
        }

        if (widgets.Count == 0)
        {
            return WarmUpVisual(window, processId, known, budget, cancellationToken);
        }

        // A page that was warmed up is remembered with its render widget. Chromium builds a hidden page's tree only once it
        // is shown, so a tab shown since (a widget not warmed up yet) is warmed up like a window read for the first time
        var warmed = known?.Warmed ?? NoWidgets;
        var pending = widgets.Where(widget => !warmed.Contains(widget)).ToList();
        if (pending.Count == 0)
        {
            return null;
        }

        var host = IsWebView2(pending[0]) ? WebContentHost.WebView2 : WebContentHost.Chromium;
        return WarmUpPages(window, processId, known, new Pages(widgets, pending, host, FindDocument, []), budget, cancellationToken);
    }

    /// <summary>
    /// The warm-up of a window without a render widget, whose web content may be hosted as a visual (roadmap M3.F23): its
    /// page has no window of its own, so it is remembered by the window. Null, with no UI Automation call, when the
    /// window's process started no msedgewebview2.exe; null, after a bounded search, when no Document of Chromium is found
    /// below the window.
    /// </summary>
    private AccessibilityWarmupInfo? WarmUpVisual(
        nint window, uint processId, Remembered? known, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (known?.Warmed.Contains(window) == true || !StartedWebView2(processId) ||
            FindVisualDocument(window, cancellationToken) is not { } document)
        {
            return null;
        }

        var pages = new Pages([window], [window], WebContentHost.WebView2, FindVisualDocument,
            new Dictionary<nint, IUiaNode> { [window] = document });
        return WarmUpPages(window, processId, known, pages, budget, cancellationToken);
    }

    /// <summary>
    /// Waits for <paramref name="pages"/>' documents (<see cref="Poll"/>) and remembers the outcome: a window whose pages got
    /// web content with the pages shown now, one whose pages stayed empty with the number of warm-ups that waited for them.
    /// </summary>
    private AccessibilityWarmupInfo WarmUpPages(
        nint window, uint processId, Remembered? known, Pages pages, TimeSpan budget, CancellationToken cancellationToken)
    {
        var warmed = known?.Warmed ?? NoWidgets;
        var attempts = (known?.Attempts ?? 0) + 1;
        var waitFor = attempts > MaxWaitingAttempts
            ? TimeSpan.Zero
            : budget < TimeSpan.Zero ? TimeSpan.Zero : budget > MaxWait ? MaxWait : budget;

        var info = Poll(pages, waitFor, cancellationToken);
        if (info.Status == AccessibilityWarmupStatus.NotReady && attempts > MaxWaitingAttempts)
        {
            info = info with
            {
                Warning = info.Warning + $" Earlier reads already waited for it {MaxWaitingAttempts} times, so this one did not wait."
            };
        }

        // Only the widgets shown now are kept: a page shown again later is checked again, at the cost of one read when its
        // tree is still there
        var ready = info.Status != AccessibilityWarmupStatus.NotReady;
        Remember(window, processId,
            ready ? pages.Shown.ToHashSet() : pages.Shown.Where(warmed.Contains).ToHashSet(),
            ready ? 0 : attempts);
        return info;
    }

    /// <summary>
    /// The visible render widgets of <paramref name="window"/>: the window itself when it is one (whatever its visibility,
    /// since it was asked for), then those inside it, at most <see cref="MaxRenderWidgets"/>. Hidden ones are pages that
    /// are not shown, such as background tabs.
    /// </summary>
    private List<nint> FindRenderWidgets(nint window)
    {
        var widgets = new List<nint>();
        if (ClassOf(window) == RenderWidgetHostClass)
        {
            widgets.Add(window);
        }

        var seen = 0;
        _windows.EnumChildWindows(window, child =>
        {
            if (++seen > MaxChildWindows)
            {
                return false;
            }

            if (ClassOf(child) == RenderWidgetHostClass && _windows.IsWindowVisible(child) && !widgets.Contains(child))
            {
                widgets.Add(child);
            }

            return widgets.Count < MaxRenderWidgets;
        });

        return widgets;
    }

    private string ClassOf(nint window)
    {
        try
        {
            return _windows.GetClassName(window);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private bool IsWebView2(nint widget)
    {
        try
        {
            var processId = _windows.GetWindowThreadProcessId(widget);
            return processId != 0 &&
                   string.Equals(_windows.GetProcessName(processId), WebView2ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the process started a WebView2 browser process (msedgewebview2.exe), read from one process list (Win32 only):
    /// web content hosted as a visual in its windows comes from there (roadmap M3.F23). A parent ID can be a reused PID, so
    /// this only tells which windows are worth a search; the Document of Chromium found below the window decides.
    /// </summary>
    private bool StartedWebView2(uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            return _processes.GetProcessList() is { } processes &&
                   processes.Values.Any(process => process.ParentProcessId == (int)processId &&
                                                   string.Equals(Path.GetFileNameWithoutExtension(process.ImageName),
                                                       WebView2ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the documents' children until they are there and stopped changing, or <paramref name="budget"/> or the
    /// cancellation ends the wait. A count above zero at the first check is taken at once: the tree was already built.
    /// </summary>
    private AccessibilityWarmupInfo Poll(Pages pages, TimeSpan budget, CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        var host = pages.Host;
        int? previous = null;
        int? count;
        var polls = 0;
        AccessibilityWarmupStatus status;
        for (var step = 0; ; step++)
        {
            count = CountDocumentChildren(pages, cancellationToken, out var stuck);
            polls++;
            if (count > 0 && (polls == 1 || count == previous))
            {
                status = AccessibilityWarmupStatus.Ready;
                break;
            }

            var elapsed = _time.GetElapsedTime(start);
            if (stuck || elapsed >= budget || cancellationToken.IsCancellationRequested)
            {
                status = count > 0 ? AccessibilityWarmupStatus.Partial : AccessibilityWarmupStatus.NotReady;
                break;
            }

            var delay = Steps[Math.Min(step, Steps.Length - 1)];
            _wait(delay < budget - elapsed ? delay : budget - elapsed, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                status = count > 0 ? AccessibilityWarmupStatus.Partial : AccessibilityWarmupStatus.NotReady;
                break;
            }

            previous = count;
        }

        var waitedMs = (int)Math.Round(_time.GetElapsedTime(start).TotalMilliseconds);
        return new AccessibilityWarmupInfo(status, host, waitedMs, polls, count, Warning(status, host, waitedMs, count));
    }

    private static string? Warning(AccessibilityWarmupStatus status, WebContentHost host, int waitedMs, int? count) => status switch
    {
        AccessibilityWarmupStatus.Partial =>
            $"The window's web content was still being added when the wait ended ({count} top-level elements so far): read it " +
            "again for the full tree.",
        AccessibilityWarmupStatus.NotReady =>
            $"The window shows web content, but {(count == null ? "no web document was found" : "its web document had no elements")} " +
            $"after {waitedMs} ms, so what was read shows the app's frame, not its page: an element missing from it may still be " +
            "there. Read again in a moment. " +
            (host == WebContentHost.WebView2
                ? "If it stays empty, use a screenshot; WebView2 content cannot be turned on from the launch."
                : "If it stays empty, relaunch the app with launch_application forceAccessibility=true, or use a screenshot."),
        _ => null
    };

    /// <summary>
    /// The children of the pages' documents, together, or null when no document was found. Each page's document is looked
    /// for until it is found; one that disappears (the page navigated) is looked for again at the next check. A provider
    /// that times out ends the wait (<paramref name="stuck"/>): every further call would wait as long.
    /// </summary>
    private int? CountDocumentChildren(Pages pages, CancellationToken cancellationToken, out bool stuck)
    {
        stuck = false;
        var found = false;
        var total = 0;
        var documents = pages.Documents;
        foreach (var widget in pages.Pending)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (!documents.TryGetValue(widget, out var document))
                {
                    if (pages.FindDocument(widget, cancellationToken) is not { } located)
                    {
                        continue;
                    }

                    documents[widget] = document = located;
                }

                total += _tree.GetChildren(document, cancellationToken).Count;
                found = true;
            }
            catch (TimeoutException)
            {
                stuck = true;
                break;
            }
            catch (UiaElementUnavailableException)
            {
                documents.Remove(widget);
            }
            catch (Exception)
            {
                // A provider that failed this time: checked again at the next poll
            }
        }

        return found ? total : null;
    }

    /// <summary>
    /// The page's Document of a render widget: the widget's own element when it is the Document, or the first Document
    /// within <see cref="DocumentSearchDepth"/> levels of panes and groups below it, or else the widget's own element when
    /// its children are Chromium's: WebView2 (runtime 155, roadmap M3.F33) gives the widget the Win32 pane "Chrome Legacy
    /// Window", whose children are the page's own elements with no Document element above them. Null when there is none
    /// (yet).
    /// </summary>
    private IUiaNode? FindDocument(nint widget, CancellationToken cancellationToken)
    {
        if (_tree.FromHandle(widget) is not { } root)
        {
            return null;
        }

        if (IsDocument(_tree.Read(root).ControlType))
        {
            return root;
        }

        return FindDocumentBelow(root, DocumentSearchDepth, DocumentSearchListings, _ => true, cancellationToken) ??
               (_tree.GetChildElements(root, cancellationToken).Any(child =>
                   string.Equals(child.Properties.FrameworkId, ChromeFrameworkId, StringComparison.OrdinalIgnoreCase))
                   ? root
                   : null);
    }

    /// <summary>
    /// The Document of web content hosted as a visual in <paramref name="window"/> (roadmap M3.F23): the first Document whose
    /// framework is Chrome within <see cref="VisualDocumentSearchDepth"/> levels of panes and groups below the window's
    /// element, listing at most <see cref="VisualDocumentSearchListings"/> levels. Null when there is none (yet).
    /// </summary>
    private IUiaNode? FindVisualDocument(nint window, CancellationToken cancellationToken) =>
        _tree.FromHandle(window) is { } root
            ? FindDocumentBelow(root, VisualDocumentSearchDepth, VisualDocumentSearchListings,
                properties => string.Equals(properties.FrameworkId, ChromeFrameworkId, StringComparison.OrdinalIgnoreCase),
                cancellationToken)
            : null;

    /// <summary>
    /// The first Document that <paramref name="accept"/> takes within <paramref name="maxDepth"/> levels of panes and groups
    /// below <paramref name="root"/>, breadth first, listing at most <paramref name="maxListings"/> levels. Null when there is
    /// none.
    /// </summary>
    private IUiaNode? FindDocumentBelow(
        IUiaNode root, int maxDepth, int maxListings, Func<UiaNodeProperties, bool> accept, CancellationToken cancellationToken)
    {
        var pending = new Queue<(IUiaNode Node, int Depth)>();
        pending.Enqueue((root, 0));
        var listings = 0;
        while (pending.Count > 0 && listings < maxListings && !cancellationToken.IsCancellationRequested)
        {
            var (node, depth) = pending.Dequeue();
            listings++;
            foreach (var child in _tree.GetChildElements(node, cancellationToken))
            {
                if (IsDocument(child.Properties.ControlType) && accept(child.Properties))
                {
                    return child.Node;
                }

                if (depth + 1 < maxDepth && Containers.Contains(child.Properties.ControlType))
                {
                    pending.Enqueue((child.Node, depth + 1));
                }
            }
        }

        return null;
    }

    private static bool IsDocument(string controlType) => string.Equals(controlType, "Document", StringComparison.OrdinalIgnoreCase);

    /// <summary>Remembers a window's warm-up, forgetting dead windows first and then the oldest when the cache is full.</summary>
    private void Remember(nint window, uint processId, IReadOnlySet<nint> warmed, int attempts)
    {
        lock (_gate)
        {
            foreach (var handle in _remembered.Keys.Where(h => h != window && !IsAlive(h)).ToList())
            {
                _remembered.Remove(handle);
            }

            if (!_remembered.ContainsKey(window) && _remembered.Count >= CacheCapacity)
            {
                _remembered.Remove(_remembered.MinBy(e => e.Value.Sequence).Key);
            }

            _remembered[window] = new Remembered(processId, warmed, attempts, ++_sequence);
        }
    }

    private bool IsAlive(nint window)
    {
        try
        {
            return _windows.IsWindow(window);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A remembered window: its process (a reused handle belongs to another), its shown render widgets whose pages got web
    /// content (at most <see cref="MaxRenderWidgets"/>; the window itself for a page hosted as a visual), and how many
    /// warm-ups waited for a page that stayed empty since.
    /// </summary>
    private sealed record Remembered(uint ProcessId, IReadOnlySet<nint> Warmed, int Attempts, long Sequence);

    /// <summary>
    /// The pages of a window a warm-up waits for, each by the window it is remembered by: a render widget, or the window
    /// itself for web content hosted as a visual (roadmap M3.F23).
    /// </summary>
    /// <param name="Shown">Every page shown now, remembered when the warm-up gets web content.</param>
    /// <param name="Pending">The pages not warmed up yet, which the warm-up checks.</param>
    /// <param name="Host">What shows the web content.</param>
    /// <param name="FindDocument">Finds a page's Document, or null when there is none (yet).</param>
    /// <param name="Documents">The Documents found so far, by page.</param>
    private sealed record Pages(
        List<nint> Shown,
        List<nint> Pending,
        WebContentHost Host,
        Func<nint, CancellationToken, IUiaNode?> FindDocument,
        Dictionary<nint, IUiaNode> Documents);
}
