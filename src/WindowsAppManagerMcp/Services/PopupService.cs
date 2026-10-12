using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>The open popups of a window (roadmap M3.5); see <see cref="IPopupService"/> for the rules.</summary>
public sealed class PopupService : IPopupService
{
    /// <summary>The most popups one window reports (and a windowAndPopups search adds to the window): front first.</summary>
    public const int MaxPopups = 16;

    /// <summary>The most windows a desktop-scope search covers: the ones in front.</summary>
    public const int DesktopWindowLimit = 40;

    /// <summary>How often <see cref="WaitForNewPopupAsync"/> looks for a new popup.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How long <see cref="WaitForNewPopupAsync"/> keeps the target's child processes before it reads them again.</summary>
    public static readonly TimeSpan ChildProcessRefresh = TimeSpan.FromSeconds(1);

    /// <summary>How long get_active_popups keeps waiting after its timeout for the reads to hand back what they have.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(1);

    private const long WsCaption = 0x00C00000;
    private const long WsDisabled = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    /// <summary>The Win32 menu class: context menus and the drop-down menus of a menu bar.</summary>
    public const string MenuClass = "#32768";

    /// <summary>
    /// The text-input and GDI+ helper windows every GUI thread can have: hidden as a rule, and never a popup even when an
    /// app shows one (the same list close_application keeps out).
    /// </summary>
    private static readonly HashSet<string> HelperWindowClasses =
        new(StringComparer.OrdinalIgnoreCase) { "IME", "MSCTFIME UI", "GDI+ Hook Window Class" };

    /// <summary>The window classes of combo box dropdowns (Win32 and WinForms).</summary>
    private static readonly HashSet<string> DropdownClasses =
        new(StringComparer.OrdinalIgnoreCase) { "ComboLBox", "ComboBoxEx32ListBox" };

    /// <summary>The window classes of WinUI and XAML flyouts, menus and popups shown in a window of their own.</summary>
    private static readonly HashSet<string> FlyoutClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Xaml_WindowedPopupClass", "Microsoft.UI.Content.PopupWindowSiteBridge", "Microsoft.UI.Content.DesktopChildSiteBridge"
        };

    /// <summary>The window classes of tooltips.</summary>
    private static readonly HashSet<string> TooltipClasses =
        new(StringComparer.OrdinalIgnoreCase) { "tooltips_class32", "Xaml_WindowedTooltipClass" };

    private readonly INativeWindowWrapper _windows;
    private readonly INativeProcessWrapper? _processes;
    private readonly IHostGuard? _hostGuard;
    private readonly ITargetPolicy? _policy;
    private readonly IUiaTree? _tree;
    private readonly IUiaWorker? _worker;
    private readonly IElementRegistry? _registry;
    private readonly TimeProvider _clock;

    /// <param name="windows">The Win32 window reads.</param>
    /// <param name="processes">The process snapshot of the child-process rule; without it no child process counts.</param>
    /// <param name="hostGuard">The agent's host windows, which the reads of popups and of the desktop leave out; without it none
    /// is left out.</param>
    /// <param name="policy">The target policy, which the reads of popups and of the desktop respect; without it no window is
    /// left out.</param>
    /// <param name="tree">get_active_popups' UI Automation reads.</param>
    /// <param name="worker">Runs them under a deadline.</param>
    /// <param name="registry">Gives each popup's element its ref.</param>
    /// <param name="clock">The clock of <see cref="WaitForNewPopupAsync"/>; the system clock by default.</param>
    public PopupService(
        INativeWindowWrapper windows,
        INativeProcessWrapper? processes = null,
        IHostGuard? hostGuard = null,
        ITargetPolicy? policy = null,
        IUiaTree? tree = null,
        IUiaWorker? worker = null,
        IElementRegistry? registry = null,
        TimeProvider? clock = null)
    {
        _windows = windows;
        _processes = processes;
        _hostGuard = hostGuard;
        _policy = policy;
        _tree = tree;
        _worker = worker;
        _registry = registry;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IReadOnlyList<PopupWindow> GetPopups(nint window) => Popups(window, out _);

    /// <inheritdoc />
    public PopupList GetPopupList(nint window)
    {
        var popups = Popups(window, out var truncated);
        return new PopupList(Marked(popups, []), truncated);
    }

    /// <summary>
    /// The popups with <see cref="PopupWindow.IsAgentHost"/> and <see cref="PopupWindow.IsDeniedByPolicy"/> set: whether the
    /// popup is a window of the agent's host, and whether the target policy denies its process (for a UWP frame, its app).
    /// <paramref name="names"/> keeps the process names read.
    /// </summary>
    private List<PopupWindow> Marked(List<PopupWindow> popups, Dictionary<uint, string?> names) =>
        popups.Select(p => p with { IsAgentHost = IsHost(p.Handle), IsDeniedByPolicy = !PolicyAllows(p.Handle, names) }).ToList();

    /// <param name="window">The window.</param>
    /// <param name="truncated">More than <see cref="MaxPopups"/> popups were open.</param>
    /// <param name="target">The target's processes when the caller keeps them across calls (a wait); a new snapshot otherwise.</param>
    private List<PopupWindow> Popups(nint window, out bool truncated, TargetProcesses? target = null)
    {
        truncated = false;
        var popups = new List<PopupWindow>();
        if (window == nint.Zero || !_windows.IsWindow(window))
        {
            return popups;
        }

        target ??= new TargetProcesses(this, window);
        var enabledPopup = EnabledPopupOf(window);
        var shell = new ShellWindow(_windows);
        var selfProcess = _windows.GetCurrentProcessId();

        // Collected first: no window is read inside the enumeration callback
        var topLevel = TopLevelWindows();
        var mainWindows = new MainWindows(this, window, target, topLevel);
        foreach (var candidate in topLevel)
        {
            if (candidate == window || !IsShown(candidate, out var bounds))
            {
                continue;
            }

            var className = _windows.GetClassName(candidate) ?? string.Empty;
            var processId = _windows.GetWindowThreadProcessId(candidate);
            if (HelperWindowClasses.Contains(className) || processId == selfProcess || shell.Contains(candidate, className, processId))
            {
                continue;
            }

            var style = (long)_windows.GetWindowLongPtr(candidate, NativeEnums.GWL_STYLE);
            var exStyle = (long)_windows.GetWindowLongPtr(candidate, NativeEnums.GWL_EXSTYLE);
            var owner = _windows.GetWindow(candidate, NativeEnums.GW_OWNER);
            if (Relation(candidate, window, className, processId, owner, style, exStyle, target, shell, mainWindows) is not { } relation)
            {
                continue;
            }

            if (popups.Count == MaxPopups)
            {
                truncated = true;
                break;
            }

            popups.Add(new PopupWindow(
                Handle: candidate,
                Kind: Classify(className, style, exStyle, owner != nint.Zero),
                Title: EmptyToNull(_windows.GetWindowText(candidate)),
                ClassName: className,
                ProcessId: (int)processId,
                OwnerHandle: owner,
                Relation: relation,
                Bounds: bounds,
                IsBlocking: candidate == enabledPopup));
        }

        return popups;
    }

    /// <summary>Why <paramref name="candidate"/> is a popup of <paramref name="window"/>, or null when it is not one.</summary>
    private PopupRelation? Relation(
        nint candidate, nint window, string className, uint processId, nint owner, long style, long exStyle,
        TargetProcesses target, ShellWindow shell, MainWindows mainWindows)
    {
        if (owner != nint.Zero)
        {
            var chain = WindowOwnerChain.SelfAndOwners(_windows, candidate);
            if (chain.Skip(1).Contains(window))
            {
                return PopupRelation.Owned;
            }

            // Owned by another window: the window at the top of its chain says whose it is. Another main window of the
            // process (a second Word or Explorer window) keeps its dialogs and popups; a hidden owner (a WinForms parking
            // window, a Delphi or Qt application window) or a popup-styled one ties it to no main window
            if (!OwnerLeavesItToTheProcess(chain.Count > 1 ? chain[^1] : owner, target, mainWindows))
            {
                return null;
            }

            if (target.Contains(processId))
            {
                return PopupRelation.SameProcess;
            }
        }
        else if (target.Contains(processId))
        {
            return IsProcessLevel(style, exStyle, className, mainWindows) ? PopupRelation.SameProcess : null;
        }

        return IsPopupStyle(style, exStyle) && !target.IsShell(shell) && target.IsChild(processId) ? PopupRelation.ChildProcess : null;
    }

    /// <summary>
    /// Whether the unowned window at the top of an owner chain (not the target) leaves the windows it owns to the process
    /// rules: it is hidden or popup-styled, or it is a dialog box of the target's process that counts as a popup itself. A
    /// visible app window does not: it is another main window, and its dialogs and popups are its own.
    /// </summary>
    private bool OwnerLeavesItToTheProcess(nint root, TargetProcesses target, MainWindows mainWindows)
    {
        if (!_windows.IsWindowVisible(root))
        {
            return true;
        }

        var style = (long)_windows.GetWindowLongPtr(root, NativeEnums.GWL_STYLE);
        var exStyle = (long)_windows.GetWindowLongPtr(root, NativeEnums.GWL_EXSTYLE);
        return IsPopupStyle(style, exStyle) ||
               (target.Contains(_windows.GetWindowThreadProcessId(root)) &&
                IsProcessLevel(style, exStyle, _windows.GetClassName(root) ?? string.Empty, mainWindows));
    }

    /// <summary>
    /// An unowned window of the target's process that is not tied to one of its main windows: popup-styled (a context menu,
    /// a dropdown, a palette, as the input guard counts them), or a dialog box (#32770) while the process shows no main
    /// window other than the target, since with two main windows nothing says whose dialog it is.
    /// </summary>
    private static bool IsProcessLevel(long style, long exStyle, string className, MainWindows mainWindows) =>
        IsPopupStyle(style, exStyle) ||
        (WindowClassification.IsDialogClass(className) && !mainWindows.OtherThanTargetExist);

    /// <summary>
    /// A window without the look of an app window: no title bar (or a tool window), and not forced into the taskbar with
    /// WS_EX_APPWINDOW (the input guard's rule).
    /// </summary>
    private static bool IsPopupStyle(long style, long exStyle)
    {
        var appWindow = ((style & WsCaption) == WsCaption && (exStyle & WsExToolWindow) == 0) || (exStyle & WsExAppWindow) != 0;
        return !appWindow;
    }

    /// <summary>The kind of a popup from its class and style; get_active_popups sharpens a plain popup with UI Automation.</summary>
    public static PopupKind Classify(string className, long style, long exStyle, bool owned)
    {
        if (string.Equals(className, MenuClass, StringComparison.Ordinal))
        {
            return PopupKind.Menu;
        }

        if (DropdownClasses.Contains(className))
        {
            return PopupKind.Dropdown;
        }

        if (TooltipClasses.Contains(className))
        {
            return PopupKind.Tooltip;
        }

        if (FlyoutClasses.Contains(className))
        {
            return PopupKind.Flyout;
        }

        if (WindowClassification.IsDialogClass(className))
        {
            return PopupKind.Dialog;
        }

        if ((style & WsCaption) == WsCaption)
        {
            return owned && (exStyle & WsExToolWindow) == 0 ? PopupKind.Dialog : PopupKind.Window;
        }

        return PopupKind.Popup;
    }

    /// <summary>A plain popup's kind from the control type of its UI Automation element (a Chromium menu is a Menu).</summary>
    public static PopupKind Sharpen(PopupKind kind, string? controlType) => kind != PopupKind.Popup
        ? kind
        : controlType switch
        {
            "Menu" or "MenuBar" => PopupKind.Menu,
            "List" or "ComboBox" or "Tree" => PopupKind.Dropdown,
            "ToolTip" => PopupKind.Tooltip,
            _ => kind
        };

    /// <inheritdoc />
    public bool IsInForeground(nint window)
    {
        try
        {
            var foreground = _windows.GetForegroundWindow();
            if (window == nint.Zero || foreground == nint.Zero)
            {
                return false;
            }

            if (foreground == window || WindowOwnerChain.SelfAndOwners(_windows, foreground).Contains(window))
            {
                return true;
            }

            var appProcess = UwpFrames.AppProcessId(_windows, window);
            return appProcess != 0 && UwpFrames.AppProcessId(_windows, foreground) == appProcess;
        }
        catch (Exception)
        {
            // Only the default scope depends on it: the window alone
            return false;
        }
    }

    /// <inheritdoc />
    public DesktopWindowSet GetDesktopWindows(nint first, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var windows = new List<nint>();
        if (first != nint.Zero && _windows.IsWindow(first))
        {
            windows.Add(first);
        }

        var selfProcess = _windows.GetCurrentProcessId();
        var names = new Dictionary<uint, string?>();
        var excluded = 0;
        var overLimit = 0;
        foreach (var candidate in TopLevelWindows())
        {
            if (candidate == first || !IsShown(candidate, out _))
            {
                continue;
            }

            var className = _windows.GetClassName(candidate) ?? string.Empty;
            var processId = _windows.GetWindowThreadProcessId(candidate);
            if (HelperWindowClasses.Contains(className) || processId == selfProcess)
            {
                continue;
            }

            if (IsHost(candidate) || !PolicyAllows(candidate, names))
            {
                excluded++;
                continue;
            }

            if (windows.Count >= limit)
            {
                overLimit++;
                continue;
            }

            windows.Add(candidate);
        }

        return new DesktopWindowSet(windows, excluded, overLimit);
    }

    private bool IsHost(nint window)
    {
        try
        {
            return _hostGuard?.IsHostWindow(window) == true;
        }
        catch (Exception)
        {
            // Fail safe: a window the guard cannot judge is left out
            return true;
        }
    }

    /// <summary>
    /// The target policy's verdict on reading a window: its process and, for a UWP frame, its app's, failing closed when the
    /// process cannot be named (<see cref="UiaTargets.ReadVerdict"/>). <paramref name="names"/> keeps the process names read.
    /// </summary>
    private bool PolicyAllows(nint window, Dictionary<uint, string?> names)
    {
        if (_policy == null)
        {
            return true;
        }

        string? NameOf(uint id)
        {
            if (!names.TryGetValue(id, out var name))
            {
                names[id] = name = _windows.GetProcessName(id);
            }

            return name;
        }

        return UiaTargets.ReadVerdict(_windows, _policy, window, NameOf).Allowed;
    }

    /// <inheritdoc />
    public async Task<ActivePopupsResult> GetActivePopupsAsync(nint window, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var named = window != nint.Zero;
        if (!named)
        {
            // The app in front: the window that owns the window in front, so a dialog in front lists as the app's popup
            var foreground = _windows.GetForegroundWindow();
            if (foreground == nint.Zero)
            {
                return new ActivePopupsResult(false, 0, null, false, null, Elapsed(stopwatch),
                    Error: "No window is in the foreground (the desktop or the lock screen has focus). Name the window.",
                    ErrorCode: UIElementErrorCode.WindowNotFound);
            }

            window = WindowOwnerChain.TopOwner(_windows, foreground);
        }

        if (!_windows.IsWindow(window))
        {
            return new ActivePopupsResult(false, (long)window, null, false, null, Elapsed(stopwatch),
                Error: "Invalid window handle: it is no window (it closed). Find the window again with find_windows.",
                ErrorCode: UIElementErrorCode.InvalidHandle);
        }

        // A window named of an app the user denies: none of its popups is listed or read
        if (named && UiaTargets.PolicyRefusal(_windows, _policy, window) is { } denied)
        {
            return new ActivePopupsResult(false, (long)window, null, false, null, Elapsed(stopwatch), Error: denied,
                ErrorCode: UIElementErrorCode.TargetDeniedByPolicy);
        }

        var title = EmptyToNull(_windows.GetWindowText(window));
        var foregroundNow = IsInForeground(window);

        // A popup is never named: one of the agent's host or of an app the user denies is neither listed nor read (as a
        // desktop search leaves such windows out), also when the window named is the host's
        var names = new Dictionary<uint, string?>();
        var marked = Marked(Popups(window, out var truncated), names);
        var popups = marked.Where(p => !p.IsAgentHost && !p.IsDeniedByPolicy).ToList();
        var excluded = marked.Count - popups.Count;
        var listed = popups.Select(p => new ActivePopup(
            Ref: null,
            Handle: (long)p.Handle,
            Kind: p.Kind,
            Title: p.Title,
            ClassName: p.ClassName,
            ProcessId: p.ProcessId,
            ProcessName: ProcessName((uint)p.ProcessId, names),
            OwnerHandle: p.OwnerHandle == nint.Zero ? null : (long)p.OwnerHandle,
            Bounds: p.Bounds,
            IsBlocking: p.IsBlocking ? true : null)).ToArray();

        if (popups.Count > 0 && _tree != null && _worker != null)
        {
            // Each popup's element read and registered on the worker; what was read before the deadline stays
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                await _worker.RunAsync(
                    UiaTargets.TargetProcessId(_windows, window),
                    token =>
                    {
                        ReadPopups(popups, listed, token);
                        return true;
                    },
                    deadline.Token,
                    StopGrace).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                // A popup that does not answer: the popups are still listed, without the refs not read in time
            }
            catch (TimeoutException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // A copy: a read the worker abandoned may still write into the array
        return new ActivePopupsResult(true, (long)window, title, foregroundNow, Snapshot(listed), Elapsed(stopwatch),
            Truncated: truncated, PopupsExcluded: excluded > 0 ? excluded : null);
    }

    /// <summary>The popups' elements, read on the worker: each one's ref and control type written into <paramref name="listed"/>.</summary>
    private void ReadPopups(IReadOnlyList<PopupWindow> popups, ActivePopup[] listed, CancellationToken token)
    {
        if (!_tree!.IsAvailable())
        {
            return;
        }

        var generation = _registry?.NextGeneration() ?? 0;
        for (var i = 0; i < popups.Count && !token.IsCancellationRequested; i++)
        {
            var popup = popups[i];
            if (UiaTargets.NotRespondingReason(_windows, popup.Handle) != null)
            {
                continue;
            }

            try
            {
                if (_tree.FromHandle(popup.Handle) is not { } node)
                {
                    continue;
                }

                var element = _tree.ReadElement(node);
                var elementRef = _registry?.Register(popup.Handle, element, new ElementOrigin(generation));
                var controlType = element.Properties.ControlType;
                Volatile.Write(ref listed[i], listed[i] with
                {
                    Ref = elementRef,
                    ControlType = controlType,
                    Kind = Sharpen(listed[i].Kind, controlType)
                });
            }
            catch (TimeoutException)
            {
                // The provider did not answer: the rest would wait as long
                return;
            }
            catch (Exception)
            {
                // It closed meanwhile, or its provider failed: listed without a ref
            }
        }
    }

    private static ActivePopup[] Snapshot(ActivePopup[] listed) =>
        listed.Select((_, i) => Volatile.Read(ref listed[i])).ToArray();

    /// <inheritdoc />
    public async Task<PopupWindow?> WaitForNewPopupAsync(
        nint window, IReadOnlySet<nint> before, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(before);
        var started = _clock.GetTimestamp();
        TargetProcesses? target = null;
        var targetRead = started;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The target's child processes (one process snapshot) are kept for a while, not read again every poll
            if (target == null || _clock.GetElapsedTime(targetRead) >= ChildProcessRefresh)
            {
                target = window != nint.Zero && _windows.IsWindow(window) ? new TargetProcesses(this, window) : null;
                targetRead = _clock.GetTimestamp();
            }

            // A tooltip that shows because the pointer rests on the control is not what the action opened
            if (Popups(window, out _, target).FirstOrDefault(p => p.Kind != PopupKind.Tooltip && !before.Contains(p.Handle)) is
                { } opened)
            {
                return opened;
            }

            var remaining = wait - _clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private string? ProcessName(uint processId, Dictionary<uint, string?> names)
    {
        if (!names.TryGetValue(processId, out var name))
        {
            try
            {
                name = _windows.GetProcessName(processId);
            }
            catch (Exception)
            {
                name = null;
            }

            names[processId] = name;
        }

        return name;
    }

    /// <summary>The top-level windows in z-order, front first, collected before any of them is read.</summary>
    private List<nint> TopLevelWindows()
    {
        var handles = new List<nint>();
        _windows.EnumWindows(hWnd =>
        {
            handles.Add(hWnd);
            return true;
        });
        return handles;
    }

    /// <summary>Visible, not cloaked, and with a size; <paramref name="bounds"/> is its visible frame.</summary>
    private bool IsShown(nint window, out BoundsDto? bounds)
    {
        bounds = null;
        if (!_windows.IsWindowVisible(window) || _windows.IsCloaked(window))
        {
            return false;
        }

        var rect = _windows.GetExtendedFrameBounds(window) ?? _windows.GetWindowRect(window);
        if (rect is not { } r || r.Right <= r.Left || r.Bottom <= r.Top)
        {
            return false;
        }

        bounds = new BoundsDto(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return true;
    }

    /// <summary>The enabled popup of a disabled window (GW_ENABLEDPOPUP): its modal dialog; zero when there is none.</summary>
    private nint EnabledPopupOf(nint window)
    {
        if (((long)_windows.GetWindowLongPtr(window, NativeEnums.GWL_STYLE) & WsDisabled) == 0)
        {
            return nint.Zero;
        }

        var popup = _windows.GetWindow(window, NativeEnums.GW_ENABLEDPOPUP);
        return popup == window ? nint.Zero : popup;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static double Elapsed(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);

    /// <summary>The desktop shell's windows: the desktop and the taskbars of the shell's process.</summary>
    private sealed class ShellWindow
    {
        private readonly nint _window;

        public ShellWindow(INativeWindowWrapper windows)
        {
            _window = windows.GetShellWindow();
            ProcessId = _window == nint.Zero ? 0 : windows.GetWindowThreadProcessId(_window);
        }

        /// <summary>The shell's process, or 0 when no shell runs.</summary>
        public uint ProcessId { get; }

        public bool Contains(nint window, string className, uint processId) =>
            WindowClassification.IsShellWindow(window, className, processId, _window, ProcessId);
    }

    /// <summary>
    /// Whether the target's process shows a main window other than the target: visible, uncloaked, unowned, with the look of
    /// an app window, and not a dialog box. Read once, when a dialog box first asks.
    /// </summary>
    private sealed class MainWindows(PopupService owner, nint target, TargetProcesses processes, IReadOnlyList<nint> topLevel)
    {
        private bool? _otherExists;

        public bool OtherThanTargetExist => _otherExists ??= topLevel.Any(IsOtherMainWindow);

        private bool IsOtherMainWindow(nint window)
        {
            var windows = owner._windows;
            if (window == target || !processes.Contains(windows.GetWindowThreadProcessId(window)) ||
                windows.GetWindow(window, NativeEnums.GW_OWNER) != nint.Zero || !owner.IsShown(window, out _))
            {
                return false;
            }

            var className = windows.GetClassName(window) ?? string.Empty;
            return !HelperWindowClasses.Contains(className) && !WindowClassification.IsDialogClass(className) &&
                   !IsPopupStyle((long)windows.GetWindowLongPtr(window, NativeEnums.GWL_STYLE),
                       (long)windows.GetWindowLongPtr(window, NativeEnums.GWL_EXSTYLE));
        }
    }

    /// <summary>The target's process (for a UWP frame its app's), and its direct children from one process snapshot.</summary>
    private sealed class TargetProcesses
    {
        private readonly PopupService _owner;
        private readonly uint _processId;
        private HashSet<uint>? _children;
        private bool? _isExplorer;

        public TargetProcesses(PopupService owner, nint window)
        {
            _owner = owner;
            _processId = UwpFrames.AppProcessId(owner._windows, window);
        }

        public bool Contains(uint processId) => processId != 0 && processId == _processId;

        /// <summary>The desktop shell's process, or any explorer.exe: its children are the programs the user started.</summary>
        public bool IsShell(ShellWindow shell)
        {
            if (_processId == 0 || _processId == shell.ProcessId)
            {
                return true;
            }

            _isExplorer ??= _owner._windows.GetProcessName(_processId) is { } name &&
                            string.Equals(TargetPolicySettings.NormalizeProcessName(name), "explorer", StringComparison.OrdinalIgnoreCase);
            return _isExplorer.Value;
        }

        /// <summary>
        /// Whether <paramref name="processId"/> is a direct child of the target's process: its parent in the snapshot, and
        /// not started before it (a child that started earlier has a reused parent id). Unknown start times count, as in
        /// the input guard's ancestor walk.
        /// </summary>
        public bool IsChild(uint processId)
        {
            if (_processId == 0 || processId == 0 || _owner._processes == null)
            {
                return false;
            }

            _children ??= ReadChildren();
            return _children.Contains(processId);
        }

        private HashSet<uint> ReadChildren()
        {
            var children = new HashSet<uint>();
            try
            {
                var processes = _owner._processes!.GetProcessList();
                if (processes == null || !processes.ContainsKey((int)_processId))
                {
                    return children;
                }

                var parentStart = _owner._processes.GetProcessStartTime((int)_processId);
                foreach (var (id, (parent, _)) in processes)
                {
                    if (parent != (int)_processId || id == parent)
                    {
                        continue;
                    }

                    var childStart = _owner._processes.GetProcessStartTime(id);
                    if (parentStart is { } p && childStart is { } c && c < p)
                    {
                        continue;
                    }

                    children.Add((uint)id);
                }
            }
            catch (Exception)
            {
                // The children are unknown: no window counts as theirs
            }

            return children;
        }
    }
}
