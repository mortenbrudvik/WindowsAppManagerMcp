using System.ComponentModel;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ProcessService.KillProcess (PAL-3 / PAL-5 / MCP-7 / PAL-13 / PAL-14 / pro-m5).
/// All process operations go through a mocked INativeProcessWrapper: no real process is closed or killed.
/// </summary>
public class ProcessKillTests
{
    private const int Pid = 4242;

    private readonly Mock<INativeProcessWrapper> _mockWrapper = new();
    private readonly Mock<IProcessHandle> _mockProcess = new();
    private readonly Mock<IHostGuard> _hostGuard = new();

    public ProcessKillTests()
    {
        _mockProcess.SetupGet(p => p.Id).Returns(Pid);
        _mockProcess.SetupGet(p => p.ProcessName).Returns("notepad");
        _mockWrapper.Setup(w => w.OpenProcess(Pid)).Returns(_mockProcess.Object);
        _mockWrapper.Setup(w => w.IsAncestorOfCurrentProcess(It.IsAny<int>())).Returns(false);
        _mockWrapper.Setup(w => w.GetDescendantsOf(It.IsAny<int>())).Returns([]);
        _hostGuard.Setup(g => g.IsHostProcess(It.IsAny<int>())).Returns(false);
    }

    private ProcessService CreateSut(bool withHostGuard = true) => new(
        TestDataFactory.CreateMockWindowService().Object,
        new Mock<IInputValidationService>().Object,
        TestDataFactory.CreateMockBrowserDetectionService().Object,
        new Mock<IExecutableResolver>().Object,
        _mockWrapper.Object,
        targetPolicy: null,
        hostGuard: withHostGuard ? _hostGuard.Object : null);

    private void VerifyNeverForceKilled() =>
        _mockProcess.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);

    #region What the user confirmed (roadmap M2.10 review)

    private static readonly DateTime StartedAt = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CheckKillTarget_ReadsTheProcessStartTime_ToPinTheProcessTheUserIsAskedAbout()
    {
        _mockWrapper.Setup(w => w.GetProcessStartTime(Pid)).Returns(StartedAt);
        _mockProcess.Setup(p => p.RefreshMainWindowHandle()).Returns(0x100);

        CreateSut().CheckKillTarget(Pid, forceKill: false, gracefulTimeoutMs: 5000).StartTime.Should().Be(StartedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillProcess_AConfirmedProcessWhosePidWasReused_ByOneWithTheSameName_IsNotTouched(bool forceKill)
    {
        // Arrange - the user approved notepad started at 08:00; it exited while they read the question, and another notepad
        // got its PID
        var confirmed = new KillTargetCheck(null, "notepad", [], StartedAt);
        _mockWrapper.Setup(w => w.GetProcessStartTime(Pid)).Returns(StartedAt.AddMinutes(1));

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: forceKill, expectedProcessName: "notepad",
            safeguards: new KillSafeguards(confirmed));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNameMismatch));
        result.ErrorMessage.Should().Contain("started").And.Contain("Nothing was done");
        VerifyNeverForceKilled();
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
    }

    [Fact]
    public async Task KillProcess_Forced_ATreeThatGainedAProcessTheUserWasNotShown_IsNotTerminated()
    {
        // Arrange - the user saw one child; a second started while the question was open
        var confirmed = new KillTargetCheck(null, "notepad", [(5001, "conhost.exe")], StartedAt);
        _mockWrapper.Setup(w => w.GetProcessStartTime(Pid)).Returns(StartedAt);
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns([(5001, "conhost.exe"), (5002, "cmd.exe")]);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true, safeguards: new KillSafeguards(confirmed));

        // Assert
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessTreeChanged));
        result.ErrorMessage.Should().Contain("cmd.exe (PID 5002)").And.Contain("Nothing was terminated");
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_Forced_TheTreeTheUserWasShown_OrPartOfIt_IsTerminated()
    {
        var confirmed = new KillTargetCheck(null, "notepad", [(5001, "conhost.exe"), (5002, "cmd.exe")], StartedAt);
        _mockWrapper.Setup(w => w.GetProcessStartTime(Pid)).Returns(StartedAt);
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns([(5001, "conhost.exe")]);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true, safeguards: new KillSafeguards(confirmed));

        result.Success.Should().BeTrue(result.ErrorMessage);
        _mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task KillProcess_Graceful_HandsBackTheGate_AfterTheCloseRequest_AndBeforeTheWait()
    {
        // Arrange - kill_process releases the input gate here: the wait (up to 60 s) needs no gate
        var order = new List<string>();
        _mockProcess.Setup(p => p.CloseMainWindow()).Callback(() => order.Add("close")).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("wait")).ReturnsAsync(true);

        // Act
        await CreateSut().KillProcessAsync(Pid, confirm: true, safeguards: new KillSafeguards(BeforeWait: () => order.Add("release")));

        // Assert
        order.Should().Equal("close", "release", "wait");
    }

    [Fact]
    public async Task KillProcess_Forced_HandsBackTheGate_AfterTheTermination_AndBeforeTheWait()
    {
        var order = new List<string>();
        _mockProcess.Setup(p => p.Kill(true)).Callback(() => order.Add("kill"));
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("wait")).ReturnsAsync(true);

        await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true, safeguards: new KillSafeguards(BeforeWait: () => order.Add("release")));

        order.Should().Equal("kill", "release", "wait");
    }

    #endregion

    #region Graceful Mode Never Escalates

    [Fact]
    public async Task KillProcess_Graceful_ProcessExits_ReportsGracefulExit()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessName.Should().Be("notepad");
        result.CloseRequested.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.Forced.Should().BeFalse();
        result.ErrorCode.Should().BeNull();
        VerifyNeverForceKilled();
    }

    /// <summary>
    /// The core regression: an app showing "Save changes?" does not exit in time. The old code then
    /// called Kill(entireProcessTree: true) and reported success, discarding the user's work.
    /// </summary>
    [Fact]
    public async Task KillProcess_Graceful_ProcessStillRunningAfterTimeout_DoesNotForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.GracefulCloseTimedOut));
        result.CloseRequested.Should().BeTrue();
        result.Exited.Should().BeFalse();
        result.Forced.Should().BeFalse();
        result.ErrorMessage.Should().Contain("still running").And.Contain("forceKill=true").And.Contain("child processes");
        VerifyNeverForceKilled();
    }

    /// <summary>
    /// No main window (tray app, windowless process, or main window disabled by a modal dialog):
    /// the old code force-killed the whole tree immediately.
    /// </summary>
    [Fact]
    public async Task KillProcess_Graceful_NoClosableWindow_DoesNotForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(false);
        _mockProcess.SetupGet(p => p.HasExited).Returns(false);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.NoClosableWindow));
        result.CloseRequested.Should().BeFalse();
        result.Exited.Should().BeFalse();
        result.Forced.Should().BeFalse();
        result.ErrorMessage.Should().Contain("still running").And.Contain("forceKill=true");
        VerifyNeverForceKilled();
        _mockProcess.Verify(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task KillProcess_Graceful_ProcessExitedBeforeCloseRequest_ReportsExited()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(false);
        _mockProcess.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.CloseRequested.Should().BeFalse();
        result.Forced.Should().BeFalse();
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_Graceful_CloseMainWindowThrows_DoesNotForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Throws(new InvalidOperationException("boom"));

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TerminationException));
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_Graceful_WaitAccessDenied_ReportsAccessDeniedWithoutForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Win32Exception(5));

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.AccessDenied));
        result.CloseRequested.Should().BeTrue();
        VerifyNeverForceKilled();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    [InlineData(60000)]
    public async Task KillProcess_Graceful_UsesConfiguredTimeout(int timeoutMs)
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, gracefulTimeoutMs: timeoutMs);

        // Assert
        _mockProcess.Verify(p => p.WaitForExitAsync(timeoutMs, It.IsAny<CancellationToken>()), Times.Once);
        result.ErrorMessage.Should().Contain($"{timeoutMs} ms");
    }

    [Fact]
    public async Task KillProcess_Graceful_CancelledWhileWaitingForTheExit_Throws()
    {
        // Arrange - the close request is sent; the process does not exit before the call is cancelled. The call is
        // cancelled once it waits for the exit, not by a timer, so a slow start cannot cancel it before the close request
        using var call = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, CancellationToken token) =>
            {
                waiting.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return true;
            });

        // Act
        var kill = CreateSut().KillProcessAsync(Pid, confirm: true, gracefulTimeoutMs: 60000, cancellationToken: call.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        call.Cancel();

        // Assert - reported as cancelled, not as a TerminationException result; never escalated
        await FluentActions.Awaiting(() => kill).Should().ThrowAsync<OperationCanceledException>();
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Once);
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_CancelledBeforeAnythingIsSent_DoesNothing()
    {
        // Arrange
        using var call = new CancellationTokenSource();
        call.Cancel();

        // Act
        var act = () => CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true, cancellationToken: call.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_Graceful_DefaultTimeoutIs5000Ms()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        _mockProcess.Verify(p => p.WaitForExitAsync(5000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public async Task KillProcess_TimeoutOutOfRange_ReturnsInvalidTimeout(int timeoutMs)
    {
        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, gracefulTimeoutMs: timeoutMs);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.InvalidTimeout));
        _mockWrapper.Verify(w => w.OpenProcess(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// kill_process asks the user only about a kill that can work: a graceful close of a process without a main window
    /// is refused before the question, as the kill itself would refuse it after the user's yes.
    /// </summary>
    [Fact]
    public void CheckKillTarget_Graceful_ProcessWithoutAMainWindow_IsNoClosableWindow()
    {
        // Arrange
        _mockProcess.Setup(p => p.RefreshMainWindowHandle()).Returns(nint.Zero);

        // Act
        var check = CreateSut().CheckKillTarget(Pid, forceKill: false, gracefulTimeoutMs: 5000);

        // Assert
        check.Refusal.Should().NotBeNull();
        check.Refusal!.ErrorCode.Should().Be(nameof(ProcessErrorCode.NoClosableWindow));
        check.Refusal.ErrorMessage.Should().Contain("forceKill=true");
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Fact]
    public void CheckKillTarget_Graceful_ProcessWithAMainWindow_CanBeAskedAbout()
    {
        // Arrange
        _mockProcess.Setup(p => p.RefreshMainWindowHandle()).Returns((nint)0x1234);

        // Act
        var check = CreateSut().CheckKillTarget(Pid, forceKill: false, gracefulTimeoutMs: 5000);

        // Assert
        check.Refusal.Should().BeNull();
        check.ProcessName.Should().Be("notepad");
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
    }

    [Fact]
    public void CheckKillTarget_Forced_DoesNotNeedAMainWindow()
    {
        _mockProcess.Setup(p => p.RefreshMainWindowHandle()).Returns(nint.Zero);

        CreateSut().CheckKillTarget(Pid, forceKill: true, gracefulTimeoutMs: 5000).Refusal.Should().BeNull();
    }

    #endregion

    #region Force Mode

    [Fact]
    public async Task KillProcess_Force_KillsTreeAndReportsForcedExit()
    {
        // Arrange
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Forced.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.CloseRequested.Should().BeFalse();
        _mockProcess.Verify(p => p.Kill(true), Times.Once);
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
    }

    /// <summary>
    /// PAL-14: the old code ignored WaitForExit's result and always reported success.
    /// </summary>
    [Fact]
    public async Task KillProcess_Force_ProcessNotExitedAfterWait_ReportsTerminationNotConfirmed()
    {
        // Arrange
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TerminationNotConfirmed));
        result.Forced.Should().BeTrue();
        result.Exited.Should().BeFalse();
    }

    /// <summary>
    /// PAL-14 / PAL-15: access denied surfaces as Win32Exception (error 5), which the old
    /// catch (UnauthorizedAccessException) never caught.
    /// </summary>
    [Fact]
    public async Task KillProcess_Force_AccessDenied_ReportsAccessDenied()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(It.IsAny<bool>())).Throws(new Win32Exception(5));

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.AccessDenied));
        result.ErrorMessage.Should().Contain("elevated");
        result.Forced.Should().BeFalse();
        result.Exited.Should().BeFalse();
    }

    [Fact]
    public async Task KillProcess_Force_SomeChildrenNotTerminated_ReportsSuccessWithWarning()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(true))
            .Throws(new AggregateException("Not all processes in process tree could be terminated.", new Win32Exception(31)));
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.Forced.Should().BeTrue();
        result.Warning.Should().Contain("child processes");
    }

    [Fact]
    public async Task KillProcess_Force_SomeChildrenNotTerminatedAndRootAlive_ReportsFailure()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(true))
            .Throws(new AggregateException("Not all processes in process tree could be terminated.", new Win32Exception(31)));
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TerminationNotConfirmed));
        result.Warning.Should().Contain("child processes");
    }

    /// <summary>
    /// .NET 8's Kill(entireProcessTree: true) catches the failure for the root and for each child,
    /// keeps going, and throws one AggregateException at the end. An elevated child that denies
    /// access therefore arrives as AggregateException(Win32Exception(5)) even though the root
    /// was terminated.
    /// </summary>
    [Fact]
    public async Task KillProcess_Force_ChildAccessDeniedButRootExited_ReportsRootTerminatedWithWarning()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(true))
            .Throws(new AggregateException("Not all processes in process tree could be terminated.", new Win32Exception(5)));
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Forced.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        result.Warning.Should().Contain("child processes").And.Contain("still be running").And.Contain("elevated");
    }

    [Fact]
    public async Task KillProcess_Force_AccessDeniedInTreeAndRootStillRunning_ReportsAccessDeniedAfterForcedAttempt()
    {
        // Arrange - the root itself is elevated; its children may have been terminated
        _mockProcess.Setup(p => p.Kill(true))
            .Throws(new AggregateException("Not all processes in process tree could be terminated.", new Win32Exception(5)));
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.AccessDenied));
        result.ErrorMessage.Should().Contain("elevated");
        result.Forced.Should().BeTrue();
        result.Exited.Should().BeFalse();
        result.Warning.Should().Contain("child processes may have been terminated");
    }

    [Fact]
    public async Task KillProcess_Force_KillThrowsUnexpectedly_ReportsTerminationException()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(true)).Throws(new InvalidOperationException("Cannot be used to terminate a process tree containing the calling process."));
        _mockProcess.SetupGet(p => p.HasExited).Returns(false);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TerminationException));
        result.ErrorMessage.Should().Contain("calling process");
    }

    #endregion

    #region Guards

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillProcess_HostAncestor_IsRefusedInBothModes(bool forceKill)
    {
        // Arrange - e.g. the Windows Terminal or VS Code process that hosts the agent. The live ancestor
        // walk still counts when the host guard has no record of it (its ancestor read failed at start).
        _mockProcess.SetupGet(p => p.ProcessName).Returns("WindowsTerminal");
        _mockWrapper.Setup(w => w.IsAncestorOfCurrentProcess(Pid)).Returns(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: forceKill);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.CannotTerminateHost));
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillProcess_HostProcessThatIsNoAncestor_IsRefusedInBothModes(bool forceKill)
    {
        // Arrange - Windows Terminal as the default terminal: it shows the agent's console but is not an
        // ancestor of the server. The host guard recorded it at start (console host or startup foreground).
        _mockProcess.SetupGet(p => p.ProcessName).Returns("WindowsTerminal");
        _hostGuard.Setup(g => g.IsHostProcess(Pid)).Returns(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: forceKill);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.CannotTerminateHost));
        result.ErrorMessage.Should().Contain("WindowsTerminal").And.Contain("agent's host");
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_ForceKill_AHostProcessInTheTree_RefusesTheWholeTree()
    {
        // Arrange - a launcher that started the agent's terminal: forceKill would end the terminal too
        const int terminalPid = 5004;
        _mockProcess.SetupGet(p => p.ProcessName).Returns("launcher");
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns([(5001, "helper.exe"), (terminalPid, "WindowsTerminal.exe")]);
        _hostGuard.Setup(g => g.IsHostProcess(terminalPid)).Returns(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.CannotTerminateHost));
        result.ErrorMessage.Should().Contain("WindowsTerminal.exe").And.Contain(terminalPid.ToString())
            .And.Contain("child process").And.Contain("without forceKill");
        result.ProcessName.Should().Be("launcher");
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_ForceKill_TheServerInTheTree_IsRefusedEvenWithoutAHostGuard()
    {
        // Arrange - the runtime would also refuse, but only with an exception after other checks
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns([(Environment.ProcessId, "WindowsAppManagerMcp.exe")]);

        // Act
        var result = await CreateSut(withHostGuard: false).KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.CannotTerminateHost));
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_Graceful_AHostProcessInTheTree_DoesNotMatter()
    {
        // Arrange - a graceful close asks only the process's own main window to close; its children live on
        _hostGuard.Setup(g => g.IsHostProcess(5004)).Returns(true);
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns([(5004, "WindowsTerminal.exe")]);
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeTrue();
        _mockWrapper.Verify(w => w.GetDescendantsOf(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task KillProcess_ForceKill_WhenTheProcessTreeCannotBeRead_RefusesInsteadOfKillingAnUncheckedTree()
    {
        // Arrange - the process snapshot failed: the children are unknown, so neither the policy nor the
        // host check could look at them, and Kill(entireProcessTree) would still end them all
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns((IReadOnlyList<(int, string)>?)null);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessTreeUnreadable));
        result.ErrorMessage.Should().Contain("child processes").And.Contain("without forceKill")
            .And.Contain("did not list the process", "the snapshot can also miss a process that was just found running");
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_Graceful_WhenTheProcessTreeCannotBeRead_StillAsksTheWindowToClose()
    {
        // Arrange - a graceful close never ends the children, so it does not need the tree
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns((IReadOnlyList<(int, string)>?)null);
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeTrue();
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_ForceKill_TreeWithoutHostProcesses_AsksTheHostGuardAboutEveryProcess()
    {
        // Arrange
        _mockWrapper.Setup(w => w.GetDescendantsOf(Pid)).Returns([(5001, "conhost.exe"), (5002, "child.exe")]);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeTrue();
        _hostGuard.Verify(g => g.IsHostProcess(Pid), Times.Once);
        _hostGuard.Verify(g => g.IsHostProcess(5001), Times.Once);
        _hostGuard.Verify(g => g.IsHostProcess(5002), Times.Once);
        _mockProcess.Verify(p => p.Kill(true), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillProcess_ExpectedNameMismatch_DoesNothing(bool forceKill)
    {
        // Arrange - the PID now belongs to a different process
        _mockProcess.SetupGet(p => p.ProcessName).Returns("WINWORD");

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: forceKill, expectedProcessName: "notepad");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNameMismatch));
        result.ProcessName.Should().Be("WINWORD");
        result.ErrorMessage.Should().Contain("notepad").And.Contain("WINWORD");
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("Notepad")]
    [InlineData("notepad.exe")]
    [InlineData("NOTEPAD.EXE")]
    [InlineData("  notepad  ")]
    public async Task KillProcess_ExpectedNameMatches_Proceeds(string expected)
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, expectedProcessName: expected);

        // Assert
        result.Success.Should().BeTrue();
    }

    [Theory]
    [InlineData("sihost")]
    [InlineData("ctfmon")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("ShellExperienceHost")]
    [InlineData("SearchHost")]
    [InlineData("TextInputHost")]
    [InlineData("fontdrvhost")]
    [InlineData("csrss")]
    [InlineData("explorer")]
    public async Task KillProcess_ProtectedProcess_IsRefusedWithoutTouchingIt(string processName)
    {
        // Arrange
        _mockProcess.SetupGet(p => p.ProcessName).Returns(processName);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProtectedProcess));
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Fact]
    public async Task KillProcess_ProcessNotFound_ReturnsProcessNotFound()
    {
        // Arrange
        _mockWrapper.Setup(w => w.OpenProcess(Pid)).Returns((IProcessHandle?)null);

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNotFound));
    }

    [Fact]
    public async Task KillProcess_ProcessExitsBeforeNameIsRead_ReturnsProcessNotFound()
    {
        // Arrange
        _mockProcess.SetupGet(p => p.ProcessName).Throws(new InvalidOperationException("Process has exited"));

        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNotFound));
    }

    [Fact]
    public async Task KillProcess_WithoutConfirmation_DoesNotOpenProcess()
    {
        // Act
        var result = await CreateSut().KillProcessAsync(Pid, confirm: false);

        // Assert
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ConfirmationRequired));
        _mockWrapper.Verify(w => w.OpenProcess(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task KillProcess_DisposesProcessHandle()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        await CreateSut().KillProcessAsync(Pid, confirm: true);

        // Assert
        _mockProcess.Verify(p => p.Dispose(), Times.Once);
    }

    #endregion
}
