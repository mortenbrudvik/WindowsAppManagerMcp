using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Runs each UI Automation action on a new background MTA thread (roadmap M3.6; see <see cref="IUiaActionRunner"/>). An
/// action that misses its deadline is left running, never reused, and counted until it returns: while
/// <see cref="MaxPendingPerTarget"/> actions on one application, or <see cref="MaxPending"/> over all, are still blocked,
/// new actions are refused at once instead of piling up blocked threads. The counts are this runner's own, apart from the
/// read worker's (<see cref="UiaWorker"/>), so a blocked Invoke never stops the reads of the dialog it opened.
/// </summary>
public sealed class UiaActionRunner : IUiaActionRunner
{
    /// <summary>The default limit on blocked actions on one application.</summary>
    public const int DefaultMaxPendingPerTarget = 2;

    /// <summary>The default limit on blocked actions on all applications.</summary>
    public const int DefaultMaxPending = 6;

    private const int Running = 0;
    private const int Returned = 1;
    private const int Abandoned = 2;

    private readonly object _lock = new();
    private readonly Dictionary<int, int> _pendingByTarget = [];
    private int _pending;

    public UiaActionRunner()
        : this(DefaultMaxPendingPerTarget, DefaultMaxPending)
    {
    }

    public UiaActionRunner(int maxPendingPerTarget, int maxPending)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingPerTarget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPending, maxPendingPerTarget);
        MaxPendingPerTarget = maxPendingPerTarget;
        MaxPending = maxPending;
    }

    /// <summary>While this many actions on one application are blocked, new actions on it are refused.</summary>
    public int MaxPendingPerTarget { get; }

    /// <summary>While this many actions on any applications are blocked, every new action is refused.</summary>
    public int MaxPending { get; }

    /// <summary>Actions that missed their deadline and have not returned yet.</summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending;
            }
        }
    }

    /// <inheritdoc />
    public async Task<UiaActionOutcome> RunAsync(int targetProcessId, Action action, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var target = targetProcessId > 0 ? targetProcessId : 0;
        if (Refusal(target) is { } refused)
        {
            return new UiaActionOutcome(UiaActionStatus.Refused, Message: refused);
        }

        var call = new Call(action, onAbandonedActionReturned: () => Remove(target));
        var thread = new Thread(call.Execute)
        {
            IsBackground = true,
            Name = "UI Automation action"
        };
        // UI Automation clients must not run on an STA thread
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        try
        {
            var error = await call.Outcome.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return new UiaActionOutcome(UiaActionStatus.Completed, error);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Counted first, so the count never dips below zero if it returns right now
            Add(target);
            if (!call.TryAbandon())
            {
                // It returned at the last moment after all
                Remove(target);
                cancellationToken.ThrowIfCancellationRequested();
                return new UiaActionOutcome(UiaActionStatus.Completed, call.Outcome.Result);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new UiaActionOutcome(UiaActionStatus.Pending);
        }
    }

    private string? Refusal(int target)
    {
        lock (_lock)
        {
            var forTarget = _pendingByTarget.GetValueOrDefault(target);
            if (forTarget >= MaxPendingPerTarget)
            {
                var application = target == 0 ? "an application the server could not identify" : $"the application (process {target})";
                return $"{forTarget} earlier actions on {application} have not returned yet: each is waiting for a dialog it " +
                       "opened, or the application is hung. This action was not started. Answer or close the open dialog first " +
                       "(find it with find_windows or wait_for_window), then try again.";
            }

            return _pending >= MaxPending
                ? $"{_pending} earlier actions have not returned yet (dialogs they opened are still open, or applications are " +
                  "hung). This action was not started. Close the open dialogs first, then try again."
                : null;
        }
    }

    private void Add(int target)
    {
        lock (_lock)
        {
            _pendingByTarget[target] = _pendingByTarget.GetValueOrDefault(target) + 1;
            _pending++;
        }
    }

    private void Remove(int target)
    {
        lock (_lock)
        {
            var remaining = _pendingByTarget.GetValueOrDefault(target) - 1;
            if (remaining > 0)
            {
                _pendingByTarget[target] = remaining;
            }
            else
            {
                _pendingByTarget.Remove(target);
            }

            _pending--;
        }
    }

    /// <summary>One action, shared by the waiting caller and its thread.</summary>
    private sealed class Call(Action action, Action onAbandonedActionReturned)
    {
        private readonly TaskCompletionSource<Exception?> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = Running;

        /// <summary>Completes with the action's exception, or null when it returned.</summary>
        public Task<Exception?> Outcome => _outcome.Task;

        public void Execute()
        {
            Exception? error = null;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // The outcome first: a caller that finds the action returned reads it at once
                _outcome.TrySetResult(error);
                if (Interlocked.CompareExchange(ref _state, Returned, Running) == Abandoned)
                {
                    onAbandonedActionReturned();
                }
            }
        }

        /// <summary>Marks the action as abandoned. False when it returned first.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Running) == Running;
    }
}
