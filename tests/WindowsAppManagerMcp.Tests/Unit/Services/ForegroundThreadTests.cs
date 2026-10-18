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
    public async Task Run_TheWaitForAnEarlierCall_DoesNotCountAgainstTheWorksOwnTime()
    {
        // Arrange - an earlier call holds the thread for 2000 ms after this call starts waiting; this call allows
        // 3000 ms and its work takes 2000 ms. The calls run on threads of their own and the release is timed on the
        // test's thread before its first await, not on the thread pool, which other tests can keep busy for longer
        // than any margin here; what is left is the time a ready thread waits for a processor. The times are chosen so
        // that every margin is a whole second: the wait (1000 ms under its limit), the work (1000 ms under its limit),
        // and a shared budget, which would leave only 1000 ms for 2000 ms of work (it would fail by a second).
        // ForegroundThread has no clock seam, so the margins, not a fake clock, keep a loaded test run from failing this.
        const int earlierHoldsMs = 2000;
        const int workMs = 2000;
        const int allowedMs = 3000;
        using var sut = new ForegroundThread();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var queuing = new ManualResetEventSlim();
        var earlier = OnOwnThread(() => sut.Run(_ =>
        {
            started.Set();
            release.Wait(Long);
            return 1;
        }, Long));
        started.Wait(Long).Should().BeTrue();

        // Act - with one shared budget, only 1000 ms would be left for 2000 ms of work
        var queued = OnOwnThread(() =>
        {
            queuing.Set();
            return sut.Run(_ =>
            {
                Thread.Sleep(workMs);
                return 2;
            }, TimeSpan.FromMilliseconds(allowedMs));
        });
        queuing.Wait(Long).Should().BeTrue();
        Thread.Sleep(earlierHoldsMs);
        release.Set();

        // Assert - neither was abandoned, so no thread was counted as blocked
        (await earlier.WaitAsync(Long)).Should().Be(1);
        (await queued.WaitAsync(Long)).Should().Be(2);
        sut.StuckThreads.Should().Be(0);
    }

    /// <summary>Runs <paramref name="call"/> on a new thread, not on the thread pool.</summary>
    private static Task<T> OnOwnThread<T>(Func<T> call) =>
        Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    [Fact]
    public async Task Run_WhenAnEarlierCallHoldsTheThreadTooLong_ThrowsBusyWithoutRunningTheWorkOrRetiringTheThread()
    {
        // Arrange
        using var sut = new ForegroundThread();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var earlierThread = 0;
        var earlier = Task.Run(() => sut.Run(_ =>
        {
            earlierThread = Environment.CurrentManagedThreadId;
            started.Set();
            release.Wait(Long);
            return 1;
        }, Long));
        started.Wait(Long).Should().BeTrue();
        var ran = false;

        // Act
        var act = () => sut.Run(_ => ran = true, TimeSpan.FromMilliseconds(50));

        // Assert - busy is not a window that does not answer: the healthy earlier work keeps its thread
        act.Should().Throw<ForegroundThreadBusyException>().WithMessage("Another window activation was still running*");
        ran.Should().BeFalse();
        sut.StuckThreads.Should().Be(0);
        release.Set();
        (await earlier.WaitAsync(Long)).Should().Be(1);
        sut.Run(_ => Environment.CurrentManagedThreadId, Long).Should().Be(earlierThread);
    }

    [Fact]
    public void RetireCurrentThread_FromTheWork_LetsTheWorkFinishAndTheNextCallGetsANewThread()
    {
        // Arrange - for example after a detach that Windows refused. The threads are compared as objects, not by
        // ManagedThreadId: the retired thread exits once its work is done, and .NET can give its id to the new one.
        using var sut = new ForegroundThread();
        Thread? retiredThread = null;

        // Act
        var result = sut.Run(_ =>
        {
            retiredThread = Thread.CurrentThread;
            sut.RetireCurrentThread();
            return 42;
        }, Long);
        var next = sut.Run(_ => Thread.CurrentThread, Long);

        // Assert
        result.Should().Be(42);
        next.Should().NotBeSameAs(retiredThread);
        sut.Run(_ => Thread.CurrentThread, Long).Should().BeSameAs(next, "the new thread is the long-lived one now");
    }

    [Fact]
    public void RetireCurrentThread_ThenTheCallerGivesUpOnTheSameWork_ReportsTheTimeoutAndRecovers()
    {
        // Arrange - the work retires its thread and then blocks, so the caller retires it again
        using var sut = new ForegroundThread();
        using var release = new ManualResetEventSlim();

        // Act
        var act = () => sut.Run(_ =>
        {
            sut.RetireCurrentThread();
            release.Wait(Long);
            return 0;
        }, TimeSpan.FromMilliseconds(100));

        // Assert
        act.Should().Throw<TimeoutException>().Which.Should().NotBeOfType<ForegroundThreadBusyException>();
        release.Set();
        SpinWait.SpinUntil(() => sut.StuckThreads == 0, Long).Should().BeTrue();
        sut.Run(_ => 7, Long).Should().Be(7);
    }

    [Fact]
    public void RetireCurrentThread_OutsideAnActivationThread_Throws()
    {
        using var sut = new ForegroundThread();

        var act = sut.RetireCurrentThread;

        act.Should().Throw<InvalidOperationException>();
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
