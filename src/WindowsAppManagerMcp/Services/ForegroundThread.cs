using System.Collections.Concurrent;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Runs window activation work on one dedicated, long-lived background thread (see
/// <see cref="IForegroundThread"/>). The thread is started by the first call, not by construction, and
/// calls run one at a time: two activations at once would only fight over the foreground.
/// </summary>
/// <remarks>
/// A call to another thread's window (ShowWindow, BringWindowToTop) can block until that thread
/// answers. When work misses its deadline, its thread is retired: it finishes that work, including the
/// detach in its finally block on the same thread, and then exits, and the next call starts a new thread.
/// The work's token is cancelled first, so it takes no further step once the blocked call returns (the
/// blocked call itself still completes). While <see cref="MaxStuckThreads"/> retired threads are still
/// blocked, calls fail at once instead of piling up more blocked threads. The wait for an earlier call
/// and the work have separate limits: an earlier call holds the thread at most for its own timeout, so
/// a call that waited in line still gets its full time and is not mistaken for a blocked one.
/// </remarks>
public sealed class ForegroundThread : IForegroundThread, IDisposable
{
    /// <summary>The default limit on retired threads that are still blocked.</summary>
    public const int DefaultMaxStuckThreads = 2;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private Worker? _worker;
    private int _stuckThreads;
    private bool _disposed;

    public ForegroundThread()
        : this(DefaultMaxStuckThreads)
    {
    }

    public ForegroundThread(int maxStuckThreads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStuckThreads, 1);
        MaxStuckThreads = maxStuckThreads;
    }

    /// <summary>While this many retired threads are still blocked, calls fail at once.</summary>
    public int MaxStuckThreads { get; }

    /// <summary>Retired threads whose work has not returned yet.</summary>
    public int StuckThreads => Volatile.Read(ref _stuckThreads);

    /// <inheritdoc />
    public T Run<T>(Func<CancellationToken, T> work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        // The wait in line has its own limit; the work's time starts once it has the thread
        if (!_oneAtATime.Wait(timeout))
        {
            throw new ForegroundThreadBusyException(
                $"Another window activation was still running after {timeout.TotalMilliseconds:0} ms, so this one did not start. Try again.");
        }

        try
        {
            var stuck = StuckThreads;
            if (stuck >= MaxStuckThreads)
            {
                throw new TimeoutException(
                    $"Window activation is blocked: {stuck} earlier activations are still waiting for windows " +
                    "that do not respond. Try again later.");
            }

            var worker = CurrentWorker();
            var item = new WorkItem<T>(work, onAbandonedWorkReturned: () => Interlocked.Decrement(ref _stuckThreads));
            worker.Post(item.Execute);

            if (item.Wait(timeout))
            {
                return item.GetResult();
            }

            // Count the thread as stuck first, so the count never dips below zero if the work returns now
            Interlocked.Increment(ref _stuckThreads);
            if (!item.TryAbandon())
            {
                // It returned at the last moment after all
                Interlocked.Decrement(ref _stuckThreads);
                return item.GetResult();
            }

            Retire(worker);
            throw new TimeoutException(
                "Window activation did not finish in time: a window did not answer. It may not be responding.");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <inheritdoc />
    public void RetireCurrentThread()
    {
        var worker = Worker.Current ?? throw new InvalidOperationException(
            "Only work running on an activation thread can retire it.");
        Retire(worker);
    }

    /// <summary>Lets the activation thread exit once it has finished its current work.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _worker?.Retire();
            _worker = null;
        }
    }

    private Worker CurrentWorker()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _worker ??= new Worker();
        }
    }

    private void Retire(Worker worker)
    {
        lock (_sync)
        {
            worker.Retire();
            if (ReferenceEquals(_worker, worker))
            {
                _worker = null;
            }
        }
    }

    /// <summary>The activation thread: runs posted work in order until it is retired.</summary>
    private sealed class Worker
    {
        [ThreadStatic]
        private static Worker? t_current;

        private readonly BlockingCollection<Action> _queue = new();
        private int _retired;

        public Worker()
        {
            var thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Window activation"
            };
            thread.Start();
        }

        /// <summary>The worker whose thread is the calling thread, or null on any other thread.</summary>
        public static Worker? Current => t_current;

        public void Post(Action action) => _queue.Add(action);

        /// <summary>
        /// The thread runs the work already posted, then exits. Calling it again does nothing: both the
        /// work (<see cref="RetireCurrentThread"/>) and a caller that gave up on it may retire it, and the
        /// queue is disposed once the thread has exited.
        /// </summary>
        public void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 0)
            {
                _queue.CompleteAdding();
            }
        }

        private void Loop()
        {
            t_current = this;
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                action();
            }

            _queue.Dispose();
        }
    }

    /// <summary>One piece of work and its outcome, shared by the waiting caller and the activation thread.</summary>
    private sealed class WorkItem<T>(Func<CancellationToken, T> work, Action onAbandonedWorkReturned)
    {
        private const int Pending = 0;
        private const int Returned = 1;
        private const int Abandoned = 2;

        private readonly TaskCompletionSource<T> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancellation = new();
        private int _state = Pending;

        /// <summary>Runs on the activation thread. Never throws: an unhandled exception would end the process.</summary>
        public void Execute()
        {
            try
            {
                if (_cancellation.IsCancellationRequested)
                {
                    _outcome.TrySetCanceled();
                }
                else
                {
                    _outcome.TrySetResult(work(_cancellation.Token));
                }
            }
            catch (Exception ex)
            {
                _outcome.TrySetException(ex);
            }
            finally
            {
                if (Interlocked.CompareExchange(ref _state, Returned, Pending) == Abandoned)
                {
                    onAbandonedWorkReturned();
                }
            }
        }

        /// <summary>Waits for the work to return. False when it is still pending or running.</summary>
        public bool Wait(TimeSpan timeout)
        {
            try
            {
                return _outcome.Task.Wait(timeout);
            }
            catch (AggregateException)
            {
                // The work threw; GetResult rethrows its exception
                return true;
            }
        }

        /// <summary>Gives up on the work and cancels its token. False when it returned first.</summary>
        public bool TryAbandon()
        {
            if (Interlocked.CompareExchange(ref _state, Abandoned, Pending) != Pending)
            {
                return false;
            }

            _cancellation.Cancel();
            return true;
        }

        /// <summary>The work's result, or its exception rethrown unchanged.</summary>
        public T GetResult() => _outcome.Task.GetAwaiter().GetResult();
    }
}
