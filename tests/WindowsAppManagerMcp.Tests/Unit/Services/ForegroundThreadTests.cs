namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// <see cref="ForegroundThread"/>: activation work runs on one dedicated thread, so an
/// AttachThreadInput and its detach happen on the same thread. The work here only records threads and
/// waits on events; nothing calls Windows.
/// </summary>
public class ForegroundThreadTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    [Fact]
    public void Run_RunsEveryCallOnTheSameDedicatedBackgroundThread()
    {
        // Arrange
        using var sut = new ForegroundThread();

        // Act
        var first = sut.Run(_ => CurrentThread(), Long);
        var second = sut.Run(_ => CurrentThread(), Long);

        // Assert
        first.ManagedThreadId.Should().Be(second.ManagedThreadId);
        first.ManagedThreadId.Should().NotBe(Environment.CurrentManagedThreadId);
        first.IsBackground.Should().BeTrue("a blocked activation must not keep the server from exiting");
        first.Name.Should().Be("Window activation");
    }

    [Fact]
    public void Run_ReturnsTheResult_AndRethrowsTheWorksExceptionUnchanged()
    {
        using var sut = new ForegroundThread();

        sut.Run(_ => 42, Long).Should().Be(42);
        var act = () => sut.Run<int>(_ => throw new InvalidOperationException("boom"), Long);

        act.Should().Throw<InvalidOperationException>().WithMessage("boom");
        sut.Run(_ => 7, Long).Should().Be(7, "an exception must not end the activation thread");
    }

    [Fact]
    public async Task Run_WhenTheWorkMissesTheDeadline_CancelsItsTokenAndTheNextCallGetsANewThread()
    {
        // Arrange
        using var sut = new ForegroundThread();
        using var release = new ManualResetEventSlim();
        var blockedThread = 0;
        var tokenCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act - the work blocks like a window call to a thread that does not answer
        var act = () => sut.Run(token =>
        {
            blockedThread = Environment.CurrentManagedThreadId;
            release.Wait(Long);
            tokenCancelled.TrySetResult(token.IsCancellationRequested);
            return 0;
        }, TimeSpan.FromMilliseconds(100));

        // Assert
        act.Should().Throw<TimeoutException>();
        sut.StuckThreads.Should().Be(1);
        var next = sut.Run(_ => Environment.CurrentManagedThreadId, Long);
        next.Should().NotBe(blockedThread, "the blocked thread is retired, not reused");

        release.Set();
        (await tokenCancelled.Task.WaitAsync(Long)).Should().BeTrue("the work must take no further step once the caller gave up");
        SpinWait.SpinUntil(() => sut.StuckThreads == 0, Long).Should().BeTrue();
    }

    [Fact]
    public void Run_WhileTooManyThreadsAreBlocked_FailsAtOnceWithoutRunningTheWork()
    {
        // Arrange
        using var sut = new ForegroundThread(maxStuckThreads: 1);
        using var release = new ManualResetEventSlim();
        var block = () => sut.Run(_ => release.Wait(Long), TimeSpan.FromMilliseconds(50));
        block.Should().Throw<TimeoutException>();
        var ran = false;

        // Act
        var act = () => sut.Run(_ => ran = true, Long);

        // Assert
        act.Should().Throw<TimeoutException>().WithMessage("*blocked*");
        ran.Should().BeFalse();

        release.Set();
        SpinWait.SpinUntil(() => sut.StuckThreads == 0, Long).Should().BeTrue();
        sut.Run(_ => true, Long).Should().BeTrue();
    }

    [Fact]
    public async Task Run_RunsConcurrentCallsOneAtATime()
    {
        // Arrange
        using var sut = new ForegroundThread();
        var running = 0;
        var maxRunning = 0;

        // Act
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() => sut.Run(_ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            Thread.Sleep(5);
            Interlocked.Decrement(ref running);
            return now;
        }, Long)));
        await Task.WhenAll(calls);

        // Assert
        maxRunning.Should().Be(1);
    }

    [Fact]
    public void Run_AfterDispose_Throws()
    {
        var sut = new ForegroundThread();
        sut.Run(_ => 1, Long);

        sut.Dispose();
        var act = () => sut.Run(_ => 1, Long);

        act.Should().Throw<ObjectDisposedException>();
    }

    private static Thread CurrentThread() => Thread.CurrentThread;

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
