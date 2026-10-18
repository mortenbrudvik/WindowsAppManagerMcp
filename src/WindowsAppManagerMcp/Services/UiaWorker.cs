using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Runs each UI Automation read on a new background MTA thread and waits for it, asynchronously, until
/// the deadline. A read that misses the deadline is abandoned, not cancelled: a thread blocked in a
/// cross-process UI Automation call cannot be interrupted, and it must never be reused for the next
/// read. Abandoned reads are counted until they return, per target process: while
/// <see cref="MaxStuckCallsPerTarget"/> reads of one application are outstanding, new reads of that
/// application fail at once instead of piling up more blocked threads, and reads of other
/// applications go on. <see cref="MaxStuckCalls"/> bounds the abandoned threads over all targets.
/// </summary>
/// <remarks>
/// <para>A thread per read costs far less than the cross-process calls it makes. M3.1 kept it rather than one
/// long-lived worker with a bounded queue: a shared worker would let a read stuck in one hung application delay the
/// reads of every other application, while the stuck counts here bound the threads such reads hold, and the UIA3
/// engine's transaction timeout returns a stuck read within about 5 s.</para>
/// <para>Reads with an unknown target (process 0) share one count.</para>
/// <para>A read whose deadline has passed before it is asked for (a token already cancelled, no grace: the caller's
/// remaining time ran out between two reads) is not started: it fails at once with a TimeoutException that says so, and it
/// counts against no application, which did nothing wrong.</para>
/// </remarks>
public sealed class UiaWorker : IUiaWorker
{
    /// <summary>The default limit on abandoned reads of one target that have not returned yet.</summary>
    public const int DefaultMaxStuckCallsPerTarget = 2;

    /// <summary>The default limit on abandoned reads of all targets together.</summary>
    public const int DefaultMaxStuckCalls = 16;

    private readonly object _lock = new();
    private readonly Dictionary<int, int> _stuckByTarget = [];
    private int _stuckCalls;

    public UiaWorker()
        : this(DefaultMaxStuckCallsPerTarget, DefaultMaxStuckCalls)
    {
    }

    public UiaWorker(int maxStuckCallsPerTarget, int maxStuckCalls)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStuckCallsPerTarget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStuckCalls, maxStuckCallsPerTarget);
        MaxStuckCallsPerTarget = maxStuckCallsPerTarget;
        MaxStuckCalls = maxStuckCalls;
    }

    /// <summary>While this many abandoned reads of one target have not returned, new reads of it fail at once.</summary>
    public int MaxStuckCallsPerTarget { get; }

    /// <summary>While this many abandoned reads of any targets have not returned, every new read fails at once.</summary>
    public int MaxStuckCalls { get; }

    /// <summary>Reads that missed their deadline and have not returned yet, over all targets.</summary>
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

    /// <summary>Reads of <paramref name="targetProcessId"/> that missed their deadline and have not returned yet.</summary>
    public int StuckCallsFor(int targetProcessId)
    {
        lock (_lock)
        {
            return _stuckByTarget.GetValueOrDefault(Key(targetProcessId));
        }
    }

    /// <inheritdoc />
    public async Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);

        if (cancellationToken.IsCancellationRequested && grace == TimeSpan.Zero)
        {
            // The deadline (the cancellation plus no grace) is behind the call already: a thread started now would be
            // abandoned at once and counted as stuck against the application, which then looks hung although nothing asked
            // it anything. With a grace the read still gets that long to return what it has, so it runs.
            throw new TimeoutException(
                "The time for this UI Automation read had run out before it started, so the application was not asked. Nothing was read.");
        }

        var target = Key(targetProcessId);
        ThrowIfTooManyStuck(target);

        var call = new Call<T>(read, cancellationToken, onAbandonedReadReturned: () => RemoveStuck(target));
        var thread = new Thread(call.Execute)
        {
            IsBackground = true,
            Name = "UI Automation read"
        };
        // UI Automation clients must not run on an STA thread
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        if (await call.WaitUntilDoneAsync(cancellationToken, grace).ConfigureAwait(false))
        {
            return call.GetResult();
        }

        // Count the read as stuck first, so the count never dips below zero if it returns right now
        AddStuck(target);
        if (!call.TryAbandon())
        {
            // It returned at the last moment after all
            RemoveStuck(target);
            return call.GetResult();
        }

        throw new TimeoutException("UI Automation did not answer in time. The application may not be responding.");
    }

    private static int Key(int targetProcessId) => targetProcessId > 0 ? targetProcessId : 0;

    private void ThrowIfTooManyStuck(int target)
    {
        lock (_lock)
        {
            var stuckForTarget = _stuckByTarget.GetValueOrDefault(target);
            if (stuckForTarget >= MaxStuckCallsPerTarget)
            {
                var application = target == 0 ? "an application the server could not identify" : $"the application (process {target})";
                throw new UiaReadRefusedException(
                    $"UI Automation is not responding for {application}: {stuckForTarget} earlier reads of it have not " +
                    "returned yet, so this read was not started. The application is probably hung; try again later. " +
                    "Other applications can still be read.");
            }

            if (_stuckCalls >= MaxStuckCalls)
            {
                throw new UiaReadRefusedException(
                    $"UI Automation is not responding: {_stuckCalls} earlier reads in {_stuckByTarget.Count} applications " +
                    "have not returned yet, so this read was not started. Some applications are probably hung; try again later.");
            }
        }
    }

    private void AddStuck(int target)
    {
        lock (_lock)
        {
            _stuckByTarget[target] = _stuckByTarget.GetValueOrDefault(target) + 1;
            _stuckCalls++;
        }
    }

    private void RemoveStuck(int target)
    {
        lock (_lock)
        {
            var remaining = _stuckByTarget.GetValueOrDefault(target) - 1;
            if (remaining > 0)
            {
                _stuckByTarget[target] = remaining;
            }
            else
            {
                _stuckByTarget.Remove(target);
            }

            _stuckCalls--;
        }
    }

    /// <summary>One read and its outcome, shared by the waiting caller and the worker thread.</summary>
    private sealed class Call<T>(
        Func<CancellationToken, T> read,
        CancellationToken cancellationToken,
        Action onAbandonedReadReturned)
    {
        private const int Running = 0;
        private const int Returned = 1;
        private const int Abandoned = 2;

        private readonly TaskCompletionSource<T> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = Running;

        public void Execute()
        {
            try
            {
                _outcome.TrySetResult(read(cancellationToken));
            }
            catch (Exception ex)
            {
                _outcome.TrySetException(ex);
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _state, Returned, Running) == Abandoned)
                {
                    onAbandonedReadReturned();
                }
            }
        }

        /// <summary>
        /// Waits until the read returns, or until <paramref name="token"/> is cancelled and then
        /// <paramref name="grace"/> has passed. False when the read is still running.
        /// </summary>
        public async Task<bool> WaitUntilDoneAsync(CancellationToken token, TimeSpan grace)
        {
            var task = _outcome.Task;
            try
            {
                await task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!task.IsCompleted && token.IsCancellationRequested)
            {
                return await WaitQuietlyAsync(task, grace).ConfigureAwait(false);
            }
            catch (Exception) when (task.IsCompleted)
            {
                // The read threw (or returned just as the token was cancelled); GetResult rethrows its exception
            }

            return true;
        }

        /// <summary>Marks the read as abandoned. False when it returned first.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Running) == Running;

        /// <summary>The read's result, or its exception rethrown unchanged.</summary>
        public T GetResult() => _outcome.Task.GetAwaiter().GetResult();

        private static async Task<bool> WaitQuietlyAsync(Task task, TimeSpan grace)
        {
            try
            {
                await task.WaitAsync(grace).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!task.IsCompleted)
            {
                return false;
            }
            catch (Exception) when (task.IsCompleted)
            {
                // The read threw; GetResult rethrows its exception
            }

            return true;
        }
    }
}
