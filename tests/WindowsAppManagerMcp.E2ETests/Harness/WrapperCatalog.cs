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
    GlobalHotkey,

    /// <summary>
    /// Opens the clipboard to read or replace its content. The clipboard is the user's, shared with every app: a test
    /// that read it could pull a password the user copied into the test output, and one that set it would replace what
    /// the user copied. No E2E tier allows it; reading the sequence number, which opens nothing, is a read.
    /// </summary>
    Clipboard,

    /// <summary>
    /// Acts on an element of an application through a UI Automation control pattern (roadmap M3.2): invokes, toggles,
    /// sets a value, expands, selects, scrolls, realizes, closes or changes a window's state, or runs the element's MSAA
    /// default action; or gives an element keyboard focus (IUiaTree.SetFocus, roadmap M3.6). Like input, it changes the
    /// target application, and it does so without the input guard's foreground and hit-test checks. The no-input tier
    /// refuses it; the input tier allows it only on an element of the fixture's process (FixtureOnlyCallPolicy).
    /// </summary>
    UiaAction
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

    public static WrapperMember Clipboard { get; } = new(WrapperCallKind.Clipboard, [], []);

    public static WrapperMember UiaAction { get; } = new(WrapperCallKind.UiaAction, [], []);

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
                [nameof(INativeWindowWrapper.GetWindowThreadId)] = WrapperMember.Read,
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
                // GetWindowRgnBox: whether the window has a region, for the OCR mask (roadmap M6.3)
                [nameof(INativeWindowWrapper.HasWindowRegion)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetCloakReason)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetExtendedFrameBounds)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.IsHungAppWindow)] = WrapperMember.Read,
                // SendMessageTimeout with WM_NULL, which the window ignores: reads whether its thread answers
                [nameof(INativeWindowWrapper.RespondsWithin)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.GetDpiForWindow)] = WrapperMember.Read,
                // A window's show state and restore rectangle (layouts, roadmap M6.8)
                [nameof(INativeWindowWrapper.GetWindowPlacement)] = WrapperMember.Read,
                [nameof(INativeWindowWrapper.SetWindowPos)] = WrapperMember.WindowAction("hWnd"),
                // set_window_zorder (roadmap M6.7): insertAfter is HWND_TOP, HWND_BOTTOM, HWND_TOPMOST, HWND_NOTOPMOST or the
                // window to go behind; that window only marks the place and is not changed
                [nameof(INativeWindowWrapper.SetWindowZOrder)] = WrapperMember.WindowAction("hWnd") with
                {
                    OtherHandleParameters = ["insertAfter"]
                },
                [nameof(INativeWindowWrapper.ShowWindow)] = WrapperMember.WindowAction("hWnd"),
                // Sets a minimized window's restore rectangle with SW_SHOWMINNOACTIVE (apply_layout, roadmap M6.8)
                [nameof(INativeWindowWrapper.SetWindowPlacement)] = WrapperMember.WindowAction("hWnd"),
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
                // GetKeyState's toggle bit: whether Caps Lock is on (get_desktop_state)
                [nameof(INativeInputWrapper.IsKeyToggled)] = WrapperMember.Read,
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
            [typeof(INativeWinEventWrapper)] = new Dictionary<string, WrapperMember>
            {
                // wait_for_window's wake-ups (roadmap M3.F22): out-of-context WinEvent hooks, which load nothing into another
                // process and change no window, only queue window events to the server's own event thread; that thread's
                // queue, message loop and its own update and stop messages change nothing outside the server either
                [nameof(INativeWinEventWrapper.GetCurrentThreadId)] = WrapperMember.Read,
                [nameof(INativeWinEventWrapper.CreateMessageQueue)] = WrapperMember.Read,
                [nameof(INativeWinEventWrapper.SetWinEventHook)] = WrapperMember.Read,
                [nameof(INativeWinEventWrapper.UnhookWinEvent)] = WrapperMember.Read,
                [nameof(INativeWinEventWrapper.WaitForMessage)] = WrapperMember.Read,
                [nameof(INativeWinEventWrapper.PostUpdateMessage)] = WrapperMember.Read,
                [nameof(INativeWinEventWrapper.PostStopMessage)] = WrapperMember.Read
            },
            [typeof(INativeLowLevelInputWrapper)] = new Dictionary<string, WrapperMember>
            {
                // Takeover detection (roadmap M7.4): low-level keyboard and mouse hooks that only look whether input was
                // injected and always pass it on unchanged (CallNextHookEx), so they change no input and no window; the
                // monitor thread's queue, message loop and its own update and stop messages change nothing outside the server
                [nameof(INativeLowLevelInputWrapper.GetCurrentThreadId)] = WrapperMember.Read,
                [nameof(INativeLowLevelInputWrapper.CreateMessageQueue)] = WrapperMember.Read,
                [nameof(INativeLowLevelInputWrapper.SetKeyboardHook)] = WrapperMember.Read,
                [nameof(INativeLowLevelInputWrapper.SetMouseHook)] = WrapperMember.Read,
                [nameof(INativeLowLevelInputWrapper.Unhook)] = WrapperMember.Read,
                [nameof(INativeLowLevelInputWrapper.WaitForMessage)] = WrapperMember.Read,
                [nameof(INativeLowLevelInputWrapper.PostUpdateMessage)] = WrapperMember.Read,
                [nameof(INativeLowLevelInputWrapper.PostStopMessage)] = WrapperMember.Read
            },
            [typeof(IUIAutomationWrapper)] = new Dictionary<string, WrapperMember>
            {
                // The browser reads moved onto IUiaTree (roadmap M3.F24), whose members are classified below.
                // WindowPattern.Close: asks the window to close, like WM_CLOSE (close_application's fallback for a
                // packaged app's frame), so it changes a window and must name a fixture window
                [nameof(IUIAutomationWrapper.CloseWindow)] = WrapperMember.WindowAction("hwnd")
            },
            [typeof(IScreenCaptureWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(IScreenCaptureWrapper.CaptureScreenRegion)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.CaptureWindow)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.CaptureWindowFromScreen)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.GetVirtualScreenBounds)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.IsValidWindow)] = WrapperMember.Read,
                // The capture members without the copy (roadmap M6, polling for OCR and visual change): the same BitBlt of
                // the screen or PrintWindow into the server's own memory DC, which changes no window and sends no input
                [nameof(IScreenCaptureWrapper.WithPixels)] = WrapperMember.Read,
                // The pointer (roadmap M7.1): its position, and its shape drawn into a device context of the server's own image;
                // the pointer is never moved and its shape never changed
                [nameof(IScreenCaptureWrapper.GetPointer)] = WrapperMember.Read,
                [nameof(IScreenCaptureWrapper.DrawPointer)] = WrapperMember.Read
            },
            // Windows.Graphics.Capture (roadmap M7.1): one frame of a window as the compositor shows it, copied through a
            // Direct3D device of the server's own; it changes no window and sends no input
            [typeof(IGraphicsCaptureWrapper)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IGraphicsCaptureWrapper.Availability)] = WrapperMember.Read,
                [nameof(IGraphicsCaptureWrapper.CaptureWindow)] = WrapperMember.Read
            },
            // Windows OCR (roadmap M6.3): recognizes text in pixels the server already holds, through the WinRT projection of
            // Windows.Media.Ocr. It reads no window or screen, sends no input and changes nothing; the capture it reads went through
            // IScreenCaptureWrapper above. NativeCallGuardTests cannot see WinRT calls through P/Invoke, so its WinRtProjection tests keep them in OcrEngineWrapper
            [typeof(IOcrEngineWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(IOcrEngineWrapper.GetCapabilities)] = WrapperMember.Read,
                [nameof(IOcrEngineWrapper.RecognizeAsync)] = WrapperMember.Read
            },
            [typeof(INativeSystemInfoWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeSystemInfoWrapper.GetDpiAwareness)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.GetForegroundLockTimeout)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.GetSessionId)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.GetMouseWheelRouting)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.GetVirtualScreen)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.AreMouseButtonsSwapped)] = WrapperMember.Read,
                [nameof(INativeSystemInfoWrapper.GetDragThreshold)] = WrapperMember.Read
            },
            // The keyboard layout send_keys maps characters with (roadmap M4.1): ToUnicodeEx is called so that it changes no
            // keyboard state
            [typeof(INativeKeyboardLayoutWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeKeyboardLayoutWrapper.GetKeyboardLayout)] = WrapperMember.Read,
                [nameof(INativeKeyboardLayoutWrapper.VkKeyScan)] = WrapperMember.Read,
                [nameof(INativeKeyboardLayoutWrapper.ScanCode)] = WrapperMember.Read,
                [nameof(INativeKeyboardLayoutWrapper.KeyText)] = WrapperMember.Read,
                // The input-method hotkeys and language toggles, from the user's registry, opened for reading (issue #10)
                [nameof(INativeKeyboardLayoutWrapper.ReadSystemHotkeys)] = WrapperMember.Read
            },
            // The folders File Explorer's windows show (roadmap M7.5): read through the shell's window list, which it never changes
            [typeof(INativeShellWindowsWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeShellWindowsWrapper.GetExplorerFolders)] = WrapperMember.Read
            },
            [typeof(INativeVirtualDesktopWrapper)] = new Dictionary<string, WrapperMember>
            {
                // IsWindowOnCurrentVirtualDesktop and GetWindowDesktopId; the wrapper cannot move a window to a desktop
                [nameof(INativeVirtualDesktopWrapper.GetPlacements)] = WrapperMember.Read
            },
            [typeof(INativeClipboardWrapper)] = new Dictionary<string, WrapperMember>
            {
                // A counter that changes with the content; it does not open the clipboard
                [nameof(INativeClipboardWrapper.GetSequenceNumber)] = WrapperMember.Read,
                // The process of the clipboard's owner window; it does not open the clipboard either
                [nameof(INativeClipboardWrapper.GetOwnerProcessId)] = WrapperMember.Read,
                // The user's clipboard: refused in every tier
                [nameof(INativeClipboardWrapper.Read)] = WrapperMember.Clipboard,
                [nameof(INativeClipboardWrapper.WriteText)] = WrapperMember.Clipboard,
                [nameof(INativeClipboardWrapper.ReplaceText)] = WrapperMember.Clipboard
            },
            [typeof(INativeMonitorWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeMonitorWrapper.EnumerateMonitors)] = WrapperMember.Read,
                // find_windows looks up the monitor of every window, fixture or not
                [nameof(INativeMonitorWrapper.MonitorFromWindow)] = WrapperMember.Read,
                [nameof(INativeMonitorWrapper.MonitorFromPoint)] = WrapperMember.Read,
                // QueryDisplayConfig and DisplayConfigGetDeviceInfo: monitor identity (roadmap M6.7); never SetDisplayConfig
                [nameof(INativeMonitorWrapper.GetDisplayTargets)] = WrapperMember.Read
            },
            [typeof(ITcpPortOwnerResolver)] = new Dictionary<string, WrapperMember>
            {
                [nameof(ITcpPortOwnerResolver.GetListeningProcessId)] = WrapperMember.Read,
                [nameof(ITcpPortOwnerResolver.TryGetListeningProcessId)] = WrapperMember.Read
            },
            [typeof(INativeProcessWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeProcessWrapper.StartProcess)] = WrapperMember.ProcessStart,
                // Opening a process only reads; the handle it returns is guarded in turn
                [nameof(INativeProcessWrapper.OpenProcess)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.IsAncestorOfCurrentProcess)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetAncestorProcesses)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetAncestorsOf)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetDescendantsOf)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetProcessStartTime)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetConsoleWindow)] = WrapperMember.Read,
                // Launch-to-window resolution: a process snapshot, an image path, and a bounded wait that changes nothing
                [nameof(INativeProcessWrapper.GetProcessList)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.GetProcessImagePath)] = WrapperMember.Read,
                // The reading tools read a browser's debug port from its command line (roadmap M5.3)
                [nameof(INativeProcessWrapper.GetProcessCommandLine)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.WaitForInputIdle)] = WrapperMember.Read,
                // list_processes (roadmap M6.6): the system's process list, and one query handle per listed process for
                // its image path and start time; neither changes a process
                [nameof(INativeProcessWrapper.ListRunningProcesses)] = WrapperMember.Read,
                [nameof(INativeProcessWrapper.ReadProcessDetails)] = WrapperMember.Read
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
                [nameof(INativeHitTestWrapper.GetRootWindow)] = WrapperMember.Read,
                // GetClientRect and MapWindowPoints: where scroll points by default
                [nameof(INativeHitTestWrapper.GetClientAreaOnScreen)] = WrapperMember.Read
            },
            [typeof(INativeSecurityWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeSecurityWrapper.GetProcessCreationTime)] = WrapperMember.Read,
                // Opens the process token for TOKEN_QUERY only
                [nameof(INativeSecurityWrapper.QueryProcessToken)] = WrapperMember.Read,
                // Opens the input desktop for DESKTOP_READOBJECTS only; the server never switches desktops
                [nameof(INativeSecurityWrapper.QueryInputDesktop)] = WrapperMember.Read,
                [nameof(INativeSecurityWrapper.GetThreadDesktopName)] = WrapperMember.Read,
                [nameof(INativeSecurityWrapper.QuerySessionState)] = WrapperMember.Read,
                // The User Account Control policy values, from the registry (runAsAdmin, roadmap M6.5)
                [nameof(INativeSecurityWrapper.ReadUacSettings)] = WrapperMember.Read
            },
            // Packaged apps and the Applications folder (roadmap M6.5). Activating an app starts its process, which no tier
            // allows (the fixture app is no packaged app); the rest only read a process's or a window's package, a package
            // family's manifests and the Start menu's apps
            [typeof(INativeAppsWrapper)] = new Dictionary<string, WrapperMember>
            {
                [nameof(INativeAppsWrapper.ActivateApplication)] = WrapperMember.ProcessStart,
                [nameof(INativeAppsWrapper.EnumerateAppsFolder)] = WrapperMember.Read,
                [nameof(INativeAppsWrapper.GetPackageFamilyName)] = WrapperMember.Read,
                [nameof(INativeAppsWrapper.ReadPackageManifests)] = WrapperMember.Read,
                // SHGetPropertyStoreForWindow: reads the window's AppUserModelID; the property store is never written
                [nameof(INativeAppsWrapper.GetWindowAppUserModelId)] = WrapperMember.Read
            },
            [typeof(IUiaTree)] = new Dictionary<string, WrapperMember>
            {
                // Tree reads, and SetFocus: acting UIA pattern calls are forbidden outside the engine adapters by
                // NativeCallGuardTests, and the tree's own members only read, except SetFocus (roadmap M3.6, below).
                // GetPattern hands out a pattern object, which the guard wraps in turn (its return type is a wrapper
                // interface), so its actions are checked below as UiaAction
                ["get_" + nameof(IUiaTree.Engine)] = WrapperMember.Read,
                [nameof(IUiaTree.IsAvailable)] = WrapperMember.Read,
                [nameof(IUiaTree.FromHandle)] = WrapperMember.Read,
                [nameof(IUiaTree.FromPoint)] = WrapperMember.Read,
                [nameof(IUiaTree.GetFocusedElement)] = WrapperMember.Read,
                [nameof(IUiaTree.GetParent)] = WrapperMember.Read,
                [nameof(IUiaTree.GetChildren)] = WrapperMember.Read,
                // A tree walker's first child: whether the node has one (the walk's maxElements check)
                [nameof(IUiaTree.HasChildren)] = WrapperMember.Read,
                [nameof(IUiaTree.Read)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadInteraction)] = WrapperMember.Read,
                [nameof(IUiaTree.IsInteractable)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadIdentity)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadValue)] = WrapperMember.Read,
                // Roadmap M3.4: a fresh read of the walk's properties (the element registry's liveness check), the clickable
                // point (a hit test the provider answers) and the description properties: reads only
                [nameof(IUiaTree.ReadElement)] = WrapperMember.Read,
                // Roadmap M3.10: an item its list has not realized, read property by property (current and cached property
                // reads only; realizing it is the VirtualizedItem pattern's Realize, a UiaAction)
                [nameof(IUiaTree.ReadVirtualizedItem)] = WrapperMember.Read,
                [nameof(IUiaTree.GetClickablePoint)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadDescription)] = WrapperMember.Read,
                // WindowPattern.Current.IsModal: a property read (verified close looks for an in-window prompt)
                [nameof(IUiaTree.ReadIsModal)] = WrapperMember.Read,
                // Cached reads of a level, a search and a small subtree (roadmap M3.1/M3.2): they only read
                [nameof(IUiaTree.GetChildElements)] = WrapperMember.Read,
                [nameof(IUiaTree.FindAll)] = WrapperMember.Read,
                [nameof(IUiaTree.ReadSubtree)] = WrapperMember.Read,
                [nameof(IUiaTree.GetRuntimeId)] = WrapperMember.Read,
                [nameof(IUiaTree.AreSameElement)] = WrapperMember.Read,
                // Getting a pattern object reads only whether the element supports it
                [nameof(IUiaTree.GetPattern)] = WrapperMember.Read,
                // Gives the element keyboard focus, which can bring its window to the front (element_action, roadmap M3.6)
                [nameof(IUiaTree.SetFocus)] = WrapperMember.UiaAction,
                // wait_for_element's wake-ups (roadmap M3.F22): a StructureChanged or PropertyChanged handler only listens, and
                // its registration's Dispose (not a wrapper, so not guarded) only stops listening; neither changes the app
                [nameof(IUiaTree.AddStructureChangedHandler)] = WrapperMember.Read,
                [nameof(IUiaTree.AddPropertyChangedHandler)] = WrapperMember.Read
            },
            // The pattern objects of IUiaTree.GetPattern: their state reads are reads, their actions UiaAction
            [typeof(IUiaInvokePattern)] = new Dictionary<string, WrapperMember>
            {
                [nameof(IUiaInvokePattern.Invoke)] = WrapperMember.UiaAction
            },
            [typeof(IUiaTogglePattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaTogglePattern.ToggleState)] = WrapperMember.Read,
                [nameof(IUiaTogglePattern.Toggle)] = WrapperMember.UiaAction
            },
            [typeof(IUiaValuePattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaValuePattern.Value)] = WrapperMember.Read,
                ["get_" + nameof(IUiaValuePattern.IsReadOnly)] = WrapperMember.Read,
                [nameof(IUiaValuePattern.SetValue)] = WrapperMember.UiaAction
            },
            [typeof(IUiaExpandCollapsePattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaExpandCollapsePattern.ExpandCollapseState)] = WrapperMember.Read,
                [nameof(IUiaExpandCollapsePattern.Expand)] = WrapperMember.UiaAction,
                [nameof(IUiaExpandCollapsePattern.Collapse)] = WrapperMember.UiaAction
            },
            [typeof(IUiaSelectionItemPattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaSelectionItemPattern.IsSelected)] = WrapperMember.Read,
                [nameof(IUiaSelectionItemPattern.Select)] = WrapperMember.UiaAction,
                [nameof(IUiaSelectionItemPattern.AddToSelection)] = WrapperMember.UiaAction,
                [nameof(IUiaSelectionItemPattern.RemoveFromSelection)] = WrapperMember.UiaAction
            },
            [typeof(IUiaScrollItemPattern)] = new Dictionary<string, WrapperMember>
            {
                [nameof(IUiaScrollItemPattern.ScrollIntoView)] = WrapperMember.UiaAction
            },
            [typeof(IUiaScrollPattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaScrollPattern.State)] = WrapperMember.Read,
                [nameof(IUiaScrollPattern.Scroll)] = WrapperMember.UiaAction,
                [nameof(IUiaScrollPattern.SetScrollPercent)] = WrapperMember.UiaAction
            },
            [typeof(IUiaRangeValuePattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaRangeValuePattern.State)] = WrapperMember.Read,
                [nameof(IUiaRangeValuePattern.SetValue)] = WrapperMember.UiaAction
            },
            [typeof(IUiaWindowPattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaWindowPattern.State)] = WrapperMember.Read,
                // Waits until the window's thread is idle; changes nothing
                [nameof(IUiaWindowPattern.WaitForInputIdle)] = WrapperMember.Read,
                [nameof(IUiaWindowPattern.SetVisualState)] = WrapperMember.UiaAction,
                [nameof(IUiaWindowPattern.Close)] = WrapperMember.UiaAction
            },
            [typeof(IUiaTextPattern)] = new Dictionary<string, WrapperMember>
            {
                [nameof(IUiaTextPattern.GetText)] = WrapperMember.Read,
                [nameof(IUiaTextPattern.GetSelectedText)] = WrapperMember.Read,
                // Moves the endpoints of a range of its own (not the selection) and reads its text
                [nameof(IUiaTextPattern.GetTextPage)] = WrapperMember.Read
            },
            [typeof(IUiaGridPattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaGridPattern.RowCount)] = WrapperMember.Read,
                ["get_" + nameof(IUiaGridPattern.ColumnCount)] = WrapperMember.Read,
                // The provider may bring the cell into view first: WPF's DataGrid and GridView and the Toolkit DataGrid scroll
                // to it (and realize its row), so it changes what the user sees. read_table calls it only for Win32, WinForms and
                // Chromium grids, whose GetItem only looks the cell up
                [nameof(IUiaGridPattern.GetItem)] = WrapperMember.UiaAction
            },
            [typeof(IUiaGridItemPattern)] = new Dictionary<string, WrapperMember>
            {
                // Where a cell sits: property reads (a WPF DataGrid cell's row is its item's index)
                ["get_" + nameof(IUiaGridItemPattern.Row)] = WrapperMember.Read,
                ["get_" + nameof(IUiaGridItemPattern.Column)] = WrapperMember.Read,
                ["get_" + nameof(IUiaGridItemPattern.RowSpan)] = WrapperMember.Read,
                ["get_" + nameof(IUiaGridItemPattern.ColumnSpan)] = WrapperMember.Read
            },
            [typeof(IUiaTablePattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaTablePattern.RowOrColumnMajor)] = WrapperMember.Read,
                [nameof(IUiaTablePattern.GetColumnHeaders)] = WrapperMember.Read,
                [nameof(IUiaTablePattern.GetRowHeaders)] = WrapperMember.Read
            },
            [typeof(IUiaItemContainerPattern)] = new Dictionary<string, WrapperMember>
            {
                // Finds an item, realized or not, without realizing it
                [nameof(IUiaItemContainerPattern.FindItemByProperty)] = WrapperMember.Read
            },
            [typeof(IUiaVirtualizedItemPattern)] = new Dictionary<string, WrapperMember>
            {
                // Makes the container create the item's UI, which can scroll it
                [nameof(IUiaVirtualizedItemPattern.Realize)] = WrapperMember.UiaAction
            },
            [typeof(IUiaLegacyIAccessiblePattern)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IUiaLegacyIAccessiblePattern.DefaultAction)] = WrapperMember.Read,
                [nameof(IUiaLegacyIAccessiblePattern.DoDefaultAction)] = WrapperMember.UiaAction
            },
            [typeof(IProcessHandle)] = new Dictionary<string, WrapperMember>
            {
                ["get_" + nameof(IProcessHandle.Id)] = WrapperMember.Read,
                ["get_" + nameof(IProcessHandle.ProcessName)] = WrapperMember.Read,
                ["get_" + nameof(IProcessHandle.HasExited)] = WrapperMember.Read,
                ["get_" + nameof(IProcessHandle.ExitCode)] = WrapperMember.Read,
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
