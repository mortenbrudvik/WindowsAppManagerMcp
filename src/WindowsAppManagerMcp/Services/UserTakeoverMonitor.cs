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
/// </remarks>
public sealed class UserTakeoverMonitor : ITakeoverMonitor, IDisposable
{
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
        _callback = OnInput;
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
            if (_thread == null)
            {
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

    /// <summary>The hook callback, on the monitor thread: counts the user's input and signals the running watches.</summary>
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
        try
        {
            var threadId = _native.GetCurrentThreadId();
            _native.CreateMessageQueue();
            lock (_lock)
            {
                _threadId = threadId;
                if (_disposed)
                {
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
            Unhook();
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
            if (hook != nint.Zero)
            {
                _hooks.Add(hook);
            }
        }

        if (keyboard == nint.Zero || mouse == nint.Zero)
        {
            // Half a detection would stop on keys and miss the mouse, or the other way round: none at all is clearer
            Unhook();
            _hookError = "Windows did not install the low-level keyboard and mouse hooks, so the user's input is not detected.";
            _logger?.LogWarning("The takeover detection hooks could not be installed; takeovers are not detected");
            return;
        }

        _hookError = null;
        _hooked = true;
    }

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
