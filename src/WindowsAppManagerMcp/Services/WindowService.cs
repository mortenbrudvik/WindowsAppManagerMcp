using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

public class WindowService : IWindowService
{
    private readonly IMonitorService _monitorService;
    private readonly INativeWindowWrapper _nativeWrapper;
    private readonly IHostGuard _hostGuard;

    public WindowService(IMonitorService monitorService, INativeWindowWrapper nativeWrapper, IHostGuard hostGuard)
    {
        _monitorService = monitorService;
        _nativeWrapper = nativeWrapper;
        _hostGuard = hostGuard ?? throw new ArgumentNullException(nameof(hostGuard));
    }

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
            if (Match(hWnd, query, session) is { } info)
                windows.Add(info);
        }

        return windows;
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
        _nativeWrapper.IsWindowVisible(handle) &&
        PassesUserFacingRules(
            handle,
            _nativeWrapper.GetClassName(handle) ?? string.Empty,
            _nativeWrapper.GetWindowText(handle) ?? string.Empty,
            includeToolWindows: false,
            includeCloaked: false,
            out _);

    public bool MoveWindow(nint handle, int x, int y, bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        var rect = _nativeWrapper.GetWindowRect(handle);
        if (rect == null)
            return false;

        var width = rect.Value.Right - rect.Value.Left;
        var height = rect.Value.Bottom - rect.Value.Top;

        var flags = bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

        var success = _nativeWrapper.SetWindowPos(handle, x, y, width, height, flags);

        if (success && bringToFront)
        {
            _nativeWrapper.BringWindowToTop(handle);
        }

        return success;
    }

    public bool ResizeWindow(nint handle, int width, int height)
    {
        if (!IsValidWindow(handle))
            return false;

        var rect = _nativeWrapper.GetWindowRect(handle);
        if (rect == null)
            return false;

        return _nativeWrapper.SetWindowPos(
            handle,
            rect.Value.Left, rect.Value.Top, width, height,
            NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE);
    }

    public bool SetWindowBounds(nint handle, int x, int y, int width, int height, bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        // Restore window first if minimized
        if (_nativeWrapper.IsIconic(handle))
        {
            _nativeWrapper.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        var flags = bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

        var success = _nativeWrapper.SetWindowPos(handle, x, y, width, height, flags);

        if (success && bringToFront)
        {
            _nativeWrapper.BringWindowToTop(handle);
        }

        return success;
    }

    public bool SetWindowState(nint handle, WindowState state)
    {
        if (!IsValidWindow(handle))
            return false;

        var cmd = state switch
        {
            WindowState.Minimized => NativeEnums.SW_MINIMIZE,
            WindowState.Maximized => NativeEnums.SW_MAXIMIZE,
            WindowState.Normal => NativeEnums.SW_RESTORE,
            _ => NativeEnums.SW_RESTORE
        };

        return _nativeWrapper.ShowWindow(handle, cmd);
    }

    public bool FocusWindow(nint handle)
    {
        if (!IsValidWindow(handle))
            return false;

        // If window is minimized, restore it first
        if (_nativeWrapper.IsIconic(handle))
        {
            _nativeWrapper.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        // Try SetForegroundWindow first
        if (_nativeWrapper.SetForegroundWindow(handle))
            return true;

        // Fallback: try BringWindowToTop
        _nativeWrapper.BringWindowToTop(handle);
        return _nativeWrapper.SetForegroundWindow(handle);
    }

    public bool SnapWindow(nint handle, SnapPosition position, int? monitorIndex = null, bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        var targetMonitor = monitorIndex ?? _monitorService.GetMonitorIndex(handle);
        var bounds = _monitorService.CalculateSnapBounds(targetMonitor, position);

        return SetWindowBounds(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height, bringToFront);
    }

    public bool MoveWindowToMonitor(nint handle, int monitorIndex, string positioning = "center", bool bringToFront = true)
    {
        if (!IsValidWindow(handle))
            return false;

        var monitors = _monitorService.GetAllMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            return false;

        var targetMonitor = monitors[monitorIndex];
        var workArea = targetMonitor.WorkArea;

        // Get current window size
        var rect = _nativeWrapper.GetWindowRect(handle);
        if (rect == null)
            return false;

        var width = rect.Value.Right - rect.Value.Left;
        var height = rect.Value.Bottom - rect.Value.Top;

        int x, y;

        switch (positioning.ToLowerInvariant())
        {
            case "center":
                x = workArea.X + (workArea.Width - width) / 2;
                y = workArea.Y + (workArea.Height - height) / 2;
                break;

            case "topleft":
            case "top_left":
                x = workArea.X;
                y = workArea.Y;
                break;

            case "topright":
            case "top_right":
                x = workArea.X + workArea.Width - width;
                y = workArea.Y;
                break;

            case "bottomleft":
            case "bottom_left":
                x = workArea.X;
                y = workArea.Y + workArea.Height - height;
                break;

            case "bottomright":
            case "bottom_right":
                x = workArea.X + workArea.Width - width;
                y = workArea.Y + workArea.Height - height;
                break;

            case "maximize":
                return SetWindowState(handle, WindowState.Maximized);

            case "restore":
                return SetWindowBounds(handle, workArea.X, workArea.Y, width, height, bringToFront);

            default:
                x = workArea.X + (workArea.Width - width) / 2;
                y = workArea.Y + (workArea.Height - height) / 2;
                break;
        }

        // Restore if minimized
        if (_nativeWrapper.IsIconic(handle))
        {
            _nativeWrapper.ShowWindow(handle, NativeEnums.SW_RESTORE);
        }

        var flags = bringToFront ? NativeEnums.SWP_NOACTIVATE : NativeEnums.SWP_NOZORDER | NativeEnums.SWP_NOACTIVATE;

        var success = _nativeWrapper.SetWindowPos(handle, x, y, width, height, flags);

        if (success && bringToFront)
        {
            _nativeWrapper.BringWindowToTop(handle);
        }

        return success;
    }

    public bool IsValidWindow(nint handle)
    {
        return handle != nint.Zero && _nativeWrapper.IsWindow(handle);
    }

    public WindowCloseResult CloseWindow(nint handle, bool force)
    {
        if (!IsValidWindow(handle))
            return WindowCloseResult.Failed(handle, WindowErrorCode.InvalidHandle, "Invalid window handle");

        if (!force && FindCloseProtection(handle) is { } protection)
            return WindowCloseResult.Failed(handle, WindowErrorCode.ProtectedWindow, protection);

        // WM_CLOSE asks for a graceful close: the application can prompt to save, or cancel it
        var posted = _nativeWrapper.PostMessage(handle, NativeEnums.WM_CLOSE, nint.Zero, nint.Zero);
        return posted.Succeeded ? WindowCloseResult.Sent(handle) : CloseNotPosted(handle, posted.Win32Error);
    }

    /// <summary>
    /// Why closing the window could take down the desktop shell or end the agent's own session, or null
    /// when it cannot. The shell rule checks the window, the top-level window it is part of and that
    /// window's root owner: DefWindowProc destroys any window that gets WM_CLOSE, so a child or an owned
    /// helper popup of the taskbar (its task list, a tooltip) is as unsafe to close as the taskbar itself.
    /// The host rule is <see cref="IHostGuard.IsHostWindow"/>, which is root-owner aware on its own.
    /// </summary>
    private string? FindCloseProtection(nint handle)
    {
        var shellWindow = _nativeWrapper.GetShellWindow();
        nint[] windows =
        [
            handle,
            _nativeWrapper.GetAncestor(handle, WindowAncestor.Root),
            _nativeWrapper.GetAncestor(handle, WindowAncestor.RootOwner)
        ];

        foreach (var window in windows.Where(w => w != nint.Zero).Distinct())
        {
            var className = _nativeWrapper.GetClassName(window);
            if (WindowClassification.IsShellWindowClass(className) || (shellWindow != nint.Zero && window == shellWindow))
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

    /// <summary>Maps the Win32 error of a PostMessage(WM_CLOSE) that Windows refused.</summary>
    private static WindowCloseResult CloseNotPosted(nint handle, int win32Error) => win32Error switch
    {
        NativeEnums.ERROR_ACCESS_DENIED => WindowCloseResult.Failed(handle, WindowErrorCode.AccessDenied,
            $"Windows blocked the close message (Win32 error {win32Error}, access denied). The window most likely " +
            "belongs to an app running with higher privileges, for example as administrator: User Interface " +
            "Privilege Isolation blocks messages to it from this server. Nothing was closed."),
        NativeEnums.ERROR_INVALID_WINDOW_HANDLE => WindowCloseResult.Failed(handle, WindowErrorCode.InvalidHandle,
            $"The window no longer exists (Win32 error {win32Error}): it closed before the close message was sent."),
        _ => WindowCloseResult.Failed(handle, WindowErrorCode.CloseFailed,
            $"Windows did not accept the close message (Win32 error {win32Error}: {new System.ComponentModel.Win32Exception(win32Error).Message}). " +
            "Nothing was closed.")
    };

    public IReadOnlyList<(nint Handle, bool Success, string? Error)> SetWindowBoundsBatch(
        IReadOnlyList<(nint Handle, int X, int Y, int Width, int Height, bool BringToFront)> placements,
        int delayBetweenMs = 50)
    {
        var results = new List<(nint Handle, bool Success, string? Error)>();

        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            try
            {
                if (!IsValidWindow(placement.Handle))
                {
                    results.Add((placement.Handle, false, "Invalid window handle"));
                    continue;
                }

                var success = SetWindowBounds(
                    placement.Handle,
                    placement.X,
                    placement.Y,
                    placement.Width,
                    placement.Height,
                    placement.BringToFront);

                results.Add((placement.Handle, success, success ? null : "SetWindowBounds failed"));

                if (delayBetweenMs > 0 && i < placements.Count - 1)
                {
                    Thread.Sleep(delayBetweenMs);
                }
            }
            catch (Exception ex)
            {
                results.Add((placement.Handle, false, ex.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<(nint Handle, bool Success, string? Error)> SnapWindowsBatch(
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)> placements,
        int delayBetweenMs = 50)
    {
        var results = new List<(nint Handle, bool Success, string? Error)>();

        for (var i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            try
            {
                if (!IsValidWindow(placement.Handle))
                {
                    results.Add((placement.Handle, false, "Invalid window handle"));
                    continue;
                }

                var success = SnapWindow(
                    placement.Handle,
                    placement.Position,
                    placement.MonitorIndex,
                    placement.BringToFront);

                results.Add((placement.Handle, success, success ? null : "SnapWindow failed"));

                if (delayBetweenMs > 0 && i < placements.Count - 1)
                {
                    Thread.Sleep(delayBetweenMs);
                }
            }
            catch (Exception ex)
            {
                results.Add((placement.Handle, false, ex.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<(nint Handle, bool Success, string? Error)> SetWindowStateBatch(
        IReadOnlyList<(nint Handle, WindowState State)> changes,
        int delayBetweenMs = 30)
    {
        var results = new List<(nint Handle, bool Success, string? Error)>();

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            try
            {
                if (!IsValidWindow(change.Handle))
                {
                    results.Add((change.Handle, false, "Invalid window handle"));
                    continue;
                }

                var success = SetWindowState(change.Handle, change.State);
                results.Add((change.Handle, success, success ? null : "SetWindowState failed"));

                if (delayBetweenMs > 0 && i < changes.Count - 1)
                {
                    Thread.Sleep(delayBetweenMs);
                }
            }
            catch (Exception ex)
            {
                results.Add((change.Handle, false, ex.Message));
            }
        }

        return results;
    }

    public IReadOnlyList<WindowCloseResult> CloseWindowsBatch(
        IReadOnlyList<nint> handles,
        bool force,
        int delayBetweenMs = 50)
    {
        var results = new List<WindowCloseResult>();

        for (var i = 0; i < handles.Count; i++)
        {
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
            if (result.Success && delayBetweenMs > 0 && i < handles.Count - 1)
            {
                Thread.Sleep(delayBetweenMs);
            }
        }

        return results;
    }

    /// <summary>
    /// Applies <paramref name="query"/> to one window and reads it if it matches. The cheap native
    /// reads and filters come first; the process name lookup comes last.
    /// </summary>
    private WindowInfo? Match(nint hWnd, WindowQuery query, ReadSession session)
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
        else if (!PassesUserFacingRules(hWnd, className, title, query.IncludeToolWindows, query.IncludeCloaked, out isCloaked))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(query.TitleContains) &&
            !title.Contains(query.TitleContains, StringComparison.OrdinalIgnoreCase))
            return null;

        var windowProcessId = _nativeWrapper.GetWindowThreadProcessId(hWnd);
        if (!query.SkipsUserFacingFilter && windowProcessId == session.CurrentProcessId)
            return null; // the server's own windows are not app windows

        var process = ResolveProcess(hWnd, className, title, windowProcessId, session);
        if (query.ProcessId is { } processId && !process.Has(processId))
            return null;

        if (!string.IsNullOrEmpty(query.ProcessName) &&
            !session.ProcessName(process.ProcessId).Contains(query.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            !(process.HostProcessId is { } hostId &&
              session.ProcessName(hostId).Contains(query.ProcessName, StringComparison.OrdinalIgnoreCase)))
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
        nint hWnd, string className, string title, bool includeToolWindows, bool includeCloaked, out bool isCloaked)
    {
        isCloaked = false;

        if (WindowClassification.IsShellWindowClass(className))
            return false;

        if (!includeToolWindows &&
            WindowClassification.IsToolWindow(_nativeWrapper.GetWindowLongPtr(hWnd, NativeEnums.GWL_EXSTYLE)))
            return false;

        if (string.IsNullOrEmpty(title))
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
            ClassName = className,
            IsCloaked = isCloaked,
            OwnerHandle = owner == nint.Zero ? null : owner,
            HostProcessName = process.HostProcessId is { } hostId ? session.ProcessName(hostId) : null,
            HostProcessId = process.HostProcessId is { } id ? (int)id : null
        };
    }

    /// <summary>
    /// The process a window belongs to. A UWP frame window (ApplicationFrameWindow) belongs to
    /// ApplicationFrameHost, but the app runs in the process of the CoreWindow that the frame hosts:
    /// report that process, with ApplicationFrameHost as the host.
    /// </summary>
    private WindowProcess ResolveProcess(nint hWnd, string className, string title, uint windowProcessId, ReadSession session)
    {
        if (!WindowClassification.IsApplicationFrameWindow(className))
            return new WindowProcess(windowProcessId, HostProcessId: null);

        return FindHostedAppProcessId(hWnd, title, windowProcessId, session) is { } appProcessId
            ? new WindowProcess(appProcessId, HostProcessId: windowProcessId)
            : new WindowProcess(windowProcessId, HostProcessId: null);
    }

    private uint? FindHostedAppProcessId(nint frame, string title, uint hostProcessId, ReadSession session)
    {
        // While the app runs, its CoreWindow is a child of the frame.
        var children = new List<nint>();
        _nativeWrapper.EnumChildWindows(frame, child =>
        {
            children.Add(child);
            return true;
        });

        foreach (var child in children)
        {
            if (!WindowClassification.IsCoreWindow(_nativeWrapper.GetClassName(child)))
                continue;

            var childProcessId = _nativeWrapper.GetWindowThreadProcessId(child);
            if (childProcessId != 0 && childProcessId != hostProcessId)
                return childProcessId;
        }

        // While the app is minimized or suspended, Windows moves its CoreWindow out of the frame to the
        // top level. Match it by title, and only when exactly one other process has such a window; an
        // ambiguous title leaves the frame attributed to its host.
        if (string.IsNullOrEmpty(title))
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
                name = nativeWrapper.GetProcessName(processId) ?? "Unknown";
                _processNames[processId] = name;
            }

            return name;
        }
    }
}
