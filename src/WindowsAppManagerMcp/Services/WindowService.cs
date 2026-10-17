using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Lists, reads, moves, resizes, restores, minimizes and closes windows.
/// </summary>
/// <remarks>
/// SetWindowPos and ShowWindow on another thread's window wait until that thread answers, and the window tools run
/// inside the process-wide input gate. So every such call goes through <see cref="IWindowCallRunner"/>: a window
/// that does not answer is given up on at its deadline with <see cref="WindowNotRespondingException"/>, and a hung
/// application cannot hold the gate, and with it every input and window tool, until it recovers.
/// </remarks>
public class WindowService : IWindowService
{
    /// <summary>
    /// The longest delay the batches sleep between two windows, whatever delayBetweenMs asks for: they sleep inside
    /// the input gate, so an agent's value must not hold it for long.
    /// </summary>
    public const int MaxDelayBetweenMs = 5000;

    /// <summary>
    /// How long a state change (minimize, maximize, restore) waits for the window to be seen in the new state before it
    /// reports StateChangeFailed: applications that animate the change take a moment.
    /// </summary>
    public static readonly TimeSpan StateChangeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>How often the state is read while a state change waits for it.</summary>
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IMonitorService _monitorService;
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IHostGuard _hostGuard;
    private readonly ITargetPolicy? _targetPolicy;
    private readonly IWindowCallRunner _windowCalls;
    private readonly TimeSpan _stateChangeTimeout;
    private readonly INativeVirtualDesktopWrapper? _virtualDesktops;
    private readonly TopmostPins _topmostPins;

    /// <param name="monitorService">Monitor lookups.</param>
    /// <param name="nativeWrapper">The window calls.</param>
    /// <param name="hostGuard">Recognizes the agent's host windows (close guard, WindowInfo.IsAgentHost).</param>
    /// <param name="targetPolicy">
    /// The server's target policy (roadmap M1.12). Closing a window and every window change (move, resize,
    /// bounds, state, snap, move to a monitor, and their batches) refuse a window it denies, so these tools never
    /// restore, raise or move it (input and the app itself still can). The server always passes it; without it no
    /// window is refused by policy.
    /// </param>
    /// <param name="windowCalls">
    /// Runs the calls that can block on another thread's window (SetWindowPos, ShowWindow) with a deadline;
    /// default a <see cref="WindowCallRunner"/> over <paramref name="nativeWrapper"/>.
    /// </param>
    /// <param name="stateChangeTimeout">
    /// How long a state change waits to see the new state; default <see cref="StateChangeTimeout"/>. Tests pass zero.
    /// </param>
    /// <param name="virtualDesktops">
    /// The shell's virtual desktop manager, which tells a window on another desktop from a suspended or prelaunched frame
    /// that the shell cloaks the same way (<see cref="GetApplicationWindowsOnOtherDesktops"/>). The server always passes it;
    /// without it every window the shell cloaks counts as on another desktop.
    /// </param>
    /// <param name="topmostPins">
    /// Where set_window_zorder records the windows it made topmost (roadmap M6.7), which get_server_info lists. The server
    /// shares one; default a new one.
    /// </param>
    public WindowService(
        IMonitorService monitorService,
        INativeWindowWrapper nativeWrapper,
        IHostGuard hostGuard,
        ITargetPolicy? targetPolicy = null,
        IWindowCallRunner? windowCalls = null,
        TimeSpan? stateChangeTimeout = null,
        INativeVirtualDesktopWrapper? virtualDesktops = null,
        TopmostPins? topmostPins = null)
    {
        _monitorService = monitorService;
        _nativeWrapper = nativeWrapper;
        _hostGuard = hostGuard ?? throw new ArgumentNullException(nameof(hostGuard));
        _targetPolicy = targetPolicy;
        _windowCalls = windowCalls ?? new WindowCallRunner(nativeWrapper);
        _stateChangeTimeout = stateChangeTimeout ?? StateChangeTimeout;
        _virtualDesktops = virtualDesktops;
        _topmostPins = topmostPins ?? new TopmostPins();
    }

    /// <summary>The delay a batch sleeps between two windows for <paramref name="delayBetweenMs"/>: 0 to <see cref="MaxDelayBetweenMs"/>.</summary>
    public static TimeSpan BatchDelay(int delayBetweenMs) =>
        TimeSpan.FromMilliseconds(Math.Clamp(delayBetweenMs, 0, MaxDelayBetweenMs));

    public IReadOnlyList<WindowInfo> GetAllWindows(bool includeMinimized = true) =>
        FindWindows(new WindowQuery { IncludeMinimized = includeMinimized });

    public IReadOnlyList<WindowInfo> FindWindows(
        string? titleContains = null,
        string? processName = null,
        int? processId = null,
        nint? handle = null,
        bool visibleOnly = true) =>
        FindWindows(new WindowQuery
        {
            TitleContains = titleContains,
            ProcessName = processName,
            ProcessId = processId,
            Handle = handle,
            VisibleOnly = visibleOnly
        });

    public IReadOnlyList<WindowInfo> FindWindows(WindowQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, "limit must be at least 1.");

        var titleRegex = query.TitleRegex is { Length: > 0 } pattern
            ? WindowTitles.CompileTitleRegex(pattern, out var invalid) ?? throw new ArgumentException(invalid, nameof(query))
            : null;

        // A handle is read directly. Otherwise collect the top-level windows first and inspect them
        // afterwards, so no filter code runs inside the native enumeration callback.
        IReadOnlyList<nint>? topLevelWindows = null;
        IReadOnlyList<nint> candidates;
        if (query.Handle is { } handle)
        {
            candidates = IsValidWindow(handle) ? [handle] : [];
        }
        else
        {
            topLevelWindows = EnumerateTopLevelWindows(_nativeWrapper);
            candidates = topLevelWindows;
        }

        var session = new ReadSession(_nativeWrapper, topLevelWindows);
        var windows = new List<WindowInfo>();
        foreach (var hWnd in candidates)
        {
            if (Match(hWnd, query, titleRegex, session) is { } info)
                windows.Add(info);
        }

        return Order(windows, query.Sort, query.Limit);
    }

    /// <summary>
    /// The windows in <paramref name="sort"/> order, at most <paramref name="limit"/>. They arrive in z-order (EnumWindows
    /// enumerates front to back), and the sorts are stable, so equal titles or processes keep their z-order.
    /// </summary>
    private static IReadOnlyList<WindowInfo> Order(List<WindowInfo> windows, WindowSort sort, int? limit)
    {
        IEnumerable<WindowInfo> ordered = sort switch
        {
            WindowSort.Title => windows.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase),
            WindowSort.Process => windows.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase),
            _ => windows
        };

        if (limit is { } max)
            ordered = ordered.Take(max);

        return ordered as List<WindowInfo> ?? ordered.ToList();
    }

    public WindowResolution ResolveWindow(WindowSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (SelectorRefusal(selector) is { } invalid)
            return WindowResolution.Failed(InvalidArgumentsCode, invalid);

        // A pattern that runs too long is the pattern's fault on every path (the listing, a handle's check, the
        // foreground's check): InvalidArguments, never a mismatch that blames the window
        try
        {
            return Resolve(selector);
        }
        catch (RegexMatchTimeoutException)
        {
            return WindowResolution.Failed(InvalidArgumentsCode, RegexTimeoutMessage(selector.TitleRegex));
        }
    }

    /// <summary>The InvalidArguments message of a titleRegex that took longer than <see cref="WindowTitles.RegexTimeout"/>.</summary>
    internal static string RegexTimeoutMessage(string? pattern) =>
        $"titleRegex '{pattern}' took longer than {WindowTitles.RegexTimeout.TotalMilliseconds:0} ms on a window title, so it was " +
        "given up. Use a simpler pattern, or titleContains or titleEquals. Nothing was done.";

    private WindowResolution Resolve(WindowSelector selector)
    {
        if (selector.Handle is { } handle)
            return ResolveHandle((nint)handle, selector);

        if (selector.Foreground)
            return ResolveForeground(selector);

        var matches = FindWindows(selector.ToQuery());
        if (selector.Index is { } index)
        {
            return index < matches.Count
                ? WindowResolution.Found(matches[index])
                : WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound),
                    $"window has index {index}, but only {matches.Count} window{(matches.Count == 1 ? "" : "s")} match{(matches.Count == 1 ? "es" : "")} " +
                    $"{selector.Describe()} (index counts from 0, front to back). Nothing was done.",
                    matches);
        }

        return matches.Count switch
        {
            1 => WindowResolution.Found(matches[0]),
            0 => WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound), NoMatchMessage(selector)),
            _ => WindowResolution.Failed(nameof(WindowErrorCode.AmbiguousWindow),
                $"{matches.Count} windows match {selector.Describe()}, so the window cannot be told; nothing was done. candidates " +
                $"lists {(matches.Count > WindowResolution.MaxCandidates ? $"the first {WindowResolution.MaxCandidates}, front to back" : "them, front to back")}" +
                "; isAgentHost marks your own terminal or editor. Call again with the one you mean as window {handle, processId} " +
                "(a reused handle is then refused), or narrow the filters (titleEquals, processId, className). Do not turn a " +
                "candidate's position into window.index: index counts the z-order at the time of the next call, which can change.",
                matches)
        };
    }

    /// <summary>The errorCode of an argument a tool cannot use: InvalidArguments, as the tool-error filter reports it.</summary>
    private const string InvalidArgumentsCode = "InvalidArguments";

    /// <summary>Why a selector cannot be used at all (InvalidArguments), or null when it can.</summary>
    private static string? SelectorRefusal(WindowSelector selector)
    {
        if (!selector.HasFilters && !selector.Handle.HasValue && !selector.Foreground)
        {
            return "window names no window: pass at least one of handle, foreground, titleContains, titleEquals, titleRegex, " +
                   "processName, processId and className. An empty selector never matches every window. Nothing was done.";
        }

        if (BlankFilter(selector) is { } blank)
        {
            return $"window.{blank} is blank, so it would match every window; leave it out or give the text to match. Nothing was done.";
        }

        if (selector.Handle is <= 0)
            return $"window.handle must be a window handle (a positive integer from find_windows), but it is {selector.Handle}. Nothing was done.";

        if (selector.ProcessId is <= 0)
            return $"window.processId must be a positive process ID, but it is {selector.ProcessId}. Nothing was done.";

        if (selector.Index is < 0)
            return $"window.index counts matches from 0 (the front one), but it is {selector.Index}. Nothing was done.";

        if (selector.Handle.HasValue && (selector.Foreground || selector.Index.HasValue))
        {
            return "window.handle names one window, so foreground and index do not apply; pass the handle with the filters it must " +
                   "still match (processId, processName, a title or className), or leave the handle out. Nothing was done.";
        }

        if (selector.Foreground && selector.Index.HasValue)
            return "window.foreground names one window, so index does not apply. Nothing was done.";

        if (selector.TitleRegex is { Length: > 0 } pattern && WindowTitles.CompileTitleRegex(pattern, out var error) is null)
            return $"{error} Nothing was done.";

        return null;
    }

    /// <summary>
    /// The name of a filter that is given but empty once normalized (a blank title, a process name of only ".exe"): such a
    /// filter would count as naming a window while matching every one. Null when there is none.
    /// </summary>
    private static string? BlankFilter(WindowSelector selector)
    {
        // An empty string is a filter left out (HasFilters ignores it); text that normalizes to nothing is refused
        if (selector.TitleContains is { Length: > 0 } contains && WindowTitles.Normalize(contains).Length == 0)
            return "titleContains";
        if (selector.TitleEquals is { Length: > 0 } equals && WindowTitles.Normalize(equals).Length == 0)
            return "titleEquals";
        if (selector.ProcessName is { Length: > 0 } processName && WindowMatching.WithoutExe(processName).Length == 0)
            return "processName";
        if (selector.ClassName is { Length: > 0 } className && string.IsNullOrWhiteSpace(className))
            return "className";
        return null;
    }

    /// <summary>
    /// A selector's handle, checked against the selector's filters: InvalidHandle when it is no window, HandleMismatch
    /// when the window no longer matches what the caller expected of it (the handle was most likely reused).
    /// </summary>
    private WindowResolution ResolveHandle(nint handle, WindowSelector selector)
    {
        if (!IsValidWindow(handle) || ReadWindowInfo(handle) is not { } window)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.InvalidHandle),
                $"{InvalidHandleMessage}: window.handle {handle} is no window (it closed). Nothing was done. Find the window again " +
                "with find_windows, or pass a selector without the handle.");
        }

        if (WindowMatching.Mismatches(window, selector) is { Count: > 0 } differences)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.HandleMismatch),
                $"Window {handle} is not the window expected: {string.Join("; ", differences)}. The window most likely closed and its " +
                $"handle was reused by another window ({Describe(window)}). Nothing was done. Find the window again with find_windows.");
        }

        return WindowResolution.Found(window);
    }

    /// <summary>The window in the foreground, checked against the selector's filters (WindowNotFound when it does not match).</summary>
    private WindowResolution ResolveForeground(WindowSelector selector)
    {
        var foreground = _nativeWrapper.GetForegroundWindow();
        if (foreground == nint.Zero || ReadWindowInfo(foreground) is not { } window)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound),
                "window.foreground: no window is in the foreground right now (windows may be switching, or a UAC prompt or the lock " +
                "screen is shown). Nothing was done. Try again shortly, or name the window by its handle.");
        }

        if (WindowMatching.Mismatches(window, selector) is { Count: > 0 } differences)
        {
            return WindowResolution.Failed(nameof(WindowErrorCode.WindowNotFound),
                $"The window in the foreground ({Describe(window)}) does not match {selector.Describe()}: " +
                $"{string.Join("; ", differences)}. Nothing was done. Bring the window you mean to the front with focus_window, or name it " +
                "without foreground.");
        }

        return WindowResolution.Found(window);
    }

    private static string Describe(WindowInfo window) =>
        $"'{window.Title}', {window.ProcessName}, PID {window.ProcessId}, class {window.ClassName}";

    /// <summary>The WindowNotFound message of a selector that matched nothing, with what the listing leaves out.</summary>
    private static string NoMatchMessage(WindowSelector selector)
    {
        var hints = new List<string>();
        if (!string.IsNullOrEmpty(selector.ProcessName) && !selector.ProcessNameContains)
            hints.Add("processName is the whole process name (set processNameContains for a part of it)");
        if (!selector.IncludeCloaked)
            hints.Add("windows on other virtual desktops need includeCloaked");
        if (!selector.IncludeUntitled && string.IsNullOrEmpty(selector.ClassName))
            hints.Add("untitled windows need includeUntitled or className");
        hints.Add("hidden windows only by handle");

        return $"No window matches {selector.Describe()}. Nothing was done. Check what is open with find_windows; {string.Join(", ", hints)}.";
    }

    public WindowDetails? GetWindowDetails(nint handle)
    {
        if (GetWindowInfo(handle) is not { } window)
            return null;

        var style = (long)_nativeWrapper.GetWindowLongPtr(handle, NativeEnums.GWL_STYLE);
        var extendedStyle = (long)_nativeWrapper.GetWindowLongPtr(handle, NativeEnums.GWL_EXSTYLE);
        var dpi = _nativeWrapper.GetDpiForWindow(handle);
        int? zOrder = null;
        if (_nativeWrapper.GetAncestor(handle, WindowAncestor.Root) == handle)
        {
            var index = EnumerateTopLevelWindows(_nativeWrapper).IndexOf(handle);
            zOrder = index >= 0 ? index : null;
        }

        return new WindowDetails(
            window,
            IsForeground: _nativeWrapper.GetForegroundWindow() == handle,
            ZOrder: zOrder,
            IsTopmost: (extendedStyle & NativeEnums.WS_EX_TOPMOST) != 0,
            IsEnabled: (style & NativeEnums.WS_DISABLED) == 0,
            IsResponding: !_nativeWrapper.IsHungAppWindow(handle),
            Dpi: dpi == 0 ? null : (int)dpi);
    }

    public WindowInfo? GetForegroundWindow()
    {
        var hWnd = _nativeWrapper.GetForegroundWindow();
        if (hWnd == nint.Zero)
            return null;

        return ReadWindowInfo(hWnd);
    }

    public WindowInfo? GetWindowInfo(nint handle)
    {
        if (!IsValidWindow(handle))
            return null;

        return ReadWindowInfo(handle);
    }

    public bool IsUserFacingAppWindow(nint handle) =>
        IsValidWindow(handle) &&
        // Listings only enumerate top-level windows; a child control is never an app window
        _nativeWrapper.GetAncestor(handle, WindowAncestor.Root) == handle &&
        _nativeWrapper.IsWindowVisible(handle) &&
        // Listings leave out the server's own windows (see Match)
        _nativeWrapper.GetWindowThreadProcessId(handle) != _nativeWrapper.GetCurrentProcessId() &&
        PassesUserFacingRules(
            handle,
            _nativeWrapper.GetClassName(handle) ?? string.Empty,
            _nativeWrapper.GetWindowText(handle) ?? string.Empty,
            includeToolWindows: false,
            includeCloaked: false,
            includeUntitled: false,
            out _);

    public WindowChangeResult MoveWindow(nint handle, int x, int y, bool bringToFront = true) =>
        ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(X: x, Y: y), BoundsFrame.Visible, PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);

    /// <summary>
    /// The SetWindowPos flags for a placement. With <paramref name="bringToFront"/> the window goes to
    /// the top of the z-order (HWND_TOP), but it is never activated: SWP_NOACTIVATE stays set.
    /// BringWindowToTop is not used, because it activates the window, so keyboard input would follow it
    /// whenever the server happened to have the foreground rights. focus_window activates, verified.
    /// </summary>
    private static uint PlacementFlags(bool bringToFront) =>
        bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

    public WindowChangeResult ResizeWindow(nint handle, int width, int height) =>
        ResizeWindow(handle, width, height, bringToFront: false);

    public WindowChangeResult ResizeWindow(nint handle, int width, int height, bool bringToFront) =>
        ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(Width: width, Height: height), BoundsFrame.Visible,
            PlacementFlags(bringToFront), WindowErrorCode.ResizeFailed);

    public WindowChangeResult SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true) =>
        ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(x, y, width, height), BoundsFrame.Outer, PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);

    public WindowChangeResult SetWindowFrame(nint handle, int x, int y, int width, int height, bool bringToFront = true) =>
        ChangeRefusal(handle) ??
        Place(handle, new PlacementTarget(x, y, width, height), BoundsFrame.Visible, PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);

    /// <summary>The longest settleMs the geometry tools wait for a window to stop moving: they wait inside the input gate.</summary>
    public const int MaxSettleMs = 5000;

    /// <summary>
    /// How long a placement that took the window to a monitor with another scale waits for the application to apply its
    /// own DPI change (WM_DPICHANGED) before it measures the window again.
    /// </summary>
    public static readonly TimeSpan DpiSettleTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>How often the frame is read while a placement waits for the window to stop moving.</summary>
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>The wait for <paramref name="settleMs"/>: 0 to <see cref="MaxSettleMs"/>.</summary>
    public static TimeSpan SettleDelay(int settleMs) => TimeSpan.FromMilliseconds(Math.Clamp(settleMs, 0, MaxSettleMs));

    public void WaitForSettledFrame(nint handle, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return;

        var waited = System.Diagnostics.Stopwatch.StartNew();
        var last = ReadFrame(handle);
        while (last != null)
        {
            // Thread.Sleep throws for a negative time, so the remaining time is read once
            var remaining = timeout - waited.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return;

            Thread.Sleep(remaining < SettlePollInterval ? remaining : SettlePollInterval);

            var now = ReadFrame(handle);
            if (now == last)
                return;

            last = now;
        }
    }

    /// <summary>
    /// What a placement sets; null keeps the window's own value. Without X and Y the window is not moved
    /// (SWP_NOMOVE), without Width and Height it is not resized (SWP_NOSIZE).
    /// </summary>
    private readonly record struct PlacementTarget(int? X = null, int? Y = null, int? Width = null, int? Height = null);

    /// <summary>A window's rect, its visible frame and the invisible borders between them, read at one moment.</summary>
    private readonly record struct WindowFrame(WindowRect Outer, WindowRect Visible, FrameInsets Insets);

    /// <summary>
    /// The window's rect and visible frame (DWMWA_EXTENDED_FRAME_BOUNDS; the rect itself when DWM cannot report it or
    /// reports an implausible frame). Null when the window is gone. Both are reads that never wait for the window's thread.
    /// </summary>
    private WindowFrame? ReadFrame(nint handle)
    {
        if (_nativeWrapper.GetWindowRect(handle) is not { } rect)
            return null;

        var insets = FrameInsets.Between(rect, _nativeWrapper.GetExtendedFrameBounds(handle));
        var outer = WindowRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var visible = new WindowRect(outer.X + insets.Left, outer.Y + insets.Top,
            outer.Width - insets.Left - insets.Right, outer.Height - insets.Top - insets.Bottom);
        return new WindowFrame(outer, visible, insets);
    }

    /// <summary>
    /// Places a window that was checked already. A minimized or maximized window is restored first, without activation
    /// (<see cref="RestoreForPlacement"/>), and measured only afterwards. The target is in <paramref name="frame"/>'s
    /// coordinates; for the visible frame the invisible borders are added. When the placement took the window to a
    /// monitor with another scale, the application has rescaled it and its borders changed (WM_DPICHANGED), so once the
    /// window has settled the same target is placed again, measured anew, as PowerToys FancyZones does; a move keeps the
    /// size the application chose (SWP_NOSIZE).
    /// </summary>
    private WindowChangeResult Place(nint handle, PlacementTarget target, BoundsFrame frame, uint flags, WindowErrorCode failure)
    {
        if (RestoreForPlacement(handle, failure) is { } notRestored)
            return notRestored;

        var scaleBefore = MonitorScale(handle);
        var placed = PlaceOnce(handle, target, frame, flags, failure);
        if (!placed.Success || MonitorScale(handle) == scaleBefore)
            return placed;

        WaitForSettledFrame(handle, DpiSettleTimeout);
        return PlaceOnce(handle, target, frame, flags, failure);
    }

    private WindowChangeResult PlaceOnce(nint handle, PlacementTarget target, BoundsFrame frame, uint flags, WindowErrorCode failure)
    {
        if (ReadFrame(handle) is not { } current)
            return WindowGone(handle);

        var (now, insets) = frame == BoundsFrame.Visible ? (current.Visible, current.Insets) : (current.Outer, FrameInsets.None);
        var rect = insets.Outer(new WindowRect(target.X ?? now.X, target.Y ?? now.Y, target.Width ?? now.Width, target.Height ?? now.Height));
        var keep = (target.X is null ? NativeEnums.SWP_NOMOVE : 0) | (target.Width is null ? NativeEnums.SWP_NOSIZE : 0);
        return SetPosition(handle, rect, flags | keep, failure);
    }

    /// <summary>The scale factor of the monitor the window is on (1.0 when it cannot be told).</summary>
    private double MonitorScale(nint handle)
    {
        var monitors = _monitorService.GetAllMonitors();
        var index = _monitorService.GetMonitorIndex(handle);
        return index >= 0 && index < monitors.Count ? monitors[index].ScaleFactor : 1.0;
    }

    /// <summary>
    /// Restores a minimized or maximized window to normal before it is placed or measured (roadmap M2.3): a maximized
    /// window that is placed keeps WS_MAXIMIZE (it is reported maximized at the new size and jumps back on the next
    /// restore), and a minimized one is measured as its 160x28 icon. SW_SHOWNOACTIVATE restores without activating the
    /// window, unlike SW_RESTORE, so keyboard input stays where it was. A window minimized from maximized comes back
    /// maximized first, so the command is sent at most twice. Null when the window is normal; MoveFailed or ResizeFailed
    /// (<paramref name="failure"/>) when it stays minimized or maximized. A hidden window that is minimized or maximized (a
    /// tray app hides its window after minimizing it) is not restored: SW_SHOWNOACTIVATE would show it behind its app's
    /// back, as the capture restore refuses to (<see cref="MinimizedWindowRestore"/>); it fails with
    /// <paramref name="failure"/> and nothing is sent.
    /// </summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private WindowChangeResult? RestoreForPlacement(nint handle, WindowErrorCode failure)
    {
        if (IsMinimizedOrMaximized(handle) && !_nativeWrapper.IsWindowVisible(handle))
        {
            var state = _nativeWrapper.IsIconic(handle) ? "minimized" : "maximized";
            return WindowChangeResult.Failed(handle, failure,
                $"The window is hidden and {state}: its application hid it (for example in the notification area), and restoring " +
                $"it to place it would show it, so it was not shown and not {(failure == WindowErrorCode.ResizeFailed ? "resized" : "moved")}. " +
                "Show it through its application, or ask the user.");
        }

        for (var i = 0; i < 2 && IsMinimizedOrMaximized(handle); i++)
        {
            var before = (_nativeWrapper.IsIconic(handle), _nativeWrapper.IsZoomed(handle));
            ShowWindow(handle, NativeEnums.SW_SHOWNOACTIVATE);

            // An application that animates the restore takes a moment to leave the state
            WaitUntil(() => (_nativeWrapper.IsIconic(handle), _nativeWrapper.IsZoomed(handle)) != before || !_nativeWrapper.IsWindow(handle),
                _stateChangeTimeout);
        }

        if (!IsMinimizedOrMaximized(handle))
            return null;

        if (!_nativeWrapper.IsWindow(handle))
            return WindowGone(handle);

        var change = failure == WindowErrorCode.ResizeFailed ? "resized" : "moved";
        return WindowChangeResult.Failed(handle, failure,
            $"The window is {ObservedState(handle)} and did not return to normal when it was restored (the application may have " +
            $"refused it), so it was not {change}: a {ObservedState(handle)} window cannot be placed. Check the window " +
            "(find_windows with its handle), or ask the user to restore it.");
    }

    private bool IsMinimizedOrMaximized(nint handle) => _nativeWrapper.IsIconic(handle) || _nativeWrapper.IsZoomed(handle);

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> has passed.</summary>
    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && waited.Elapsed < timeout)
        {
            Thread.Sleep(StatePollInterval);
        }
    }

    public WindowChangeResult SetWindowState(nint handle, WindowStateRequest state) =>
        ChangeRefusal(handle) ?? ApplyState(handle, state);

    /// <summary>
    /// Changes the show state of a window that was checked already, and reports success only when the window is seen in
    /// that state within <see cref="StateChangeTimeout"/>: ShowWindow's return value says whether the window was visible
    /// before the call, not whether the command worked.
    /// </summary>
    private WindowChangeResult ApplyState(nint handle, WindowStateRequest state)
    {
        var command = state switch
        {
            WindowStateRequest.Minimize => NativeEnums.SW_MINIMIZE,
            WindowStateRequest.Maximize => NativeEnums.SW_MAXIMIZE,
            WindowStateRequest.Restore => NativeEnums.SW_RESTORE,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a window state.")
        };

        var wasMinimized = _nativeWrapper.IsIconic(handle);
        ShowWindow(handle, command);
        return WaitForState(handle, state, wasMinimized);
    }

    /// <summary>
    /// Waits up to <see cref="StateChangeTimeout"/> for the window to be in <paramref name="state"/> (applications that
    /// animate take a moment), polling the state, which reads it without waiting for the window's thread.
    /// </summary>
    private WindowChangeResult WaitForState(nint handle, WindowStateRequest state, bool wasMinimized)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!IsInState(handle, state, wasMinimized))
        {
            if (!_nativeWrapper.IsWindow(handle))
                return WindowGone(handle);

            if (waited.Elapsed >= _stateChangeTimeout)
            {
                return WindowChangeResult.Failed(handle, WindowErrorCode.StateChangeFailed,
                    $"The window did not become {Describe(state)} within {_stateChangeTimeout.TotalMilliseconds:0} ms: it is " +
                    $"{ObservedState(handle)}. The application may have refused or delayed the change, or it may run with higher " +
                    "privileges (for example as administrator), which Windows does not let this server change. Check the window " +
                    "(find_windows with its handle) before acting on it.");
            }

            Thread.Sleep(StatePollInterval);
        }

        return WindowChangeResult.Done(handle);
    }

    /// <summary>
    /// Whether the window is in <paramref name="state"/>. Restore is the Windows Restore command: a minimized window is no
    /// longer minimized (it returns to normal, or to maximized when it was maximized before it was minimized), and a
    /// window that was not minimized is no longer maximized.
    /// </summary>
    private bool IsInState(nint handle, WindowStateRequest state, bool wasMinimized) => state switch
    {
        WindowStateRequest.Minimize => _nativeWrapper.IsIconic(handle),
        WindowStateRequest.Maximize =>
            !_nativeWrapper.IsIconic(handle) && _nativeWrapper.IsZoomed(handle) && _nativeWrapper.IsWindowVisible(handle),
        _ => !_nativeWrapper.IsIconic(handle) && _nativeWrapper.IsWindowVisible(handle) && (wasMinimized || !_nativeWrapper.IsZoomed(handle))
    };

    private static string Describe(WindowStateRequest state) => state switch
    {
        WindowStateRequest.Minimize => "minimized",
        WindowStateRequest.Maximize => "maximized",
        _ => "restored"
    };

    private string ObservedState(nint handle) =>
        _nativeWrapper.IsIconic(handle) ? "minimized"
        : !_nativeWrapper.IsWindowVisible(handle) ? "hidden"
        : _nativeWrapper.IsZoomed(handle) ? "maximized"
        : "normal";

    public WindowChangeResult SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true)
    {
        if (ChangeRefusal(handle) is { } refused)
            return refused;

        var targetMonitor = monitorIndex ?? _monitorService.GetMonitorIndex(handle);
        if (MonitorIndexRefusal(handle, targetMonitor) is { } invalid)
            return invalid;

        MonitorRect bounds;
        try
        {
            bounds = _monitorService.CalculateSnapBounds(targetMonitor, position);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The displays changed between the check and the calculation
            return MonitorIndexRefusal(handle, targetMonitor) ?? throw new InvalidOperationException("The monitor list changed.");
        }

        return Place(handle, new PlacementTarget(bounds.X, bounds.Y, bounds.Width, bounds.Height), BoundsFrame.Visible,
            PlacementFlags(bringToFront), WindowErrorCode.MoveFailed);
    }

    public WindowChangeResult MoveWindowToMonitor(
        nint handle, int monitorIndex, MonitorPositioning positioning = MonitorPositioning.Center, bool bringToFront = true)
    {
        if (ChangeRefusal(handle) is { } refused)
            return refused;

        var monitors = _monitorService.GetAllMonitors();
        if (MonitorIndexRefusal(handle, monitorIndex, monitors.Count) is { } invalid)
            return invalid;

        var maximize = positioning == MonitorPositioning.Maximize;
        if (maximize && _monitorService.GetMonitorIndex(handle) == monitorIndex)
            return OnMonitor(handle, ApplyState(handle, WindowStateRequest.Maximize), monitorIndex, maximize);

        var moved = MoveOntoMonitor(handle, monitors[monitorIndex], positioning,
            // SW_MAXIMIZE raises (and activates) the window anyway, so the move before it keeps the z-order
            maximize ? NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE : PlacementFlags(bringToFront));
        if (!moved.Success)
            return moved;

        // An app that keeps or restores its own position is still on the old monitor: SW_MAXIMIZE would maximize it there,
        // and activate it, so it is sent only once the window is on the target monitor
        if (maximize && _monitorService.GetMonitorIndex(handle) != monitorIndex)
            return OnMonitor(handle, moved, monitorIndex, maximized: false);

        return OnMonitor(handle, maximize ? ApplyState(handle, WindowStateRequest.Maximize) : moved, monitorIndex, maximize);
    }

    /// <summary>
    /// Places the window in <paramref name="target"/>'s work area at its normal size, clamped to the work area so that the
    /// whole visible frame, title bar included, is on that monitor. The window is restored first and measured only then
    /// (a minimized window measures as its 160x28 icon). Across a scale change the window is moved in two steps: onto the
    /// target monitor without changing its size (SWP_NOSIZE), where a per-monitor-aware application rescales itself for
    /// the new DPI, then, once it has settled, sized from the size the application chose and positioned. The size is never
    /// scaled by the ratio of the scales in advance: the application would scale it a second time, and DWM already scales
    /// a window that is not per-monitor aware.
    /// </summary>
    private WindowChangeResult MoveOntoMonitor(nint handle, MonitorInfo target, MonitorPositioning positioning, uint flags)
    {
        if (RestoreForPlacement(handle, WindowErrorCode.MoveFailed) is { } notRestored)
            return notRestored;

        var workArea = target.WorkArea;
        if (MonitorScale(handle) != target.ScaleFactor)
        {
            if (ReadFrame(handle) is not { } before)
                return WindowGone(handle);

            // Centred, so that the target monitor holds most of the window even before it is clamped
            var (x, y) = Position(MonitorPositioning.Center, workArea, before.Visible.Width, before.Visible.Height);
            var onto = before.Insets.Outer(before.Visible with { X = x, Y = y });
            var arrived = SetPosition(handle, onto, NativeEnums.SWP_NOSIZE | NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE,
                WindowErrorCode.MoveFailed);
            if (!arrived.Success)
                return arrived;

            WaitForSettledFrame(handle, DpiSettleTimeout);
        }

        if (ReadFrame(handle) is not { } frame)
            return WindowGone(handle);

        var width = Math.Clamp(frame.Visible.Width, 1, Math.Max(1, workArea.Width));
        var height = Math.Clamp(frame.Visible.Height, 1, Math.Max(1, workArea.Height));
        var (left, top) = Position(positioning, workArea, width, height);
        return SetPosition(handle, frame.Insets.Outer(new WindowRect(left, top, width, height)), flags, WindowErrorCode.MoveFailed);
    }

    /// <summary>The top-left corner of a window of this size at <paramref name="positioning"/> in the work area.</summary>
    private static (int X, int Y) Position(MonitorPositioning positioning, MonitorRect workArea, int width, int height) => positioning switch
    {
        MonitorPositioning.Center or MonitorPositioning.Maximize =>
            (workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2),
        MonitorPositioning.TopLeft or MonitorPositioning.Restore => (workArea.X, workArea.Y),
        MonitorPositioning.TopRight => (workArea.X + workArea.Width - width, workArea.Y),
        MonitorPositioning.BottomLeft => (workArea.X, workArea.Y + workArea.Height - height),
        MonitorPositioning.BottomRight => (workArea.X + workArea.Width - width, workArea.Y + workArea.Height - height),
        _ => throw new ArgumentOutOfRangeException(nameof(positioning), positioning, "Not a positioning.")
    };

    /// <summary>
    /// <paramref name="result"/> when it failed; otherwise Done only when the window is on monitor
    /// <paramref name="monitorIndex"/> (MonitorFromWindow) afterwards, and MoveFailed when it is not: an application can
    /// keep or restore its own position, and SW_MAXIMIZE maximizes a window on the monitor it is on.
    /// </summary>
    private WindowChangeResult OnMonitor(nint handle, WindowChangeResult result, int monitorIndex, bool maximized)
    {
        if (!result.Success)
            return result;

        var actual = _monitorService.GetMonitorIndex(handle);
        if (actual == monitorIndex)
            return WindowChangeResult.Done(handle);

        return WindowChangeResult.Failed(handle, WindowErrorCode.MoveFailed, maximized
            ? $"The window was maximized on monitor {actual}, not on monitor {monitorIndex}: it did not move there. Check it with find_windows."
            : $"The window is on monitor {actual}, not on monitor {monitorIndex}: it did not move there (the application may keep its " +
              "own position). Check it with find_windows.");
    }

    public bool IsValidWindow(nint handle)
    {
        return handle != nint.Zero && _nativeWrapper.IsWindow(handle);
    }

    /// <summary>The flags of every z-order change: it never moves, resizes or activates the window.</summary>
    private const uint ZOrderFlags = NativeEnums.SWP_NOMOVE | NativeEnums.SWP_NOSIZE | NativeEnums.SWP_NOACTIVATE;

    public WindowChangeResult SetWindowZOrder(nint handle, ZOrderMode mode, nint? above = null)
    {
        if (ChangeRefusal(handle) is { } refused)
            return refused;

        var window = TopLevel(handle);
        var wasTopmost = IsTopmost(window);
        WindowChangeResult result;
        if (mode == ZOrderMode.Above)
        {
            if (above is not { } other || !IsValidWindow(other))
            {
                return WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle,
                    $"aboveHandle {above ?? 0} is no window (it closed). Nothing was changed. Find it again with find_windows.");
            }

            result = PlaceAbove(handle, window, TopLevel(other));
        }
        else
        {
            var insertAfter = mode switch
            {
                ZOrderMode.Top => NativeEnums.HWND_TOP,
                ZOrderMode.Bottom => NativeEnums.HWND_BOTTOM,
                ZOrderMode.Topmost => NativeEnums.HWND_TOPMOST,
                ZOrderMode.NoTopmost => NativeEnums.HWND_NOTOPMOST,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a z-order mode.")
            };
            result = SetZOrder(handle, window, insertAfter);
            if (result.Success && TopmostRefusal(handle, window, mode) is { } notApplied)
                result = notApplied;
        }

        if (!result.Success)
            return result;

        if (!IsTopmost(window))
            _topmostPins.Unpin(window);
        else if (!wasTopmost)
            _topmostPins.Pin(window, _nativeWrapper.GetWindowThreadId(window));
        return result;
    }

    /// <summary>The top-level window of <paramref name="handle"/>, which has the place in the z-order; itself when it has none.</summary>
    private nint TopLevel(nint handle) =>
        _nativeWrapper.GetAncestor(handle, WindowAncestor.Root) is var root && root != nint.Zero ? root : handle;

    private bool IsTopmost(nint window) =>
        ((long)_nativeWrapper.GetWindowLongPtr(window, NativeEnums.GWL_EXSTYLE) & NativeEnums.WS_EX_TOPMOST) != 0;

    /// <summary>ZOrderNotApplied when the window's topmost state is not what <paramref name="mode"/> asked for; otherwise null.</summary>
    private WindowChangeResult? TopmostRefusal(nint handle, nint window, ZOrderMode mode)
    {
        var expected = mode switch
        {
            ZOrderMode.Topmost => true,
            ZOrderMode.NoTopmost or ZOrderMode.Bottom => false,
            _ => (bool?)null
        };
        if (expected is not { } topmost || IsTopmost(window) == topmost)
            return null;

        return WindowChangeResult.Failed(handle, WindowErrorCode.ZOrderNotApplied, topmost
            ? "Windows took the change, but the window is not topmost when read back: its app sets its own z-order (some apps and " +
              "games do). Check it with get_window_info."
            : "Windows took the change, but the window is still topmost when read back: its app keeps itself topmost. Check it " +
              "with get_window_info.");
    }

    /// <summary>
    /// Places <paramref name="window"/> directly above <paramref name="other"/>: behind the window in front of it, in its band.
    /// A window that is topmost is above every window that is not, so nothing is sent for it; one that is not becomes topmost
    /// first when the other is (it can only be above a topmost window as one). A normal window that would go behind a topmost
    /// one goes to the top of the normal windows instead (HWND_TOP), which is the same place, so it never becomes topmost by
    /// the way it is inserted. Read back: ZOrderNotApplied unless it is in front of the other afterwards.
    /// </summary>
    private WindowChangeResult PlaceAbove(nint handle, nint window, nint other)
    {
        if (other == window)
        {
            return WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle,
                "aboveHandle is the window itself. Nothing was changed. Pass another window to place it above.");
        }

        var windowTopmost = IsTopmost(window);
        var otherTopmost = IsTopmost(other);
        var zOrder = EnumerateTopLevelWindows(_nativeWrapper);
        var otherIndex = zOrder.IndexOf(other);
        var inFront = zOrder.Take(Math.Max(otherIndex, 0)).Where(w => w != window).LastOrDefault();
        var already = windowTopmost && !otherTopmost || otherIndex > 0 && zOrder[otherIndex - 1] == window;
        if (!already && otherTopmost && !windowTopmost)
        {
            // HWND_TOPMOST takes it to the front of the topmost windows; then behind the one in front of the other, if any
            if (SetZOrder(handle, window, NativeEnums.HWND_TOPMOST) is { Success: false } notTopmost)
                return notTopmost;
            if (inFront != nint.Zero && SetZOrder(handle, window, inFront) is { Success: false } notBehind)
                return notBehind;
        }
        else if (!already)
        {
            var insertAfter = inFront == nint.Zero || IsTopmost(inFront) && !otherTopmost
                ? otherTopmost ? NativeEnums.HWND_TOPMOST : NativeEnums.HWND_TOP
                : inFront;
            if (SetZOrder(handle, window, insertAfter) is { Success: false } notPlaced)
                return notPlaced;
        }

        var after = EnumerateTopLevelWindows(_nativeWrapper);
        var windowAfter = after.IndexOf(window);
        var otherAfter = after.IndexOf(other);
        return windowAfter >= 0 && (otherAfter < 0 || windowAfter < otherAfter)
            ? WindowChangeResult.Done(handle)
            : WindowChangeResult.Failed(handle, WindowErrorCode.ZOrderNotApplied,
                "Windows took the change, but the window is not above the other window when read back: an app changed the z-order " +
                "again. Check it with get_window_info.");
    }

    /// <summary>SetWindowZOrder, with a deadline (<see cref="IWindowCallRunner"/>); a refusal as a placement's (AccessDenied).</summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private WindowChangeResult SetZOrder(nint handle, nint window, nint insertAfter)
    {
        var placed = _windowCalls.Run(handle, () => _nativeWrapper.SetWindowZOrder(window, insertAfter, ZOrderFlags));
        return placed.Succeeded ? WindowChangeResult.Done(handle) : NotPlaced(handle, placed.Win32Error, WindowErrorCode.ZOrderNotApplied);
    }

    /// <summary>
    /// Why a window change is refused before anything is sent: the handle is not a window (InvalidHandle), or the
    /// target policy denies the window (TargetDeniedByPolicy). Null when the change can go ahead.
    /// </summary>
    private WindowChangeResult? ChangeRefusal(nint handle)
    {
        if (!IsValidWindow(handle))
            return WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle, InvalidHandleMessage);

        return EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? WindowChangeResult.Failed(handle, WindowErrorCode.TargetDeniedByPolicy, WindowChangeRefusal(handle, verdict))
            : null;
    }

    /// <summary>The error of a window handle that is not a window.</summary>
    public const string InvalidHandleMessage = "Invalid window handle";

    private static WindowChangeResult WindowGone(nint handle) =>
        WindowChangeResult.Failed(handle, WindowErrorCode.InvalidHandle,
            "The window no longer exists: it closed during the call. Call find_windows for a current handle.");

    private WindowChangeResult? MonitorIndexRefusal(nint handle, int monitorIndex) =>
        MonitorIndexRefusal(handle, monitorIndex, _monitorService.GetAllMonitors().Count);

    /// <summary>InvalidMonitorIndex with the valid range when <paramref name="monitorIndex"/> is not a monitor, otherwise null.</summary>
    private static WindowChangeResult? MonitorIndexRefusal(nint handle, int monitorIndex, int monitorCount) =>
        monitorIndex >= 0 && monitorIndex < monitorCount
            ? null
            : WindowChangeResult.Failed(handle, WindowErrorCode.InvalidMonitorIndex, MonitorIndexMessage(monitorIndex, monitorCount));

    /// <summary>The error of a monitor index that is out of range, with the valid range. Nothing was changed.</summary>
    public static string MonitorIndexMessage(int monitorIndex, int monitorCount) =>
        monitorCount == 0
            ? $"Monitor index {monitorIndex} does not exist: no monitor was found. Nothing was changed."
            : $"Monitor index {monitorIndex} does not exist: valid indexes are 0 to {monitorCount - 1} " +
              $"({monitorCount} monitor{(monitorCount == 1 ? string.Empty : "s")}). Nothing was changed. Call get_monitors to see them.";

    /// <summary>
    /// SetWindowPos, with a deadline (<see cref="IWindowCallRunner"/>). A refused call reports its Win32 error:
    /// ERROR_ACCESS_DENIED (5) is <see cref="WindowErrorCode.AccessDenied"/> (User Interface Privilege Isolation blocks
    /// changes to windows of more privileged processes), any other error <paramref name="failure"/>.
    /// </summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private WindowChangeResult SetPosition(nint handle, WindowRect rect, uint flags, WindowErrorCode failure)
    {
        var placed = _windowCalls.Run(handle, () => _nativeWrapper.SetWindowPos(handle, rect.X, rect.Y, rect.Width, rect.Height, flags));
        return placed.Succeeded ? WindowChangeResult.Done(handle) : NotPlaced(handle, placed.Win32Error, failure);
    }

    private static WindowChangeResult NotPlaced(nint handle, int win32Error, WindowErrorCode failure) => win32Error switch
    {
        NativeEnums.ERROR_ACCESS_DENIED => WindowChangeResult.Failed(handle, WindowErrorCode.AccessDenied,
            $"Windows blocked the change (Win32 error {win32Error}, access denied). The window most likely belongs to an app " +
            "running with higher privileges, for example as administrator: User Interface Privilege Isolation blocks this " +
            "server from moving or resizing it. Nothing was changed; ask the user to do this step."),
        NativeEnums.ERROR_INVALID_WINDOW_HANDLE => WindowGone(handle),
        // SetWindowPos can fail without setting the last error, and error 0's text is "The operation completed successfully"
        0 => WindowChangeResult.Failed(handle, failure,
            $"Windows refused to {(failure == WindowErrorCode.ResizeFailed ? "resize" : "move")} the window without giving a reason " +
            "(the application may have rejected the change). Nothing was changed."),
        _ => WindowChangeResult.Failed(handle, failure,
            $"Windows did not {(failure == WindowErrorCode.ResizeFailed ? "resize" : "move")} the window (Win32 error {win32Error}: " +
            $"{new System.ComponentModel.Win32Exception(win32Error).Message}).")
    };

    /// <summary>ShowWindow, with a deadline (<see cref="IWindowCallRunner"/>). Its return value is the previous visibility, so it is not returned.</summary>
    /// <exception cref="WindowNotRespondingException">The window did not answer in time.</exception>
    private void ShowWindow(nint handle, int command) =>
        _windowCalls.Run(handle, () => _nativeWrapper.ShowWindow(handle, command));

    public WindowCloseResult CloseWindow(nint handle, bool force)
    {
        if (CheckClose(handle, force) is { } refused)
            return refused;

        // WM_CLOSE asks for a graceful close: the application can prompt to save, or cancel it
        var posted = _nativeWrapper.PostMessage(handle, NativeEnums.WM_CLOSE, nint.Zero, nint.Zero);
        return posted.Succeeded ? WindowCloseResult.Sent(handle) : CloseNotPosted(handle, posted.Win32Error);
    }

    public WindowCloseResult? CheckClose(nint handle, bool force)
    {
        if (!IsValidWindow(handle))
            return WindowCloseResult.Failed(handle, WindowErrorCode.InvalidHandle, InvalidHandleMessage);

        // The user's policy comes first: force is a tool argument and does not override it
        if (FindPolicyRefusal(handle) is { } refusal)
            return WindowCloseResult.Failed(handle, WindowErrorCode.TargetDeniedByPolicy, refusal);

        if (!force && FindCloseProtection(handle) is { } protection)
            return WindowCloseResult.Failed(handle, WindowErrorCode.ProtectedWindow, protection);

        return null;
    }

    /// <summary>
    /// The classes of the helper windows that close_application never closes, whatever their style: the text-input
    /// windows every GUI thread gets (Default IME, MSCTFIME UI) and the GDI+ hook window. They are hidden anyway; this
    /// keeps them out even when an app shows one.
    /// </summary>
    private static readonly HashSet<string> HelperWindowClasses =
        new(StringComparer.OrdinalIgnoreCase) { "IME", "MSCTFIME UI", "GDI+ Hook Window Class" };

    /// <summary>
    /// The classes of File Explorer's windows (CabinetWClass, and ExploreWClass of older versions): the only application
    /// windows of the shell's process, which draws the desktop, the taskbars and their flyouts too.
    /// </summary>
    private static readonly HashSet<string> FileExplorerWindowClasses =
        new(StringComparer.OrdinalIgnoreCase) { "CabinetWClass", "ExploreWClass" };

    /// <summary>The longest prompt text the close results carry; longer text is cut with an ellipsis.</summary>
    public const int MaxPromptTextLength = 1000;

    /// <summary>The most button captions a prompt reports.</summary>
    public const int MaxPromptButtons = 10;

    public IReadOnlyList<OwnedWindowInfo> GetOwnedWindows(nint handle, bool includeHidden = false)
    {
        if (!IsValidWindow(handle))
            return [];

        // The same dialog FindBlockingDialog reports as the prompt, so isBlocking agrees with the close tools
        var blocking = FindBlockingWindow(handle)?.Window;
        var session = new ReadSession(_nativeWrapper, topLevelWindows: null);
        var owned = new List<OwnedWindowInfo>();
        foreach (var window in OwnedTopLevelWindows(handle))
        {
            if (!includeHidden && !_nativeWrapper.IsWindowVisible(window))
                continue;

            var className = _nativeWrapper.GetClassName(window) ?? string.Empty;
            var title = _nativeWrapper.GetWindowText(window) ?? string.Empty;
            var process = ResolveProcess(window, className, title, _nativeWrapper.GetWindowThreadProcessId(window), session);
            if (BuildWindowInfo(window, title, className, _nativeWrapper.IsCloaked(window), process, session) is { } info)
                owned.Add(new OwnedWindowInfo(info, WindowClassification.IsDialogClass(className), window == blocking));
        }

        return owned;
    }

    public ClosePrompt? FindBlockingDialog(nint handle)
    {
        if (!IsValidWindow(handle))
            return null;

        return FindBlockingWindow(handle) is { } blocking ? ReadDialog(blocking.Window, blocking.Kind) : null;
    }

    /// <summary>The window of the prompt that blocks <paramref name="handle"/>, and how it was found, or null.</summary>
    private (nint Window, ClosePromptKind Kind)? FindBlockingWindow(nint handle)
    {
        // A modal dialog disables its owner and is the owner's enabled popup
        if (EnabledPopupOfDisabled(handle) is { } popup && IsShownPopup(popup))
            return (popup, ClosePromptKind.ModalPopup);

        // A dialog of an owned window (the app's hidden owner or a tool window) disables that window instead
        var windowDisabled = IsDisabled(handle);
        foreach (var window in OwnedTopLevelWindows(handle))
        {
            if (WindowClassification.IsDialogClass(_nativeWrapper.GetClassName(window)) && IsShownPopup(window) &&
                (windowDisabled || IsDisabled(_nativeWrapper.GetWindow(window, NativeEnums.GW_OWNER))))
            {
                return (window, ClosePromptKind.OwnedDialog);
            }
        }

        return null;
    }

    public IReadOnlyList<WindowInfo> GetApplicationWindows() => ApplicationWindows(otherDesktops: false);

    public IReadOnlyList<WindowInfo> GetApplicationWindowsOnOtherDesktops()
    {
        var cloaked = ApplicationWindows(otherDesktops: true);
        if (_virtualDesktops == null || cloaked.Count == 0)
            return cloaked;

        // The shell also cloaks suspended and prelaunched frames: the virtual desktop manager puts those on no desktop. A
        // window it cannot place is kept, so a window with unsaved work on another desktop is never left out of the report
        var placements = _virtualDesktops.GetPlacements(cloaked.Select(w => w.Handle).ToList());
        return cloaked
            .Where((_, i) => i >= placements.Count ||
                             placements[i] is VirtualDesktopPlacement.OtherDesktop or VirtualDesktopPlacement.Unknown)
            .ToList();
    }

    /// <summary>
    /// The Alt-Tab set: the windows that are not cloaked, or with <paramref name="otherDesktops"/> those the shell cloaks
    /// (on another virtual desktop, or a suspended or prelaunched frame) and that would be in it otherwise.
    /// </summary>
    private List<WindowInfo> ApplicationWindows(bool otherDesktops)
    {
        var topLevelWindows = EnumerateTopLevelWindows(_nativeWrapper);
        var session = new ReadSession(_nativeWrapper, topLevelWindows);
        var windows = new List<WindowInfo>();
        uint? shellProcessId = null;
        foreach (var window in topLevelWindows)
        {
            if (!_nativeWrapper.IsWindowVisible(window))
                continue;

            var className = _nativeWrapper.GetClassName(window) ?? string.Empty;
            if (WindowClassification.IsShellWindowClass(className) || HelperWindowClasses.Contains(className) ||
                WindowClassification.IsCoreWindow(className))
                continue;

            // Alt-Tab: no tool window, and an owned or no-activate window only with WS_EX_APPWINDOW
            var extendedStyle = (long)_nativeWrapper.GetWindowLongPtr(window, NativeEnums.GWL_EXSTYLE);
            var appWindow = (extendedStyle & NativeEnums.WS_EX_APPWINDOW) != 0;
            if (WindowClassification.IsToolWindow((nint)extendedStyle) ||
                (!appWindow && (extendedStyle & NativeEnums.WS_EX_NOACTIVATE) != 0) ||
                (!appWindow && _nativeWrapper.GetWindow(window, NativeEnums.GW_OWNER) != nint.Zero))
                continue;

            if (otherDesktops)
            {
                // Cloaked by the shell: on another virtual desktop, or a suspended or prelaunched frame, which
                // GetApplicationWindowsOnOtherDesktops tells apart (an app's own cloak is a hidden window)
                if ((_nativeWrapper.GetCloakReason(window) & NativeEnums.DWM_CLOAKED_SHELL) == 0)
                    continue;
            }
            else if (_nativeWrapper.IsCloaked(window))
            {
                continue;
            }

            var windowProcessId = _nativeWrapper.GetWindowThreadProcessId(window);
            if (windowProcessId == session.CurrentProcessId)
                continue;

            // The shell's own process (explorer.exe) also draws the taskbar's flyouts and other shell surfaces: of its
            // windows only File Explorer's are application windows
            shellProcessId ??= _nativeWrapper.GetShellWindow() is var shell && shell != nint.Zero
                ? _nativeWrapper.GetWindowThreadProcessId(shell)
                : 0;
            if (windowProcessId == shellProcessId && !FileExplorerWindowClasses.Contains(className))
                continue;

            var title = _nativeWrapper.GetWindowText(window) ?? string.Empty;
            var process = ResolveProcess(window, className, title, windowProcessId, session);
            if (BuildWindowInfo(window, title, className, isCloaked: otherDesktops, process, session) is { } info)
                windows.Add(info);
        }

        return windows;
    }

    /// <summary>
    /// The top-level windows that <paramref name="owner"/> owns, directly or through other owned windows, in z-order.
    /// The owner chain of each window is read up to <see cref="WindowOwnerChain.MaxOwners"/> owners.
    /// </summary>
    private List<nint> OwnedTopLevelWindows(nint owner)
    {
        var owned = new List<nint>();
        foreach (var window in EnumerateTopLevelWindows(_nativeWrapper))
        {
            if (window == owner)
                continue;

            var current = window;
            for (var i = 0; i < WindowOwnerChain.MaxOwners; i++)
            {
                current = _nativeWrapper.GetWindow(current, NativeEnums.GW_OWNER);
                if (current == nint.Zero || current == window)
                    break;

                if (current == owner)
                {
                    owned.Add(window);
                    break;
                }
            }
        }

        return owned;
    }

    /// <summary>The enabled popup (GW_ENABLEDPOPUP) of a disabled window, or null when the window is enabled or has none.</summary>
    private nint? EnabledPopupOfDisabled(nint window)
    {
        if (!IsDisabled(window))
            return null;

        var popup = _nativeWrapper.GetWindow(window, NativeEnums.GW_ENABLEDPOPUP);
        return popup == nint.Zero || popup == window ? null : popup;
    }

    /// <summary>Whether the window has WS_DISABLED: a modal dialog took its input. False for zero.</summary>
    private bool IsDisabled(nint window) =>
        window != nint.Zero && ((long)_nativeWrapper.GetWindowLongPtr(window, NativeEnums.GWL_STYLE) & NativeEnums.WS_DISABLED) != 0;

    /// <summary>A popup the user can see: visible and not cloaked.</summary>
    private bool IsShownPopup(nint window) =>
        _nativeWrapper.IsWindowVisible(window) && !_nativeWrapper.IsCloaked(window);

    /// <summary>
    /// A dialog as a prompt: its title and class, the text of its Static controls and the captions of its buttons
    /// (Button controls, accelerator ampersands removed). GetWindowText reads a control of another process from the
    /// text Windows keeps for it, without sending it a message, so a hung dialog cannot block this.
    /// </summary>
    private ClosePrompt ReadDialog(nint dialog, ClosePromptKind kind)
    {
        var children = new List<nint>();
        _nativeWrapper.EnumChildWindows(dialog, child =>
        {
            children.Add(child);
            return true;
        });

        var text = new List<string>();
        var buttons = new List<string>();
        foreach (var child in children)
        {
            var className = _nativeWrapper.GetClassName(child) ?? string.Empty;
            var childText = _nativeWrapper.GetWindowText(child) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(childText))
                continue;

            if (string.Equals(className, "Static", StringComparison.OrdinalIgnoreCase))
                text.Add(childText.Trim());
            else if (string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase) && buttons.Count < MaxPromptButtons)
                buttons.Add(WithoutAccelerators(childText).Trim());
        }

        return new ClosePrompt(
            dialog,
            _nativeWrapper.GetWindowText(dialog) ?? string.Empty,
            _nativeWrapper.GetClassName(dialog) ?? string.Empty,
            PromptText(text),
            buttons,
            kind);
    }

    /// <summary>Prompt text lines as one text of at most <see cref="MaxPromptTextLength"/> characters, or null when there are none.</summary>
    public static string? PromptText(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct());
        if (text.Length == 0)
            return null;

        return text.Length <= MaxPromptTextLength ? text : text[..(MaxPromptTextLength - 1)].TrimEnd() + "…";
    }

    /// <summary>A control caption without its accelerator marks: "&amp;Yes" is "Yes", and "&amp;&amp;" is one ampersand.</summary>
    public static string WithoutAccelerators(string caption)
    {
        var result = new System.Text.StringBuilder(caption.Length);
        for (var i = 0; i < caption.Length; i++)
        {
            if (caption[i] == '&')
            {
                if (i + 1 < caption.Length && caption[i + 1] == '&')
                {
                    result.Append('&');
                    i++;
                }

                continue;
            }

            result.Append(caption[i]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Why closing the window could take down the desktop shell or end the agent's own session, or null
    /// when it cannot. The shell rule checks the window, the top-level window it is part of and every
    /// window that owns it (<see cref="WindowOwnerChain"/>: GW_OWNER as well as GA_ROOTOWNER, so an owned
    /// overlapped helper counts too): DefWindowProc destroys any window that gets WM_CLOSE, so a child or
    /// an owned helper popup of the taskbar (its task list, a tooltip) is as unsafe to close as the
    /// taskbar itself. A shell window is the shell's desktop window, or a window of a shell class that
    /// belongs to the shell's process (<see cref="WindowClassification.IsShellWindow"/>), so another app's
    /// WorkerW helper is not one. A dialog that the user can see is checked alone, without its owners:
    /// WM_CLOSE on a dialog works like its Cancel button (DefDlgProc turns it into IDCANCEL), and an owned
    /// window that closes leaves its owner alone, so a dialog of the shell such as Shut Down Windows can be
    /// dismissed. The host rule is <see cref="IHostGuard.IsHostWindow"/>, which follows the owner chain on
    /// its own, so a dialog of the agent's host stays protected.
    /// </summary>
    private string? FindCloseProtection(nint handle)
    {
        var shellWindow = _nativeWrapper.GetShellWindow();
        var shellProcessId = shellWindow == nint.Zero ? 0u : _nativeWrapper.GetWindowThreadProcessId(shellWindow);
        var root = _nativeWrapper.GetAncestor(handle, WindowAncestor.Root);
        IReadOnlyList<nint> windows = IsVisibleTopLevelDialog(handle, root)
            ? [handle]
            : WindowOwnerChain.SelfAndOwners(_nativeWrapper, handle);

        foreach (var window in windows.Where(w => w != nint.Zero).Distinct())
        {
            var className = _nativeWrapper.GetClassName(window);
            if (WindowClassification.IsShellWindow(
                    window, className, _nativeWrapper.GetWindowThreadProcessId(window), shellWindow, shellProcessId))
            {
                var part = window == handle ? "is" : $"belongs to {window}, which is";
                return $"Window {handle} {part} part of the desktop shell ({className}): the desktop or a taskbar. " +
                       "Closing it can open the Shut Down Windows dialog or take down part of the shell. " +
                       "Nothing was sent. Pass force=true only if the user explicitly asked to close this window.";
            }
        }

        if (_hostGuard.IsHostWindow(handle))
        {
            return $"Window {handle} belongs to the agent's host: the terminal, editor or chat app this server was " +
                   "started from, or a window that one owns. Closing it can end the agent's own session. " +
                   "Nothing was sent. Pass force=true only if the user explicitly asked to close this window.";
        }

        return null;
    }

    /// <summary>
    /// The error of a window change that the target policy refuses with <paramref name="verdict"/>: the
    /// Window Control tools report it with TargetDeniedByPolicy.
    /// </summary>
    public static string WindowChangeRefusal(nint handle, TargetPolicyVerdict verdict) =>
        $"Window {handle} is refused by the server's target policy: {verdict.Reason}. Nothing was changed: the window was not " +
        "moved, restored, raised or activated. The policy is set by the user in the server's environment and no tool argument " +
        "changes it; ask the user to do this step.";

    /// <summary>
    /// Why the target policy refuses the window, or null when it allows it (or there is no policy). The
    /// window is matched by its process, and a UWP frame also by the app it hosts, as the listings report it;
    /// one whose process cannot be named is refused (<see cref="EvaluateTargetPolicy"/>).
    /// </summary>
    private string? FindPolicyRefusal(nint handle) =>
        EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? $"Window {handle} is refused by the server's target policy: {verdict.Reason}. Nothing was sent. The policy is set by " +
              "the user in the server's environment; force does not override it. Ask the user to close this window."
            : null;

    public TargetPolicyVerdict EvaluateTargetPolicy(nint handle)
    {
        if (_targetPolicy == null)
            return TargetPolicyVerdict.Allow;

        var session = new ReadSession(_nativeWrapper, topLevelWindows: null);
        var className = _nativeWrapper.GetClassName(handle) ?? string.Empty;
        var title = _nativeWrapper.GetWindowText(handle) ?? string.Empty;
        var process = ResolveProcess(handle, className, title, _nativeWrapper.GetWindowThreadProcessId(handle), session);

        // A process that cannot be named cannot be checked, so it is refused while the policy denies anything, as the input
        // guard, the captures and the UI Automation reads refuse it; a security prompt of a shared process is refused by its
        // structure (roadmap M3.F5)
        var name = session.ProcessName(process.ProcessId);
        return _targetPolicy.EvaluateFailClosed(handle, process.HostProcessId is { } hostId ? [name, session.ProcessName(hostId)] : [name]);
    }

    /// <summary>
    /// A dialog box (#32770) that the user can see and dismiss: visible and top-level, not a child
    /// dialog (a property page) inside another window. A hidden one is a helper window, not a prompt.
    /// </summary>
    private bool IsVisibleTopLevelDialog(nint handle, nint root) =>
        root == handle &&
        WindowClassification.IsDialogClass(_nativeWrapper.GetClassName(handle)) &&
        _nativeWrapper.IsWindowVisible(handle);

    /// <summary>Maps the Win32 error of a PostMessage(WM_CLOSE) that Windows refused.</summary>
    private static WindowCloseResult CloseNotPosted(nint handle, int win32Error) => win32Error switch
    {
        NativeEnums.ERROR_ACCESS_DENIED => WindowCloseResult.Failed(handle, WindowErrorCode.AccessDenied,
            $"Windows blocked the close message (Win32 error {win32Error}, access denied). The window most likely " +
            "belongs to an app running with higher privileges, for example as administrator: User Interface " +
            "Privilege Isolation blocks messages to it from this server. Nothing was closed."),
        NativeEnums.ERROR_INVALID_WINDOW_HANDLE => WindowCloseResult.Failed(handle, WindowErrorCode.InvalidHandle,
            $"The window no longer exists (Win32 error {win32Error}): it closed before the close message was sent."),
        // A full message queue: the application has stopped reading its messages
        NativeEnums.ERROR_NOT_ENOUGH_QUOTA => WindowCloseResult.Failed(handle, WindowErrorCode.NotResponding,
            $"Windows did not accept the close message (Win32 error {win32Error}): the window's message queue is full, so " +
            "the application is not responding. Nothing was closed. Wait and try again, or ask the user; kill_process ends " +
            "an app that stays hung, losing its unsaved work."),
        _ => WindowCloseResult.Failed(handle, WindowErrorCode.CloseFailed,
            $"Windows did not accept the close message (Win32 error {win32Error}: {new System.ComponentModel.Win32Exception(win32Error).Message}). " +
            "Nothing was closed.")
    };

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowBoundsBatchAsync(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            placements.Count,
            i => placements[i].Handle,
            i => SetWindowFrame(placements[i].Handle, placements[i].X, placements[i].Y, placements[i].Width, placements[i].Height,
                placements[i].BringToFront),
            WindowErrorCode.MoveFailed,
            delayBetweenMs,
            cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SnapWindowsBatchAsync(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            placements.Count,
            i => placements[i].Handle,
            i => SnapWindow(placements[i].Handle, placements[i].Position, placements[i].MonitorIndex, placements[i].BringToFront),
            WindowErrorCode.MoveFailed,
            delayBetweenMs,
            cancellationToken);

    public Task<IReadOnlyList<WindowChangeResult>> SetWindowStateBatchAsync(
        IReadOnlyList<(nint Handle, WindowStateRequest State)> changes,
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default) =>
        RunBatchAsync(
            changes.Count,
            i => changes[i].Handle,
            i => SetWindowState(changes[i].Handle, changes[i].State),
            WindowErrorCode.StateChangeFailed,
            delayBetweenMs,
            cancellationToken);

    /// <summary>
    /// Changes <paramref name="count"/> windows one after another. A window that fails does not stop the batch: its result
    /// says why, with NotResponding for a window that did not answer in time and <paramref name="failure"/> for an
    /// unexpected error. The batch pauses <paramref name="delayBetweenMs"/> after each window that was changed.
    /// </summary>
    private static async Task<IReadOnlyList<WindowChangeResult>> RunBatchAsync(
        int count,
        Func<int, nint> handleOf,
        Func<int, WindowChangeResult> change,
        WindowErrorCode failure,
        int delayBetweenMs,
        CancellationToken cancellationToken)
    {
        var results = new List<WindowChangeResult>(count);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WindowChangeResult result;
            try
            {
                result = change(i);
            }
            catch (WindowNotRespondingException ex)
            {
                result = WindowChangeResult.Failed(handleOf(i), WindowErrorCode.NotResponding, ex.Message);
            }
            catch (Exception ex)
            {
                result = WindowChangeResult.Failed(handleOf(i), failure, ex.Message);
            }

            results.Add(result);

            if (result.Success)
            {
                await PauseBetweenWindowsAsync(delayBetweenMs, i, count, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<WindowCloseResult>> CloseWindowsBatchAsync(
        IReadOnlyList<nint> handles,
        bool force,
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WindowCloseResult>();

        for (var i = 0; i < handles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var handle = handles[i];
            WindowCloseResult result;
            try
            {
                result = CloseWindow(handle, force);
            }
            catch (Exception ex)
            {
                result = WindowCloseResult.Failed(handle, WindowErrorCode.CloseFailed, ex.Message);
            }

            results.Add(result);

            // Pause only after a close message was actually sent
            if (result.Success)
            {
                await PauseBetweenWindowsAsync(delayBetweenMs, i, handles.Count, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>
    /// The batches' pause after window <paramref name="index"/> of <paramref name="count"/>: none after the last one,
    /// <see cref="BatchDelay"/> otherwise. A cancelled call ends the pause at once.
    /// </summary>
    private static Task PauseBetweenWindowsAsync(int delayBetweenMs, int index, int count, CancellationToken cancellationToken) =>
        delayBetweenMs > 0 && index < count - 1
            ? Task.Delay(BatchDelay(delayBetweenMs), cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Applies <paramref name="query"/> to one window and reads it if it matches. The cheap native
    /// reads and filters come first; the process name lookup comes last.
    /// </summary>
    private WindowInfo? Match(nint hWnd, WindowQuery query, Regex? titleRegex, ReadSession session)
    {
        if (query.VisibleOnly && !_nativeWrapper.IsWindowVisible(hWnd))
            return null;

        var className = _nativeWrapper.GetClassName(hWnd) ?? string.Empty;
        if (!string.IsNullOrEmpty(query.ClassName) &&
            !string.Equals(className, query.ClassName, StringComparison.OrdinalIgnoreCase))
            return null;

        var title = _nativeWrapper.GetWindowText(hWnd) ?? string.Empty;
        bool isCloaked;
        if (query.SkipsUserFacingFilter)
        {
            isCloaked = _nativeWrapper.IsCloaked(hWnd);
        }
        else if (!PassesUserFacingRules(hWnd, className, title, query.IncludeToolWindows, query.IncludeCloaked, query.IncludeUntitled,
                     out isCloaked))
        {
            return null;
        }

        // Titles as WindowTitles reads them: invisible format characters and Edge's " and N more pages" never hide a match
        if (!string.IsNullOrEmpty(query.TitleContains) && !WindowTitles.Contains(title, query.TitleContains))
            return null;

        if (!string.IsNullOrEmpty(query.TitleEquals) && !WindowTitles.AreEqual(title, query.TitleEquals))
            return null;

        if (titleRegex != null && !WindowTitles.Matches(title, titleRegex))
            return null;

        var windowProcessId = _nativeWrapper.GetWindowThreadProcessId(hWnd);
        if (!query.SkipsUserFacingFilter && windowProcessId == session.CurrentProcessId)
            return null; // the server's own windows are not app windows

        var process = ResolveProcess(hWnd, className, title, windowProcessId, session);
        if (query.ProcessId is { } processId && !process.Has(processId))
            return null;

        if (!string.IsNullOrEmpty(query.ProcessName) &&
            !WindowMatching.ProcessNameMatches(
                session.ProcessName(process.ProcessId),
                process.HostProcessId is { } hostId ? session.ProcessName(hostId) : null,
                query.ProcessName,
                query.ProcessNameContains))
            return null;

        var info = BuildWindowInfo(hWnd, title, className, isCloaked, process, session);
        if (info == null || (!query.IncludeMinimized && info.State == WindowState.Minimized))
            return null;

        return info;
    }

    /// <summary>
    /// The user-facing rules that come after visibility, cheapest first: not the desktop or a
    /// taskbar, not a tool window, a title, and not cloaked. Owned windows (dialogs) pass: they are
    /// what an agent most often looks for, and some apps make even their main window an owned window.
    /// <paramref name="isCloaked"/> is read only when the cheaper rules pass.
    /// </summary>
    private bool PassesUserFacingRules(
        nint hWnd, string className, string title, bool includeToolWindows, bool includeCloaked, bool includeUntitled, out bool isCloaked)
    {
        isCloaked = false;

        if (WindowClassification.IsShellWindowClass(className))
            return false;

        if (!includeToolWindows &&
            WindowClassification.IsToolWindow(_nativeWrapper.GetWindowLongPtr(hWnd, NativeEnums.GWL_EXSTYLE)))
            return false;

        if (!includeUntitled && string.IsNullOrEmpty(title))
            return false;

        isCloaked = _nativeWrapper.IsCloaked(hWnd);
        return includeCloaked || !isCloaked;
    }

    /// <summary>
    /// Reads any window, with no listing filter: untitled, cloaked, tool and shell windows and the
    /// server's own windows are described too. Null only when the window cannot be read (it is gone).
    /// </summary>
    private WindowInfo? ReadWindowInfo(nint hWnd)
    {
        var session = new ReadSession(_nativeWrapper, topLevelWindows: null);
        var className = _nativeWrapper.GetClassName(hWnd) ?? string.Empty;
        var title = _nativeWrapper.GetWindowText(hWnd) ?? string.Empty;
        var process = ResolveProcess(hWnd, className, title, _nativeWrapper.GetWindowThreadProcessId(hWnd), session);

        return BuildWindowInfo(hWnd, title, className, _nativeWrapper.IsCloaked(hWnd), process, session);
    }

    private WindowInfo? BuildWindowInfo(
        nint hWnd, string title, string className, bool isCloaked, WindowProcess process, ReadSession session)
    {
        var rect = _nativeWrapper.GetWindowRect(hWnd);
        if (rect == null)
            return null;

        var state = WindowState.Normal;
        if (_nativeWrapper.IsIconic(hWnd))
            state = WindowState.Minimized;
        else if (_nativeWrapper.IsZoomed(hWnd))
            state = WindowState.Maximized;

        var owner = _nativeWrapper.GetWindow(hWnd, NativeEnums.GW_OWNER);
        var frame = _nativeWrapper.GetExtendedFrameBounds(hWnd) is { } extended
            ? WindowRect.FromLTRB(extended.Left, extended.Top, extended.Right, extended.Bottom)
            : null;

        return new WindowInfo(
            Handle: hWnd,
            Title: title,
            ProcessName: session.ProcessName(process.ProcessId),
            ProcessId: (int)process.ProcessId,
            Bounds: WindowRect.FromLTRB(rect.Value.Left, rect.Value.Top, rect.Value.Right, rect.Value.Bottom),
            State: state,
            IsVisible: _nativeWrapper.IsWindowVisible(hWnd),
            MonitorIndex: _monitorService.GetMonitorIndex(hWnd))
        {
            VisibleBounds = frame,
            ClassName = className,
            IsCloaked = isCloaked,
            OwnerHandle = owner == nint.Zero ? null : owner,
            HostProcessName = process.HostProcessId is { } hostId ? session.ProcessName(hostId) : null,
            HostProcessId = process.HostProcessId is { } id ? (int)id : null,
            IsAgentHost = _hostGuard.IsHostWindow(hWnd),
            IsTopmost = IsTopmost(hWnd)
        };
    }

    /// <summary>
    /// The process a window belongs to. A UWP frame window (ApplicationFrameWindow) belongs to
    /// ApplicationFrameHost, but the app runs in the process of the CoreWindow that the frame hosts:
    /// report that process, with ApplicationFrameHost as the host. A window of ApplicationFrameHost of another
    /// class, such as the frame a prompt may come up in, is attributed the same way to a CoreWindow child of
    /// another process (roadmap M3.F7). The process name that check reads is read once per process in a call, and is
    /// the one the window is reported with anyway.
    /// </summary>
    private WindowProcess ResolveProcess(nint hWnd, string className, string title, uint windowProcessId, ReadSession session)
    {
        if (!UwpFrames.IsFrame(className, windowProcessId, session.ProcessName))
            return new WindowProcess(windowProcessId, HostProcessId: null);

        return FindHostedAppProcessId(hWnd, className, title, windowProcessId, session) is { } appProcessId
            ? new WindowProcess(appProcessId, HostProcessId: windowProcessId)
            : new WindowProcess(windowProcessId, HostProcessId: null);
    }

    private uint? FindHostedAppProcessId(nint frame, string className, string title, uint hostProcessId, ReadSession session)
    {
        // While the app runs, its CoreWindow is a child of the frame.
        if (UwpFrames.FindHostedAppProcessId(_nativeWrapper, frame, hostProcessId) is { } runningAppProcessId)
            return runningAppProcessId;

        // While the app is minimized or suspended, Windows moves its CoreWindow out of the frame to the
        // top level. Match it by title, and only when exactly one other process has such a window; an
        // ambiguous title leaves the frame attributed to its host. Only an app's frame (ApplicationFrameWindow)
        // is minimized that way; the frame host's other windows are matched by their CoreWindow child alone.
        if (string.IsNullOrEmpty(title) || !WindowClassification.IsApplicationFrameWindow(className))
            return null;

        var candidates = session.TopLevelCoreWindows
            .Where(w => w.ProcessId != 0 && w.ProcessId != hostProcessId && string.Equals(w.Title, title, StringComparison.Ordinal))
            .Select(w => w.ProcessId)
            .Distinct()
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static List<nint> EnumerateTopLevelWindows(INativeWindowWrapper nativeWrapper)
    {
        var handles = new List<nint>();
        nativeWrapper.EnumWindows(hWnd =>
        {
            handles.Add(hWnd);
            return true;
        });
        return handles;
    }

    /// <summary>The process of a window, and for a UWP frame window also the frame host process.</summary>
    private readonly record struct WindowProcess(uint ProcessId, uint? HostProcessId)
    {
        public bool Has(int processId) => ProcessId == processId || HostProcessId == processId;
    }

    /// <summary>
    /// Values shared by the reads of one call: each process name is looked up once, and the
    /// top-level UWP CoreWindows are listed only when a frame needs them.
    /// </summary>
    private sealed class ReadSession(INativeWindowWrapper nativeWrapper, IReadOnlyList<nint>? topLevelWindows)
    {
        private readonly Dictionary<uint, string> _processNames = [];
        private uint? _currentProcessId;
        private List<(string Title, uint ProcessId)>? _topLevelCoreWindows;

        public uint CurrentProcessId => _currentProcessId ??= nativeWrapper.GetCurrentProcessId();

        /// <summary>The top-level CoreWindows: (title, process ID).</summary>
        public IReadOnlyList<(string Title, uint ProcessId)> TopLevelCoreWindows =>
            _topLevelCoreWindows ??= (topLevelWindows ?? EnumerateTopLevelWindows(nativeWrapper))
                .Where(hWnd => WindowClassification.IsCoreWindow(nativeWrapper.GetClassName(hWnd)))
                .Select(hWnd => (nativeWrapper.GetWindowText(hWnd) ?? string.Empty, nativeWrapper.GetWindowThreadProcessId(hWnd)))
                .ToList();

        /// <summary>The name of the process, or "Unknown" when it has exited or cannot be read.</summary>
        public string ProcessName(uint processId)
        {
            if (!_processNames.TryGetValue(processId, out var name))
            {
                name = nativeWrapper.GetProcessName(processId) ?? WindowInfo.UnknownProcessName;
                _processNames[processId] = name;
            }

            return name;
        }
    }
}
