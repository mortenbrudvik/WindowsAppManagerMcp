namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The event wake-ups of one wait (roadmap M3.F22): an event source signals it when something the wait checks may have
/// changed (a window event for wait_for_window, a UI Automation event for wait_for_element), and the wait's sleep between
/// two checks then ends early, so the next check comes sooner. It only makes the waits quicker: they check as they would
/// without it and decide only from their checks, so an event that is lost, late or never raised costs only time.
/// </summary>
/// <remarks>
/// <para><see cref="Signal"/> is called on the event source's threads (the window event thread, UI Automation's event
/// threads): it only sets a flag and completes a task whose continuations run elsewhere, so it never blocks and never
/// runs wait code there.</para>
/// <para>A woken sleep still lasts at least <see cref="MinSleepMs"/>: the events of one change come in a burst (a window's
/// create, show and name change), which then wakes one check, and an application that raises events all the time cannot
/// make the wait check without pause.</para>
/// </remarks>
public sealed class WaitWakeUp
{
    /// <summary>The least a sleep lasts, however soon an event wakes it (shorter sleeps keep their length).</summary>
    public const int MinSleepMs = 25;

    private readonly object _lock = new();
    private TaskCompletionSource _signal = NewSignal();
    private bool _signalled;

    /// <summary>Whether an event came since the last <see cref="Reset"/>.</summary>
    public bool IsSignalled
    {
        get
        {
            lock (_lock)
            {
                return _signalled;
            }
        }
    }

    /// <summary>Something the wait checks may have changed. Safe on any thread; it never blocks.</summary>
    public void Signal()
    {
        lock (_lock)
        {
            _signalled = true;
            _signal.TrySetResult();
        }
    }

    /// <summary>
    /// Forgets the events so far. The wait calls it before a check reads, so an event that comes while the check reads
    /// still wakes the sleep after it.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_signalled)
            {
                _signalled = false;
                _signal = NewSignal();
            }
        }
    }

    /// <summary>
    /// Sleeps for <paramref name="wait"/> through <paramref name="delay"/>, or until an event once
    /// <see cref="MinSleepMs"/> has passed. True when an event ended it early.
    /// </summary>
    public async Task<bool> SleepAsync(TimeSpan wait, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        var least = TimeSpan.FromMilliseconds(Math.Min(MinSleepMs, Math.Max(0, wait.TotalMilliseconds)));
        await delay(least, cancellationToken).ConfigureAwait(false);

        var rest = wait - least;
        Task woken;
        lock (_lock)
        {
            if (rest <= TimeSpan.Zero)
            {
                return false;
            }

            if (_signalled)
            {
                return true;
            }

            woken = _signal.Task;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sleeping = delay(rest, stop.Token);
        if (await Task.WhenAny(sleeping, woken).ConfigureAwait(false) == sleeping)
        {
            // The whole interval passed (or the wait was cancelled, which this rethrows)
            await sleeping.ConfigureAwait(false);
            return false;
        }

        stop.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
