using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="WindowCallRunner"/>, which runs window calls that can block on another thread's window
/// (SetWindowPos, ShowWindow) with a deadline, so a hung application cannot hold the input gate forever. The calls
/// here are plain delegates that block on an event, and the native wrapper is a mock: nothing touches a window.
/// </summary>
public class WindowCallRunnerTests
{
    /// <summary>
    /// The deadline of a runner with calls that must answer: far above the time a new thread takes to start and run a
    /// trivial delegate, even on a loaded machine, so such a call is never given up on.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    /// <summary>The deadline of a runner whose every call blocks or is refused: nothing has to answer in time.</summary>
    private static readonly TimeSpan BlockedDeadline = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private const nint HungWindow = 0x1001, OtherHungWindow = 0x1002, SameAppWindow = 0x1003, OtherWindow = 0x2001, ThirdWindow = 0x3001;
    private const uint HungApp = 4242, OtherHungApp = 4343, OtherApp = 5151, ThirdApp = 6161;

    private readonly Mock<INativeWindowWrapper> _windows = new();

    public WindowCallRunnerTests()
    {
        _windows.Setup(w => w.GetWindowThreadProcessId(HungWindow)).Returns(HungApp);
        _windows.Setup(w => w.GetWindowThreadProcessId(SameAppWindow)).Returns(HungApp);
        _windows.Setup(w => w.GetWindowThreadProcessId(OtherHungWindow)).Returns(OtherHungApp);
        _windows.Setup(w => w.GetWindowThreadProcessId(OtherWindow)).Returns(OtherApp);
        _windows.Setup(w => w.GetWindowThreadProcessId(ThirdWindow)).Returns(ThirdApp);
    }

    private WindowCallRunner CreateSut(TimeSpan? deadline = null, int maxStuckCallsPerProcess = 1, int maxStuckCalls = 8) =>
        new(_windows.Object, deadline ?? Deadline, maxStuckCallsPerProcess, maxStuckCalls);

    [Fact]
    public void Run_ReturnsTheCallsResult_FromABackgroundThread()
    {
        var sut = CreateSut();
        var callerThread = Environment.CurrentManagedThreadId;

        var (threadId, isBackground) = sut.Run(OtherWindow, () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsBackground));

        threadId.Should().NotBe(callerThread);
        isBackground.Should().BeTrue("a blocked call must not keep the server process alive");
        sut.StuckCalls.Should().Be(0);
    }

    [Fact]
    public void Run_RethrowsTheCallsExceptionUnchanged()
    {
        var sut = CreateSut();

        var act = () => sut.Run<bool>(OtherWindow, () => throw new InvalidOperationException("refused"));

        act.Should().Throw<InvalidOperationException>().WithMessage("refused");
    }

    [Fact]
    public void Run_AWindowThatWindowsReportsHung_IsRefusedWithoutCallingIt()
    {
        // Arrange - IsHungAppWindow: the window's thread has not processed messages for several seconds
        _windows.Setup(w => w.IsHungAppWindow(HungWindow)).Returns(true);
        var sut = CreateSut();
        var called = false;

        // Act
        var act = () => sut.Run(HungWindow, () => called = true);

        // Assert
        act.Should().Throw<WindowNotRespondingException>().Which.Window.Should().Be(HungWindow);
        called.Should().BeFalse();
    }

    [Fact]
    public void Run_ACallThatBlocks_FailsWithNotRespondingAtTheDeadline_AndKeepsRunningOnItsOwn()
    {
        // Arrange - the window's thread does not answer SetWindowPos
        var sut = CreateSut(BlockedDeadline);
        using var answer = new ManualResetEventSlim();
        var returned = new ManualResetEventSlim();
        var stopwatch = Stopwatch.StartNew();

        // Act
        var act = () => sut.Run(HungWindow, () =>
        {
            answer.Wait(Long);
            returned.Set();
            return true;
        });

        // Assert - the caller gets NotResponding at the deadline, long before the call returns
        act.Should().Throw<WindowNotRespondingException>().Which.Message.Should().Contain("did not answer");
        stopwatch.Elapsed.Should().BeLessThan(Long / 2);
        sut.StuckCalls.Should().Be(1);

        // Once the window answers, the abandoned call returns and stops counting as stuck
        answer.Set();
        returned.Wait(Long).Should().BeTrue();
        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
    }

    [Fact]
    public void Run_WhileACallToTheSameApplicationIsStillBlocked_FailsAtOnce_AndOtherApplicationsStillWork()
    {
        // Arrange - one call to the hung application is still blocked
        var sut = CreateSut();
        using var answer = new ManualResetEventSlim();
        var blocked = () => sut.Run(HungWindow, () => answer.Wait(Long));
        blocked.Should().Throw<WindowNotRespondingException>();
        var secondCalled = false;

        // Act
        var sameApp = () => sut.Run(SameAppWindow, () => secondCalled = true);

        // Assert - refused before the call started, so without a deadline of its own
        sameApp.Should().Throw<WindowNotRespondingException>().Which.Message.Should().Contain("still");
        secondCalled.Should().BeFalse();
        sut.Run(OtherWindow, () => true).Should().BeTrue("another application is not affected");

        // Once the blocked call returns, the application can be called again
        answer.Set();
        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
        sut.Run(SameAppWindow, () => true).Should().BeTrue();
    }

    [Fact]
    public void Run_WhileTooManyCallsAreBlockedOverall_FailsAtOnceForEveryApplication()
    {
        // Arrange - two applications each have a blocked call, the most this runner allows
        var sut = CreateSut(BlockedDeadline, maxStuckCallsPerProcess: 1, maxStuckCalls: 2);
        using var answer = new ManualResetEventSlim();
        foreach (var window in new[] { HungWindow, OtherHungWindow })
        {
            var blocked = () => sut.Run(window, () => answer.Wait(Long));
            blocked.Should().Throw<WindowNotRespondingException>();
        }

        var called = false;

        // Act
        var act = () => sut.Run(ThirdWindow, () => called = true);

        // Assert
        act.Should().Throw<WindowNotRespondingException>();
        called.Should().BeFalse();
        answer.Set();
        SpinWait.SpinUntil(() => sut.StuckCalls == 0, Long).Should().BeTrue();
    }

    [Fact]
    public void DefaultDeadline_IsFiveSeconds_AsLongAsWindowsWaitsBeforeItCallsAWindowHung()
    {
        WindowCallRunner.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(5));
        new WindowCallRunner(_windows.Object).Timeout.Should().Be(WindowCallRunner.DefaultTimeout);
    }
}
