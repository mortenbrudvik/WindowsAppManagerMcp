namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Error codes for window operations.
/// </summary>
public enum WindowErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The window handle is invalid or the window no longer exists.</summary>
    InvalidHandle = 1,

    /// <summary>The specified snap position is not valid.</summary>
    InvalidSnapPosition = 2,

    /// <summary>The specified monitor index is out of range.</summary>
    InvalidMonitorIndex = 3,

    /// <summary>Failed to move the window.</summary>
    MoveFailed = 4,

    /// <summary>Failed to resize the window.</summary>
    ResizeFailed = 5,

    /// <summary>Failed to change window state.</summary>
    StateChangeFailed = 6,

    /// <summary>
    /// The window could not be brought to the foreground: Windows kept another window in front
    /// (the foreground lock, an open menu, or an elevated foreground window).
    /// </summary>
    FocusFailed = 7,

    /// <summary>
    /// The close message could not be sent (a Win32 error other than access denied, or an unexpected
    /// error in a batch); the error text has the reason.
    /// </summary>
    CloseFailed = 8,

    // Codes added in M1 are numbered from 20, so that earlier window codes can be added after
    // CloseFailed without renumbering. Activation codes (EnsureForeground, roadmap M1.3):

    /// <summary>The window's thread is not responding (IsHungAppWindow), or a call to it did not return in time.</summary>
    NotResponding = 20,

    /// <summary>The window is cloaked: it is on another virtual desktop, or its app has hidden it.</summary>
    Cloaked = 21,

    // Close guard codes (roadmap M1.7):

    /// <summary>
    /// The window is part of the desktop shell (the desktop or a taskbar) or of the agent's own host (the
    /// terminal, editor or chat app the server was started from), and force was not true. Nothing was sent.
    /// </summary>
    ProtectedWindow = 22,

    /// <summary>
    /// Windows refused the message with ERROR_ACCESS_DENIED (5): User Interface Privilege Isolation blocks
    /// messages to windows of more privileged processes, such as an app running as administrator.
    /// </summary>
    AccessDenied = 23,

    /// <summary>
    /// A batch above the confirmation threshold, or force on a protected window (roadmap M2.F1), was sent without confirm=true
    /// by a client that cannot ask the user. Nothing was done.
    /// </summary>
    ConfirmationRequired = 24,

    /// <summary>
    /// The server's target policy (WAM_DENY_PROCESSES, WAM_ALLOW_PROCESSES) refuses the window's process
    /// (roadmap M1.12). force does not override it. Nothing was sent.
    /// </summary>
    TargetDeniedByPolicy = 25,

    /// <summary>
    /// The user paused the server's input with the pause hotkey (roadmap M1.10), so the window was not
    /// activated: activating takes the keyboard focus from the user and can send input. Nothing was done.
    /// </summary>
    PausedByUser = 26,

    /// <summary>
    /// The window (its top-level window) is hidden: it does not have WS_VISIBLE, for example an app that hid
    /// its window in the notification area. It was not activated, because keyboard input would then go to a
    /// window the user cannot see.
    /// </summary>
    WindowHidden = 27,

    // Codes added in M2 (honest results and typed parameters, roadmap M2.1, M2.2 and M2.10):

    /// <summary>get_foreground_window: no window is in the foreground (windows are switching, or a UAC prompt or the lock screen is shown).</summary>
    NoForegroundWindow = 28,

    /// <summary>The window state is not one of the allowed values (minimize, maximize, restore and their aliases).</summary>
    InvalidState = 29,

    /// <summary>The positioning of move_window_to_monitor is not one of the allowed values.</summary>
    InvalidPositioning = 30,

    /// <summary>The user declined, dismissed or did not answer the confirmation question (elicitation). Nothing was done.</summary>
    ConfirmationDeclined = 31,

    /// <summary>
    /// The window is not the one the call expected: after the user confirmed a close, its handle belongs to a different
    /// process than the one the user was asked about (roadmap M2.10); or a window selector named a handle together with
    /// what the window must be (processId, processName, a title or className), and the window no longer matches it, so
    /// the handle was most likely reused by another window (roadmap M2.4). Nothing was sent to it.
    /// </summary>
    HandleMismatch = 32,

    // Verified close (roadmap M2.5):

    /// <summary>
    /// The close message was sent, but the window was still open and visible when the wait ended, and no prompt was
    /// found: the application is busy, slow or ignores the request.
    /// </summary>
    StillOpen = 33,

    /// <summary>
    /// The close message was sent, but the window waits for an answer to a prompt (a "Save changes?" dialog); the result
    /// describes the prompt. Nothing was answered.
    /// </summary>
    PromptShown = 34,

    // Window selectors (roadmap M2.4):

    /// <summary>
    /// No window matches the call's window selector (or its index is past the matches). Nothing was done. The listing
    /// rules of find_windows apply: cloaked and untitled windows only with includeCloaked and includeUntitled, and
    /// processName is the whole process name unless processNameContains is set.
    /// </summary>
    WindowNotFound = 35,

    /// <summary>
    /// More than one window matches the call's window selector, and it has no index. Nothing was done; the error lists up
    /// to 5 candidates (handle, title, process and class) so the caller can name one exactly.
    /// </summary>
    AmbiguousWindow = 36,

    /// <summary>
    /// wait_for_window (roadmap M3.7): the condition was not met within timeoutMs. The result still says what was last
    /// seen (satisfied false, timedOut true, the window as last seen). wait_for_element (roadmap M3.8) reports it too, with
    /// the element as last seen and lastObservedState.
    /// </summary>
    WaitTimedOut = 37,

    // Monitor selectors and the z-order (roadmap M6.7):

    /// <summary>
    /// A `monitor` argument names no monitor: an id no monitor has now, a name that is no monitor, 'current' while no window
    /// is in the foreground, or 'left' or 'right' with one monitor. Nothing was done. An index that is out of range is
    /// InvalidMonitorIndex.
    /// </summary>
    MonitorNotFound = 38,

    /// <summary>
    /// A `monitor` argument of 'left' or 'right' fits two monitors stacked above each other; the error lists them. Nothing was
    /// done.
    /// </summary>
    AmbiguousMonitor = 39,

    /// <summary>
    /// set_window_zorder: Windows took the call, but the window was not where it was asked to be when read back (the app
    /// re-asserted its own topmost state, or a window of a higher band, such as the shell's, stays above it).
    /// </summary>
    ZOrderNotApplied = 40
}

/// <summary>
/// Error codes for application launch operations.
/// </summary>
public enum LaunchErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The executable path is empty or null.</summary>
    EmptyExecutablePath = 1,

    /// <summary>
    /// The executable contains characters that are not valid in a Windows path (", &lt;, &gt;, |, *, ?, control characters),
    /// or a bare name / relative path contains shell metacharacters.
    /// </summary>
    InvalidCharacters = 2,

    /// <summary>Path traversal attempt detected (..).</summary>
    PathTraversal = 3,

    /// <summary>The protocol is blocked for security reasons.</summary>
    BlockedProtocol = 4,

    /// <summary>Unknown protocol (not in allowed list).</summary>
    UnknownProtocol = 5,

    /// <summary>Invalid URI format.</summary>
    InvalidUri = 6,

    /// <summary>Working directory contains invalid characters.</summary>
    InvalidWorkingDirectory = 7,

    /// <summary>Invalid path format.</summary>
    InvalidPath = 8,

    /// <summary>
    /// Failed to start the process. No longer returned (roadmap M2.6): when ShellExecute starts no process, the request
    /// went to a running app, and the launch succeeds with launchStatus possibleHandoff. Kept so the number is not reused.
    /// </summary>
    ProcessStartFailed = 9,

    /// <summary>An exception occurred during launch.</summary>
    LaunchException = 10,

    /// <summary>
    /// An argument contains characters that cmd.exe would interpret (&amp; | &lt; &gt; ^ % ! " or control characters),
    /// and the target could not be verified to be a native executable. A batch file (.bat/.cmd) is refused before that as a
    /// script (<see cref="ScriptFile"/>, roadmap M3.F8).
    /// </summary>
    UnsafeBatchArguments = 11,

    /// <summary>
    /// An element of the arguments array is null or contains a NUL character. Not to be confused with the
    /// InvalidArguments tool error (ToolErrorReporting), which means the call's arguments could not be bound.
    /// </summary>
    InvalidArgumentValue = 12,

    /// <summary>
    /// A bare executable name was found only in the working directory. Bare names are never resolved
    /// from the working directory, to avoid running a planted program.
    /// </summary>
    ExecutableInWorkingDirectory = 13,

    /// <summary>
    /// The user declined or cancelled a prompt Windows showed for the start, most likely a UAC prompt for a program that
    /// needs administrator rights (Win32 error 1223, within the start timeout). Nothing was started.
    /// </summary>
    ElevationCancelled = 14,

    /// <summary>
    /// A bare name was not found in the Windows directory, System32, the absolute PATH entries or App Paths. It is not
    /// passed to Windows unresolved, because Windows would also search the current directory.
    /// </summary>
    ExecutableNotFound = 15,

    /// <summary>
    /// The target is a command interpreter, script host or other program that runs what its command line names (cmd,
    /// PowerShell, Windows Script Host, Python, ...), and it was given arguments: the server does not run commands or
    /// scripts for the agent (roadmap M3.F8). Nothing was started; without arguments it starts as usual.
    /// </summary>
    InterpreterWithArguments = 16,

    /// <summary>
    /// The launch would run a script file (.bat, .cmd, .ps1, .vbs, .py, ...), as the target or through explorer.exe, which
    /// opens its argument with the program registered for it (roadmap M3.F8). Nothing was started.
    /// </summary>
    ScriptFile = 17,

    /// <summary>
    /// A bare name that is no program on disk is the exact display name of more than one installed app (roadmap M6.5). The
    /// error lists them; launch one by its appId or path from list_installed_apps. Nothing was started.
    /// </summary>
    AmbiguousApp = 18,

    /// <summary>
    /// Windows could not activate the packaged app (roadmap M6.5): it is not installed for the user, its package is being
    /// updated, or it failed to start. The error gives the HRESULT. Nothing was started.
    /// </summary>
    ActivationFailed = 19,

    /// <summary>
    /// runAsAdmin on a computer where Windows elevates without asking (User Account Control is off, or administrators are
    /// not asked for consent), and the user could not be asked to confirm through the client (roadmap M6.5). Nothing was
    /// started.
    /// </summary>
    ElevationNotConfirmed = 20
}

/// <summary>
/// Error codes for process termination operations.
/// </summary>
public enum ProcessErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The process ID is invalid (zero or negative).</summary>
    InvalidProcessId = 1,

    /// <summary>The process was not found or has already exited.</summary>
    ProcessNotFound = 2,

    /// <summary>The process is protected and cannot be terminated.</summary>
    ProtectedProcess = 3,

    /// <summary>Cannot terminate the current process.</summary>
    CannotTerminateSelf = 4,

    /// <summary>
    /// Access denied - insufficient permissions to terminate the process
    /// (typically it runs elevated or as another user).
    /// </summary>
    AccessDenied = 5,

    /// <summary>The confirmation parameter was not set to true.</summary>
    ConfirmationRequired = 6,

    /// <summary>An exception occurred during termination.</summary>
    TerminationException = 7,

    /// <summary>
    /// Graceful mode: the process has no main window that can receive a close request (windowless,
    /// tray-only, or main window disabled by a modal dialog). The process was left running.
    /// </summary>
    NoClosableWindow = 8,

    /// <summary>
    /// Graceful mode: a close request was sent but the process was still running when the timeout
    /// elapsed (for example it is showing a "save changes?" prompt). The process was left running.
    /// </summary>
    GracefulCloseTimedOut = 9,

    /// <summary>The process name did not match expectedProcessName (the PID may have been reused). Nothing was done.</summary>
    ProcessNameMismatch = 10,

    /// <summary>
    /// The process belongs to the agent's host (IHostGuard.IsHostProcess: an ancestor of this server such as the
    /// terminal or editor hosting the agent, the console host's process or the console server behind it, or the
    /// startup foreground window's process), or, with forceKill, one of the child processes the forced kill would end does. Nothing was done.
    /// </summary>
    CannotTerminateHost = 11,

    /// <summary>Forced termination was requested, but the process had not exited when the wait ended.</summary>
    TerminationNotConfirmed = 12,

    /// <summary>The graceful close timeout is outside the allowed range.</summary>
    InvalidTimeout = 13,

    /// <summary>
    /// The server's target policy (WAM_DENY_PROCESSES, WAM_ALLOW_PROCESSES) refuses the process (roadmap
    /// M1.12). Nothing was done.
    /// </summary>
    TargetDeniedByPolicy = 14,

    /// <summary>
    /// forceKill: the process list could not be read, or did not list the process (it may just have exited), so
    /// the child processes the forced kill would also end could not be checked against the target policy and the
    /// host guard. Nothing was terminated.
    /// </summary>
    ProcessTreeUnreadable = 15,

    /// <summary>
    /// The MCP client can ask the user (elicitation), and the user declined, dismissed or did not answer the
    /// confirmation question (roadmap M2.10). Nothing was done.
    /// </summary>
    ConfirmationDeclined = 16,

    /// <summary>
    /// forceKill after the user confirmed: the process gained a child process the user was not shown while the question
    /// was open, so the forced kill would end a process nobody approved. Nothing was terminated.
    /// </summary>
    ProcessTreeChanged = 17
}

/// <summary>
/// Error codes for layout preset operations.
/// </summary>
public enum LayoutErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The layout preset was not found.</summary>
    PresetNotFound = 1,

    /// <summary>Failed to save the layout preset.</summary>
    SaveFailed = 2,

    /// <summary>Failed to delete the layout preset.</summary>
    DeleteFailed = 3,

    /// <summary>Failed to launch an application in the layout.</summary>
    LaunchFailed = 4,

    /// <summary>Failed to arrange a window in the layout.</summary>
    ArrangeFailed = 5,

    /// <summary>Invalid layout name.</summary>
    InvalidName = 6,

    /// <summary>The layout storage folder could not be created or used.</summary>
    StorageUnavailable = 7,

    /// <summary>No open window matched one or more placements in the layout.</summary>
    WindowNotFound = 8,

    /// <summary>No windows matched the capture filters, so nothing was saved.</summary>
    NoWindowsCaptured = 9,

    /// <summary>apply_layout's matchBy is not one of the allowed values (roadmap M2.2). Nothing was arranged.</summary>
    InvalidMatchBy = 10,

    /// <summary>
    /// A placement has no criterion apply_layout's matchBy can use (title_only without titleContains, process_only
    /// without a process name, or neither), so it was skipped instead of matching every window (roadmap M2.8).
    /// </summary>
    NoMatchCriterion = 11
}

/// <summary>
/// Error codes for the capture tools (take_screenshot, capture_region, capture_window, capture_with_elements).
/// </summary>
public enum ScreenshotErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The format is not auto, png, jpeg or jpg.</summary>
    InvalidFormat = 1,

    /// <summary>maxDimension or maxWidth is out of range, or would scale the image to zero pixels.</summary>
    InvalidImageSize = 2,

    /// <summary>The output mode is unknown, or outputPath was given without a file output.</summary>
    InvalidOutputMode = 3,

    /// <summary>outputPath is not absolute, has the wrong extension, or its folder does not exist.</summary>
    InvalidOutputPath = 4,

    /// <summary>outputPath names an existing file and overwrite was not true.</summary>
    FileExists = 5,

    /// <summary>The image file could not be written.</summary>
    FileWriteFailed = 6,

    /// <summary>The monitor index is out of range.</summary>
    InvalidMonitorIndex = 7,

    /// <summary>The region's width or height is not positive.</summary>
    InvalidRegion = 8,

    /// <summary>The window handle is invalid or the window no longer exists.</summary>
    InvalidHandle = 9,

    /// <summary>
    /// Windows could not capture the pixels: PrintWindow and the window-DC copy both failed, the window is
    /// larger than the pixel budget, or the window closed during the capture. The error has the reason.
    /// </summary>
    CaptureFailed = 10,

    /// <summary>
    /// capture_window and capture_with_elements: the server's target policy (WAM_DENY_PROCESSES,
    /// WAM_ALLOW_PROCESSES) refuses the window's process (roadmap M1.12). Nothing was captured or read.
    /// </summary>
    TargetDeniedByPolicy = 11,

    // Capture health codes (roadmap M2.9):

    /// <summary>
    /// capture_window and capture_with_elements: the window, or the top-level window it is part of, is minimized.
    /// Windows does not draw it, so nothing was captured. Restore it first, or pass restoreIfMinimized.
    /// </summary>
    WindowMinimized = 12,

    /// <summary>
    /// capture_window and capture_with_elements: the window, or a parent of it, is hidden (no WS_VISIBLE). Nothing
    /// was captured: a hidden window draws nothing the user can see. The same name as the focus_window code.
    /// </summary>
    WindowHidden = 13,

    /// <summary>
    /// capture_window and capture_with_elements: DWM cloaks the window (it is on another virtual desktop, it is a
    /// suspended Store app, or its app hid it this way), so its pixels are not drawn. Nothing was captured. The same
    /// name as the focus_window code.
    /// </summary>
    Cloaked = 14,

    /// <summary>capture_region: no part of the region is on the virtual screen (the area of all monitors). Nothing was captured.</summary>
    RegionOffScreen = 15,

    /// <summary>The pixels were captured, but the image could not be built, scaled or encoded (for example out of memory).</summary>
    EncodeFailed = 16,

    /// <summary>
    /// capture_window with a ref (roadmap M3.11): the element is offscreen, has no bounds, or lies outside the captured
    /// window (its client area without the frame), so there is nothing of it to crop to. The same name as element_action's
    /// UIElementErrorCode.ElementOffscreen.
    /// </summary>
    ElementOffscreen = 17,

    // OCR codes (roadmap M6.3): read_screen_text and find_text_on_screen also return the capture codes above

    /// <summary>
    /// Windows OCR cannot be used: the system has no Windows.Media.Ocr, no OCR language is installed, or the engine failed.
    /// The error says how the user adds a language; the server never installs one.
    /// </summary>
    OcrUnavailable = 18,

    /// <summary>The language given is not an installed OCR language. The result lists the installed ones.</summary>
    OcrLanguageNotInstalled = 19
}

/// <summary>
/// Error codes for browser reading operations.
/// </summary>
public enum BrowserErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>The window handle is invalid or the window no longer exists.</summary>
    InvalidHandle = 1,

    /// <summary>The window is not a recognized browser.</summary>
    NotABrowser = 2,

    /// <summary>
    /// UI Automation found neither the page's address in its Document nor the browser's address bar (roadmap M5.7: looked
    /// up by class, AutomationId and structure, never by name).
    /// </summary>
    UrlBarNotFound = 3,

    /// <summary>
    /// UI Automation found the address bar but not the page's address: the address bar has the keyboard focus, or whether
    /// it has cannot be told (the browser is not the foreground window, or its focus cannot be read), so its text may be
    /// typed, not the page's; or it holds text that is not a URL (roadmap M5.7).
    /// </summary>
    UrlExtractionFailed = 4,

    /// <summary>
    /// The page shown has no Document UI Automation can read: no shown, onscreen Document of a tab (roadmap M5.7).
    /// </summary>
    ContentAreaNotFound = 5,

    /// <summary>Failed to extract content from the browser.</summary>
    ContentExtractionFailed = 6,

    /// <summary>UI Automation is not available or failed to initialize.</summary>
    AutomationUnavailable = 7,

    /// <summary>No foreground browser window was found.</summary>
    NoBrowserInForeground = 8,

    /// <summary>An exception occurred during the operation.</summary>
    OperationException = 9,

    /// <summary>The browser executable was not found.</summary>
    BrowserNotFound = 10,

    /// <summary>The specified browser type is not supported.</summary>
    UnsupportedBrowser = 11,

    /// <summary>The CDP port is already in use.</summary>
    PortInUse = 12,

    /// <summary>Failed to launch browser with debug mode.</summary>
    DebugLaunchFailed = 13,

    // The UI Automation read of a browser window (roadmap M3.F24), under the names the UI Automation tools give these codes

    /// <summary>
    /// The user's target policy refuses the UI Automation read of the browser window: the policy covers the UI Automation
    /// reads of a window as it covers input and captures. Nothing was read through UI Automation.
    /// </summary>
    TargetDeniedByPolicy = 14,

    /// <summary>
    /// The browser window does not respond: Windows reports it hung, or it did not answer a message within 500 ms. Nothing
    /// was read through UI Automation, because every call would block until it recovers.
    /// </summary>
    NotResponding = 15,

    /// <summary>
    /// The UI Automation read of the browser window did not finish within timeoutMs, or earlier reads of the browser are
    /// still stuck; the browser may be busy or hung.
    /// </summary>
    OperationTimedOut = 16,

    /// <summary>
    /// The tab shows the browser's new tab page (roadmap M5.7): its Document is the new tab page, or the address bar is
    /// empty. It has no address of a site.
    /// </summary>
    NewTabPage = 17,

    /// <summary>
    /// close_debug_browser (roadmap M5.4): the process is not a debug browser the server may close. It was not started by
    /// this server, and its command line does not show a DevTools port with a profile in the server's own folders; or it
    /// is a helper process, or its command line cannot be read or no longer names the profile it was started with.
    /// Nothing was closed: it may be the user's own browser.
    /// </summary>
    NotADebugBrowser = 18,

    /// <summary>
    /// The browser could not load the page browser_navigate asked for (roadmap M5.5): Page.navigate answered with an
    /// errorText such as net::ERR_NAME_NOT_RESOLVED, which the result carries (loadState failed), or the address answered
    /// with a file, which the browser downloads while the tab keeps its page (loadState download).
    /// </summary>
    NavigationFailed = 19,

    /// <summary>
    /// browser_click (roadmap M5.8): another element covers the element at the point the click would land on (an overlay,
    /// a cookie banner, a modal), so the click was not sent: it would reach that element instead.
    /// </summary>
    ElementObscured = 20,

    /// <summary>
    /// A DOM bridge tool (roadmap M5.8) was aimed at one of the browser's own pages (chrome:, edge:, devtools:, an
    /// extension's chrome-extension: page and the like: settings, passwords, flags), which these tools never act on or read.
    /// </summary>
    BrowserInternalPage = 21,

    /// <summary>
    /// browser_handle_dialog (roadmap M5.8, dialogs): accept was asked for a confirm or prompt whose text the agent had not
    /// been shown (no read of it, and no action that opened it reported it). Nothing was pressed; the result carries the
    /// dialog's text, so the next call may accept it.
    /// </summary>
    DialogNotRead = 22,

    // CDP (Chrome DevTools Protocol) errors are numbered from 30 so that other
    // browser error codes can be added above without renumbering.

    /// <summary>The CDP endpoint could not be reached or did not answer like a DevTools endpoint.</summary>
    CdpUnavailable = 30,

    /// <summary>The browser or page did not answer a CDP request within the timeout.</summary>
    CdpTimeout = 31,

    /// <summary>No CDP tab could be matched to the window.</summary>
    CdpTargetNotFound = 32,

    /// <summary>More than one CDP tab matches the window, so the right one cannot be chosen.</summary>
    CdpTargetAmbiguous = 33,

    /// <summary>The CDP port is owned by a different process than the window.</summary>
    CdpPortOwnerMismatch = 34,

    /// <summary>The page has a JavaScript dialog (alert/confirm/prompt/beforeunload) open.</summary>
    CdpDialogOpen = 35,

    /// <summary>The page threw an exception or the browser rejected the evaluation.</summary>
    CdpEvaluationFailed = 36,

    /// <summary>The CDP connection closed early or returned a response that could not be read.</summary>
    CdpProtocolError = 37,

    /// <summary>HTML content (textOnly=false) was requested, but it is only available via CDP.</summary>
    HtmlRequiresCdp = 38,

    /// <summary>
    /// The tab's DevTools connection ended during the call: the browser closed it, the tab detached or crashed, or the
    /// connection was reset (roadmap M5.2). The tab does not come back; read the window again without targetId.
    /// </summary>
    CdpTargetClosed = 39,

    /// <summary>
    /// The browser's answer was larger than the server reads: a /json page over 16 MB or a DevTools message over 64 MB
    /// (roadmap M5.2). Nothing of it was returned.
    /// </summary>
    CdpResponseTooLarge = 40
}

/// <summary>
/// Error codes for get_clipboard and set_clipboard (roadmap M2.16). A refused read returns no content at all.
/// </summary>
public enum ClipboardErrorCode
{
    /// <summary>No error occurred.</summary>
    None = 0,

    /// <summary>
    /// get_clipboard: the clipboard holds content the agent did not put there (the user or another app copied it), and
    /// reading the user's clipboard is not allowed: WAM_ALLOW_USER_CLIPBOARD is off and the user was not asked, or the
    /// clipboard changed while they were asked. Nothing was read.
    /// </summary>
    ClipboardNotFromAgent = 1,

    /// <summary>
    /// get_clipboard: the content is marked private (ExcludeClipboardContentFromMonitorProcessing,
    /// CanIncludeInClipboardHistory = 0 or Clipboard Viewer Ignore), as password managers mark what they copy. It is
    /// never returned, whoever copied it, unless it is the text set_clipboard put there.
    /// </summary>
    ClipboardPrivate = 2,

    /// <summary>get_clipboard: the clipboard holds nothing in the format asked for; availableFormats lists what it holds.</summary>
    FormatNotAvailable = 3,

    /// <summary>Another application kept the clipboard open, or did not answer, so it could not be read or set.</summary>
    ClipboardBusy = 4,

    /// <summary>Windows refused to read or set the clipboard data; the error has the Win32 error.</summary>
    ClipboardFailed = 5,

    /// <summary>get_clipboard: the user was asked to let the agent read their clipboard and did not allow it. Nothing was read.</summary>
    ConfirmationDeclined = 6,

    /// <summary>get_clipboard: the format is not text, html or files.</summary>
    InvalidFormat = 7,

    /// <summary>
    /// get_clipboard: the agent's copy keys were sent moments ago, but the clipboard did not change within a second
    /// (nothing selected, or the application did not copy). Nothing was read, and the user is not asked.
    /// </summary>
    NothingCopied = 8
}
