using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Runs each UI Automation read on a new background MTA thread and waits for it until the
/// deadline. A read that misses the deadline is abandoned, not cancelled: a thread blocked in a
/// cross-process UI Automation call cannot be interrupted, and it must never be reused for the next
/// read. Abandoned reads are counted until they return; while <see cref="MaxStuckCalls"/> of them
/// are outstanding, new reads fail at once instead of piling up more blocked threads.
/// </summary>
/// <remarks>
/// A thread per read costs far less than the cross-process calls it makes. M3.1 may replace this
/// with a long-lived worker and a bounded queue behind the same interface.
/// </remarks>
public sealed class UiaWorker : IUiaWorker
{
    /// <summary>The default limit on abandoned reads that have not returned yet.</summary>
    public const int DefaultMaxStuckCalls = 4;

    private int _stuckCalls;

    public UiaWorker()
        : this(DefaultMaxStuckCalls)
    {
    }

    public UiaWorker(int maxStuckCalls)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStuckCalls, 1);
        MaxStuckCalls = maxStuckCalls;
    }

    /// <summary>While this many abandoned reads have not returned, new reads fail at once.</summary>
    public int MaxStuckCalls { get; }

    /// <summary>Reads that missed their deadline and have not returned yet.</summary>
    public int StuckCalls => Volatile.Read(ref _stuckCalls);

    /// <inheritdoc />
    public T Run<T>(Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);

        var stuck = StuckCalls;
        if (stuck >= MaxStuckCalls)
        {
            throw new TimeoutException(
                $"UI Automation is not responding: {stuck} earlier reads have not returned yet. " +
                "An application is probably hung; try again later.");
        }

        var call = new Call<T>(read, cancellationToken, onAbandonedReadReturned: () => Interlocked.Decrement(ref _stuckCalls));
        var thread = new Thread(call.Execute)
        {
            IsBackground = true,
            Name = "UI Automation read"
        };
        // UI Automation clients must not run on an STA thread
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        if (call.WaitUntilDone(cancellationToken, grace))
        {
            return call.GetResult();
        }

        // Count the read as stuck first, so the count never dips below zero if it returns right now
        Interlocked.Increment(ref _stuckCalls);
        if (!call.TryAbandon())
        {
            // It returned at the last moment after all
            Interlocked.Decrement(ref _stuckCalls);
            return call.GetResult();
        }

        throw new TimeoutException("UI Automation did not answer in time. The application may not be responding.");
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
        public bool WaitUntilDone(CancellationToken token, TimeSpan grace)
        {
            var task = _outcome.Task;
            try
            {
                task.Wait(token);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return WaitQuietly(task, grace);
            }
            catch (AggregateException)
            {
                // The read threw; GetResult rethrows its exception
                return true;
            }
        }

        /// <summary>Marks the read as abandoned. False when it returned first.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Running) == Running;

        /// <summary>The read's result, or its exception rethrown unchanged.</summary>
        public T GetResult() => _outcome.Task.GetAwaiter().GetResult();

        private static bool WaitQuietly(Task task, TimeSpan grace)
        {
            try
            {
                return task.Wait(grace);
            }
            catch (AggregateException)
            {
                return true;
            }
        }
    }
}
