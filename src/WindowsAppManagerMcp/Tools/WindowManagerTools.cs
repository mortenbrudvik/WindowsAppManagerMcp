using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;
using static WindowsAppManagerMcp.Models.WindowErrorCode;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class WindowManagerTools
{
    /// <summary>The errors of the single-window change tools (move, resize, bounds, state, snap, move to a monitor).</summary>
    private const string WindowChangeErrors =
        " errorCode: InvalidHandle; TargetDeniedByPolicy (the user's policy denies the app: nothing is changed, ask the user); " +
        "AccessDenied (Windows blocked the change: the window belongs to an app running as administrator; ask the user); " +
        "NotResponding (the window's app is hung or did not answer within 5 s, or an earlier call to it, or 8 calls to apps that " +
        "do not answer, are still blocked; a minimized window may already have been restored, and a call that timed out can still " +
        "take effect once the app answers, so check the window before acting on it).";

    /// <summary>The per-window errors of the window batches.</summary>
    private const string BatchErrors =
        " Each window is handled on its own and has its own success, error and errorCode: InvalidArguments for an item that is " +
        "null or misses a field, InvalidHandle, TargetDeniedByPolicy (the user's policy denies the app; it is not changed), " +
        "AccessDenied, NotResponding (the window's app does not answer within 5 s).";

    private readonly IWindowService _windowService;
    private readonly IForegroundService _foregroundService;
    private readonly IInputSession? _session;
    private readonly TimeSpan _answerTimeout;
    private readonly TimeSpan? _gateWaitTimeout;
    private readonly IWindowCloseVerifier _closeVerifier;

    /// <param name="windowService">Reads and changes windows.</param>
    /// <param name="foregroundService">Brings a window to the front (focus_window).</param>
    /// <param name="session">The input session, whose pause the close tools respect (they take the input gate themselves).</param>
    /// <param name="answerTimeout">How long close_windows_batch and close_application wait for the user's answer; default <see cref="UserConfirmation.DefaultAnswerTimeout"/>.</param>
    /// <param name="gateWaitTimeout">How long the close tools wait for the input gate; default <see cref="InputGateToolFilter.GateWaitTimeout"/>.</param>
    /// <param name="closeVerifier">
    /// Waits for closed windows and finds their prompts (roadmap M2.5); default a <see cref="WindowCloseVerifier"/> over
    /// <paramref name="windowService"/> alone, which finds Win32 prompts only.
    /// </param>
    public WindowManagerTools(
        IWindowService windowService,
        IForegroundService foregroundService,
        IInputSession? session = null,
        TimeSpan? answerTimeout = null,
        TimeSpan? gateWaitTimeout = null,
        IWindowCloseVerifier? closeVerifier = null)
    {
        _windowService = windowService;
        _foregroundService = foregroundService;
        _session = session;
        _answerTimeout = answerTimeout ?? UserConfirmation.DefaultAnswerTimeout;
        _gateWaitTimeout = gateWaitTimeout;
        _closeVerifier = closeVerifier ?? new WindowCloseVerifier(windowService);
    }

    [McpServerTool(Name = "move_window", Title = "Move Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move a window to a specific position. Returns success and the window's newBounds." + WindowChangeErrors +
                 " MoveFailed: Windows did not move it (error has the reason).")]
    public WindowOperationResult MoveWindow(
        [Description("Window handle (as integer) from find_windows")]
        long handle,
        [Description("X coordinate (left edge)")]
        int x,
        [Description("Y coordinate (top edge)")]
        int y,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.MoveWindow(hwnd, x, y, bringToFront));
    }

    [McpServerTool(Name = "resize_window", Title = "Resize Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Resize a window to specific dimensions. Returns success and the window's newBounds." + WindowChangeErrors +
                 " ResizeFailed: Windows did not resize it (error has the reason). InvalidArguments: width or height is not positive.")]
    public WindowOperationResult ResizeWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("New width in pixels (at least 1)")]
        int width,
        [Description("New height in pixels (at least 1)")]
        int height)
    {
        var hwnd = (nint)handle;
        return SizeRefusal(width, height) ?? Precheck(hwnd) ?? Change(hwnd, () => _windowService.ResizeWindow(hwnd, width, height));
    }

    [McpServerTool(Name = "set_window_bounds", Title = "Set Window Bounds", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move and resize a window in a single operation. Returns success and the window's newBounds." + WindowChangeErrors +
                 " MoveFailed: Windows did not place it (error has the reason). InvalidArguments: width or height is not positive.")]
    public WindowOperationResult SetWindowBounds(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("X coordinate (left edge)")]
        int x,
        [Description("Y coordinate (top edge)")]
        int y,
        [Description("New width in pixels (at least 1)")]
        int width,
        [Description("New height in pixels (at least 1)")]
        int height,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return SizeRefusal(width, height) ?? Precheck(hwnd) ??
               Change(hwnd, () => _windowService.SetWindowBounds(hwnd, x, y, width, height, bringToFront));
    }

    [McpServerTool(Name = "set_window_state", Title = "Minimize, Maximize or Restore Window", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the window state (minimize, maximize, restore). success is true only when the window was seen in that state " +
                 "afterwards (within 300 ms); newState reports the state it is in (minimized, maximized or normal)." + WindowChangeErrors +
                 " StateChangeFailed: the window did not reach the state (the app refused or delayed it, or it runs as administrator). " +
                 "An unknown state fails with InvalidState and the allowed values.")]
    public WindowOperationResult SetWindowState(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Desired state: 'minimize', 'maximize' or 'restore' ('minimized', 'maximized' and 'normal' are accepted too). " +
                     "Restore returns a minimized window to its previous state (normal, or maximized if it was maximized before) and makes a maximized window normal.")]
        WindowStateRequest state)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SetWindowState(hwnd, state), reportsState: true);
    }

    [McpServerTool(Name = "focus_window", Title = "Focus Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Bring a window to the foreground and verify it. Works around the Windows foreground lock without sending keys. " +
        "Returns isForeground (also as success), method (already, setForeground, attachThreadInput or inputUnlock), " +
        "the window actually in front (foregroundHandle, foregroundTitle, foregroundProcess) and blockingPopup, a dialog of the " +
        "window that takes keyboard input instead of it. inMenuMode: a menu is still open, so typed letters would pick menu items. " +
        "errorCode: FocusFailed, NotResponding, Cloaked, WindowHidden (the window is hidden, so keys would go to a window the user cannot see), " +
        "TargetDeniedByPolicy (the user's policy denies the app), PausedByUser or InvalidHandle. " +
        "The foreground can change again before your next call, so confirm with get_foreground_window immediately before keyboard or mouse input.")]
    public FocusResult FocusWindow(
        [Description("Window handle (as integer)")]
        long handle)
    {
        return FocusResult.From(_foregroundService.EnsureForeground((nint)handle));
    }

    /// <summary>close_windows_batch and close_application close at most this many windows without confirm=true.</summary>
    public const int MaxCloseBatchWithoutConfirm = 3;

    /// <summary>How long close_window and close_windows_batch wait for the windows to close by default (roadmap M2.5).</summary>
    public const int DefaultCloseWaitMs = 2000;

    /// <summary>The longest wait close_window, close_windows_batch and close_application accept.</summary>
    public const int MaxCloseWaitMs = 30000;

    /// <summary>How long close_application waits for its windows to close and its processes to exit by default.</summary>
    public const int DefaultApplicationCloseTimeoutMs = 5000;

    /// <summary>How long close_application waits at most for its processes to exit once their windows are gone.</summary>
    private static readonly TimeSpan MaxExitWait = TimeSpan.FromSeconds(2);

    private const string ForceDescription =
        "Close even a protected window: the desktop shell (Program Manager, the taskbars) or the agent's own host " +
        "(the terminal, editor or chat app this server was started from). Default false: such windows are refused " +
        "with errorCode ProtectedWindow. Set it only when the user explicitly asked to close that window.";

    private const string WaitDescription =
        "How long to wait for the window to close, in milliseconds (0-30000, default 2000). 0 only sends the close message " +
        "(status requested). The wait ends early when the window closes or shows a prompt.";

    private const string StatusDescription =
        "status: closed; hidden (the app hid the window instead of closing it, for example into the notification area; it " +
        "still runs: use close_application or kill_process to end it); stillOpen (errorCode StillOpen: the app is busy or " +
        "ignores the request); promptShown (errorCode PromptShown: a prompt such as 'Save changes?' waits; prompt gives its " +
        "handle, title, className, text, buttons and kind, and nothing was answered: ask the user, or answer it with the " +
        "element and input tools; kind inWindowDialog is drawn inside the window, so use the window's handle); requested " +
        "(waitMs 0: sent, not checked). success is true for closed, hidden and requested.";

    [McpServerTool(Name = "close_window", Title = "Close Window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close a window gracefully by sending WM_CLOSE, then wait up to waitMs and report what happened. " + StatusDescription + " " +
                 "Refuses the desktop shell and the agent's own terminal or editor window (errorCode ProtectedWindow) unless force is true, " +
                 "refuses an app the user's policy denies (TargetDeniedByPolicy; force does not override it), " +
                 "and reports AccessDenied when Windows blocks the message (the window belongs to an app running as administrator). " +
                 "InvalidArguments: waitMs is outside 0-30000. To close every window of an app, use close_application.")]
    public async Task<CloseWindowResult> CloseWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description(ForceDescription)]
        bool force = false,
        [Description(WaitDescription)]
        int waitMs = DefaultCloseWaitMs,
        CancellationToken cancellationToken = default)
    {
        if (WaitRefusal(waitMs, nameof(waitMs)) is { } invalid)
        {
            return new CloseWindowResult(false, Error: invalid, ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);
        }

        var hwnd = (nint)handle;
        if (!_windowService.IsValidWindow(hwnd))
        {
            return new CloseWindowResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle));
        }

        // Only the close message is sent inside the input gate; the wait for the window runs outside it, so a slow app or a
        // prompt does not hold up every input and window call for up to waitMs
        InputGateToolFilter.ThrowIfPaused(_session);
        WindowCloseResult sent;
        using (await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false))
        {
            sent = _windowService.CloseWindow(hwnd, force);
        }

        if (!sent.Success)
        {
            return new CloseWindowResult(false, Error: sent.Error, ErrorCode: sent.ErrorCode.ToString());
        }

        if (waitMs == 0)
        {
            return new CloseWindowResult(true, CloseNames.Of(CloseStatus.Requested));
        }

        var observed = await _closeVerifier.WaitForCloseAsync([hwnd], TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false);
        var outcome = Outcome(observed[0], waitMs);
        return new CloseWindowResult(outcome.Success, outcome.Status, outcome.Prompt, outcome.Error, outcome.ErrorCode);
    }

    [McpServerTool(Name = "close_windows_batch", Title = "Close Windows (Batch)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close multiple windows gracefully in a single batch operation, then wait up to waitMs for all of them and report each " +
                 "window's status as close_window does (closed, hidden, stillOpen, promptShown with its prompt, or requested). " +
                 "More than 3 windows need a confirmation: when the MCP client can ask the user (elicitation), the user is asked, with each " +
                 "window's title, process and PID, and nothing is closed unless they confirm (errorCode ConfirmationDeclined); otherwise " +
                 "confirm=true is required (ConfirmationRequired). A window that changed hands after the user confirmed is not closed " +
                 "(HandleMismatch). Protected windows (the desktop shell, the agent's own terminal or editor) " +
                 "are refused per window with errorCode ProtectedWindow unless force is true, and apps the user's policy denies with TargetDeniedByPolicy; " +
                 "the other windows are still closed. An item that is null fails alone with InvalidArguments. InvalidArguments (the whole " +
                 "call): waitMs is outside 0-30000.")]
    public async Task<BatchWindowResult> CloseWindowsBatch(
        [Description("Array of window handles to close")]
        WindowCloseRequest[] windows,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        [Description("Must be true to close more than 3 windows in one batch when the client cannot ask the user. Check the list (find_windows) first: closing many windows at once can lose unsaved work.")]
        bool confirm = false,
        [Description(ForceDescription + " Applies to every window in the batch.")]
        bool force = false,
        [Description("How long to wait for the windows to close after the last close message, in milliseconds (0-30000, default 2000); " +
                     "every window is waited for at the same time. 0 only sends the close messages (status requested).")]
        int waitMs = DefaultCloseWaitMs,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        RequireItems(windows, nameof(windows));
        if (WaitRefusal(waitMs, nameof(waitMs)) is { } invalid)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, invalid);
        }

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        Dictionary<nint, int?>? confirmedOwners = null;
        if (windows.Length > MaxCloseBatchWithoutConfirm)
        {
            if (UserConfirmation.CanAsk(server))
            {
                // Ask the user, not the model, and before the input gate: a waiting user must not hold up every input call
                var targets = windows.Where(w => w != null).Select(w => (Handle: (nint)w.Handle, Window: _windowService.GetWindowInfo((nint)w.Handle)))
                    .ToList();
                var answer = await UserConfirmation.AskAsync(server!, CloseQuestion(targets, force), $"Close these {targets.Count} windows",
                    _answerTimeout, cancellationToken).ConfigureAwait(false);
                if (!answer.Confirmed)
                {
                    throw new ToolErrorException(nameof(ConfirmationDeclined),
                        $"{answer.Reason} Nothing was closed. Ask the user how to continue.");
                }

                // Every handle the user was asked about, with its owner then (null: it was no window)
                confirmedOwners = targets.GroupBy(t => t.Handle).ToDictionary(g => g.Key, g => g.First().Window?.ProcessId);
            }
            else if (!confirm)
            {
                throw new ToolErrorException(
                    nameof(ConfirmationRequired),
                    $"close_windows_batch was asked to close {windows.Length} windows; more than {MaxCloseBatchWithoutConfirm} " +
                    "require confirm=true. Nothing was closed. Check the handles (find_windows) and call again with confirm=true.");
            }
        }

        List<WindowResultItem?> refusals;
        IReadOnlyList<WindowCloseResult> results;
        using (await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false))
        {
            // A window the user was asked about must still belong to the same process; a reused handle is not closed
            refusals = windows.Select((w, i) => w == null
                ? NullItem(i, nameof(windows), "a handle")
                : confirmedOwners == null ? null : HandleMismatchRefusal(w.Handle, confirmedOwners)).ToList();
            var handles = windows.Where((_, i) => refusals[i] == null).Select(w => (nint)w.Handle).ToList();

            results = await _windowService.CloseWindowsBatchAsync(handles, force, delayBetweenMs, cancellationToken).ConfigureAwait(false);
        }

        // The wait runs outside the gate, for every window at once
        var sent = results.Where(r => r.Success).Select(r => r.Handle).ToList();
        var observed = waitMs == 0 || sent.Count == 0
            ? new Dictionary<nint, CloseObservation>()
            : (await _closeVerifier.WaitForCloseAsync(sent, TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false))
                .GroupBy(o => o.Handle).ToDictionary(g => g.Key, g => g.First());

        return Summary(windows.Length, InOrder(refusals, results.Select(r =>
        {
            if (!r.Success)
            {
                return new WindowResultItem(r.Handle.ToInt64(), false, Error: r.Error, ErrorCode: r.ErrorCode.ToString());
            }

            if (!observed.TryGetValue(r.Handle, out var observation))
            {
                return new WindowResultItem(r.Handle.ToInt64(), true, Status: CloseNames.Of(CloseStatus.Requested));
            }

            var outcome = Outcome(observation, waitMs);
            return new WindowResultItem(r.Handle.ToInt64(), outcome.Success, Error: outcome.Error, ErrorCode: outcome.ErrorCode,
                Status: outcome.Status, Prompt: outcome.Prompt);
        })));
    }

    [McpServerTool(Name = "close_application", Title = "Close Application", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close an application gracefully: send WM_CLOSE to each of its windows the user can switch to (the Alt+Tab set: visible, " +
                 "not owned, not tool windows; never hidden helper or tray windows), then wait up to timeoutMs. It never forces: apps may ask " +
                 "to save, and kill_process is the way to end an app that will not close. Name the app by exactly one of handle (one of its " +
                 "windows), processId or processName (every process with that name that has such a window). A packaged (UWP) app is closed " +
                 "through its frame window, and its processId is the app's own (as find_windows reports it), not ApplicationFrameHost's. " +
                 "Returns status closed (every window gone; exited says whether the processes ended, runningProcessIds lists those that keep " +
                 "running, for example in the notification area), promptShown (errorCode PromptShown, promptDetected, and prompt with its " +
                 "handle, title, text and buttons: nothing was answered) or stillOpen (errorCode StillOpen), with windows (each window's " +
                 "status) and remainingWindows. Refused before anything is sent, with nothing closed: NoClosableWindow (no such window), " +
                 "ProtectedWindow (the agent's own host or the desktop shell; there is no force), TargetDeniedByPolicy, InvalidHandle, " +
                 "InvalidArguments. More than 3 windows need a confirmation as close_windows_batch does (ConfirmationDeclined, " +
                 "ConfirmationRequired), and a window that changed hands meanwhile is not closed (HandleMismatch).")]
    public async Task<CloseApplicationResult> CloseApplication(
        [Description("A window of the application (as integer); its process is closed")]
        long? handle = null,
        [Description("The application's process ID")]
        int? processId = null,
        [Description("The application's process name, with or without .exe (e.g. 'notepad'); matched exactly, ignoring case")]
        string? processName = null,
        [Description("How long to wait for the windows to close and the processes to exit, in milliseconds (0-30000, default 5000)")]
        int timeoutMs = DefaultApplicationCloseTimeoutMs,
        [Description("Must be true to close more than 3 windows when the client cannot ask the user. Check the windows (find_windows) first.")]
        bool confirm = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (WaitRefusal(timeoutMs, nameof(timeoutMs)) is { } invalidWait)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, invalidWait);
        }

        var name = ApplicationName(handle, processId, processName);

        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        var processIds = TargetProcesses(handle, processId, name, out var appWindows);
        var targets = appWindows.Where(w => processIds.Contains(w.ProcessId)).ToList();
        if (targets.Count == 0)
        {
            return ApplicationRefusal(processIds, nameof(ProcessErrorCode.NoClosableWindow),
                $"{Describe(handle, processId, name)} has no window the user can switch to (no visible, unowned window in Alt+Tab), " +
                "so there is nothing to close gracefully: it may run without windows or only in the notification area. Nothing was " +
                "sent. Check with find_windows or list_processes; kill_process ends a process without a window.");
        }

        // A close that would be refused is refused for the whole app before anything is asked or sent
        foreach (var target in targets)
        {
            if (_windowService.CheckClose(target.Handle, force: false) is { ErrorCode: not InvalidHandle } refused)
            {
                return ApplicationRefusal(processIds, refused.ErrorCode.ToString(),
                    $"{refused.Error} Nothing was closed: close_application closes all of an application's windows or none.");
            }
        }

        Dictionary<nint, int?>? confirmedOwners = null;
        if (targets.Count > MaxCloseBatchWithoutConfirm)
        {
            if (UserConfirmation.CanAsk(server))
            {
                var answer = await UserConfirmation.AskAsync(server!, ApplicationQuestion(targets), $"Close these {targets.Count} windows",
                    _answerTimeout, cancellationToken).ConfigureAwait(false);
                if (!answer.Confirmed)
                {
                    throw new ToolErrorException(nameof(ConfirmationDeclined),
                        $"{answer.Reason} Nothing was closed. Ask the user how to continue.");
                }

                confirmedOwners = targets.ToDictionary(t => t.Handle, t => (int?)t.ProcessId);
            }
            else if (!confirm)
            {
                throw new ToolErrorException(
                    nameof(ConfirmationRequired),
                    $"close_application would close {targets.Count} windows; more than {MaxCloseBatchWithoutConfirm} require confirm=true. " +
                    "Nothing was closed. Check the windows (find_windows) and call again with confirm=true.");
            }
        }

        var items = new Dictionary<nint, ClosedWindowItem>();
        var sent = new List<nint>();
        using (await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false))
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (confirmedOwners != null && HandleMismatchRefusal(target.Handle, confirmedOwners) is { } mismatch)
                {
                    items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, NotSent, mismatch.Error, mismatch.ErrorCode);
                    continue;
                }

                var result = _windowService.CloseWindow(target.Handle, force: false);
                if (!result.Success && result.ErrorCode == CloseFailed && WindowClassification.IsApplicationFrameWindow(target.ClassName) &&
                    await _closeVerifier.CloseWithUiAutomationAsync(target.Handle, cancellationToken).ConfigureAwait(false))
                {
                    // A packaged app's frame that did not take the message, asked through its Window pattern instead
                    result = WindowCloseResult.Sent(target.Handle);
                }

                if (result.Success)
                {
                    sent.Add(target.Handle);
                }
                else
                {
                    items[target.Handle] = new ClosedWindowItem(target.Handle, target.Title, NotSent, result.Error, result.ErrorCode.ToString());
                }
            }
        }

        // The waits run outside the gate
        var waited = System.Diagnostics.Stopwatch.StartNew();
        var observed = sent.Count == 0
            ? []
            : await _closeVerifier.WaitForCloseAsync(sent, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);
        foreach (var observation in observed)
        {
            var target = targets.First(t => t.Handle == observation.Handle);
            var outcome = Outcome(observation, timeoutMs);
            items[observation.Handle] = new ClosedWindowItem(target.Handle, target.Title, outcome.Status, outcome.Error, outcome.ErrorCode);
        }

        var prompt = observed.Select(o => o.Prompt).FirstOrDefault(p => p != null);
        var remaining = _windowService.GetApplicationWindows().Where(w => processIds.Contains(w.ProcessId))
            .Select(w => new RemainingWindowDto(w.Handle.ToInt64(), w.Title, w.ProcessId, w.ProcessName)).ToList();
        var windowItems = targets.Select(t => items[t.Handle]).ToList();
        var pids = processIds.Order().ToList();

        if (prompt != null)
        {
            return new CloseApplicationResult(false, CloseNames.Of(CloseStatus.PromptShown), pids, windowItems, remaining, true,
                ClosePromptDto.From(prompt),
                Error: "The application did not close: it shows a prompt (see prompt) that waits for an answer. Nothing was answered. " +
                       "Ask the user, or answer it with the element and input tools, then call close_application again.",
                ErrorCode: nameof(PromptShown));
        }

        if (remaining.Count > 0)
        {
            return new CloseApplicationResult(false, CloseNames.Of(CloseStatus.StillOpen), pids, windowItems, remaining, false,
                Error: $"{remaining.Count} window{(remaining.Count == 1 ? " is" : "s are")} still open after {timeoutMs} ms, without a " +
                       "prompt (see windows for why: a close Windows refused, or an app that is busy or ignores it). Check them with " +
                       "find_windows; kill_process ends an app that will not close.",
                ErrorCode: nameof(StillOpen));
        }

        var exitWait = TimeSpan.FromMilliseconds(timeoutMs) - waited.Elapsed;
        var running = await _closeVerifier.WaitForProcessesToExitAsync(
            pids, exitWait > MaxExitWait ? MaxExitWait : exitWait > TimeSpan.Zero ? exitWait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        return new CloseApplicationResult(true, CloseNames.Of(CloseStatus.Closed), pids, windowItems, remaining, false,
            Exited: running.Count == 0, RunningProcessIds: running.Count == 0 ? null : running);
    }

    /// <summary>The status of a window close_application did not send a close message to.</summary>
    private const string NotSent = "notSent";

    /// <summary>The success, status, prompt and error of a window after its close, as the close tools report it.</summary>
    private static (bool Success, string Status, ClosePromptDto? Prompt, string? Error, string? ErrorCode) Outcome(CloseObservation observation, int waitMs) =>
        observation.Status switch
        {
            CloseStatus.Closed or CloseStatus.Hidden or CloseStatus.Requested => (true, CloseNames.Of(observation.Status), null, null, null),
            CloseStatus.PromptShown => (false, CloseNames.Of(CloseStatus.PromptShown), ClosePromptDto.From(observation.Prompt),
                "The window did not close: it shows a prompt (see prompt) that waits for an answer. Nothing was answered. Ask the user, " +
                "or answer it with the element and input tools.", nameof(PromptShown)),
            _ => (false, CloseNames.Of(CloseStatus.StillOpen), null,
                $"The close message was sent, but the window was still open after {waitMs} ms and shows no prompt: the app may be busy " +
                "or slow, or ignore the request. Check it with find_windows, or wait longer.", nameof(StillOpen))
        };

    /// <summary>InvalidArguments text for a wait outside 0 to <see cref="MaxCloseWaitMs"/>, or null.</summary>
    private static string? WaitRefusal(int waitMs, string name) =>
        waitMs is >= 0 and <= MaxCloseWaitMs
            ? null
            : $"{name} must be between 0 and {MaxCloseWaitMs} milliseconds, but it is {waitMs}. Nothing was closed.";

    /// <summary>
    /// close_application's one target, checked: exactly one of handle, processId and processName, and the name without
    /// ".exe". Returns the name (null unless processName was given).
    /// </summary>
    private static string? ApplicationName(long? handle, int? processId, string? processName)
    {
        var given = (handle.HasValue ? 1 : 0) + (processId.HasValue ? 1 : 0) + (processName != null ? 1 : 0);
        if (given != 1)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"Pass exactly one of handle, processId and processName ({given} given). Nothing was closed.");
        }

        if (processId is <= 0)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"processId must be a positive process ID, but it is {processId}. Nothing was closed.");
        }

        if (processName == null)
        {
            return null;
        }

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length > 0
            ? name
            : throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, "processName is empty. Nothing was closed.");
    }

    /// <summary>The processes close_application closes, and the application windows of the desktop it chose them from.</summary>
    private HashSet<int> TargetProcesses(long? handle, int? processId, string? name, out IReadOnlyList<WindowInfo> appWindows)
    {
        if (handle is { } window)
        {
            // The handle's own process (for a UWP frame, the app's), whether or not the window itself is in Alt+Tab
            var info = _windowService.GetWindowInfo((nint)window)
                       ?? throw new ToolErrorException(nameof(InvalidHandle),
                           $"{WindowService.InvalidHandleMessage}: {window} is no window. Nothing was closed.");
            appWindows = _windowService.GetApplicationWindows();
            return [info.ProcessId];
        }

        appWindows = _windowService.GetApplicationWindows();
        return processId is { } id
            ? [id]
            : appWindows.Where(w => string.Equals(w.ProcessName, name, StringComparison.OrdinalIgnoreCase)).Select(w => w.ProcessId).ToHashSet();
    }

    private static string Describe(long? handle, int? processId, string? name) =>
        handle is { } h ? $"The application of window {h}" : processId is { } id ? $"Process {id}" : $"No process named '{name}'";

    private static CloseApplicationResult ApplicationRefusal(IEnumerable<int> processIds, string errorCode, string error) =>
        new(false, null, processIds.Order().ToList(), [], [], false, Error: error, ErrorCode: errorCode);

    /// <summary>The question close_application asks the user: the application's windows, with their titles, processes and PIDs.</summary>
    private static string ApplicationQuestion(IReadOnlyList<WindowInfo> targets)
    {
        var apps = string.Join(", ", targets.Select(t => $"{t.ProcessName} (PID {t.ProcessId})").Distinct());
        var lines = targets.Take(20).Select((w, i) =>
            $"{i + 1}. {UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId})");
        var more = targets.Count > 20 ? $"\n... and {targets.Count - 20} more" : string.Empty;
        return $"The AI agent asks to close {apps}: {targets.Count} windows. Each may ask to save your work, or close without asking.\n" +
               string.Join("\n", lines) + more + "\nAllow it?";
    }

    [McpServerTool(Name = "snap_window", Title = "Snap Window", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap a window to predefined screen positions (like Windows Snap Assist). Supported positions: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen; any other position fails with errorCode InvalidSnapPosition. Returns success and the window's newBounds." + WindowChangeErrors +
                 " InvalidMonitorIndex: monitorIndex is not a monitor (error gives the valid range). MoveFailed: Windows did not place it.")]
    public WindowOperationResult SnapWindow(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Snap position: left_half, right_half, top_half, bottom_half, top_left_quarter, top_right_quarter, bottom_left_quarter, bottom_right_quarter, left_third, center_third, right_third, left_two_thirds, right_two_thirds, fullscreen")]
        SnapPosition position,
        [Description("Target monitor index (0-based, see get_monitors). Defaults to the window's current monitor. An index that is not a monitor fails with InvalidMonitorIndex.")]
        int? monitorIndex = null,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.SnapWindow(hwnd, position, monitorIndex, bringToFront));
    }

    [McpServerTool(Name = "move_window_to_monitor", Title = "Move Window to Monitor", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move a window to a specific monitor, optionally positioning it. Returns success and the window's newBounds." + WindowChangeErrors +
                 " InvalidMonitorIndex: monitorIndex is not a monitor (error gives the valid range). MoveFailed: Windows did not place it, " +
                 "or with positioning 'maximize' it was not maximized on that monitor. An unknown positioning fails with InvalidPositioning.")]
    public WindowOperationResult MoveWindowToMonitor(
        [Description("Window handle (as integer)")]
        long handle,
        [Description("Target monitor index (0-based, see get_monitors)")]
        int monitorIndex,
        [Description("Position on the monitor: 'center' (default), 'topleft', 'topright', 'bottomleft', 'bottomright' (each at the window's current size), 'maximize' (moved there, then maximized) or 'restore' (current size, at the work area's top-left corner)")]
        MonitorPositioning positioning = MonitorPositioning.Center,
        [Description("Raise the window to the top of the z-order without activating it: keyboard input does not move to it (use focus_window for that). A minimized window is restored first, and restoring can activate it; positioning 'maximize' ignores bringToFront and can activate it too. Default: true")]
        bool bringToFront = true)
    {
        var hwnd = (nint)handle;
        return Precheck(hwnd) ?? Change(hwnd, () => _windowService.MoveWindowToMonitor(hwnd, monitorIndex, positioning, bringToFront));
    }

    [McpServerTool(Name = "set_windows_bounds_batch", Title = "Set Window Bounds (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Move and resize multiple windows in a single batch operation. More efficient than multiple individual calls." + BatchErrors +
                 " MoveFailed: Windows did not place the window.")]
    public async Task<BatchWindowResult> SetWindowsBoundsBatch(
        [Description("Array of window placements with handle, x, y, width, height, and optional bringToFront (raises the window without activating it; a minimized window is restored first, and restoring can activate it)")]
        WindowBoundsPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));

        // Items that are invalid, or for windows the policy denies, get their result here; the others go to the service
        var refusals = placements.Select((p, i) => p == null
            ? NullItem(i, nameof(placements), "handle, x, y, width and height")
            : ItemSizeRefusal(p.Handle, p.Width, p.Height) ?? BatchPolicyRefusal(p.Handle)).ToList();
        var servicePlacements = placements.Where((_, i) => refusals[i] == null).Select(p =>
            ((nint)p.Handle, p.X, p.Y, p.Width, p.Height, p.BringToFront)).ToList();

        var results = await _windowService.SetWindowBoundsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
    }

    [McpServerTool(Name = "snap_windows_batch", Title = "Snap Windows (Batch)", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Snap multiple windows to predefined positions in a single batch operation. More efficient than multiple individual calls." + BatchErrors +
                 " InvalidSnapPosition: the item's position is missing or unknown (error lists the positions). InvalidMonitorIndex: its monitorIndex is not a monitor. MoveFailed: Windows did not place the window.")]
    public async Task<BatchWindowResult> SnapWindowsBatch(
        [Description("Array of snap placements with handle, position, optional monitorIndex, and optional bringToFront (raises the window without activating it; a minimized window is restored first, and restoring can activate it)")]
        WindowSnapPlacement[] placements,
        [Description("Delay between operations in milliseconds (0-5000, default: 50)")]
        int delayBetweenMs = 50,
        CancellationToken cancellationToken = default)
    {
        RequireItems(placements, nameof(placements));

        // An item's position is checked here, so one bad item fails alone instead of failing the call
        var positions = new SnapPosition[placements.Length];
        var refusals = placements.Select((p, i) =>
        {
            if (p == null)
                return NullItem(i, nameof(placements), "handle and position");

            return ToolEnum<SnapPosition>.TryParse(p.Position, out positions[i], out var error)
                ? BatchPolicyRefusal(p.Handle)
                : new WindowResultItem(p.Handle, false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidSnapPosition));
        }).ToList();
        var servicePlacements = placements.Select((p, i) => (Placement: p, Index: i)).Where(x => refusals[x.Index] == null)
            .Select(x => ((nint)x.Placement.Handle, positions[x.Index], x.Placement.MonitorIndex, x.Placement.BringToFront))
            .ToList();

        var results = await _windowService.SnapWindowsBatchAsync(servicePlacements, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(placements.Length, InOrder(refusals, results.Select(r => Item(r))));
    }

    [McpServerTool(Name = "set_windows_state_batch", Title = "Set Window States (Batch)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Set the state (minimize, maximize, restore) of multiple windows in a single batch operation. Each item's success is true only " +
                 "when its window was seen in that state afterwards, and newState reports the state it is in." + BatchErrors +
                 " InvalidState: the item's state is missing or unknown (error lists the states). StateChangeFailed: the window did not reach the state.")]
    public async Task<BatchWindowResult> SetWindowsStateBatch(
        [Description("Array of state changes with handle and state ('minimize', 'maximize' or 'restore'; 'minimized', 'maximized' and 'normal' are accepted too)")]
        WindowStateChange[] changes,
        [Description("Delay between operations in milliseconds (0-5000, default: 30)")]
        int delayBetweenMs = 30,
        CancellationToken cancellationToken = default)
    {
        RequireItems(changes, nameof(changes));

        var states = new WindowStateRequest[changes.Length];
        var refusals = changes.Select((c, i) =>
        {
            if (c == null)
                return NullItem(i, nameof(changes), "handle and state");

            return ToolEnum<WindowStateRequest>.TryParse(c.State, out states[i], out var error)
                ? BatchPolicyRefusal(c.Handle)
                : new WindowResultItem(c.Handle, false, Error: $"Item {i}: {error}", ErrorCode: nameof(InvalidState));
        }).ToList();
        var serviceChanges = changes.Select((c, i) => (Change: c, Index: i)).Where(x => refusals[x.Index] == null)
            .Select(x => ((nint)x.Change.Handle, states[x.Index]))
            .ToList();

        var results = await _windowService.SetWindowStateBatchAsync(serviceChanges, delayBetweenMs, cancellationToken).ConfigureAwait(false);

        return Summary(changes.Length, InOrder(refusals, results.Select(r => Item(r, reportsState: true))));
    }

    /// <summary>The question close_windows_batch asks the user: every window, with its title, process and PID.</summary>
    private static string CloseQuestion(IReadOnlyList<(nint Handle, WindowInfo? Window)> targets, bool force)
    {
        var lines = targets.Take(20).Select((t, i) => t.Window is { } w
            ? $"{i + 1}. {UserConfirmation.Quote(string.IsNullOrEmpty(w.Title) ? w.ClassName : w.Title)} ({w.ProcessName}, PID {w.ProcessId}){(w.IsAgentHost ? ", the agent's own window" : string.Empty)}"
            : $"{i + 1}. window {t.Handle} (no longer exists)");
        var more = targets.Count > 20 ? $"\n... and {targets.Count - 20} more" : string.Empty;
        var protection = force
            ? " force is set: protected windows (the desktop shell, the agent's own terminal or editor) are closed too."
            : string.Empty;
        return $"The AI agent asks to close {targets.Count} windows. Each app may ask to save your work, or close without asking.{protection}\n" +
               string.Join("\n", lines) + more + "\nAllow it?";
    }

    /// <summary>
    /// HandleMismatch for a window the user did not see: it belongs to another process than when the user confirmed, or
    /// it was no window then and is one now (its handle was reused), or null. A window that is gone is left to the
    /// close, which reports InvalidHandle.
    /// </summary>
    private WindowResultItem? HandleMismatchRefusal(long handle, IReadOnlyDictionary<nint, int?> confirmedOwners)
    {
        if (!confirmedOwners.TryGetValue((nint)handle, out var asked) || _windowService.GetWindowInfo((nint)handle) is not { } now ||
            now.ProcessId == asked)
        {
            return null;
        }

        var then = asked is { } pid
            ? $"not to the process (PID {pid}) the user confirmed: the window closed and its handle was reused"
            : "but it was no window when the user was asked (the user saw it as 'no longer exists'): a new window got its handle";
        return new WindowResultItem(handle, false,
            Error: $"Window {handle} now belongs to {now.ProcessName} (PID {now.ProcessId}), {then}. It was not closed.",
            ErrorCode: nameof(HandleMismatch));
    }

    /// <summary>
    /// The refusal of a change before anything is sent: InvalidHandle, or TargetDeniedByPolicy for a window the user's
    /// target policy denies (these tools never restore, raise or move such a window). Null when the change can go ahead.
    /// </summary>
    private WindowOperationResult? Precheck(nint handle)
    {
        if (!_windowService.IsValidWindow(handle))
        {
            return new WindowOperationResult(false, Error: WindowService.InvalidHandleMessage, ErrorCode: nameof(InvalidHandle));
        }

        return _windowService.EvaluateTargetPolicy(handle) is { Allowed: false } verdict
            ? new WindowOperationResult(false, Error: WindowService.WindowChangeRefusal(handle, verdict), ErrorCode: nameof(TargetDeniedByPolicy))
            : null;
    }

    /// <summary>
    /// Runs a window change and reports its outcome with the window as it is afterwards: its bounds, or with
    /// <paramref name="reportsState"/> its state. A window that did not answer in time (<see cref="WindowNotRespondingException"/>,
    /// from the service's <see cref="IWindowCallRunner"/>) is NotResponding: the application is hung or busy, and the
    /// tool returns instead of holding the input gate until it answers.
    /// </summary>
    private WindowOperationResult Change(nint handle, Func<WindowChangeResult> change, bool reportsState = false)
    {
        try
        {
            var result = change();
            var window = _windowService.GetWindowInfo(handle);
            return new WindowOperationResult(
                Success: result.Success,
                NewBounds: reportsState ? null : Bounds(window),
                NewState: reportsState ? State(window) : null,
                Error: result.Error,
                ErrorCode: result.ErrorCodeName);
        }
        catch (WindowNotRespondingException ex)
        {
            return new WindowOperationResult(false, Error: ex.Message, ErrorCode: nameof(NotResponding));
        }
    }

    /// <summary>A batch item for the service's result, with the window's bounds (or state) as it is afterwards.</summary>
    private WindowResultItem Item(WindowChangeResult result, bool reportsState = false)
    {
        // An invalid handle has nothing to read; any other window is read, failed or not, so the agent sees where it is
        var window = result.ErrorCode == InvalidHandle ? null : _windowService.GetWindowInfo(result.Handle);
        return new WindowResultItem(
            Handle: result.Handle.ToInt64(),
            Success: result.Success,
            NewBounds: reportsState ? null : Bounds(window),
            NewState: reportsState ? State(window) : null,
            Error: result.Error,
            ErrorCode: result.ErrorCodeName);
    }

    private static BoundsDto? Bounds(WindowInfo? window) =>
        window == null ? null : new BoundsDto(window.Bounds.X, window.Bounds.Y, window.Bounds.Width, window.Bounds.Height);

    private static string? State(WindowInfo? window) => window?.State.ToString().ToLowerInvariant();

    /// <summary>InvalidArguments when a size is not positive (roadmap M2.2): nothing is sent.</summary>
    private static WindowOperationResult? SizeRefusal(int width, int height) =>
        width >= 1 && height >= 1
            ? null
            : new WindowOperationResult(false, Error: SizeMessage(width, height), ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    private static WindowResultItem? ItemSizeRefusal(long handle, int width, int height) =>
        width >= 1 && height >= 1
            ? null
            : new WindowResultItem(handle, false, Error: SizeMessage(width, height), ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    private static string SizeMessage(int width, int height) =>
        $"width and height must be at least 1 pixel, but they are {width} x {height}. Nothing was changed.";

    /// <summary>
    /// The batch's array argument itself: an explicit null fails the call with InvalidArguments, as a missing one does.
    /// </summary>
    private static void RequireItems<T>(T[]? items, string name)
    {
        if (items == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"{name} must be an array of items, not null. Nothing was done.");
        }
    }

    /// <summary>The result of a batch item that is null: it fails alone with InvalidArguments (roadmap M2.1).</summary>
    private static WindowResultItem NullItem(int index, string array, string fields) =>
        new(0, false, Error: $"Item {index} of {array} is null; each item needs {fields}. Nothing was done for it.",
            ErrorCode: ToolErrorReporting.InvalidArgumentsErrorCode);

    /// <summary>A batch item for a window the target policy denies, or null when the window is allowed or not a window.</summary>
    private WindowResultItem? BatchPolicyRefusal(long handle) =>
        _windowService.IsValidWindow((nint)handle) && Precheck((nint)handle) is { } denied
            ? new WindowResultItem(Handle: handle, Success: false, Error: denied.Error, ErrorCode: denied.ErrorCode)
            : null;

    /// <summary>
    /// The batch's items in request order: the refusal where there is one, and the service's results, in order,
    /// for the other requests.
    /// </summary>
    private static List<WindowResultItem> InOrder(IReadOnlyList<WindowResultItem?> refusals, IEnumerable<WindowResultItem> results)
    {
        using var done = results.GetEnumerator();
        var items = new List<WindowResultItem>(refusals.Count);
        foreach (var refusal in refusals)
        {
            if (refusal != null)
            {
                items.Add(refusal);
            }
            else if (done.MoveNext())
            {
                items.Add(done.Current);
            }
        }

        return items;
    }

    private static BatchWindowResult Summary(int requested, IReadOnlyList<WindowResultItem> items) => new(
        TotalRequested: requested,
        Succeeded: items.Count(i => i.Success),
        Failed: items.Count(i => !i.Success),
        Results: items);
}

public record WindowOperationResult(
    bool Success,
    BoundsDto? NewBounds = null,
    string? NewState = null,
    string? Error = null,
    string? ErrorCode = null
);
