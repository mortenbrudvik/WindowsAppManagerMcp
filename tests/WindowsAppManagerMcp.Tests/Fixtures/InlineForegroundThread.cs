namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// An <see cref="IForegroundThread"/> that runs the work on the calling thread, so service tests are
/// deterministic. <see cref="ForegroundThread"/> itself is tested in ForegroundThreadTests.
/// </summary>
public sealed class InlineForegroundThread : IForegroundThread
{
    public int Calls { get; private set; }

    public TimeSpan LastTimeout { get; private set; }

    public T Run<T>(Func<CancellationToken, T> work, TimeSpan timeout)
    {
        Calls++;
        LastTimeout = timeout;
        return work(CancellationToken.None);
    }
}
