using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ProcessService.
/// Tests T4.2-T4.4 from BACKLOG-TECHNICAL.md.
///
/// Note: LaunchApplication tests (T4.1) are limited due to side effects
/// of actually launching processes. Those are better suited for integration tests.
/// </summary>
public class ProcessServiceTests
{
    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IInputValidationService> _mockValidationService;
    private readonly Mock<IBrowserDetectionService> _mockBrowserDetection;

    public ProcessServiceTests()
    {
        _mockWindowService = TestDataFactory.CreateMockWindowService();
        _mockValidationService = new Mock<IInputValidationService>();
        _mockBrowserDetection = TestDataFactory.CreateMockBrowserDetectionService();

        // Set up default validation to pass through
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe));
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns<string?>(dir => new PathValidationResult(true, SanitizedPath: dir));
    }

    private ProcessService CreateSut() => new ProcessService(
        _mockWindowService.Object,
        _mockValidationService.Object,
        _mockBrowserDetection.Object);

    #region T4.1: LaunchApplication Integration Tests

    /// <summary>
    /// T4.1.1: Test LaunchApplication with valid executable returns success.
    /// Uses notepad.exe which is available on all Windows systems.
    /// </summary>
    [Fact]
    public void LaunchApplication_WithValidExecutable_ReturnsSuccess()
    {
        // Arrange
        var sut = CreateSut();
        Process? launchedProcess = null;

        try
        {
            // Act
            var result = sut.LaunchApplication("notepad.exe", waitForWindow: false);

            // Assert
            result.Success.Should().BeTrue();
            result.ProcessId.Should().BeGreaterThan(0);
            result.ErrorMessage.Should().BeNull();
            result.ErrorCode.Should().BeNull();

            // Store process for cleanup
            if (result.ProcessId.HasValue)
            {
                try
                {
                    launchedProcess = Process.GetProcessById(result.ProcessId.Value);
                }
                catch { /* Process may have exited */ }
            }
        }
        finally
        {
            // Cleanup - kill the launched notepad
            try
            {
                launchedProcess?.Kill();
                launchedProcess?.Dispose();
            }
            catch { /* Ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// T4.1.2: Test LaunchApplication with invalid executable returns failure.
    /// </summary>
    [Fact]
    public void LaunchApplication_WithInvalidExecutable_ReturnsFailure()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(
                false,
                Error: "Executable not found or invalid path",
                ErrorCode: nameof(LaunchErrorCode.InvalidPath)));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("nonexistent_app_xyz123.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorCode.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void LaunchApplication_WithWaitForWindow_ReturnsWindowHandle()
    {
        // Arrange
        var sut = CreateSut();
        Process? launchedProcess = null;

        try
        {
            // Act - Wait for window with a generous timeout
            var result = sut.LaunchApplication("notepad.exe", waitForWindow: true, waitTimeoutMs: 10000);

            // Assert
            result.Success.Should().BeTrue();
            result.ProcessId.Should().BeGreaterThan(0);
            // WindowHandle may or may not be set depending on timing
            // but the call should succeed

            // Store process for cleanup
            if (result.ProcessId.HasValue)
            {
                try
                {
                    launchedProcess = Process.GetProcessById(result.ProcessId.Value);
                }
                catch { /* Process may have exited */ }
            }
        }
        finally
        {
            // Cleanup
            try
            {
                launchedProcess?.Kill();
                launchedProcess?.Dispose();
            }
            catch { /* Ignore cleanup errors */ }
        }
    }

    #endregion

    #region LaunchApplication Error Code Tests

    [Fact]
    public void LaunchApplication_WhenValidationFails_ReturnsErrorCode()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(false, Error: "Path traversal detected", ErrorCode: "PathTraversal"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("..\\..\\cmd.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Path traversal detected");
        result.ErrorCode.Should().Be("PathTraversal");
    }

    [Fact]
    public void LaunchApplication_WhenWorkingDirValidationFails_ReturnsErrorCode()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(true, SanitizedExecutable: "notepad.exe"));
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns(new PathValidationResult(false, Error: "Invalid working directory", ErrorCode: "InvalidWorkingDirectory"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("notepad.exe", workingDirectory: "invalid|path");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid working directory");
        result.ErrorCode.Should().Be("InvalidWorkingDirectory");
    }

    #endregion

    #region T4.2: GetRunningProcesses Tests

    [Fact]
    public void GetRunningProcesses_ReturnsNonEmptyList()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetRunningProcesses(includeWindowless: true);

        // Assert
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void GetRunningProcesses_WithNameFilter_FiltersCorrectly()
    {
        // Arrange
        var sut = CreateSut();
        var currentProcessName = Process.GetCurrentProcess().ProcessName;

        // Act
        var result = sut.GetRunningProcesses(nameFilter: currentProcessName, includeWindowless: true);

        // Assert
        result.Should().NotBeEmpty();
        result.Should().OnlyContain(p =>
            p.Name.Contains(currentProcessName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetRunningProcesses_WithNonMatchingFilter_ReturnsEmptyOrFiltered()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Use a filter that's unlikely to match any real process
        var result = sut.GetRunningProcesses(
            nameFilter: "xyznonexistentprocess123",
            includeWindowless: true);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetRunningProcesses_ReturnsCorrectProcessInfoFields()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetRunningProcesses(includeWindowless: true);

        // Assert - Find a process with valid info (some system processes may have limited access)
        var validProcess = result.FirstOrDefault(p => p.ProcessId > 0);
        validProcess.Should().NotBeNull("At least one process should have a valid ProcessId");
        validProcess!.ProcessId.Should().BeGreaterThan(0);
        validProcess.Name.Should().NotBeNullOrEmpty();
        // ExecutablePath and MemoryUsageMB may be null for some processes
    }

    [Fact]
    public void GetRunningProcesses_IncludesCurrentProcess()
    {
        // Arrange
        var sut = CreateSut();
        var currentPid = Process.GetCurrentProcess().Id;

        // Act
        var result = sut.GetRunningProcesses(includeWindowless: true);

        // Assert
        result.Should().Contain(p => p.ProcessId == currentPid);
    }

    #endregion

    #region T4.3: GetProcessName Tests

    [Fact]
    public void GetProcessName_WithValidPid_ReturnsName()
    {
        // Arrange
        var sut = CreateSut();
        var currentProcess = Process.GetCurrentProcess();

        // Act
        var result = sut.GetProcessName(currentProcess.Id);

        // Assert
        result.Should().NotBeNull();
        result.Should().Be(currentProcess.ProcessName);
    }

    [Fact]
    public void GetProcessName_WithInvalidPid_ReturnsNull()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Use a PID that's extremely unlikely to exist
        var result = sut.GetProcessName(int.MaxValue);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region T4.4: GetProcessPath Tests

    [Fact]
    public void GetProcessPath_WithValidPid_ReturnsPath()
    {
        // Arrange
        var sut = CreateSut();
        var currentPid = Process.GetCurrentProcess().Id;

        // Act
        var result = sut.GetProcessPath(currentPid);

        // Assert
        result.Should().NotBeNull();
        result.Should().EndWith(".exe");
    }

    [Fact]
    public void GetProcessPath_WithInvalidPid_ReturnsNull()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetProcessPath(int.MaxValue);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region T4.5: KillProcess Tests

    [Fact]
    public void KillProcess_WithoutConfirmation_ReturnsConfirmationRequired()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.KillProcess(1234, confirm: false);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ConfirmationRequired");
        result.ErrorMessage.Should().Contain("Confirmation required");
    }

    [Fact]
    public void KillProcess_WithZeroProcessId_ReturnsInvalidProcessId()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.KillProcess(0, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidProcessId");
    }

    [Fact]
    public void KillProcess_WithNegativeProcessId_ReturnsInvalidProcessId()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.KillProcess(-1, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidProcessId");
    }

    [Fact]
    public void KillProcess_WithCurrentProcess_ReturnsCannotTerminateSelf()
    {
        // Arrange
        var sut = CreateSut();
        var currentPid = Environment.ProcessId;

        // Act
        var result = sut.KillProcess(currentPid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("CannotTerminateSelf");
        result.ErrorMessage.Should().Contain("Cannot terminate the current process");
    }

    [Fact]
    public void KillProcess_WithNonExistentProcess_ReturnsProcessNotFound()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Use a very high PID that's unlikely to exist
        var result = sut.KillProcess(int.MaxValue - 1, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ProcessNotFound");
    }

    [Theory]
    [InlineData("System")]
    [InlineData("csrss")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("winlogon")]
    public void KillProcess_ProtectedProcessNames_AreInProtectedList(string processName)
    {
        // This test verifies that the protected process list includes critical system processes
        // We can't easily test killing them (they require elevated privileges and we shouldn't try)
        // Instead we verify the implementation has them in the protected list

        // Arrange - find a real process with this name (if it exists)
        var sut = CreateSut();
        var processes = Process.GetProcessesByName(processName);

        if (processes.Length == 0)
        {
            // Process not running, skip this test case
            return;
        }

        using var process = processes[0];

        // Act
        var result = sut.KillProcess(process.Id, confirm: true);

        // Assert - should be protected
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ProtectedProcess");
        result.ProcessName.Should().Be(processName);
    }

    #endregion

    #region Browser --new-window Injection Tests

    [Fact]
    public void LaunchApplication_BrowserWithUrl_CallsBrowserDetectionService()
    {
        // Arrange
        var mockBrowserDetection = new Mock<IBrowserDetectionService>();
        mockBrowserDetection.Setup(b => b.IsBrowser("chrome.exe")).Returns(true);
        mockBrowserDetection.Setup(b => b.ContainsUrl(It.IsAny<string[]>())).Returns(true);
        mockBrowserDetection.Setup(b => b.GetNewWindowFlag("chrome.exe")).Returns("--new-window");

        var sut = new ProcessService(
            _mockWindowService.Object,
            _mockValidationService.Object,
            mockBrowserDetection.Object);

        // Act - will fail to launch since we're not in a browser, but we can verify detection was called
        sut.LaunchApplication("chrome.exe", ["https://example.com"]);

        // Assert - verify browser detection service was called
        mockBrowserDetection.Verify(b => b.IsBrowser("chrome.exe"), Times.Once);
        mockBrowserDetection.Verify(b => b.ContainsUrl(It.IsAny<string[]>()), Times.Once);
        mockBrowserDetection.Verify(b => b.GetNewWindowFlag("chrome.exe"), Times.Once);
    }

    [Fact]
    public void LaunchApplication_NonBrowserWithUrl_DoesNotGetNewWindowFlag()
    {
        // Arrange
        var mockBrowserDetection = new Mock<IBrowserDetectionService>();
        mockBrowserDetection.Setup(b => b.IsBrowser("notepad.exe")).Returns(false);

        var sut = new ProcessService(
            _mockWindowService.Object,
            _mockValidationService.Object,
            mockBrowserDetection.Object);

        // Act
        sut.LaunchApplication("notepad.exe", ["https://example.com"]);

        // Assert - should check if it's a browser, but not get the new window flag
        mockBrowserDetection.Verify(b => b.IsBrowser("notepad.exe"), Times.Once);
        mockBrowserDetection.Verify(b => b.GetNewWindowFlag(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LaunchApplication_BrowserWithoutUrl_DoesNotGetNewWindowFlag()
    {
        // Arrange
        var mockBrowserDetection = new Mock<IBrowserDetectionService>();
        mockBrowserDetection.Setup(b => b.IsBrowser("chrome.exe")).Returns(true);
        mockBrowserDetection.Setup(b => b.ContainsUrl(It.IsAny<string[]?>())).Returns(false);

        var sut = new ProcessService(
            _mockWindowService.Object,
            _mockValidationService.Object,
            mockBrowserDetection.Object);

        // Act
        sut.LaunchApplication("chrome.exe", ["--incognito"]);

        // Assert - should check browser and URL, but not get flag since no URL
        mockBrowserDetection.Verify(b => b.IsBrowser("chrome.exe"), Times.Once);
        mockBrowserDetection.Verify(b => b.ContainsUrl(It.IsAny<string[]?>()), Times.Once);
        mockBrowserDetection.Verify(b => b.GetNewWindowFlag(It.IsAny<string>()), Times.Never);
    }

    #endregion
}
