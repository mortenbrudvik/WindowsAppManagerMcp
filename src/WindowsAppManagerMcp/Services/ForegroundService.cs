using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// How long <see cref="ForegroundService"/> waits for Windows to show a window in front.
/// </summary>
/// <param name="StepSettle">How long each step waits for its activation to show before the next step is tried.</param>
/// <param name="PollInterval">How often the foreground window is read while waiting.</param>
/// <param name="MenuSettle">How long to wait for a menu to close after Esc.</param>
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
        MenuSettle: TimeSpan.FromMilliseconds(100),
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

    /// <summary>Esc presses at most: one closes an open menu, the next leaves the menu bar.</summary>
    private const int MaxMenuEscapes = 2;

    /// <summary>Owner chains are short; this only stops a loop on a corrupt chain.</summary>
    private const int MaxOwnerChainLength = 32;

    /// <summary>WS_DISABLED in GWL_STYLE: the window takes no input, usually because a modal dialog is open.</summary>
    private const long WsDisabled = 0x08000000;

    private readonly INativeWindowWrapper _windows;
    private readonly INativeThreadInputWrapper _threads;
    private readonly INativeInputWrapper _input;
    private readonly IProcessSecurityProbe _security;
    private readonly IWindowService _windowService;
    private readonly IForegroundThread _activationThread;
    private readonly ForegroundTiming _timing;

    public ForegroundService(
        INativeWindowWrapper windows,
        INativeThreadInputWrapper threads,
        INativeInputWrapper input,
        IProcessSecurityProbe security,
        IWindowService windowService,
        IForegroundThread activationThread,
        ForegroundTiming? timing = null)
    {
        _windows = windows;
        _threads = threads;
        _input = input;
        _security = security;
        _windowService = windowService;
        _activationThread = activationThread;
        _timing = timing ?? ForegroundTiming.Default;
    }

    /// <inheritdoc />
    public FocusOutcome EnsureForeground(nint handle, int timeoutMs = DefaultTimeoutMs)
    {
        if (handle == nint.Zero || !_windows.IsWindow(handle))
        {
            return Describe(handle, Activation.Failed(
                root: nint.Zero, _windows.GetForegroundWindow(), WindowErrorCode.InvalidHandle, "Invalid window handle"));
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 0, MaxTimeoutMs));
        Activation activation;
        try
        {
            activation = _activationThread.Run(token => Activate(handle, timeout, token), timeout + _timing.ThreadGrace);
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
        if (IsInFront(root, foreground) && !_windows.IsIconic(root))
        {
            return Activation.InFront(root, FocusMethod.Already, foreground, menuClosed: false);
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

        // Every change below first checks that the caller is still waiting: a call to another thread's
        // window (ShowWindow, BringWindowToTop) can block past the deadline, and a late activation would
        // surprise the agent and the user.
        if (token.IsCancellationRequested)
        {
            return Activation.Stopped(root, foreground);
        }

        if (_windows.IsIconic(root))
        {
            _windows.ShowWindow(root, NativeEnums.SW_RESTORE);
        }

        // A window that a modal dialog disables takes no input; activate the dialog, as the taskbar does
        var activate = root;
        if (IsDisabled(root) && EnabledPopup(root) is var popup && popup != nint.Zero)
        {
            activate = popup;
        }

        var steps = new List<string>();

        // Step 1: SetForegroundWindow, which the foreground lock may refuse
        if (token.IsCancellationRequested)
        {
            return Activation.Stopped(root, foreground);
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
        if (token.IsCancellationRequested)
        {
            return Activation.Stopped(root, foreground);
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
                    accepted = !token.IsCancellationRequested && _windows.SetForegroundWindow(activate);
                }
                finally
                {
                    _threads.AttachThreadInput(currentThread, foregroundThread, attach: false);
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
        if (token.IsCancellationRequested)
        {
            return Activation.Stopped(root, foreground);
        }
        if (!_security.IsInputDesktopAvailable())
        {
            steps.Add("the input unlock was skipped: the input desktop is not available (locked session or a UAC prompt)");
        }
        else
        {
            var sent = _input.SendInput([InputEvent.Mouse(NativeEnums.MOUSEEVENTF_MOVE)]);
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
    /// items, so Esc closes the menu, but only while the window is verified to be in front: Esc goes to
    /// whatever window has keyboard focus.
    /// </summary>
    private Activation Activated(nint root, string method, nint foreground, CancellationToken token)
    {
        var menuClosed = false;
        for (var escapes = 0; escapes < MaxMenuEscapes && !token.IsCancellationRequested; escapes++)
        {
            if (!IsInMenuMode(foreground))
            {
                break;
            }

            foreground = _windows.GetForegroundWindow();
            if (!IsInFront(root, foreground))
            {
                break;
            }

            var escape = _input.SendInput(
            [
                InputEvent.Key(NativeEnums.VK_ESCAPE, NativeEnums.KEYEVENTF_KEYDOWN),
                InputEvent.Key(NativeEnums.VK_ESCAPE, NativeEnums.KEYEVENTF_KEYUP)
            ]);
            if (escape == 0)
            {
                break;
            }

            menuClosed = true;
            WaitUntil(() => !IsInMenuMode(foreground), _timing.MenuSettle, token);
        }

        // Verify once more: the outcome reports what is in front now
        foreground = _windows.GetForegroundWindow();
        return IsInFront(root, foreground)
            ? Activation.InFront(root, method, foreground, menuClosed)
            : Activation.Failed(root, foreground, WindowErrorCode.FocusFailed,
                "The window came to the front but lost the foreground again.") with { MenuClosed = menuClosed };
    }

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
    /// True when <paramref name="foreground"/> is <paramref name="root"/> or a window it owns. The cheap
    /// check comes first: the foreground window's root owner (GA_ROOTOWNER) must be the target's. That
    /// is enough for an unowned target. When the target is itself owned (a dialog), its owner and the
    /// owner's other dialogs share that root owner, so the foreground window's owner chain must also
    /// pass through the target.
    /// </summary>
    private bool IsInFront(nint root, nint foreground)
    {
        if (foreground == nint.Zero)
        {
            return false;
        }

        if (foreground == root)
        {
            return true;
        }

        var rootOwner = RootOwnerOf(root);
        if (RootOwnerOf(foreground) != rootOwner)
        {
            return false;
        }

        if (rootOwner == root)
        {
            return true;
        }

        var owner = foreground;
        for (var i = 0; i < MaxOwnerChainLength; i++)
        {
            owner = _windows.GetWindow(owner, NativeEnums.GW_OWNER);
            if (owner == nint.Zero)
            {
                return false;
            }

            if (owner == root)
            {
                return true;
            }
        }

        return false;
    }

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

    private nint RootOwnerOf(nint window)
    {
        var rootOwner = _windows.GetAncestor(window, WindowAncestor.RootOwner);
        return rootOwner == nint.Zero ? window : rootOwner;
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

        public static Activation InFront(nint root, string method, nint foreground, bool menuClosed) =>
            new(root, IsForeground: true, method, foreground, WindowErrorCode.None, Error: null) { MenuClosed = menuClosed };

        public static Activation Failed(nint root, nint foreground, WindowErrorCode errorCode, string error) =>
            new(root, IsForeground: false, Method: null, foreground, errorCode, error);

        /// <summary>The caller stopped waiting; nobody reads this result.</summary>
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
