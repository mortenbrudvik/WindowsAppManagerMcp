using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The input guard (see <see cref="IInputTargetGuard"/> for the rules and their order). It only reads
/// window, process and desktop state, except for bringing an explicit keyboard target to the front through
/// <see cref="IForegroundService.EnsureForeground"/>.
/// </summary>
public sealed class InputTargetGuard : IInputTargetGuard
{
    /// <summary>WS_DISABLED: the window takes no input, usually because a modal dialog is open (IsWindowEnabled is false).</summary>
    private const long WsDisabled = 0x08000000;

    /// <summary>WS_CAPTION (WS_BORDER | WS_DLGFRAME): a title bar.</summary>
    private const long WsCaption = 0x00C00000;

    private const long WsVisible = 0x10000000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    private readonly INativeWindowWrapper _windows;
    private readonly INativeHitTestWrapper _hitTest;
    private readonly INativeThreadInputWrapper _threads;
    private readonly INativeProcessWrapper _processes;
    private readonly IWindowService _windowService;
    private readonly IForegroundService _foreground;
    private readonly IProcessSecurityProbe _security;
    private readonly IHostGuard _hostGuard;
    private readonly ITargetPolicy _policy;
    private readonly InputGuardOptions _options;

    public InputTargetGuard(
        INativeWindowWrapper windows,
        INativeHitTestWrapper hitTest,
        INativeThreadInputWrapper threads,
        INativeProcessWrapper processes,
        IWindowService windowService,
        IForegroundService foreground,
        IProcessSecurityProbe security,
        IHostGuard hostGuard,
        ITargetPolicy policy,
        InputGuardOptions options)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _hitTest = hitTest ?? throw new ArgumentNullException(nameof(hitTest));
        _threads = threads ?? throw new ArgumentNullException(nameof(threads));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _security = security ?? throw new ArgumentNullException(nameof(security));
        _hostGuard = hostGuard ?? throw new ArgumentNullException(nameof(hostGuard));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public InputGuardResult CheckKeyboard(InputTarget target, KeyboardInputKind kind, IReadOnlyList<InputEvent>? keys)
    {
        // 1. The request itself: a chord that closes, quits or locks needs to know which window it is for
        if (!target.HasWindow && kind == KeyboardInputKind.Keys && keys != null &&
            _options.FindDestructiveChord(keys) is { } chord)
        {
            return InputGuardResult.Refuse(InputErrorCode.DestructiveChordNeedsTarget,
                $"The keys contain {chord.Spec}, a chord that can close, quit or lock the window it reaches. Without " +
                "windowHandle it would go to whatever window is in front, often the agent's own terminal. Nothing was sent. " +
                "Pass windowHandle with the window you mean. To close a window, use close_window with its handle.",
                target, DescribeForeground());
        }

        // Keys after a chord that hands the keyboard to another window would reach a window nobody checked
        if (kind == KeyboardInputKind.Keys && keys != null && _options.FindKeysAfterFocusChange(keys) is { } focusChord)
        {
            return InputGuardResult.Refuse(InputErrorCode.KeysAfterFocusChange,
                $"The keys go on after {focusChord}, which hands the keyboard to another window: it switches or closes the " +
                "window, or opens the Start menu or the window menu. The keys after it would reach a window the guard never " +
                $"checked, such as the agent's own terminal. Nothing was sent. Make {focusChord} the last key of one send_keys " +
                "call, then send the rest in a new call (with windowHandle), which is checked again.",
                target, DescribeForeground());
        }

        // 2. Desktop probes
        if (DesktopRefusal(target) is { } desktop)
        {
            return desktop;
        }

        var action = kind == KeyboardInputKind.Text ? "typed" : "sent";
        if (target.HasWindow)
        {
            // 3. The target itself
            if (TargetRefusal(target, out var handle, out var targetRoot) is { } refused)
            {
                return refused;
            }

            // 4. The receiving window: bring the target to the front, then it must be in front
            var outcome = _foreground.EnsureForeground(handle);
            if (!outcome.IsForeground)
            {
                if (outcome.ErrorCode == WindowErrorCode.InvalidHandle)
                {
                    return InvalidHandle(target, handle);
                }

                var front = outcome.ForegroundHandle == nint.Zero ? null : Describe(outcome.ForegroundHandle);
                return InputGuardResult.Refuse(InputErrorCode.TargetNotForeground,
                    $"Window {handle} could not be brought to the foreground ({outcome.ErrorCode}), so nothing was {action}; " +
                    $"{Name(front)} is in front. {outcome.Error} Call focus_window for details, or ask the user to bring the window to the front.",
                    target, front);
            }

            var foreground = _windows.GetForegroundWindow();
            if (foreground == nint.Zero || !_foreground.IsInFront(handle))
            {
                var front = foreground == nint.Zero ? null : Describe(foreground);
                return InputGuardResult.Refuse(InputErrorCode.TargetNotForeground,
                    $"Window {handle} came to the foreground but lost it again; {Name(front)} is in front now. Nothing was {action}. Try again.",
                    target, front);
            }

            var receiving = Read(foreground);
            if (foreground != targetRoot)
            {
                if (WindowRefusal(foreground, receiving, target, action, targetRoot) is { } receivingRefused)
                {
                    return receivingRefused;
                }

                // A modal dialog of a dialog the target owns: keys could answer it
                if (IsModalOver(foreground, targetRoot))
                {
                    return ModalRefusal(target, receiving, $"Nothing was {action}.");
                }
            }

            // 5. Letters typed into an active menu select its items
            if (kind == KeyboardInputKind.Text && (outcome.InMenuMode || IsInMenuMode(foreground)))
            {
                return MenuRefusal(target, receiving);
            }

            return InputGuardResult.Allow(target, foreground, receiving.Summary, anchor: handle);
        }

        // No target: the keys go to the foreground window, whichever it is
        var current = _windows.GetForegroundWindow();
        if (current == nint.Zero)
        {
            return InputGuardResult.Refuse(InputErrorCode.TargetNotForeground,
                $"No window is in the foreground, so the keys have nowhere to go. Nothing was {action}. " +
                "Pass windowHandle with the window you mean; the server brings it to the front first.",
                target);
        }

        var window = Read(current);
        if (WindowRefusal(current, window, target, action, targetRoot: nint.Zero) is { } windowRefused)
        {
            return windowRefused;
        }

        if (kind == KeyboardInputKind.Text && IsInMenuMode(current))
        {
            return MenuRefusal(target, window);
        }

        return InputGuardResult.Allow(target, current, window.Summary, anchor: current);
    }

    public InputGuardResult RecheckKeyboard(InputGuardResult approved)
    {
        ArgumentNullException.ThrowIfNull(approved);

        var foreground = _windows.GetForegroundWindow();
        if (foreground != nint.Zero && foreground == approved.Receiving)
        {
            return approved;
        }

        // Another window is in front: carry on only into a window of the same target (a dialog it opened)
        if (foreground == nint.Zero || approved.Anchor == nint.Zero || !_foreground.IsInFront(approved.Anchor))
        {
            var front = foreground == nint.Zero ? null : Describe(foreground);
            return InputGuardResult.Refuse(InputErrorCode.TargetLostFocus,
                $"The foreground window changed: {Name(front)} is in front now, not {Name(approved.ReceivingWindow)} " +
                "or a window it owns. Nothing more was sent.",
                approved.Target, approved.ReceivingWindow);
        }

        var window = Read(foreground);
        var anchorRoot = RootOf(approved.Anchor);
        if (WindowRefusal(foreground, window, approved.Target, "sent", approved.Target.HasWindow ? anchorRoot : nint.Zero) is { } refused)
        {
            return refused;
        }

        // A modal dialog opened while typing (a "Save changes?" prompt): letters and Enter could press its buttons
        if (IsModalOver(foreground, anchorRoot))
        {
            return ModalRefusal(approved.Target, window, "Nothing more was sent.");
        }

        return InputGuardResult.Allow(approved.Target, foreground, window.Summary, approved.Anchor);
    }

    public InputGuardResult CheckMouse(int x, int y, InputTarget target)
    {
        if (DesktopRefusal(target) is { } desktop)
        {
            return desktop;
        }

        var hit = RootAt(x, y);
        if (target.HasWindow)
        {
            if (TargetRefusal(target, out _, out var targetRoot) is { } refused)
            {
                return refused;
            }

            return CheckHitForTarget(x, y, hit, target, targetRoot);
        }

        return CheckHitWithoutTarget(x, y, hit, target);
    }

    public InputGuardResult RecheckMouse(int x, int y, InputGuardResult approved)
    {
        ArgumentNullException.ThrowIfNull(approved);

        var hit = RootAt(x, y);
        if (hit == approved.Receiving)
        {
            return approved;
        }

        return approved.Target.HasWindow
            ? CheckHitForTarget(x, y, hit, approved.Target, approved.Anchor)
            : CheckHitWithoutTarget(x, y, hit, approved.Target);
    }

    public WindowSummary? DescribeForeground()
    {
        var foreground = _windows.GetForegroundWindow();
        return foreground == nint.Zero ? null : Describe(foreground);
    }

    /// <summary>The window at the point must be the target's: its top-level window, a window it owns, or a popup of its process.</summary>
    private InputGuardResult CheckHitForTarget(int x, int y, nint hit, InputTarget target, nint targetRoot)
    {
        if (hit == nint.Zero || !BelongsToTarget(hit, targetRoot))
        {
            var covering = hit == nint.Zero ? null : Describe(hit);
            return InputGuardResult.Refuse(InputErrorCode.TargetOccluded,
                $"The point ({x}, {y}) is on {Name(covering)}, not on window {targetRoot} or a popup of it, so nothing was sent. " +
                "Bring the window to the front with focus_window, or take fresh coordinates from get_ui_elements, then try again.",
                target, covering);
        }

        var window = Read(hit);
        if (hit != targetRoot && WindowRefusal(hit, window, target, "sent", targetRoot) is { } refused)
        {
            return refused;
        }

        return InputGuardResult.Allow(target, hit, window.Summary, anchor: targetRoot);
    }

    private InputGuardResult CheckHitWithoutTarget(int x, int y, nint hit, InputTarget target)
    {
        if (hit == nint.Zero)
        {
            // Fails closed, as keys do when no window is in front
            return InputGuardResult.Refuse(InputErrorCode.TargetOccluded,
                $"No window could be found at ({x}, {y}), so it is not known where the input would go. Nothing was sent. " +
                "Take fresh coordinates from get_ui_elements or a screenshot, and pass windowHandle with the window you mean.",
                target);
        }

        var window = Read(hit);
        return WindowRefusal(hit, window, target, "sent", targetRoot: nint.Zero)
               ?? InputGuardResult.Allow(target, hit, window.Summary, anchor: hit);
    }

    /// <summary>
    /// True when the top-level window <paramref name="hit"/> at the point takes input for the target: it is
    /// the target's top-level window, a window the target owns (GW_OWNER chain), or a popup (no title bar,
    /// not an app window) of the target's process or of a program the target started itself (a direct
    /// child process). The last rule covers WebView2 and Chromium dropdowns and menus, which the WebView2
    /// browser process (msedgewebview2.exe, started by the app) creates unowned, and IME windows. It is
    /// limited to popups so that another main window of the same process (a second Notepad, Explorer or
    /// browser window, which share one process) still counts as covering the target. The desktop and the
    /// taskbars never count as a popup, and the child-process part never applies to the desktop shell
    /// (explorer.exe), which starts almost every program the user opens.
    /// </summary>
    private bool BelongsToTarget(nint hit, nint targetRoot)
    {
        if (hit == targetRoot || WindowOwnerChain.IsOwnedBy(_windows, hit, targetRoot))
        {
            return true;
        }

        if (!IsPopup(hit) || IsShellWindow(hit))
        {
            return false;
        }

        var hitProcess = (int)_windows.GetWindowThreadProcessId(hit);
        var targetProcess = (int)_windows.GetWindowThreadProcessId(targetRoot);
        if (hitProcess == 0 || targetProcess == 0)
        {
            return false;
        }

        if (hitProcess == targetProcess)
        {
            return true;
        }

        if (IsShellProcess(targetProcess))
        {
            return false;
        }

        var ancestors = _processes.GetAncestorsOf(hitProcess);
        return ancestors is { Count: > 0 } && ancestors[0] == targetProcess;
    }

    /// <summary>The desktop (the shell window, Progman, WorkerW) or a taskbar.</summary>
    private bool IsShellWindow(nint window)
    {
        var shellWindow = _windows.GetShellWindow();
        return (shellWindow != nint.Zero && window == shellWindow) ||
               WindowClassification.IsShellWindowClass(_windows.GetClassName(window));
    }

    /// <summary>
    /// The desktop shell's process (the shell window's, or the one the host guard recorded), or any other
    /// explorer.exe, which runs File Explorer windows in a separate process and starts what they open.
    /// </summary>
    private bool IsShellProcess(int processId)
    {
        var shellWindow = _windows.GetShellWindow();
        if ((shellWindow != nint.Zero && (int)_windows.GetWindowThreadProcessId(shellWindow) == processId) ||
            _hostGuard.Host?.ShellProcessId == processId)
        {
            return true;
        }

        var name = _windows.GetProcessName((uint)processId);
        return name != null &&
               string.Equals(TargetPolicySettings.NormalizeProcessName(name), "explorer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A window without the look of an app window: no title bar (or a tool window), and not forced into
    /// the taskbar with WS_EX_APPWINDOW.
    /// </summary>
    private bool IsPopup(nint window)
    {
        var style = (long)_windows.GetWindowLongPtr(window, NativeEnums.GWL_STYLE);
        var exStyle = (long)_windows.GetWindowLongPtr(window, NativeEnums.GWL_EXSTYLE);
        var appWindow = ((style & WsCaption) == WsCaption && (exStyle & WsExToolWindow) == 0) || (exStyle & WsExAppWindow) != 0;
        return !appWindow;
    }

    /// <summary>The checks of an explicit target: a window; then probes, policy and host; then not disabled by a modal dialog.</summary>
    private InputGuardResult? TargetRefusal(InputTarget target, out nint handle, out nint root)
    {
        handle = target.WindowHandle ?? nint.Zero;
        root = nint.Zero;
        if (handle == nint.Zero || !_windows.IsWindow(handle))
        {
            return InvalidHandle(target, handle);
        }

        root = RootOf(handle);
        var window = Read(root);
        if (WindowRefusal(root, window, target, "sent", root) is { } refused)
        {
            return refused;
        }

        if (IsDisabled(root))
        {
            var popup = BlockingPopupOf(root);
            var blocking = popup == nint.Zero ? null : Describe(popup);
            return InputGuardResult.Refuse(InputErrorCode.BlockedByModal,
                $"Window {root} is disabled by {(blocking == null ? "a modal dialog" : "its modal dialog " + Name(blocking))}: it takes no " +
                "input until the dialog closes, so nothing was sent. Deal with the dialog first (pass its handle as windowHandle), " +
                "or ask the user.",
                target, window.Summary, blocking);
        }

        return null;
    }

    /// <summary>
    /// The probes, policy and host checks of a window the input would reach. The host check is skipped only
    /// for an explicit target with allowHostTarget, and only when the window is the target's top-level
    /// window <paramref name="targetRoot"/> or a window it owns: a popup that is accepted only because it
    /// belongs to the target's process or a child process is still checked.
    /// </summary>
    private InputGuardResult? WindowRefusal(nint hWnd, WindowRead window, InputTarget target, string action, nint targetRoot)
    {
        // Probes: UIPI drops input to a more privileged process without an error
        foreach (var processId in new[] { (int)_windows.GetWindowThreadProcessId(hWnd), window.Info?.ProcessId ?? 0 }
                     .Where(id => id > 0).Distinct())
        {
            var integrity = _security.GetIntegrity(processId);
            if (integrity.IsMorePrivilegedThan(_security.ServerIntegrity))
            {
                return InputGuardResult.Refuse(InputErrorCode.TargetElevated,
                    $"{Name(window.Summary)} runs at a higher integrity level ({integrity.Level}) than this server " +
                    $"({_security.ServerIntegrity.Level}), for example as administrator, so Windows would silently drop the input. " +
                    $"Nothing was {action}. Ask the user to do this step, or to run the server elevated.",
                    target, window.Summary);
            }
        }

        // Policy: set by the user in the server's environment
        var verdict = window.Info is { } info
            ? _policy.Evaluate(info)
            : _policy.Evaluate([window.Summary.ProcessName]);
        if (!verdict.Allowed)
        {
            return InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy,
                $"{Name(window.Summary)} is refused by the server's target policy: {verdict.Reason}. Nothing was {action}. " +
                "The policy is set by the user in the server's environment and no tool argument changes it; ask the user to do this step.",
                target, window.Summary);
        }

        // Host: the agent's own terminal, editor or chat app
        var explicitHost = target.HasWindow && target.AllowHostTarget && targetRoot != nint.Zero &&
                           (hWnd == targetRoot || WindowOwnerChain.IsOwnedBy(_windows, hWnd, targetRoot));
        if (!explicitHost && _hostGuard.IsHostWindow(hWnd))
        {
            var hint = target.HasWindow && target.AllowHostTarget
                ? "allowHostTarget lifts this only for the window named in windowHandle and the windows it owns, and this window " +
                  "is neither. Pass the host window's own handle as windowHandle, and only if the user explicitly asked for input to it."
                : target.HasWindow
                ? "It belongs to the agent's host, and allowHostTarget was not set. Pass allowHostTarget=true only if the user explicitly asked for input to that window."
                :"Pass windowHandle with the window you mean; the server brings it to the front for keys and checks that the point is on it for clicks. " +
                  "To act on the host window itself, pass its handle as windowHandle with allowHostTarget=true, and only if the user explicitly asked for it." +
                  (target.AllowHostTarget ? " allowHostTarget has no effect without windowHandle." : string.Empty);
            return InputGuardResult.Refuse(InputErrorCode.TargetIsAgentHost,
                $"The input would reach {Name(window.Summary)}, the agent's own host: the terminal, editor or chat app this server " +
                $"was started from, or a window it owns. Nothing was {action}. {hint}",
                target, window.Summary);
        }

        return null;
    }

    private InputGuardResult? DesktopRefusal(InputTarget target)
    {
        if (_security.IsSessionLocked())
        {
            return InputGuardResult.Refuse(InputErrorCode.SessionLocked,
                "The session is locked, so input cannot reach any window. Nothing was sent. Ask the user to unlock it, then try again.",
                target);
        }

        if (!_security.IsInputDesktopAvailable())
        {
            return InputGuardResult.Refuse(InputErrorCode.DesktopUnavailable,
                "The input desktop cannot be reached: a UAC prompt or the Ctrl+Alt+Del screen is shown, or the session is " +
                "disconnected. Nothing was sent. Wait until the user has dealt with it, then try again.",
                target);
        }

        return null;
    }

    private InputGuardResult MenuRefusal(InputTarget target, WindowRead window) =>
        InputGuardResult.Refuse(InputErrorCode.TargetInMenuMode,
            $"A menu is active in {Name(window.Summary)}, so typed letters would select menu items. Nothing was typed. " +
            "Close the menu first (send_keys {ESC} with windowHandle) or click into the text field, then type again.",
            target, window.Summary);

    private static InputGuardResult ModalRefusal(InputTarget target, WindowRead dialog, string sent) =>
        InputGuardResult.Refuse(InputErrorCode.BlockedByModal,
            $"{Name(dialog.Summary)} is a modal dialog: the window it belongs to is disabled until it closes, and keys could " +
            $"answer it (Enter, Esc or a letter can press its buttons). {sent} Deal with the dialog first (pass its handle as " +
            "windowHandle), or ask the user.",
            target, dialog.Summary, dialog.Summary);

    /// <summary>
    /// True when <paramref name="window"/> is a modal dialog within <paramref name="anchorRoot"/>'s windows: one
    /// of its owners, up to and including <paramref name="anchorRoot"/>, is disabled. A modeless dialog leaves
    /// its owner enabled. The anchor itself is never a modal dialog over itself, even when it is a modal
    /// dialog of another window (a target that is a Save As dialog).
    /// </summary>
    private bool IsModalOver(nint window, nint anchorRoot)
    {
        if (window == anchorRoot)
        {
            return false;
        }

        foreach (var owner in WindowOwnerChain.SelfAndOwners(_windows, window).Skip(1))
        {
            if (IsDisabled(owner))
            {
                return true;
            }

            if (owner == anchorRoot)
            {
                break;
            }
        }

        return false;
    }

    private InputGuardResult InvalidHandle(InputTarget target, nint handle) =>
        InputGuardResult.Refuse(InputErrorCode.InvalidHandle,
            $"windowHandle {handle} is not a window (it may have closed). Nothing was sent. Get a current handle from find_windows.",
            target);

    /// <summary>
    /// The enabled window that a disabled window's modal dialog is: GW_ENABLEDPOPUP, or else the first
    /// visible, enabled top-level window (in Z order) that the window owns through the owner chain, which
    /// also finds WPF and WinForms dialogs. Zero when there is none.
    /// </summary>
    private nint BlockingPopupOf(nint window)
    {
        var popup = _windows.GetWindow(window, NativeEnums.GW_ENABLEDPOPUP);
        if (popup != nint.Zero && popup != window)
        {
            return popup;
        }

        var candidates = new List<nint>();
        _windows.EnumWindows(hWnd =>
        {
            candidates.Add(hWnd);
            return true;
        });

        return candidates.FirstOrDefault(hWnd =>
            hWnd != window &&
            (_windows.GetWindowLongPtr(hWnd, NativeEnums.GWL_STYLE) & WsVisible) != 0 &&
            !IsDisabled(hWnd) &&
            WindowOwnerChain.IsOwnedBy(_windows, hWnd, window));
    }

    private bool IsDisabled(nint window) => (_windows.GetWindowLongPtr(window, NativeEnums.GWL_STYLE) & WsDisabled) != 0;

    private bool IsInMenuMode(nint window) =>
        window != nint.Zero && _threads.GetGuiThreadInfo(_threads.GetWindowThreadId(window)) is { IsInMenuMode: true };

    private nint RootOf(nint handle)
    {
        var root = _windows.GetAncestor(handle, WindowAncestor.Root);
        return root == nint.Zero ? handle : root;
    }

    /// <summary>The top-level window at a screen point (WindowFromPoint, then GA_ROOT), or zero.</summary>
    private nint RootAt(int x, int y)
    {
        var hit = _hitTest.WindowFromPoint(x, y);
        if (hit == nint.Zero)
        {
            return nint.Zero;
        }

        var root = _hitTest.GetRootWindow(hit);
        return root == nint.Zero ? hit : root;
    }

    private WindowSummary Describe(nint hWnd) => Read(hWnd).Summary;

    /// <summary>
    /// Reads a window as the window tools report it (for a UWP frame, the app's own process), or from the
    /// window wrapper when it cannot be read that way.
    /// </summary>
    private WindowRead Read(nint hWnd)
    {
        var info = _windowService.GetWindowInfo(hWnd);
        if (info != null)
        {
            return new WindowRead(info, new WindowSummary(hWnd, info.Title, info.ProcessName, info.ProcessId));
        }

        var processId = _windows.GetWindowThreadProcessId(hWnd);
        var processName = processId == 0 ? null : _windows.GetProcessName(processId);
        return new WindowRead(null, new WindowSummary(hWnd, _windows.GetWindowText(hWnd) ?? string.Empty,
            processName ?? "Unknown", (int)processId));
    }

    private static string Name(WindowSummary? window) => window switch
    {
        null => "no window",
        { Title.Length: > 0 } => $"'{window.Title}' ({window.ProcessName}, handle {window.Handle})",
        _ => $"an untitled window of {window.ProcessName} (handle {window.Handle})"
    };

    private sealed record WindowRead(WindowInfo? Info, WindowSummary Summary);
}
