namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// <see cref="NativeGraphicsCaptureWrapper.Availability"/> over a scripted support check, so nothing native runs: "this
/// Windows has no Windows.Graphics.Capture" (an answer, kept) is told apart from "it cannot be asked now" (a check that
/// missed its deadline or failed, or a call that has not returned; asked again later), and a capture refused for either
/// reason says which. Only the refusals are captured, and they throw before any window is read.
/// </summary>
public class NativeGraphicsCaptureWrapperAvailabilityTests
{
    /// <summary>
    /// The deadline of a check that answers at once: far above a thread's start-up on a loaded machine, so only a check
    /// that does not answer misses it (TESTING.md), and free, because an answer ends the wait.
    /// </summary>
    private static readonly TimeSpan AnswerDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The deadline of the test whose first check never answers: the test waits it out once, and the check made after the
    /// release has as long, still seconds above a thread's start-up.
    /// </summary>
    private static readonly TimeSpan StuckDeadline = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(true, GraphicsCaptureAvailability.Available)]
    [InlineData(false, GraphicsCaptureAvailability.Unsupported)]
    public void AnAnswer_IsKept(bool supported, GraphicsCaptureAvailability expected)
    {
        var checks = 0;
        using var sut = new NativeGraphicsCaptureWrapper(AnswerDeadline, () =>
        {
            checks++;
            return supported;
        });

        sut.Availability.Should().Be(expected);
        sut.Availability.Should().Be(expected);
        checks.Should().Be(1, "the answer of this Windows does not change");
    }

    [Fact]
    public void ACheckThatFails_IsUnknown_AndIsAskedAgain()
    {
        var checks = 0;
        using var sut = new NativeGraphicsCaptureWrapper(AnswerDeadline, () => ++checks == 1 ? throw new InvalidOperationException("RPC busy") : true);

        sut.Availability.Should().Be(GraphicsCaptureAvailability.Unknown);
        sut.Availability.Should().Be(GraphicsCaptureAvailability.Available);
        checks.Should().Be(2);
    }

    [Fact]
    public void ACheckThatMissesItsDeadline_IsUnknown_ThenBusyWhileItIsStuck_ThenAskedAgain()
    {
        // Arrange - the first check blocks until released, as a stuck graphics driver would
        using var release = new ManualResetEventSlim();
        var checks = 0;
        using var sut = new NativeGraphicsCaptureWrapper(StuckDeadline, () =>
        {
            if (Interlocked.Increment(ref checks) == 1)
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return true;
        });

        // Act & Assert - the check missed its deadline: whether this Windows has it is not known
        sut.Availability.Should().Be(GraphicsCaptureAvailability.Unknown);

        // While the abandoned check is stuck, nothing else is started on its thread's behalf
        sut.Availability.Should().Be(GraphicsCaptureAvailability.Busy);
        var busy = () => sut.CaptureWindow(new nint(0x1234), includeFrame: true);
        busy.Should().Throw<GraphicsCaptureException>()
            .Which.Message.Should().Contain("has not returned").And.Contain("try again").And.NotContain("1903");

        // Once it returns, the next read asks again
        release.Set();
        SpinWait.SpinUntil(() => sut.Availability != GraphicsCaptureAvailability.Busy, TimeSpan.FromSeconds(10)).Should().BeTrue();
        sut.Availability.Should().Be(GraphicsCaptureAvailability.Available);
        checks.Should().Be(2);
    }

    [Fact]
    public void ACaptureOnAComputerWithout_SaysWhatItNeeds()
    {
        using var sut = new NativeGraphicsCaptureWrapper(AnswerDeadline, () => false);

        var act = () => sut.CaptureWindow(new nint(0x1234), includeFrame: true);

        act.Should().Throw<GraphicsCaptureException>()
            .Which.Message.Should().Contain("not available on this computer").And.Contain("Windows 10 1903");
    }

    [Fact]
    public void ACaptureWhileTheCheckCannotAnswer_SaysToTryAgain()
    {
        using var sut = new NativeGraphicsCaptureWrapper(AnswerDeadline, () => throw new InvalidOperationException("RPC busy"));

        var act = () => sut.CaptureWindow(new nint(0x1234), includeFrame: true);

        act.Should().Throw<GraphicsCaptureException>()
            .Which.Message.Should().Contain("could not be checked").And.Contain("try again").And.NotContain("1903");
    }
}
