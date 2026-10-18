namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An <see cref="IUiaWorker"/> whose reads run inline (as <see cref="InlineUiaWorker"/>) until <see cref="Expired"/> is set;
/// from then on each read goes to a real <see cref="UiaWorker"/> after its deadline has passed (its token already cancelled,
/// with the caller's grace), as when a caller's time runs out between two reads. With no grace the real worker starts no
/// thread then and throws what it throws for such a read, so a service's message for it is tested against the worker's own.
/// </summary>
public sealed class ExpiringUiaWorker : IUiaWorker
{
    private readonly InlineUiaWorker _inline = new();
    private readonly UiaWorker _worker = new();

    /// <summary>Whether the deadline of every further read has passed before the read is asked for.</summary>
    public bool Expired { get; set; }

    /// <summary>The reads given to the real worker after their deadline, which it did not start (no grace) or ran cancelled.</summary>
    public int ExpiredCalls { get; private set; }

    public Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
    {
        if (!Expired)
        {
            return _inline.RunAsync(targetProcessId, read, cancellationToken, grace);
        }

        ExpiredCalls++;
        return _worker.RunAsync(targetProcessId, read, new CancellationToken(canceled: true), grace);
    }
}
