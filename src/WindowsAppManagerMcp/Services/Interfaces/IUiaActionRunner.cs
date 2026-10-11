namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Runs a UI Automation action (a pattern call that changes the application) on a thread of its own, and stops waiting for
/// it at a deadline (roadmap M3.6). A Win32 Invoke, Toggle or Expand returns only when a modal dialog it opened closes; the
/// thread that runs it stays blocked, and must be neither the shared read worker's (whose stuck count would then refuse the
/// reads the agent needs to answer the dialog) nor reused. The number of such blocked actions is capped.
/// </summary>
public interface IUiaActionRunner
{
    /// <summary>
    /// Runs <paramref name="action"/> on a new MTA thread and waits for it until <paramref name="timeout"/> passes.
    /// </summary>
    /// <param name="targetProcessId">The application the action acts on (0: unknown), for the per-application cap.</param>
    /// <param name="action">The pattern call.</param>
    /// <param name="timeout">How long to wait before leaving it running (<see cref="UiaActionStatus.Pending"/>).</param>
    /// <param name="cancellationToken">The request's token: cancelling it stops the wait (OperationCanceledException); the
    /// action itself cannot be stopped.</param>
    Task<UiaActionOutcome> RunAsync(int targetProcessId, Action action, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>What became of an action given to <see cref="IUiaActionRunner"/>.</summary>
public enum UiaActionStatus
{
    /// <summary>It returned, or threw (<see cref="UiaActionOutcome.Error"/>).</summary>
    Completed,

    /// <summary>It had not returned at the deadline; its thread is left running and counted until it does.</summary>
    Pending,

    /// <summary>It was not started: too many earlier actions are still blocked (<see cref="UiaActionOutcome.Message"/>).</summary>
    Refused
}

/// <summary>The outcome of <see cref="IUiaActionRunner.RunAsync"/>.</summary>
/// <param name="Error">The exception the action threw, for <see cref="UiaActionStatus.Completed"/>; null when it returned.</param>
/// <param name="Message">Why it was refused.</param>
public sealed record UiaActionOutcome(UiaActionStatus Status, Exception? Error = null, string? Message = null)
{
    public static UiaActionOutcome Returned { get; } = new(UiaActionStatus.Completed);
}
