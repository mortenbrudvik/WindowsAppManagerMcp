using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// How long <see cref="ForegroundService"/> waits for Windows to show a window in front.
/// </summary>
/// <param name="StepSettle">How long each step waits for its activation to show before the next step is tried.</param>
/// <param name="PollInterval">How often the foreground window is read while waiting.</param>
/// <param name="MenuSettle">How long to wait for a menu to close after the Esc.</param>
/// <param name="ThreadGrace">
/// How much longer than the caller's timeout the activation thread may take before the call gives up on
/// it: the window calls themselves can block until a window answers.
/// </param>
public sealed record ForegroundTiming(TimeSpan StepSettle, TimeSpan PollInterval, TimeSpan MenuSettle, TimeSpan ThreadGrace)
{
    /// <summary>The timing the server uses.</summary>
    public static ForegroundTiming Default { get; } = new(
        StepSettle: TimeSpan.FromMilliseconds(200),
        PollInterval: TimeSpan.FromMilliseconds(15),
        MenuSettle: TimeSpan.FromMilliseconds(250),
        ThreadGrace: TimeSpan.FromSeconds(2));
}

/// <summary>
/// Verified window activation (see <see cref="IForegroundService.EnsureForeground"/>). The activation
/// steps run on the dedicated thread of <see cref="IForegroundThread"/>; reading the result back runs on
/// the caller's thread.
/// </summary>
public sealed class ForegroundService : IForegroundService
{
    /// <summary>The default total wait, in milliseconds.</summary>
    public const int DefaultTimeoutMs = 1000;

    /// <summary>The longest total wait, in milliseconds.</summary>
    public const int MaxTimeoutMs = 10_000;

    /// <summary>WS_DISABLED in GWL_STYLE: the window takes no input, usually because a modal dialog is open.</summary>
    private const long WsDisabled = 0x08000000;

    private readonly INativeWindowWrapper _windows;
    private readonly INativeThreadInputWrapper _threads;
    private readonly INativeInputWrapper _input;
    private readonly IProcessSecurityProbe _security;
    private readonly IWindowService _windowService;
    private readonly IForegroundThread _activationThread;
    private readonly ForegroundTiming _timing;
    private readonly ILogger<ForegroundService>? _logger;
    private readonly IInputSession? _session;

    /// <param name="session">
    /// The input session (roadmap M1.10). With it, activation is refused while the user has paused input
    /// (PausedByUser), and the input unlock and the Esc go through it, so a pause also stops them. The server
    /// always passes it; without it (tests of the activation steps) input goes straight to <paramref name="input"/>.
    /// </param>
    public ForegroundService(
        INativeWindowWrapper windows,
        INativeThreadInputWrapper threads,
        INativeInputWrapper input,
        IProcessSecurityProbe security,
        IWindowService windowService,
        IForegroundThread activationThread,
        ForegroundTiming? timing = null,
        ILogger<ForegroundService>? logger = null,
        IInputSession? session = null)
    {
        _windows = windows;
        _threads = threads;
        _input = input;
        _security = security;
        _windowService = windowService;
        _activationThread = activationThread;
        _timing = timing ?? ForegroundTiming.Default;
        _logger = logger;
        _session = session;
    }

    /// <inheritdoc />
    public FocusOutcome EnsureForeground(nint handle, int timeoutMs = DefaultTimeoutMs)
    {
        if (handle == nint.Zero || !_windows.IsWindow(handle))
        {
            return Describe(handle, Activation.Failed(
                root: nint.Zero, _windows.GetForegroundWindow(), WindowErrorCode.InvalidHandle, "Invalid window handle"));
        }

        // Activating takes the keyboard focus from the user and can send input: not while the user has paused input
        if (_session?.IsPaused == true)
        {
            return Describe(handle, Paused(RootOf(handle), _windows.GetForegroundWindow()));
        }

        // The user's policy: a denied window is not restored or brought to the front, where a screenshot shows it
        var targetRoot = RootOf(handle);
        if (PolicyRefusal(targetRoot, targetRoot) is { } denied)
        {
            return Describe(handle, denied);
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 0, MaxTimeoutMs));
        Activation activation;
        try
        {
            activation = _activationThread.Run(token => Activate(handle, timeout, token), timeout + _timing.ThreadGrace);
        }
        catch (ForegroundThreadBusyException ex)
        {
            // Nothing was tried, and no window was slow: an earlier activation still held the thread
            activation = Activation.Failed(RootOf(handle), _windows.GetForegroundWindow(), WindowErrorCode.FocusFailed, ex.Message);
        }
        catch (TimeoutException ex)
        {
            activation = Activation.Failed(RootOf(handle), _windows.GetForegroundWindow(), WindowErrorCode.NotResponding, ex.Message);
        }

        return Describe(handle, activation);
    }

    /// <inheritdoc />
    public bool IsInFront(nint handle) =>
        handle != nint.Zero && _windows.IsWindow(handle) && IsInFront(RootOf(handle), _windows.GetForegroundWindow());

    /// <summary>The activation steps. Runs on the activation thread.</summary>
    private Activation Activate(nint handle, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Deadline.After(timeout);
        var root = RootOf(handle);

        // The first USER call on this thread also gives it the message queue AttachThreadInput needs
        var foreground = _windows.GetForegroundWindow();

        // SetForegroundWindow can activate a hidden window, and keys would then go to a window the user cannot see
        if (!_windows.IsWindowVisible(root))
        {
            return Activation.Failed(root, foreground, WindowErrorCode.WindowHidden,
                "The window is hidden (an app that hid it in the notification area, for example), so it was not activated: " +
                "keys would go to a window the user cannot see. Show it from the app or its notification icon first, or ask the user.");
        }

        if (IsInFront(root, foreground) && !_windows.IsIconic(root))
        {
            // A menu open in a window that was already in front is the user's: report it, leave it open
            return Activation.InFront(root, FocusMethod.Already, foreground, menuClosed: false, IsInMenuMode(foreground));
        }

        if (_windows.IsHungAppWindow(root))
        {
            return Activation.Failed(root, foreground, WindowErrorCode.NotResponding,
                "The window is not responding (its thread has not processed messages for several seconds), so it was not activated.");
        }

        if (_windows.IsCloaked(root))
        {
            return Activation.Failed(root, foreground, WindowErrorCode.Cloaked,
                "The window is cloaked: it is on another virtual desktop or its app has hidden it, so it was not activated.");
        }

        // The user's policy for the windows the restore and the activation would put on screen, before anything changes
        var allowed = new HashSet<nint>();
        if (OwnedWindowRefusal(root, allowed) is { } deniedOwned)
        {
            return deniedOwned;
        }

        // Every step below first checks that the caller is still waiting: a call to another thread's
        // window (ShowWindow, BringWindowToTop) can block past the deadline, and once the caller has given
        // up no further step is taken. The blocked call itself still completes, and SW_RESTORE and
        // BringWindowToTop activate, so the window can still come to the front late: callers check
        // IsInFront again before input. Each step also checks that the user has not paused input meanwhile.
        if (Interrupted(root, foreground, token) is { } beforeRestore)
        {
            return beforeRestore;
        }

        var restored = false;
        if (_windows.IsIconic(root))
        {
            _windows.ShowWindow(root, NativeEnums.SW_RESTORE);
            restored = true;
        }

        // A window that a modal dialog disables takes no input; activate the dialog, as the taskbar does
        var activate = root;
        if (IsDisabled(root) && EnabledPopup(root) is var popup && popup != nint.Zero)
        {
            // Checked above, unless the dialog only showed up now (it opened meanwhile, or the restore made it visible)
            if (!allowed.Contains(popup) && PolicyRefusal(root, popup, restored) is { } deniedPopup)
            {
                return deniedPopup;
            }

            activate = popup;
        }

        var steps = new List<string>();

        // Step 1: SetForegroundWindow, which the foreground lock may refuse
        if (Interrupted(root, foreground, token) is { } beforeStep1)
        {
            return beforeStep1;
        }
        var accepted = _windows.SetForegroundWindow(activate);
        if (WaitUntilInFront(root, accepted ? _timing.StepSettle : TimeSpan.Zero, deadline, token, out foreground))
        {
            return Activated(root, FocusMethod.SetForeground, foreground, token);
        }
        steps.Add(accepted
            ? "SetForegroundWindow reported success, but the window did not come to the front"
            : "SetForegroundWindow was refused (foreground lock)");

        // Step 2: share the foreground thread's input, which the foreground lock allows to activate
        if (Interrupted(root, foreground, token) is { } beforeStep2)
        {
            return beforeStep2;
        }
        if (AttachSkipReason(foreground, out var foregroundThread) is { } skipReason)
        {
            steps.Add("attaching to the foreground thread was skipped: " + skipReason);
        }
        else
        {
            var currentThread = _threads.GetCurrentThreadId();
            if (_threads.AttachThreadInput(currentThread, foregroundThread, attach: true))
            {
                try
                {
                    _windows.BringWindowToTop(activate);
                    accepted = Interrupted(root, foreground, token) == null && _windows.SetForegroundWindow(activate);
                }
                finally
                {
                    Detach(currentThread, foregroundThread);
                }

                if (WaitUntilInFront(root, accepted ? _timing.StepSettle : TimeSpan.Zero, deadline, token, out foreground))
                {
                    return Activated(root, FocusMethod.AttachThreadInput, foreground, token);
                }
                steps.Add("SetForegroundWindow with the input attached to the foreground thread did not bring it to the front");
            }
            else
            {
                steps.Add("Windows refused to attach to the foreground thread");
            }
        }

        // Step 3, last resort: become the process that received the last input. A zero-distance mouse
        // move presses no key and no button and moves nothing; a key (Alt in particular) would reach the
        // window in front, often the agent's own terminal.
        if (Interrupted(root, foreground, token) is { } beforeStep3)
        {
            return beforeStep3;
        }
        if (!_security.IsInputDesktopAvailable())
        {
            steps.Add("the input unlock was skipped: the input desktop is not available (locked session or a UAC prompt)");
        }
        else
        {
            if (Inject([InputEvent.Mouse(NativeEnums.MOUSEEVENTF_MOVE)]) is not { } sent)
            {
                return Paused(root, foreground);
            }

            _windows.SetForegroundWindow(activate);
            if (WaitUntilInFront(root, Timeout.InfiniteTimeSpan, deadline, token, out foreground))
            {
                return Activated(root, FocusMethod.InputUnlock, foreground, token);
            }
            steps.Add(sent == 0
                ? "the input unlock was blocked (SendInput sent nothing)"
                : "the input unlock (a zero-distance mouse move) did not help");
        }

        return Activation.Failed(root, _windows.GetForegroundWindow(), WindowErrorCode.FocusFailed, string.Join("; ", steps) + ".");
    }

    /// <summary>
    /// The window is in front after an activation step. If it is in menu mode, keys would select menu
    /// items, so one Esc closes the menu, but only while the window is verified to be in front (Esc goes
    /// to whatever window has keyboard focus) and no modifier key is down (Esc would be a shortcut). There is no second Esc: nothing shows whether the window
    /// has processed the first one yet, and an Esc that arrives after its menu has closed reaches the
    /// window itself (IDCANCEL in a dialog). A menu that is still open afterwards is reported instead.
    /// </summary>
    private Activation Activated(nint root, string method, nint foreground, CancellationToken token)
    {
        // Paused while the step ran: report the pause, not a success the caller would act on
        if (_session?.IsPaused == true)
        {
            return Paused(root, foreground);
        }

        var escapeSent = false;
        if (!token.IsCancellationRequested && IsInMenuMode(foreground))
        {
            foreground = _windows.GetForegroundWindow();
            if (IsInFront(root, foreground) && ModifiersDown())
            {
                // With a modifier down the Esc would be a shortcut (Ctrl+Esc opens Start, Alt+Esc sends the window
                // back, Ctrl+Shift+Esc opens Task Manager): leave the menu open and report it, as the input tools
                // refuse with ModifiersHeld
                _logger?.LogInformation("A menu of window {Window} was left open: a modifier key is down", root);
            }
            else if (IsInFront(root, foreground))
            {
                if (Inject(
                    [
                        InputEvent.Key(NativeEnums.VK_ESCAPE, NativeEnums.KEYEVENTF_KEYDOWN),
                        InputEvent.Key(NativeEnums.VK_ESCAPE, NativeEnums.KEYEVENTF_KEYUP)
                    ]) is not { } escape)
                {
                    return Paused(root, foreground);
                }

                escapeSent = escape != 0;
                if (escapeSent)
                {
                    var menuWindow = foreground;
                    WaitUntil(() => !IsInMenuMode(menuWindow), _timing.MenuSettle, token);
                }
            }
        }

        // Verify once more: the outcome reports what is in front now, and whether it is in menu mode
        foreground = _windows.GetForegroundWindow();
        if (!IsInFront(root, foreground))
        {
            return Activation.Failed(root, foreground, WindowErrorCode.FocusFailed,
                "The window came to the front but lost the foreground again.");
        }

        var inMenuMode = IsInMenuMode(foreground);
        return Activation.InFront(root, method, foreground, menuClosed: escapeSent && !inMenuMode, inMenuMode);
    }

    /// <summary>
    /// Undoes the attach, from the thread that made it. If Windows refuses, this thread may still share
    /// the other thread's input state (often the agent's terminal's), so it is retired: it exits after
    /// this activation, and the next one starts on a new, unattached thread.
    /// </summary>
    private void Detach(uint currentThread, uint foregroundThread)
    {
        if (_threads.AttachThreadInput(currentThread, foregroundThread, attach: false))
        {
            return;
        }

        _logger?.LogWarning(
            "Detaching the window activation thread {CurrentThread} from thread {ForegroundThread} failed; the activation thread is replaced",
            currentThread, foregroundThread);
        _activationThread.RetireCurrentThread();
    }

    /// <summary>
    /// Sends activation input (the zero-distance move, the Esc) through the input session, so the user's pause
    /// stops it and an interrupted batch is released. The number of events inserted, or null when input is paused.
    /// </summary>
    private uint? Inject(IReadOnlyList<InputEvent> events)
    {
        if (_session == null)
        {
            return _input.SendInput(events);
        }

        var outcome = _session.Send(events);
        return outcome.PausedByUser ? null : (uint)outcome.Inserted;
    }

    /// <summary>
    /// True when Windows reports a modifier key down (Shift, Ctrl, Alt, Windows): the user's, or stuck. Read
    /// through the input session when there is one, as the input tools read it before each batch.
    /// </summary>
    private bool ModifiersDown() =>
        _session != null ? _session.ReadModifiersDown().Count > 0 : KeyNames.SidedModifiers.Any(_input.IsKeyDown);

    /// <summary>
    /// The refusal when the target policy denies a window that restoring or activating <paramref name="root"/> would
    /// put on screen, checked before anything changes: the modal dialog that activation goes to when one disables the
    /// root (it can belong to another app, such as a credential prompt), and, for a minimized root, every window it
    /// owns, because the restore shows them again. Windows hides the windows a minimized window owns, and
    /// GW_ENABLEDPOPUP finds only visible ones, so those are found by enumerating the top-level windows. The windows
    /// the policy allows are added to <paramref name="allowed"/>. Null when it allows them all.
    /// </summary>
    private Activation? OwnedWindowRefusal(nint root, HashSet<nint> allowed)
    {
        var owned = new List<nint>();
        if (IsDisabled(root) && EnabledPopup(root) is var popup && popup != nint.Zero)
        {
            owned.Add(popup);
        }

        if (_windows.IsIconic(root))
        {
            _windows.EnumWindows(window =>
            {
                if (window != root && !owned.Contains(window) && WindowOwnerChain.IsOwnedBy(_windows, window, root))
                {
                    owned.Add(window);
                }

                return true;
            });
        }

        foreach (var window in owned)
        {
            if (PolicyRefusal(root, window) is { } refusal)
            {
                return refusal;
            }

            allowed.Add(window);
        }

        return null;
    }

    /// <summary>
    /// The refusal when the server's target policy denies <paramref name="window"/>: <paramref name="root"/> itself,
    /// or a window it owns that restoring or activating it would put on screen. Null when the policy allows it.
    /// <paramref name="restored"/>: the minimized root was already restored when the window was found.
    /// </summary>
    private Activation? PolicyRefusal(nint root, nint window, bool restored = false)
    {
        if (_windowService.EvaluateTargetPolicy(window) is not { Allowed: false } verdict)
        {
            return null;
        }

        var refusal = window == root
            ? $"The window is refused by the server's target policy: {verdict.Reason}. Nothing was restored or activated."
            : $"Window {window}, which the window owns (the modal dialog that activating it would bring to the front, or a window " +
              $"that restoring it would show), is refused by the server's target policy: {verdict.Reason}. " +
              (restored
                  ? "The minimized window was restored before that dialog was found (it was not among the windows it owned when " +
                    "the restore was checked), so the dialog may be on screen now; nothing was activated."
                  : "Nothing was restored or activated.");
        return Activation.Failed(root, _windows.GetForegroundWindow(), WindowErrorCode.TargetDeniedByPolicy,
            refusal + " The policy is set by the user in the server's environment and no tool argument changes it; ask the user to do this step.");
    }

    /// <summary>
    /// Why no further activation step may be taken: the caller stopped waiting, or the user paused input (the
    /// steps take the foreground from the user). Null when the next step may run.
    /// </summary>
    private Activation? Interrupted(nint root, nint foreground, CancellationToken token) =>
        token.IsCancellationRequested ? Activation.Stopped(root, foreground)
        : _session?.IsPaused == true ? Paused(root, foreground)
        : null;

    private static Activation Paused(nint root, nint foreground) =>
        Activation.Failed(root, foreground, WindowErrorCode.PausedByUser,
            "Input is paused: the user pressed the pause hotkey, so the window was not activated, or activation stopped part-way " +
            "(activating takes the keyboard focus from the user). Stop and ask the user how to continue; only the user can resume.");

    /// <summary>Why the attach step must be skipped, or null when it may run.</summary>
    private string? AttachSkipReason(nint foreground, out uint foregroundThread)
    {
        foregroundThread = 0;
        if (foreground == nint.Zero)
        {
            return "no window is in the foreground";
        }

        if (_windows.IsHungAppWindow(foreground))
        {
            return "the foreground window is not responding";
        }

        foregroundThread = _threads.GetWindowThreadId(foreground);
        if (foregroundThread == 0 || foregroundThread == _threads.GetCurrentThreadId())
        {
            return "the foreground window's thread cannot be attached to";
        }

        var foregroundIntegrity = _security.GetIntegrity((int)_windows.GetWindowThreadProcessId(foreground));
        if (foregroundIntegrity.IsMorePrivilegedThan(_security.ServerIntegrity))
        {
            return $"the foreground window's process has a higher integrity level ({foregroundIntegrity.Level}), so UIPI refuses the attach";
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="foreground"/> is <paramref name="root"/> or a window it owns, directly
    /// or through other owned windows: <paramref name="root"/> is in the foreground window's owner chain.
    /// The walk goes up from the foreground window only, so a dialog target's owner and its sibling
    /// dialogs never count as the dialog.
    /// </summary>
    /// <remarks>
    /// The chain is <see cref="WindowOwnerChain"/>, the rule the close guard, the host guard and the input
    /// guard's mouse rule use, so activation, input and close agree on which windows belong to a window.
    /// It follows GW_OWNER as well as GA_ROOTOWNER: GA_ROOTOWNER walks GetParent, which returns the owner
    /// only for WS_POPUP windows, so a WPF or WinForms owned window (overlapped, owned through
    /// GWLP_HWNDPARENT) is its own root owner and only GW_OWNER leads to the target.
    /// </remarks>
    private bool IsInFront(nint root, nint foreground) =>
        foreground != nint.Zero &&
        (foreground == root || WindowOwnerChain.IsOwnedBy(_windows, foreground, root));

    private bool IsInMenuMode(nint window) =>
        window != nint.Zero && _threads.GetGuiThreadInfo(_threads.GetWindowThreadId(window)) is { IsInMenuMode: true };

    /// <summary>
    /// Reads the foreground window until <paramref name="root"/> is in front, for at most
    /// <paramref name="settle"/> (infinite: until the deadline) and never past the deadline. Reads at
    /// least once.
    /// </summary>
    private bool WaitUntilInFront(nint root, TimeSpan settle, Deadline deadline, CancellationToken token, out nint foreground)
    {
        var stepDeadline = settle == Timeout.InfiniteTimeSpan ? deadline : deadline.NoLaterThan(settle);
        while (true)
        {
            foreground = _windows.GetForegroundWindow();
            if (IsInFront(root, foreground))
            {
                return true;
            }

            if (token.IsCancellationRequested || stepDeadline.HasPassed)
            {
                return false;
            }

            Thread.Sleep(Min(_timing.PollInterval, stepDeadline.Remaining));
        }
    }

    private void WaitUntil(Func<bool> condition, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Deadline.After(timeout);
        while (!condition() && !token.IsCancellationRequested && !deadline.HasPassed)
        {
            Thread.Sleep(Min(_timing.PollInterval, deadline.Remaining));
        }
    }

    /// <summary>
    /// Reads back what is in front and fills in the titles and process names. Runs on the caller's thread.
    /// </summary>
    private FocusOutcome Describe(nint target, Activation activation)
    {
        var foreground = activation.Foreground;
        var foregroundInfo = foreground == nint.Zero ? null : _windowService.GetWindowInfo(foreground);

        // The dialog in front of the target, or the dialog of a target that a modal dialog disables
        nint? blockingPopup = null;
        if (activation.IsForeground && foreground != activation.Root)
        {
            blockingPopup = foreground;
        }
        else if (activation.Root != nint.Zero && IsDisabled(activation.Root) && EnabledPopup(activation.Root) is var popup &&
                 popup != nint.Zero)
        {
            blockingPopup = popup;
        }

        var error = activation.ErrorCode == WindowErrorCode.FocusFailed
            ? $"The window could not be brought to the foreground; {Describe(foregroundInfo)} is in front: {activation.Error}"
            : activation.Error;

        return new FocusOutcome
        {
            Target = target,
            IsForeground = activation.IsForeground,
            Method = activation.Method,
            ForegroundHandle = foreground,
            ForegroundTitle = foregroundInfo?.Title,
            ForegroundProcess = foregroundInfo?.ProcessName,
            ForegroundProcessId = foregroundInfo?.ProcessId,
            BlockingPopup = blockingPopup,
            BlockingPopupTitle = blockingPopup is { } popupHandle ? _windows.GetWindowText(popupHandle) : null,
            MenuClosed = activation.MenuClosed,
            InMenuMode = activation.InMenuMode,
            ErrorCode = activation.ErrorCode,
            Error = error
        };

        static string Describe(WindowInfo? window) =>
            window == null ? "no window" : $"'{window.Title}' ({window.ProcessName})";
    }

    private nint RootOf(nint handle)
    {
        var root = _windows.GetAncestor(handle, WindowAncestor.Root);
        return root == nint.Zero ? handle : root;
    }

    private bool IsDisabled(nint window) => (_windows.GetWindowLongPtr(window, NativeEnums.GWL_STYLE) & WsDisabled) != 0;

    /// <summary>
    /// The enabled popup <paramref name="window"/> owns (GW_ENABLEDPOPUP), or 0. GetWindow returns the
    /// window itself when it has none.
    /// </summary>
    private nint EnabledPopup(nint window)
    {
        var popup = _windows.GetWindow(window, NativeEnums.GW_ENABLEDPOPUP);
        return popup == window ? nint.Zero : popup;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>What the activation thread found and did, before titles and process names are read.</summary>
    private sealed record Activation(
        nint Root,
        bool IsForeground,
        string? Method,
        nint Foreground,
        WindowErrorCode ErrorCode,
        string? Error)
    {
        public bool MenuClosed { get; init; }

        public bool InMenuMode { get; init; }

        public static Activation InFront(nint root, string method, nint foreground, bool menuClosed, bool inMenuMode) =>
            new(root, IsForeground: true, method, foreground, WindowErrorCode.None, Error: null)
            {
                MenuClosed = menuClosed,
                InMenuMode = inMenuMode
            };

        public static Activation Failed(nint root, nint foreground, WindowErrorCode errorCode, string error) =>
            new(root, IsForeground: false, Method: null, foreground, errorCode, error);

        /// <summary>The caller stopped waiting; nobody reads this result, and no further step was taken.</summary>
        public static Activation Stopped(nint root, nint foreground) =>
            Failed(root, foreground, WindowErrorCode.NotResponding, "The activation was stopped because it took too long.");
    }

    /// <summary>A point in time on the monotonic clock.</summary>
    private readonly record struct Deadline(long Timestamp)
    {
        public static Deadline After(TimeSpan timeout) =>
            new(Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency));

        public TimeSpan Remaining
        {
            get
            {
                var ticks = Timestamp - Stopwatch.GetTimestamp();
                return ticks > 0 ? TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency) : TimeSpan.Zero;
            }
        }

        public bool HasPassed => Stopwatch.GetTimestamp() >= Timestamp;

        /// <summary>This deadline, or <paramref name="span"/> from now if that is earlier.</summary>
        public Deadline NoLaterThan(TimeSpan span) => After(span) is var other && other.Timestamp < Timestamp ? other : this;
    }
}
