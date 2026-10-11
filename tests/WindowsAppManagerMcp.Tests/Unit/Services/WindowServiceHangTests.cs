using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// WindowService's window changes against a window whose application does not answer (CONCU-01): SetWindowPos and
/// ShowWindow go through <see cref="IWindowCallRunner"/>, so a hung window is refused and a call that blocks gives up
/// at the deadline with <see cref="WindowNotRespondingException"/>, instead of holding the tool (and the input gate
/// it runs in) until the application recovers. The native wrapper is a mock whose calls block on an event.
/// </summary>
public class WindowServiceHangTests
{
    /// <summary>
    /// The deadline when a responsive window's call must answer: far above the time a new thread takes to start, even
    /// on a loaded machine, so that call is never given up on.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    /// <summary>The deadline when every call blocks or is refused: nothing has to answer in time.</summary>
    private static readonly TimeSpan BlockedDeadline = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private const nint Hung = 0x1001, Responsive = 0x2001;
    private const int SwRestore = 9;

    private readonly Mock<INativeWindowWrapper> _native = new();

    public WindowServiceHangTests()
    {
        foreach (var (window, process) in new[] { (Hung, 4242u), (Responsive, 5151u) })
        {
            _native.Setup(n => n.IsWindow(window)).Returns(true);
            _native.Setup(n => n.GetWindowRect(window)).Returns((0, 0, 800, 600));
            _native.Setup(n => n.GetWindowThreadProcessId(window)).Returns(process);
        }

        _native.Setup(n => n.SetWindowPos(Responsive, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(Win32CallResult.Ok);
    }

    private WindowService CreateSut(TimeSpan? deadline = null) =>
        new(TestDataFactory.CreateMockMonitorService().Object, _native.Object, Mock.Of<IHostGuard>(),
            windowCalls: new WindowCallRunner(_native.Object, deadline ?? Deadline, maxStuckCallsPerProcess: 1, maxStuckCalls: 8));

    [Fact]
    public void MoveWindow_OfAWindowWindowsReportsHung_IsRefused_WithoutCallingSetWindowPos()
    {
        _native.Setup(n => n.IsHungAppWindow(Hung)).Returns(true);
        var sut = CreateSut();

        var act = () => sut.MoveWindow(Hung, 10, 10);

        act.Should().Throw<WindowNotRespondingException>().Which.Message.Should().Contain("not responding");
        _native.Verify(n => n.SetWindowPos(Hung, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()),
            Times.Never);
    }

    [Fact]
    public void SetWindowBounds_WhenSetWindowPosBlocks_GivesUpAtTheDeadline()
    {
        // Arrange - the application stops answering only now, so Windows does not report it hung yet
        using var answer = new ManualResetEventSlim();
        _native.Setup(n => n.SetWindowPos(Hung, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(() => answer.Wait(Long) ? Win32CallResult.Ok : Win32CallResult.Failure(0));
        var sut = CreateSut(BlockedDeadline);
        var stopwatch = Stopwatch.StartNew();

        // Act
        var act = () => sut.SetWindowBounds(Hung, 0, 0, 640, 480);

        // Assert
        act.Should().Throw<WindowNotRespondingException>();
        stopwatch.Elapsed.Should().BeLessThan(Long / 2);
        answer.Set();
    }

    [Theory]
    [InlineData(WindowStateRequest.Minimize)]
    [InlineData(WindowStateRequest.Maximize)]
    [InlineData(WindowStateRequest.Restore)]
    public void SetWindowState_WhenShowWindowBlocks_GivesUpAtTheDeadline(WindowStateRequest state)
    {
        using var answer = new ManualResetEventSlim();
        _native.Setup(n => n.ShowWindow(Hung, It.IsAny<int>())).Returns(() => answer.Wait(Long));
        var sut = CreateSut(BlockedDeadline);

        var act = () => sut.SetWindowState(Hung, state);

        act.Should().Throw<WindowNotRespondingException>();
        answer.Set();
    }

    [Fact]
    public void SetWindowBounds_OfAMinimizedWindow_WhoseRestoreBlocks_DoesNotMoveIt()
    {
        using var answer = new ManualResetEventSlim();
        _native.Setup(n => n.IsIconic(Hung)).Returns(true);
        _native.Setup(n => n.ShowWindow(Hung, SwRestore)).Returns(() => answer.Wait(Long));
        var sut = CreateSut(BlockedDeadline);

        var act = () => sut.SetWindowBounds(Hung, 0, 0, 640, 480);

        act.Should().Throw<WindowNotRespondingException>();
        _native.Verify(n => n.SetWindowPos(Hung, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()),
            Times.Never);
        answer.Set();
    }

    [Fact]
    public async Task SetWindowBoundsBatch_AHungWindow_FailsOnlyItsOwnPlacement()
    {
        _native.Setup(n => n.IsHungAppWindow(Hung)).Returns(true);
        var sut = CreateSut();

        var results = await sut.SetWindowBoundsBatchAsync(
            [(Hung, 0, 0, 640, 480, true), (Responsive, 640, 0, 640, 480, true)], delayBetweenMs: 0);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeFalse();
        results[0].Error.Should().Contain("not responding");
        results[1].Success.Should().BeTrue();
    }

    [Fact]
    public async Task SetWindowBoundsBatch_CancelledDuringThePauseBetweenWindows_StopsAtOnce()
    {
        // Arrange - two windows five seconds apart; the call is cancelled during the pause
        var sut = CreateSut();
        using var call = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        // Act
        var act = () => sut.SetWindowBoundsBatchAsync(
            [(Responsive, 0, 0, 640, 480, false), (Responsive, 640, 0, 640, 480, false)], delayBetweenMs: 5000,
            cancellationToken: call.Token);

        // Assert - the batch, which holds the input gate, ends with the call; the second window is not changed
        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        _native.Verify(n => n.SetWindowPos(Responsive, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()),
            Times.Once);
    }

    [Fact]
    public async Task SetWindowStateBatch_CancelledBeforeItStarts_ChangesNoWindow()
    {
        // Arrange
        var sut = CreateSut();
        using var call = new CancellationTokenSource();
        call.Cancel();

        // Act
        var act = () => sut.SetWindowStateBatchAsync([(Responsive, WindowStateRequest.Restore)], cancellationToken: call.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _native.Verify(n => n.ShowWindow(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)] // Task.Delay(-1) would never return
    [InlineData(5000, 5000)]
    [InlineData(int.MaxValue, 5000)] // slept inside the input gate: an agent's value must not hold it for days
    public void BatchDelay_IsBetweenZeroAndMaxDelayBetweenMs(int delayBetweenMs, int expected)
    {
        WindowService.BatchDelay(delayBetweenMs).Should().Be(TimeSpan.FromMilliseconds(expected));
        WindowService.MaxDelayBetweenMs.Should().Be(5000);
    }
}
