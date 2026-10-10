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
    public void Run_ReturnsTheReadsResult_FromABackgroundMtaThread()
    {
        // Arrange
        var sut = new UiaWorker();
        var callerThread = Environment.CurrentManagedThreadId;

        // Act
        var (threadId, isBackground, apartment) = sut.Run(
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
    public void Run_RethrowsTheReadsExceptionUnchanged()
    {
        // Arrange
        var sut = new UiaWorker();

        // Act
        var act = () => sut.Run<int>(HungApp, _ => throw new UiaElementUnavailableException("gone"), CancellationToken.None, TimeSpan.Zero);

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
    public void Run_WhenTheReadIsStuckPastTheDeadline_ThrowsTimeoutAndCountsTheReadUntilItReturns()
    {
        // Arrange - a read blocked in a call that ignores the token
        var sut = new UiaWorker();
        using var release = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(Short);

        try
        {
            // Act
            var act = () => sut.Run(HungApp, _ => release.Wait(Long), deadline.Token, Short);

            // Assert
            act.Should().Throw<TimeoutException>().WithMessage("*did not answer in time*");
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
    public void Run_WhileTooManyReadsOfTheTargetAreStuck_FailsAtOnceWithoutStartingTheRead()
    {
        // Arrange
        var sut = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 4);
        using var release = new ManualResetEventSlim();
        var started = false;

        try
        {
            Stick(sut, HungApp, release);

            // Act
            var act = () => sut.Run(HungApp, _ => started = true, CancellationToken.None, TimeSpan.Zero);

            // Assert
            act.Should().Throw<UiaReadRefusedException>().WithMessage($"*not responding for the application (process {HungApp})*");
            started.Should().BeFalse();
        }
        finally
        {
            release.Set();
        }

        // Once the stuck read returns, reads of the application run again
        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
        sut.Run(HungApp, _ => 42, CancellationToken.None, TimeSpan.Zero).Should().Be(42);
    }

    [Fact]
    public void Run_WhileOneApplicationIsHung_StillReadsOtherApplications()
    {
        // Arrange - the hung application has used up its own limit
        var sut = new UiaWorker(maxStuckCallsPerTarget: 2, maxStuckCalls: 4);
        using var release = new ManualResetEventSlim();

        try
        {
            Stick(sut, HungApp, release);
            Stick(sut, HungApp, release);
            var hung = () => sut.Run(HungApp, _ => 1, CancellationToken.None, TimeSpan.Zero);
            hung.Should().Throw<UiaReadRefusedException>();

            // Act
            var other = sut.Run(OtherApp, _ => "read", CancellationToken.None, TimeSpan.Zero);

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
    public void Run_WhileTooManyReadsAreStuckOverall_FailsAtOnceForEveryTarget()
    {
        // Arrange - two hung applications fill the overall limit
        var sut = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 2);
        using var release = new ManualResetEventSlim();

        try
        {
            Stick(sut, HungApp, release);
            Stick(sut, OtherApp, release);

            // Act
            var act = () => sut.Run(7, _ => 1, CancellationToken.None, TimeSpan.Zero);

            // Assert
            act.Should().Throw<UiaReadRefusedException>().WithMessage("*2 earlier reads in 2 applications*");
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
    }

    [Fact]
    public void Run_WithAnUnknownTarget_SharesOneCount()
    {
        // Arrange
        var sut = new UiaWorker(maxStuckCallsPerTarget: 1, maxStuckCalls: 4);
        using var release = new ManualResetEventSlim();

        try
        {
            Stick(sut, 0, release);

            // Act - a negative process ID is unknown too
            var act = () => sut.Run(-1, _ => 1, CancellationToken.None, TimeSpan.Zero);

            // Assert
            act.Should().Throw<UiaReadRefusedException>().WithMessage("*could not identify*");
            sut.Run(HungApp, _ => 2, CancellationToken.None, TimeSpan.Zero).Should().Be(2);
        }
        finally
        {
            release.Set();
        }

        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
    }

    [Fact]
    public void Run_WithATokenThatIsNeverCancelled_WaitsForTheRead()
    {
        // Arrange
        var sut = new UiaWorker();

        // Act
        var result = sut.Run(HungApp, _ =>
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
    public void Run_RejectsANegativeGrace()
    {
        var act = () => new UiaWorker().Run(HungApp, _ => 1, CancellationToken.None, TimeSpan.FromMilliseconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Makes one read of <paramref name="target"/> miss its deadline and stay stuck until <paramref name="release"/> is set.</summary>
    private static void Stick(UiaWorker sut, int target, ManualResetEventSlim release)
    {
        using var deadline = new CancellationTokenSource(Short);
        var stuck = () => sut.Run(target, _ => release.Wait(Long), deadline.Token, Short);
        stuck.Should().Throw<TimeoutException>().Which.Should().NotBeOfType<UiaReadRefusedException>();
    }
}
