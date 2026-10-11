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
/// <item>Only the window being read is touched: nothing global is set, and no other application is asked anything.</item>
/// <item>A window that got web content is remembered (at most <see cref="CacheCapacity"/>, a dead window or one whose
/// handle now belongs to another process is forgotten), so later reads do not wait. A window that stayed empty is waited
/// for again, at most <see cref="MaxWaitingAttempts"/> times; after that it is only checked, so a page that is really
/// empty does not cost every read a wait.</item>
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

    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase) { "Pane", "Group", "Custom", "Window" };

    private readonly IUiaTree _tree;
    private readonly INativeWindowWrapper _windows;
    private readonly TimeProvider _time;
    private readonly Action<TimeSpan, CancellationToken> _wait;
    private readonly object _gate = new();
    private readonly Dictionary<nint, Remembered> _remembered = [];
    private long _sequence;

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="windows">Finds the render widgets and tells whether a remembered window is still alive.</param>
    /// <param name="time">Measures the wait; default the system clock.</param>
    /// <param name="wait">Waits between two checks, returning early when the token is cancelled; default a wait on the
    /// token's handle. Tests pass one that moves a manual clock.</param>
    public AccessibilityWarmup(
        IUiaTree tree,
        INativeWindowWrapper windows,
        TimeProvider? time = null,
        Action<TimeSpan, CancellationToken>? wait = null)
    {
        _tree = tree;
        _windows = windows;
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

    private AccessibilityWarmupInfo? WarmUpWindow(nint window, TimeSpan budget, CancellationToken cancellationToken)
    {
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

        if (known is { Warmed: true })
        {
            return null;
        }

        if (FindRenderWidgets(window) is not { Count: > 0 } widgets)
        {
            return null;
        }

        var host = IsWebView2(widgets[0]) ? WebContentHost.WebView2 : WebContentHost.Chromium;
        var attempts = (known?.Attempts ?? 0) + 1;
        var waitFor = attempts > MaxWaitingAttempts
            ? TimeSpan.Zero
            : budget < TimeSpan.Zero ? TimeSpan.Zero : budget > MaxWait ? MaxWait : budget;

        var info = Poll(widgets, host, waitFor, cancellationToken);
        if (info.Status == AccessibilityWarmupStatus.NotReady && attempts > MaxWaitingAttempts)
        {
            info = info with
            {
                Warning = info.Warning + $" Earlier reads already waited for it {MaxWaitingAttempts} times, so this one did not wait."
            };
        }

        Remember(window, processId, warmed: info.Status != AccessibilityWarmupStatus.NotReady, attempts);
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
    /// Checks the documents' children until they are there and stopped changing, or <paramref name="budget"/> or the
    /// cancellation ends the wait. A count above zero at the first check is taken at once: the tree was already built.
    /// </summary>
    private AccessibilityWarmupInfo Poll(List<nint> widgets, WebContentHost host, TimeSpan budget, CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        var documents = new Dictionary<nint, IUiaNode>();
        int? previous = null;
        int? count;
        var polls = 0;
        AccessibilityWarmupStatus status;
        for (var step = 0; ; step++)
        {
            count = CountDocumentChildren(widgets, documents, cancellationToken, out var stuck);
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
    /// The children of the render widgets' documents, together, or null when no document was found. Each widget's document
    /// is looked for until it is found; one that disappears (the page navigated) is looked for again at the next check. A
    /// provider that times out ends the wait (<paramref name="stuck"/>): every further call would wait as long.
    /// </summary>
    private int? CountDocumentChildren(
        List<nint> widgets, Dictionary<nint, IUiaNode> documents, CancellationToken cancellationToken, out bool stuck)
    {
        stuck = false;
        var found = false;
        var total = 0;
        foreach (var widget in widgets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (!documents.TryGetValue(widget, out var document))
                {
                    if (FindDocument(widget, cancellationToken) is not { } located)
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
    /// within <see cref="DocumentSearchDepth"/> levels of panes and groups below it. Null when there is none (yet).
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

        var pending = new Queue<(IUiaNode Node, int Depth)>();
        pending.Enqueue((root, 0));
        var listings = 0;
        while (pending.Count > 0 && listings < DocumentSearchListings && !cancellationToken.IsCancellationRequested)
        {
            var (node, depth) = pending.Dequeue();
            listings++;
            foreach (var child in _tree.GetChildElements(node, cancellationToken))
            {
                if (IsDocument(child.Properties.ControlType))
                {
                    return child.Node;
                }

                if (depth + 1 < DocumentSearchDepth && Containers.Contains(child.Properties.ControlType))
                {
                    pending.Enqueue((child.Node, depth + 1));
                }
            }
        }

        return null;
    }

    private static bool IsDocument(string controlType) => string.Equals(controlType, "Document", StringComparison.OrdinalIgnoreCase);

    /// <summary>Remembers a window's warm-up, forgetting dead windows first and then the oldest when the cache is full.</summary>
    private void Remember(nint window, uint processId, bool warmed, int attempts)
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

    /// <summary>A remembered window: its process (a reused handle belongs to another), and whether it got web content.</summary>
    private sealed record Remembered(uint ProcessId, bool Warmed, int Attempts, long Sequence);
}
