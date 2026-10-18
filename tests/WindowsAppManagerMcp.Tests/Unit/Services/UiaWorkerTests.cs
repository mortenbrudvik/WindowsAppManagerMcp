namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="UiaWorker"/>, which runs UI Automation reads with a hard deadline.
/// The reads here are plain delegates; nothing touches UI Automation.
/// </summary>
public class UiaWorkerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private const int HungApp = 4242;
    private const int OtherApp = 5151;

    [Fact]
    public async Task RunAsync_ReturnsTheReadsResult_FromABackgroundMtaThread()
    {
        // Arrange
        var sut = new UiaWorker();
        var callerThread = Environment.CurrentManagedThreadId;

        // Act
        var (threadId, isBackground, apartment) = await sut.RunAsync(
            HungApp,
            _ => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsBackground, Thread.CurrentThread.GetApartmentState()),
            CancellationToken.None,
            TimeSpan.Zero);

        // Assert
        threadId.Should().NotBe(callerThread);
        isBackground.Should().BeTrue("a stuck read must not keep the server process alive");
        apartment.Should().Be(ApartmentState.MTA);
        sut.StuckCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DoesNotBlockTheCaller_WhileTheReadRuns()
    {
        // Arrange - a read that runs until the test lets it return
        var sut = new UiaWorker();
        using var release = new ManualResetEventSlim();

        // Act - the call returns at once with a task; no thread of the caller waits for the read
        var read = sut.RunAsync(HungApp, _ => release.Wait(Long) ? "read" : "timed out", CancellationToken.None, TimeSpan.Zero);
        var returnedBeforeTheRead = !read.IsCompleted;
        release.Set();

        // Assert
        returnedBeforeTheRead.Should().BeTrue();
        (await read.WaitAsync(Long)).Should().Be("read");
    }

    [Fact]
    public async Task RunAsync_WhenTheTokenIsCancelledAfterTheReadReturned_ReturnsTheResult()
    {
        // Arrange
        var sut = new UiaWorker();
        using var cancellation = new CancellationTokenSource();

        // Act
        var result = await sut.RunAsync(HungApp, _ => "read", cancellation.Token, TimeSpan.Zero);
        cancellation.Cancel();

        // Assert
        result.Should().Be("read");
        sut.StuckCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_RethrowsTheReadsExceptionUnchanged()
    {
        // Arrange
        var sut = new UiaWorker();

        // Act
        var act = () => sut.RunAsync<int>(HungApp, _ => throw new UiaElementUnavailableException("gone"), CancellationToken.None, TimeSpan.Zero);

        // Assert
        await act.Should().ThrowAsync<UiaElementUnavailableException>().WithMessage("gone");
    }

    [Fact]
    public async Task RunAsync_WhenTheReadStopsAtTheDeadline_ReturnsItsPartialResult()
    {
        // Arrange - the read polls the token, like the tree walk does
        var sut = new UiaWorker();
        using var deadline = new CancellationTokenSource(Short);

        // Act
        var result = await sut.RunAsync(
            HungApp,
            token =>
            {
                var read = 0;
                while (!token.IsCancellationRequested)
                {
                    read++;
                    Thread.Sleep(1);
                }

                return $"partial after {read}";
            },
            deadline.Token,
            Long);

        // Assert
        result.Should().StartWith("partial");
        sut.StuckCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WhenTheReadIsStuckPastTheDeadline_ThrowsTimeoutAndCountsTheReadUntilItReturns()
    {
        // Arrange - a read blocked in a call that ignores the token
        var sut = new UiaWorker();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(Short);

        try
        {
            // Act
            var act = () => sut.RunAsync(HungApp, _ => release.Wait(Long), deadline.Token, Short);

            // Assert
            (await act.Should().ThrowAsync<TimeoutException>()).WithMessage("*did not answer in time*");
            sut.StuckCalls.Should().Be(1);
            sut.StuckCallsFor(HungApp).Should().Be(1);
            sut.StuckCallsFor(OtherApp).Should().Be(0);
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue("the count drops once the abandoned read returns");
        sut.StuckCallsFor(HungApp).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WhileTooManyReadsOfTheTargetAreStuck_FailsAtOnceWithoutStartingTheRead()
    {
        // Arrange
        var sut = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 4);
        using var release = new ManualResetEventSlim();
        var started = false;

        try
        {
            await StickAsync(sut, HungApp, release);

            // Act
            var act = () => sut.RunAsync(HungApp, _ => started = true, CancellationToken.None, TimeSpan.Zero);

            // Assert
            (await act.Should().ThrowAsync<UiaReadRefusedException>()).WithMessage($"*not responding for the application (process {HungApp})*");
            started.Should().BeFalse();
        }
        finally
        {
            release.Set();
        }

        // Once the stuck read returns, reads of the application run again
        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
        (await sut.RunAsync(HungApp, _ => 42, CancellationToken.None, TimeSpan.Zero)).Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_WhileOneApplicationIsHung_StillReadsOtherApplications()
    {
        // Arrange - the hung application has used up its own limit
        var sut = new UiaWorker(maxStuckCallsPerTarget: 2, maxStuckCalls: 4);
        using var release = new ManualResetEventSlim();

        try
        {
            await StickAsync(sut, HungApp, release);
            await StickAsync(sut, HungApp, release);
            var hung = () => sut.RunAsync(HungApp, _ => 1, CancellationToken.None, TimeSpan.Zero);
            await hung.Should().ThrowAsync<UiaReadRefusedException>();

            // Act
            var other = await sut.RunAsync(OtherApp, _ => "read", CancellationToken.None, TimeSpan.Zero);

            // Assert
            other.Should().Be("read");
            sut.StuckCallsFor(HungApp).Should().Be(2);
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WhileTooManyReadsAreStuckOverall_FailsAtOnceForEveryTarget()
    {
        // Arrange - two hung applications fill the overall limit
        var sut = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 2);
        using var release = new ManualResetEventSlim();

        try
        {
            await StickAsync(sut, HungApp, release);
            await StickAsync(sut, OtherApp, release);

            // Act
            var act = () => sut.RunAsync(7, _ => 1, CancellationToken.None, TimeSpan.Zero);

            // Assert
            (await act.Should().ThrowAsync<UiaReadRefusedException>()).WithMessage("*2 earlier reads in 2 applications*");
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithAnUnknownTarget_SharesOneCount()
    {
        // Arrange
        var sut = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 4);
        using var release = new ManualResetEventSlim();

        try
        {
            await StickAsync(sut, 0, release);

            // Act - a negative process ID is unknown too
            var act = () => sut.RunAsync(-1, _ => 1, CancellationToken.None, TimeSpan.Zero);

            // Assert
            (await act.Should().ThrowAsync<UiaReadRefusedException>()).WithMessage("*could not identify*");
            (await sut.RunAsync(HungApp, _ => 2, CancellationToken.None, TimeSpan.Zero)).Should().Be(2);
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WhenTheDeadlineHasAlreadyPassed_ThrowsTimeoutWithoutStartingTheRead_OrCountingItAsStuck()
    {
        // Arrange - the token is cancelled before the call, with no grace: the deadline is behind it already
        var sut = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 4);
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var started = false;

        // Act
        var act = () => sut.RunAsync(HungApp, _ => started = true, deadline.Token, TimeSpan.Zero);

        // Assert - what a read that missed its deadline throws, but nothing says the application does not answer
        var thrown = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        thrown.Should().BeOfType<UiaDeadlinePassedException>("callers that describe the timeout tell it apart from one the application caused");
        thrown.Message.Should().NotContain("not responding").And.NotContain("did not answer");
        started.Should().BeFalse("no thread is started for a read that could not be waited for");
        sut.StuckCalls.Should().Be(0);
        sut.StuckCallsFor(HungApp).Should().Be(0);

        // A read of the application right after it is not refused as though the application were hung
        (await sut.RunAsync(HungApp, _ => 42, CancellationToken.None, TimeSpan.Zero)).Should().Be(42);
    }

    [Fact]
    public async Task RunAsync_WithATokenAlreadyCancelled_AndAGrace_StillLetsTheReadReturnWhatItHas()
    {
        // Arrange - the deadline is the cancellation plus the grace: a read that stops on the token returns its partial result
        var sut = new UiaWorker();
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        // Act
        var result = await sut.RunAsync(HungApp, token => token.IsCancellationRequested ? "partial" : "whole", deadline.Token, Long);

        // Assert
        result.Should().Be("partial");
        sut.StuckCalls.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WithATokenThatIsNeverCancelled_WaitsForTheRead()
    {
        // Arrange
        var sut = new UiaWorker();

        // Act
        var result = await sut.RunAsync(HungApp, _ =>
        {
            Thread.Sleep(Short);
            return "done";
        }, CancellationToken.None, TimeSpan.Zero);

        // Assert
        result.Should().Be("done");
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(2, 1)]
    public void Constructor_RejectsAPerTargetLimitBelowOneOrAboveTheOverallLimit(int perTarget, int overall)
    {
        var act = () => new UiaWorker(perTarget, overall);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task RunAsync_RejectsANegativeGrace()
    {
        var act = () => new UiaWorker().RunAsync(HungApp, _ => 1, CancellationToken.None, TimeSpan.FromMilliseconds(-1));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>Makes one read of <paramref name="target"/> miss its deadline and stay stuck until <paramref name="release"/> is set.</summary>
    private static async Task StickAsync(UiaWorker sut, int target, ManualResetEventSlim release)
    {
        using var deadline = new CancellationTokenSource(Short);
        var stuck = () => sut.RunAsync(target, _ => release.Wait(Long), deadline.Token, Short);
        (await stuck.Should().ThrowAsync<TimeoutException>()).Which.Should().NotBeOfType<UiaReadRefusedException>();
    }
}
