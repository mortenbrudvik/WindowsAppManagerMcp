namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An <see cref="IUiaWorker"/> that runs the read on the calling thread and returns a completed task, so service
/// tests are deterministic. <see cref="UiaWorker"/> itself is tested in UiaWorkerTests.
/// </summary>
public sealed class InlineUiaWorker : IUiaWorker
{
    public int Calls { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public TimeSpan LastGrace { get; private set; }

    public int LastTargetProcessId { get; private set; }

    public Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
    {
        Calls++;
        LastTargetProcessId = targetProcessId;
        LastToken = cancellationToken;
        LastGrace = grace;
        try
        {
            return Task.FromResult(read(cancellationToken));
        }
        catch (Exception ex)
        {
            // As the real worker: the read's exception comes out of the task, unchanged
            return Task.FromException<T>(ex);
        }
    }
}
