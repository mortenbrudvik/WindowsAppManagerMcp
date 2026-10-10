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

    public ProcessKillTests()
    {
        _mockProcess.SetupGet(p => p.Id).Returns(Pid);
        _mockProcess.SetupGet(p => p.ProcessName).Returns("notepad");
        _mockWrapper.Setup(w => w.OpenProcess(Pid)).Returns(_mockProcess.Object);
        _mockWrapper.Setup(w => w.IsAncestorOfCurrentProcess(It.IsAny<int>())).Returns(false);
    }

    private ProcessService CreateSut() => new(
        TestDataFactory.CreateMockWindowService().Object,
        new Mock<IInputValidationService>().Object,
        TestDataFactory.CreateMockBrowserDetectionService().Object,
        new Mock<IExecutableResolver>().Object,
        _mockWrapper.Object);

    private void VerifyNeverForceKilled() =>
        _mockProcess.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);

    #region Graceful Mode Never Escalates

    [Fact]
    public void KillProcess_Graceful_ProcessExits_ReportsGracefulExit()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(true);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

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
    public void KillProcess_Graceful_ProcessStillRunningAfterTimeout_DoesNotForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(false);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

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
    public void KillProcess_Graceful_NoClosableWindow_DoesNotForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(false);
        _mockProcess.SetupGet(p => p.HasExited).Returns(false);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.NoClosableWindow));
        result.CloseRequested.Should().BeFalse();
        result.Exited.Should().BeFalse();
        result.Forced.Should().BeFalse();
        result.ErrorMessage.Should().Contain("still running").And.Contain("forceKill=true");
        VerifyNeverForceKilled();
        _mockProcess.Verify(p => p.WaitForExit(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void KillProcess_Graceful_ProcessExitedBeforeCloseRequest_ReportsExited()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(false);
        _mockProcess.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.CloseRequested.Should().BeFalse();
        result.Forced.Should().BeFalse();
        VerifyNeverForceKilled();
    }

    [Fact]
    public void KillProcess_Graceful_CloseMainWindowThrows_DoesNotForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Throws(new InvalidOperationException("boom"));

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TerminationException));
        VerifyNeverForceKilled();
    }

    [Fact]
    public void KillProcess_Graceful_WaitAccessDenied_ReportsAccessDeniedWithoutForceKill()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Throws(new Win32Exception(5));

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

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
    public void KillProcess_Graceful_UsesConfiguredTimeout(int timeoutMs)
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(false);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, gracefulTimeoutMs: timeoutMs);

        // Assert
        _mockProcess.Verify(p => p.WaitForExit(timeoutMs), Times.Once);
        result.ErrorMessage.Should().Contain($"{timeoutMs} ms");
    }

    [Fact]
    public void KillProcess_Graceful_DefaultTimeoutIs5000Ms()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(true);

        // Act
        CreateSut().KillProcess(Pid, confirm: true);

        // Assert
        _mockProcess.Verify(p => p.WaitForExit(5000), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60001)]
    public void KillProcess_TimeoutOutOfRange_ReturnsInvalidTimeout(int timeoutMs)
    {
        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, gracefulTimeoutMs: timeoutMs);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.InvalidTimeout));
        _mockWrapper.Verify(w => w.OpenProcess(It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region Force Mode

    [Fact]
    public void KillProcess_Force_KillsTreeAndReportsForcedExit()
    {
        // Arrange
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(true);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: true);

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
    public void KillProcess_Force_ProcessNotExitedAfterWait_ReportsTerminationNotConfirmed()
    {
        // Arrange
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(false);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: true);

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
    public void KillProcess_Force_AccessDenied_ReportsAccessDenied()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(It.IsAny<bool>())).Throws(new Win32Exception(5));

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.AccessDenied));
        result.ErrorMessage.Should().Contain("elevated");
        result.Forced.Should().BeFalse();
        result.Exited.Should().BeFalse();
    }

    [Fact]
    public void KillProcess_Force_SomeChildrenNotTerminated_ReportsSuccessWithWarning()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(true))
            .Throws(new AggregateException("Not all processes in process tree could be terminated.", new Win32Exception(31)));
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(true);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.Forced.Should().BeTrue();
        result.Warning.Should().Contain("child processes");
    }

    [Fact]
    public void KillProcess_Force_SomeChildrenNotTerminatedAndRootAlive_ReportsFailure()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(true))
            .Throws(new AggregateException("Not all processes in process tree could be terminated.", new Win32Exception(31)));
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(false);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TerminationNotConfirmed));
        result.Warning.Should().Contain("child processes");
    }

    [Fact]
    public void KillProcess_Force_KillThrowsUnexpectedly_ReportsTerminationException()
    {
        // Arrange
        _mockProcess.Setup(p => p.Kill(true)).Throws(new InvalidOperationException("Cannot be used to terminate a process tree containing the calling process."));
        _mockProcess.SetupGet(p => p.HasExited).Returns(false);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: true);

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
    public void KillProcess_HostAncestor_IsRefusedInBothModes(bool forceKill)
    {
        // Arrange - e.g. the Windows Terminal or VS Code process that hosts the agent
        _mockProcess.SetupGet(p => p.ProcessName).Returns("WindowsTerminal");
        _mockWrapper.Setup(w => w.IsAncestorOfCurrentProcess(Pid)).Returns(true);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: forceKill);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.CannotTerminateHost));
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KillProcess_ExpectedNameMismatch_DoesNothing(bool forceKill)
    {
        // Arrange - the PID now belongs to a different process
        _mockProcess.SetupGet(p => p.ProcessName).Returns("WINWORD");

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: forceKill, expectedProcessName: "notepad");

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
    public void KillProcess_ExpectedNameMatches_Proceeds(string expected)
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(true);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, expectedProcessName: expected);

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
    public void KillProcess_ProtectedProcess_IsRefusedWithoutTouchingIt(string processName)
    {
        // Arrange
        _mockProcess.SetupGet(p => p.ProcessName).Returns(processName);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProtectedProcess));
        _mockProcess.Verify(p => p.CloseMainWindow(), Times.Never);
        VerifyNeverForceKilled();
    }

    [Fact]
    public void KillProcess_ProcessNotFound_ReturnsProcessNotFound()
    {
        // Arrange
        _mockWrapper.Setup(w => w.OpenProcess(Pid)).Returns((IProcessHandle?)null);

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNotFound));
    }

    [Fact]
    public void KillProcess_ProcessExitsBeforeNameIsRead_ReturnsProcessNotFound()
    {
        // Arrange
        _mockProcess.SetupGet(p => p.ProcessName).Throws(new InvalidOperationException("Process has exited"));

        // Act
        var result = CreateSut().KillProcess(Pid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNotFound));
    }

    [Fact]
    public void KillProcess_WithoutConfirmation_DoesNotOpenProcess()
    {
        // Act
        var result = CreateSut().KillProcess(Pid, confirm: false);

        // Assert
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.ConfirmationRequired));
        _mockWrapper.Verify(w => w.OpenProcess(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void KillProcess_DisposesProcessHandle()
    {
        // Arrange
        _mockProcess.Setup(p => p.CloseMainWindow()).Returns(true);
        _mockProcess.Setup(p => p.WaitForExit(It.IsAny<int>())).Returns(true);

        // Act
        CreateSut().KillProcess(Pid, confirm: true);

        // Assert
        _mockProcess.Verify(p => p.Dispose(), Times.Once);
    }

    #endregion
}
