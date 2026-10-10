namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="UiaWorker"/>, which runs UI Automation reads with a hard deadline.
/// The reads here are plain delegates; nothing touches UI Automation.
/// </summary>
public class UiaWorkerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    [Fact]
    public void Run_ReturnsTheReadsResult_FromABackgroundMtaThread()
    {
        // Arrange
        var sut = new UiaWorker();
        var callerThread = Environment.CurrentManagedThreadId;

        // Act
        var (threadId, isBackground, apartment) = sut.Run(
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
    public void Run_RethrowsTheReadsExceptionUnchanged()
    {
        // Arrange
        var sut = new UiaWorker();

        // Act
        var act = () => sut.Run<int>(_ => throw new UiaElementUnavailableException("gone"), CancellationToken.None, TimeSpan.Zero);

        // Assert
        act.Should().Throw<UiaElementUnavailableException>().WithMessage("gone");
    }

    [Fact]
    public void Run_WhenTheReadStopsAtTheDeadline_ReturnsItsPartialResult()
    {
        // Arrange - the read polls the token, like the tree walk does
        var sut = new UiaWorker();
        using var deadline = new CancellationTokenSource(Short);

        // Act
        var result = sut.Run(
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
    public void Run_WhenTheReadIsStuckPastTheDeadline_ThrowsTimeoutAndCountsTheReadUntilItReturns()
    {
        // Arrange - a read blocked in a call that ignores the token
        var sut = new UiaWorker();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(Short);

        try
        {
            // Act
            var act = () => sut.Run(_ => release.Wait(Long), deadline.Token, Short);

            // Assert
            act.Should().Throw<TimeoutException>().WithMessage("*did not answer in time*");
            sut.StuckCalls.Should().Be(1);
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue("the count drops once the abandoned read returns");
    }

    [Fact]
    public void Run_WhileTooManyReadsAreStuck_FailsAtOnceWithoutStartingTheRead()
    {
        // Arrange
        var sut = new UiaWorker(maxStuckCalls: 1);
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(Short);
        var started = false;

        try
        {
            var stuck = () => sut.Run(_ => release.Wait(Long), deadline.Token, Short);
            stuck.Should().Throw<TimeoutException>();

            // Act
            var act = () => sut.Run(_ => started = true, CancellationToken.None, TimeSpan.Zero);

            // Assert
            act.Should().Throw<TimeoutException>().WithMessage("*not responding*");
            started.Should().BeFalse();
        }
        finally
        {
            release.Set();
        }

        // Once the stuck read returns, reads run again
        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
        sut.Run(_ => 42, CancellationToken.None, TimeSpan.Zero).Should().Be(42);
    }

    [Fact]
    public void Run_WithATokenThatIsNeverCancelled_WaitsForTheRead()
    {
        // Arrange
        var sut = new UiaWorker();

        // Act
        var result = sut.Run(_ =>
        {
            Thread.Sleep(Short);
            return "done";
        }, CancellationToken.None, TimeSpan.Zero);

        // Assert
        result.Should().Be("done");
    }

    [Fact]
    public void Constructor_RejectsAStuckLimitBelowOne()
    {
        var act = () => new UiaWorker(maxStuckCalls: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Run_RejectsANegativeGrace()
    {
        var act = () => new UiaWorker().Run(_ => 1, CancellationToken.None, TimeSpan.FromMilliseconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
