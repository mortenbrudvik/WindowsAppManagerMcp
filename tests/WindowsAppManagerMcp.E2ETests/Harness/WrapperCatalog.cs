using System.Reflection;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>What a native wrapper member does, as far as the E2E guard is concerned.</summary>
public enum WrapperCallKind
{
    /// <summary>Reads desktop, window, monitor or process state and changes nothing.</summary>
    Read,

    /// <summary>Changes a window: moves, sizes, shows or activates it, or posts it a message.</summary>
    WindowAction,

    /// <summary>Sends keyboard or mouse input, or moves the cursor.</summary>
    Input,

    /// <summary>Closes or terminates the process behind a process handle.</summary>
    ProcessAction,

    /// <summary>Starts a process.</summary>
    ProcessStart,

    /// <summary>
    /// Attaches or detaches the calling thread's input queue to or from another thread's
    /// (AttachThreadInput). It sends no input and changes no window by itself, but while attached the
    /// two threads share keyboard state, focus and the active window. The window calls made while
    /// attached are window actions, checked on their own.
    /// </summary>
    ThreadInputAttach,

    /// <summary>
    /// Registers or frees a system-wide hotkey for the calling thread, or runs or stops that thread's message
    /// loop (the user's pause hotkey), or opens or marks the named objects every server on the desktop shares
    /// (the shared pause, the mark of the server that holds the chord). It sends no input and changes no
    /// window, but while it is registered the chord no longer reaches any other application, and the shared
    /// pause pauses the servers the user runs, so no E2E tier allows it.
    /// </summary>
    GlobalHotkey
}

/// <summary>
/// One classified wrapper member. For a <see cref="WrapperCallKind.WindowAction"/>,
/// <see cref="WindowParameters"/> names every parameter that is a window handle (all of them must name
/// fixture windows) and <see cref="OtherHandleParameters"/> names the handle-sized parameters that are
/// not windows (message parameters), so that every nint parameter is accounted for.
/// </summary>
public sealed record WrapperMember(
    WrapperCallKind Kind,
    IReadOnlyList<string> WindowParameters,
    IReadOnlyList<string> OtherHandleParameters)
{
    public static WrapperMember Read { get; } = new(WrapperCallKind.Read, [], []);

    public static WrapperMember Input { get; } = new(WrapperCallKind.Input, [], []);

    public static WrapperMember ProcessAction { get; } = new(WrapperCallKind.ProcessAction, [], []);

    public static WrapperMember ProcessStart { get; } = new(WrapperCallKind.ProcessStart, [], []);

    public static WrapperMember ThreadInputAttach { get; } = new(WrapperCallKind.ThreadInputAttach, [], []);

    public static WrapperMember GlobalHotkey { get; } = new(WrapperCallKind.GlobalHotkey, [], []);

    public static WrapperMember WindowAction(params string[] windowParameters) =>
        new(WrapperCallKind.WindowAction, windowParameters, []);
}

/// <summary>
/// The explicit classification of every member of every native wrapper interface of the server. The
/// guard looks members up here; a member that is not listed is refused in every tier, so a member a
/// later change adds (even one with a read-like name) is refused until someone classifies it.
/// WrapperCatalogTests fail when a wrapper member of the server is missing here.
/// </summary>
public static class WrapperCatalog
{
    private static readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, WrapperMember>> Catalog =
        new Dictionary<Type, IReadOnlyDictionary<string, WrapperMember>>
        {
            [typeof(INativeWindowWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeWindowWrapper.EnumWindows)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.IsWindowVisible)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.IsWindow)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.IsIconic)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.IsZoomed)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetWindowText)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetWindowThreadProcessId)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetWindowRect)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetForegroundWindow)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetCurrentProcessId)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetProcessName)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetAncestor)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetShellWindow)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.EnumChildWindows)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetClassName)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetWindowLongPtr)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetWindow)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.IsCloaked)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetExtendedFrameBounds)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.IsHungAppWindow)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetDpiForWindow)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.SetWindowPos)] = WrapperMember.WindowAction("hWnd"),
                [nameof(INativeWindowWrapper.ShowWindow)] = WrapperMember.WindowAction("hWnd"),
                [nameof(INativeWindowWrapper.SetForegroundWindow)] = WrapperMember.WindowAction("hWnd"),
                [nameof(INativeWindowWrapper.BringWindowToTop)] = WrapperMember.WindowAction("hWnd"),
                [nameof(INativeWindowWrapper.PostMessage)] = WrapperMember.WindowAction("hWnd") with
                {
                    OtherHandleParameters = ["wParam", "lParam"]
                }
            },
            [typeof(INativeInputWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeInputWrapper.GetCursorPos)] = WrapperMember.Read,
                // GetAsyncKeyState: whether a key or button is down (held modifiers, stuck-input recovery)
                [nameof(INativeInputWrapper.IsKeyDown)] = WrapperMember.Read,
                [nameof(INativeInputWrapper.SetCursorPos)] = WrapperMember.Input,
                [nameof(INativeInputWrapper.SendInput)] = WrapperMember.Input
            },
            [typeof(INativeHotkeyWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeHotkeyWrapper.GetCurrentThreadId)] = WrapperMember.Read,
                // The rest exists only for the pause hotkey's own thread; refused in every tier
                [nameof(INativeHotkeyWrapper.CreateMessageQueue)] = WrapperMember.GlobalHotkey,
                [nameof(INativeHotkeyWrapper.RegisterHotKey)] = WrapperMember.GlobalHotkey,
                [nameof(INativeHotkeyWrapper.UnregisterHotKey)] = WrapperMember.GlobalHotkey,
                [nameof(INativeHotkeyWrapper.WaitForMessage)] = WrapperMember.GlobalHotkey,
                [nameof(INativeHotkeyWrapper.PostStopMessage)] = WrapperMember.GlobalHotkey,
                // The shared pause is the one the user's own servers follow: setting it would pause them
                [nameof(INativeHotkeyWrapper.OpenDesktopPause)] = WrapperMember.GlobalHotkey,
                [nameof(INativeHotkeyWrapper.MarkChordHolder)] = WrapperMember.GlobalHotkey,
                // Opens the mark only to see whether it exists, and closes it again
                [nameof(INativeHotkeyWrapper.IsChordHeldByAServer)] = WrapperMember.Read
            },
            [typeof(IDesktopPauseFlag)] = new Dictionary<string, WrapperMember>
            {
                // The shared pause that OpenDesktopPause hands out: reading it and waiting on it change nothing, but
                // setting or resetting it pauses or resumes every server the user runs on the desktop
                ["get_" + nameof(IDesktopPauseFlag.IsSet)] = WrapperMember.Read,
                ["get_" + nameof(IDesktopPauseFlag.WaitHandle)] = WrapperMember.Read,
                [nameof(IDesktopPauseFlag.Set)] = WrapperMember.GlobalHotkey,
                [nameof(IDesktopPauseFlag.Reset)] = WrapperMember.GlobalHotkey
            },
            [typeof(IUIAutomationWrapper)] = new Dictionary<string, WrapperMember>
            {
                // The element comes back as an object the services only read: acting UIA pattern calls
                // outside the Native wrappers are forbidden by NativeCallGuardTests.
                [nameof(IUIAutomationWrapper.TryGetElementFromHandle)] = WrapperMember.Read,
                [nameof(IUIAutomationWrapper.GetBrowserUrl)] = WrapperMember.Read,
                [nameof(IUIAutomationWrapper.GetWindowTitle)] = WrapperMember.Read,
                [nameof(IUIAutomationWrapper.GetBrowserContent)] = WrapperMember.Read,
                [nameof(IUIAutomationWrapper.IsAvailable)] = WrapperMember.Read
            },
            [typeof(IScreenCaptureWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(IScreenCaptureWrapper.CaptureScreenRegion)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.CaptureWindow)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.CaptureWindowFromScreen)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.GetVirtualScreenBounds)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.IsValidWindow)] = WrapperMember.Read
            },
            [typeof(INativeSystemInfoWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeSystemInfoWrapper.GetDpiAwareness)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.GetForegroundLockTimeout)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.GetSessionId)] = WrapperMember.Read
            },
            [typeof(INativeMonitorWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeMonitorWrapper.EnumerateMonitors)] = WrapperMember.Read,
                // find_windows looks up the monitor of every window, fixture or not
                [nameof(INativeMonitorWrapper.MonitorFromWindow)] = WrapperMember.Read,
                [nameof(INativeMonitorWrapper.MonitorFromPoint)] = WrapperMember.Read
            },
            [typeof(ITcpPortOwnerResolver)] = new Dictionary<string, WrapperMember>
            {
                [nameof(ITcpPortOwnerResolver.GetListeningProcessId)] = WrapperMember.Read
            },
            [typeof(INativeProcessWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeProcessWrapper.StartProcess)] = WrapperMember.ProcessStart,
                // Runs where.exe: a process start, even though it only looks something up
                [nameof(INativeProcessWrapper.FindWithWhereExe)] = WrapperMember.ProcessStart,
                // Opening a process only reads; the handle it returns is guarded in turn
                [nameof(INativeProcessWrapper.OpenProcess)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.IsAncestorOfCurrentProcess)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetAncestorProcesses)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetAncestorsOf)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetDescendantsOf)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetProcessStartTime)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetConsoleWindow)] = WrapperMember.Read
            },
            [typeof(INativeThreadInputWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeThreadInputWrapper.GetCurrentThreadId)] = WrapperMember.Read,
                [nameof(INativeThreadInputWrapper.GetWindowThreadId)] = WrapperMember.Read,
                [nameof(INativeThreadInputWrapper.GetGuiThreadInfo)] = WrapperMember.Read,
                // Shares the calling thread's input with another thread; refused in the no-input tier
                [nameof(INativeThreadInputWrapper.AttachThreadInput)] = WrapperMember.ThreadInputAttach
            },
            [typeof(INativeHitTestWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeHitTestWrapper.WindowFromPoint)] = WrapperMember.Read,
                [nameof(INativeHitTestWrapper.GetRootWindow)] = WrapperMember.Read
            },
            [typeof(INativeSecurityWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeSecurityWrapper.GetProcessCreationTime)] = WrapperMember.Read,
                // Opens the process token for TOKEN_QUERY only
                [nameof(INativeSecurityWrapper.QueryProcessToken)] = WrapperMember.Read,
                // Opens the input desktop for DESKTOP_READOBJECTS only; the server never switches desktops
                [nameof(INativeSecurityWrapper.QueryInputDesktop)] = WrapperMember.Read,
                [nameof(INativeSecurityWrapper.GetThreadDesktopName)] = WrapperMember.Read,
                [nameof(INativeSecurityWrapper.QuerySessionState)] = WrapperMember.Read
            },
            [typeof(IUiaTree)] = new Dictionary<string, WrapperMember>
            {
                // Tree reads only: acting UIA pattern calls are forbidden outside the Native wrappers by
                // NativeCallGuardTests, and IUiaTree exposes none
                [nameof(IUiaTree.IsAvailable)] = WrapperMember.Read,
                [nameof(IUiaTree.FromHandle)] = WrapperMember.Read,
                [nameof(IUiaTree.FromPoint)] = WrapperMember.Read,
                [nameof(IUiaTree.GetFocusedElement)] = WrapperMember.Read,
                [nameof(IUiaTree.GetParent)] = WrapperMember.Read,
                [nameof(IUiaTree.GetChildren)] = WrapperMember.Read,
                [nameof(IUiaTree.Read)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadInteraction)] = WrapperMember.Read,
                [nameof(IUiaTree.IsInteractable)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadIdentity)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadValue)] = WrapperMember.Read
            },
            [typeof(IProcessHandle)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IProcessHandle.Id)] = WrapperMember.Read,
                ["get_" + nameof(IProcessHandle.ProcessName)] = WrapperMember.Read,
                ["get_" + nameof(IProcessHandle.HasExited)] = WrapperMember.Read,
                [nameof(IProcessHandle.RefreshMainWindowHandle)] = WrapperMember.Read,
                [nameof(IProcessHandle.WaitForExitAsync)] = WrapperMember.Read,
                // Releases the handle the server holds; the process is not affected
                [nameof(IDisposable.Dispose)] = WrapperMember.Read,
                [nameof(IProcessHandle.CloseMainWindow)] = WrapperMember.ProcessAction,
                [nameof(IProcessHandle.Kill)] = WrapperMember.ProcessAction
            }
        };

    /// <summary>The wrapper interfaces the catalog classifies.</summary>
    public static IReadOnlyCollection<Type> Interfaces { get; } = Catalog.Keys.ToArray();

    /// <summary>The classified members of <paramref name="serviceType"/>, by member name.</summary>
    public static IReadOnlyDictionary<string, WrapperMember> MembersOf(Type serviceType) =>
        Catalog.TryGetValue(serviceType, out var members) ? members : new Dictionary<string, WrapperMember>();

    /// <summary>
    /// How <paramref name="method"/>, called on <paramref name="serviceType"/>, is classified; null when it
    /// is not, and the guard must refuse it.
    /// </summary>
    public static WrapperMember? Find(Type serviceType, MethodInfo method) =>
        Catalog.TryGetValue(serviceType, out var members) && members.TryGetValue(method.Name, out var member)
            ? member
            : null;
}
