using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Runs each window call that can block on another thread's window on a new background thread and waits for it
/// until the deadline (see <see cref="IWindowCallRunner"/>). A window that Windows already reports hung is refused
/// without a call. A call that misses the deadline is abandoned, not cancelled: a thread blocked in SetWindowPos or
/// ShowWindow cannot be interrupted. Abandoned calls are counted until they return, per application: while
/// <see cref="MaxStuckCallsPerProcess"/> calls to one application are outstanding, new calls to it fail at once, and
/// calls to other applications go on; <see cref="MaxStuckCalls"/> bounds the abandoned threads over all of them. So
/// however many windows a call or a batch touches, a hung application holds it, and the input gate, for at most
/// one deadline per application, and never more than <see cref="MaxStuckCalls"/> deadlines in all.
/// </summary>
/// <remarks>
/// An abandoned call still completes when the window answers: the move, restore or minimize can happen later,
/// outside the input gate, as with SWP_ASYNCWINDOWPOS. The input guard re-checks the target before every batch,
/// so input does not follow a window that moves or comes to the front late.
/// </remarks>
public sealed class WindowCallRunner : IWindowCallRunner
{
    /// <summary>
    /// The default deadline: as long as Windows waits before it reports a window hung (IsHungAppWindow), so a
    /// window that is only slow is not given up on before Windows itself would.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The default limit on abandoned calls to one application that have not returned yet.</summary>
    public const int DefaultMaxStuckCallsPerProcess = 1;

    /// <summary>The default limit on abandoned calls to all applications together.</summary>
    public const int DefaultMaxStuckCalls = 8;

    private readonly INativeWindowWrapper _windows;
    private readonly object _lock = new();
    private readonly Dictionary<uint, int> _stuckByProcess = [];
    private int _stuckCalls;

    public WindowCallRunner(INativeWindowWrapper windows)
        : this(windows, DefaultTimeout, DefaultMaxStuckCallsPerProcess, DefaultMaxStuckCalls)
    {
    }

    public WindowCallRunner(INativeWindowWrapper windows, TimeSpan timeout, int maxStuckCallsPerProcess, int maxStuckCalls)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStuckCallsPerProcess, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStuckCalls, maxStuckCallsPerProcess);
        Timeout = timeout;
        MaxStuckCallsPerProcess = maxStuckCallsPerProcess;
        MaxStuckCalls = maxStuckCalls;
    }

    /// <summary>How long a call may take before it is given up on.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>While this many abandoned calls to one application have not returned, new calls to it fail at once.</summary>
    public int MaxStuckCallsPerProcess { get; }

    /// <summary>While this many abandoned calls to any applications have not returned, every new call fails at once.</summary>
    public int MaxStuckCalls { get; }

    /// <summary>Calls that missed their deadline and have not returned yet, over all applications.</summary>
    public int StuckCalls
    {
        get
        {
            lock (_lock)
            {
                return _stuckCalls;
            }
        }
    }

    /// <inheritdoc />
    public T Run<T>(nint window, Func<T> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (_windows.IsHungAppWindow(window))
        {
            throw new WindowNotRespondingException(window,
                $"Window {window} is not responding (its application has not processed messages for several seconds), so it was " +
                "not changed. Try again once the application responds.");
        }

        var process = _windows.GetWindowThreadProcessId(window);
        ThrowIfTooManyStuck(window, process);

        var work = new Work<T>(call, onAbandonedCallReturned: () => RemoveStuck(process));
        var thread = new Thread(work.Execute)
        {
            IsBackground = true,
            Name = "Window call"
        };
        thread.Start();

        if (work.Wait(Timeout))
        {
            return work.GetResult();
        }

        // Count the call as stuck first, so the count never dips below zero if it returns right now
        AddStuck(process);
        if (!work.TryAbandon())
        {
            // It returned at the last moment after all
            RemoveStuck(process);
            return work.GetResult();
        }

        throw new WindowNotRespondingException(window,
            $"Window {window} did not answer within {Timeout.TotalSeconds:0.#} s: its application is not responding or is busy. " +
            "The change may still happen once it answers; check the window before you act on it.");
    }

    private void ThrowIfTooManyStuck(nint window, uint process)
    {
        lock (_lock)
        {
            var stuckForProcess = _stuckByProcess.GetValueOrDefault(process);
            if (stuckForProcess >= MaxStuckCallsPerProcess)
            {
                throw new WindowNotRespondingException(window,
                    $"Window {window} was not changed: an earlier call to its application (process {process}) is still waiting " +
                    "for an answer. The application is probably not responding; try again later.");
            }

            if (_stuckCalls >= MaxStuckCalls)
            {
                throw new WindowNotRespondingException(window,
                    $"Window {window} was not changed: {_stuckCalls} earlier window calls are still waiting for applications " +
                    "that do not answer. Try again later.");
            }
        }
    }

    private void AddStuck(uint process)
    {
        lock (_lock)
        {
            _stuckByProcess[process] = _stuckByProcess.GetValueOrDefault(process) + 1;
            _stuckCalls++;
        }
    }

    private void RemoveStuck(uint process)
    {
        lock (_lock)
        {
            var remaining = _stuckByProcess.GetValueOrDefault(process) - 1;
            if (remaining > 0)
            {
                _stuckByProcess[process] = remaining;
            }
            else
            {
                _stuckByProcess.Remove(process);
            }

            _stuckCalls--;
        }
    }

    /// <summary>One call and its outcome, shared by the waiting caller and the call's thread.</summary>
    private sealed class Work<T>(Func<T> call, Action onAbandonedCallReturned)
    {
        private const int Running = 0;
        private const int Returned = 1;
        private const int Abandoned = 2;

        private readonly TaskCompletionSource<T> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = Running;

        /// <summary>Runs on the call's thread. Never throws: an unhandled exception would end the process.</summary>
        public void Execute()
        {
            try
            {
                _outcome.TrySetResult(call());
            }
            catch (Exception ex)
            {
                _outcome.TrySetException(ex);
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _state, Returned, Running) == Abandoned)
                {
                    onAbandonedCallReturned();
                }
            }
        }

        /// <summary>Waits for the call to return. False when it is still running.</summary>
        public bool Wait(TimeSpan timeout)
        {
            try
            {
                return _outcome.Task.Wait(timeout);
            }
            catch (AggregateException)
            {
                // The call threw; GetResult rethrows its exception
                return true;
            }
        }

        /// <summary>Gives up on the call. False when it returned first.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Running) == Running;

        /// <summary>The call's result, or its exception rethrown unchanged.</summary>
        public T GetResult() => _outcome.Task.GetAwaiter().GetResult();
    }
}
