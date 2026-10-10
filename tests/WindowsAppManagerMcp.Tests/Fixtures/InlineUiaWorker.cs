namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An <see cref="IUiaWorker"/> that runs the read on the calling thread, so service tests are
/// deterministic. <see cref="UiaWorker"/> itself is tested in UiaWorkerTests.
/// </summary>
public sealed class InlineUiaWorker : IUiaWorker
{
    public int Calls { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public TimeSpan LastGrace { get; private set; }

    public T Run<T>(Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
    {
        Calls++;
        LastToken = cancellationToken;
        LastGrace = grace;
        return read(cancellationToken);
    }
}
