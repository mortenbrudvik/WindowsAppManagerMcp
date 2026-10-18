using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Takeover detection (roadmap M7.4; see <see cref="ITakeoverMonitor"/>) on a thread of its own with a message loop: a
/// low-level hook calls back on the thread that installed it while that thread waits for messages. Everything native goes
/// through <see cref="INativeLowLevelInputWrapper"/>, which the tests replace with a fake.
/// </summary>
/// <remarks>
/// <para>Off by default: with mode off nothing is started or installed. Otherwise the thread starts with the first watch and
/// runs until the monitor is disposed, when the server stops. It installs the keyboard and mouse hooks while an operation is
/// watched and removes them when the last watch ends, so between operations the server watches nothing. Starting and ending
/// a watch only post the thread a message.</para>
/// <para>The hook callback only asks <see cref="UserInputFilter"/> whether the input counts and, when it does and watches are
/// running, queues their signal to the thread pool: Windows waits for a low-level hook before the input reaches any window,
/// so it must return at once. A watch whose hooks are not installed yet (the first moments after it starts) misses
/// input.</para>
/// <para>Whatever stops detection shows in <see cref="Status"/> as Unavailable, with why, so the operations' results can warn
/// that the user's input was not watched: hooks Windows did not install (with its error), a callback that failed (it throws
/// nothing back into Windows), and a thread that stopped on its own (an exception, or its message loop failed or ended).
/// Such a thread is started again by the next watch, at most <see cref="MaxRestarts"/> times; then detection stays
/// unavailable until the server restarts.</para>
/// </remarks>
public sealed class UserTakeoverMonitor : ITakeoverMonitor, IDisposable
{
    /// <summary>
    /// How often a later watch starts the monitor thread again after it stopped on its own (an exception, or its message loop
    /// failed or ended); after that, detection stays unavailable until the server restarts, so a thread that keeps dying is
    /// not started for every operation.
    /// </summary>
    public const int MaxRestarts = 3;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly INativeLowLevelInputWrapper _native;
    private readonly TakeoverOptions _options;
    private readonly ILogger<UserTakeoverMonitor>? _logger;
    private readonly UserInputFilter _filter;
    private readonly LowLevelInputCallback _callback;
    private readonly object _lock = new();
    private readonly List<TakeoverWatch> _watches = [];

    // The watches the callback signals, replaced whole under the lock so the callback reads it without one
    private TakeoverWatch[] _active = [];

    // Only the monitor thread touches the hooks
    private readonly List<nint> _hooks = [];
    private volatile bool _hooked;
    private volatile string? _hookError;
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;

    // How many monitor threads were started: the first one and the restarts
    private int _starts;

    // 1 once a failing hook callback was logged, until the hooks are installed again
    private int _callbackFailureLogged;

    /// <param name="native">The native hook calls and the thread's message loop.</param>
    /// <param name="options">The mode the user set.</param>
    /// <param name="time">The clock of the mouse bursts.</param>
    /// <param name="logger">The logger.</param>
    public UserTakeoverMonitor(
        INativeLowLevelInputWrapper native,
        TakeoverOptions options,
        TimeProvider? time = null,
        ILogger<UserTakeoverMonitor>? logger = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _filter = new UserInputFilter(time);
        _callback = OnHookInput;
    }

    /// <inheritdoc />
    public TakeoverMode Mode => _options.Mode;

    /// <inheritdoc />
    public TakeoverStatus Status
    {
        get
        {
            if (Mode == TakeoverMode.Off)
            {
                return new TakeoverStatus(TakeoverMode.Off, TakeoverState.Off, _options.Source, _options.Warning);
            }

            var state = _hookError != null ? TakeoverState.Unavailable
                : _hooked ? TakeoverState.Watching
                : TakeoverState.Ready;
            return new TakeoverStatus(Mode, state, _options.Source, _hookError ?? _options.Warning);
        }
    }

    /// <inheritdoc />
    public TakeoverWatch Watch()
    {
        if (Mode == TakeoverMode.Off)
        {
            return TakeoverWatch.None;
        }

        var watch = new TakeoverWatch(Mode, Unwatch);
        uint update;
        lock (_lock)
        {
            if (_disposed)
            {
                return TakeoverWatch.None;
            }

            _watches.Add(watch);
            _active = [.. _watches];
            // The first watch starts the thread; a later one starts it again when it stopped on its own, a few times at most
            if (_thread == null && _starts <= MaxRestarts)
            {
                _starts++;
                _thread = new Thread(Run) { IsBackground = true, Name = "WAM takeover detection" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }

            // The first watch wants the hooks. A thread whose queue does not exist yet looks for itself once it does
            update = _watches.Count == 1 ? _threadId : 0;
        }

        if (update != 0)
        {
            _native.PostUpdateMessage(update);
        }

        return watch;
    }

    /// <summary>Stops the monitor thread, which removes its hooks first; waits for it at most <see cref="StopTimeout"/>.</summary>
    public void Dispose()
    {
        Thread? thread;
        uint threadId;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            thread = _thread;
            threadId = _threadId;
        }

        if (thread == null)
        {
            return;
        }

        if (threadId != 0)
        {
            _native.PostStopMessage(threadId);
        }

        if (!thread.Join(StopTimeout))
        {
            _logger?.LogWarning("The takeover detection thread did not stop within {Timeout} s", StopTimeout.TotalSeconds);
        }
    }

    private void Unwatch(TakeoverWatch watch)
    {
        uint update;
        lock (_lock)
        {
            if (!_watches.Remove(watch))
            {
                return;
            }

            _active = [.. _watches];
            update = _watches.Count == 0 && !_disposed ? _threadId : 0;
        }

        // The last one is gone: the thread removes the hooks
        if (update != 0)
        {
            _native.PostUpdateMessage(update);
        }
    }

    /// <summary>
    /// The hook callback, on the monitor thread, which Windows called: nothing may be thrown back. A failure means the input,
    /// which may have been the user's, was not looked at, so the status says detection is unavailable until the hooks are
    /// installed again (the next operation); it is logged once in that time, not on every event.
    /// </summary>
    private void OnHookInput(LowLevelInput input)
    {
        try
        {
            OnInput(input);
        }
        catch (Exception ex)
        {
            _hookError = "The takeover detection hook failed while it handled an input event, so the user's input may not be detected.";
            if (Interlocked.Exchange(ref _callbackFailureLogged, 1) == 0)
            {
                _logger?.LogWarning(ex, "The takeover detection hook callback failed; takeovers may not be detected (logged once until the hooks are installed again)");
            }
        }
    }

    /// <summary>Counts the user's input and signals the running watches.</summary>
    private void OnInput(LowLevelInput input)
    {
        if (!_filter.Counts(input))
        {
            return;
        }

        var active = Volatile.Read(ref _active);
        if (active.Length > 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static watches =>
            {
                foreach (var watch in watches)
                {
                    watch.Signal();
                }
            }, active, preferLocal: false);
        }
    }

    /// <summary>The monitor thread: its queue, the hooks while an operation is watched, and the message loop until the stop message.</summary>
    private void Run()
    {
        // True when the loop ended because the monitor is disposed; otherwise the thread stopped on its own
        var disposed = false;
        try
        {
            var threadId = _native.GetCurrentThreadId();
            _native.CreateMessageQueue();
            lock (_lock)
            {
                _threadId = threadId;
                if (_disposed)
                {
                    disposed = true;
                    return;
                }
            }

            Update();
            while (true)
            {
                switch (_native.WaitForMessage())
                {
                    case WinEventLoopMessage.Update:
                        Update();
                        break;
                    case WinEventLoopMessage.Stop:
                        lock (_lock)
                        {
                            disposed = _disposed;
                        }

                        if (!disposed)
                        {
                            // WM_QUIT: only Dispose's stop message should end the loop
                            _hookError = "The takeover detection thread's message loop ended before the server stopped, so the user's input is no longer detected.";
                            _logger?.LogWarning("The takeover detection thread's message loop ended unexpectedly; takeovers are no longer detected");
                        }

                        return;
                    case WinEventLoopMessage.Failed:
                        _hookError = "The takeover detection thread's message loop failed, so the user's input is no longer detected.";
                        _logger?.LogWarning("The takeover detection thread's message loop failed; takeovers are no longer detected");
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            _hookError = "The takeover detection thread stopped, so the user's input is no longer detected.";
            _logger?.LogWarning(ex, "The takeover detection thread stopped; takeovers are no longer detected");
        }
        finally
        {
            // The hooks first: the thread that installed them must remove them, and a new thread may start once this one is gone
            Unhook();
            Ended(disposed);
        }
    }

    /// <summary>
    /// On the monitor thread, as it ends: lets the next watch start a new one, unless the monitor is disposed or the thread
    /// has already been started again <see cref="MaxRestarts"/> times; then the status says detection is off for good.
    /// </summary>
    private void Ended(bool disposed)
    {
        lock (_lock)
        {
            _thread = null;
            _threadId = 0;
            if (!disposed && !_disposed && _starts > MaxRestarts)
            {
                _hookError = $"{_hookError} It stopped {_starts} times, so it is not started again: takeovers are not detected " +
                             "until the server is restarted.";
                _logger?.LogWarning("The takeover detection thread stopped {Starts} times and is not started again", _starts);
            }
        }
    }

    /// <summary>On the monitor thread: installs the hooks while a watch runs, removes them when none does.</summary>
    private void Update()
    {
        bool wanted;
        lock (_lock)
        {
            wanted = _watches.Count > 0 && !_disposed;
        }

        if (!wanted)
        {
            Unhook();
            return;
        }

        if (_hooks.Count > 0)
        {
            return;
        }

        var keyboard = _native.SetKeyboardHook(_callback);
        var mouse = _native.SetMouseHook(_callback);
        foreach (var hook in new[] { keyboard, mouse })
        {
            if (hook.Succeeded)
            {
                _hooks.Add(hook.Handle);
            }
        }

        if (!keyboard.Succeeded || !mouse.Succeeded)
        {
            // Half a detection would stop on keys and miss the mouse, or the other way round: none at all is clearer
            Unhook();
            _hookError = $"Windows did not install the low-level keyboard and mouse hooks (keyboard: {Describe(keyboard)}; mouse: " +
                         $"{Describe(mouse)}), so the user's input is not detected.";
            _logger?.LogWarning("The takeover detection hooks could not be installed (keyboard: {Keyboard}; mouse: {Mouse}); takeovers are not detected",
                Describe(keyboard), Describe(mouse));
            return;
        }

        _hookError = null;
        Interlocked.Exchange(ref _callbackFailureLogged, 0);
        _hooked = true;
    }

    /// <summary>"installed", or the Windows error of a hook that was not.</summary>
    private static string Describe(LowLevelHookResult hook) => hook.Succeeded ? "installed" : $"Windows error {hook.Win32Error}";

    /// <summary>On the monitor thread: removes the hooks it installed.</summary>
    private void Unhook()
    {
        foreach (var hook in _hooks)
        {
            _native.Unhook(hook);
        }

        _hooks.Clear();
        _hooked = false;
    }
}
