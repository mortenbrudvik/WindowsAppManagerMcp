using System.Diagnostics;
using System.Reflection;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ProcessService.
/// Tests T4.1-T4.5 from BACKLOG-TECHNICAL.md.
///
/// Desktop safety: ProcessService starts processes with Process.Start and has no launcher
/// seam, so by default this class cannot start anything. The default validation mock accepts
/// every executable but sanitizes it to an empty file name, and Process.Start throws
/// InvalidOperationException for an empty file name before it calls into the OS. Tests that
/// really launch an application call <see cref="AllowRealLaunches"/> and are marked
/// [DesktopFact], so they only run when WAM_ALLOW_DESKTOP_TESTS=1.
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

        // Default validation accepts the executable but hands Process.Start an empty file
        // name, so LaunchApplication runs all of its logic without starting a process.
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(true, SanitizedExecutable: string.Empty));
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns<string?>(dir => new PathValidationResult(true, SanitizedPath: dir));
    }

    private ProcessService CreateSut() => new ProcessService(
        _mockWindowService.Object,
        _mockValidationService.Object,
        _mockBrowserDetection.Object);

    /// <summary>
    /// Lets validation pass executables through unchanged, so LaunchApplication starts real
    /// processes. Only for [DesktopFact] tests.
    /// </summary>
    private void AllowRealLaunches()
    {
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe));
    }

    #region T4.1: LaunchApplication Integration Tests

    /// <summary>
    /// T4.1.1: Test LaunchApplication with valid executable returns success.
    /// Desktop test: launches a real notepad.exe.
    /// </summary>
    [DesktopFact]
    public void LaunchApplication_WithValidExecutable_ReturnsSuccess()
    {
        // Arrange
        AllowRealLaunches();
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

    /// <summary>
    /// Desktop test: launches a real notepad.exe and waits for its window.
    /// </summary>
    [DesktopFact]
    public void LaunchApplication_WithWaitForWindow_ReturnsWindowHandle()
    {
        // Arrange
        AllowRealLaunches();
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

    [Fact]
    public void LaunchApplication_WhenProcessStartThrows_ReturnsLaunchExceptionWithValidationWarnings()
    {
        // Arrange - empty sanitized file name makes Process.Start throw without launching
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(true, SanitizedExecutable: string.Empty, Warning: "exe warning"));
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns(new PathValidationResult(true, Warning: "dir warning"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("app.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.LaunchException));
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.Warning.Should().Be("exe warning; dir warning");
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

        // Act - Windows process IDs are multiples of 4 and Process.GetProcessById matches
        // exactly, so no process can have this ID and nothing is terminated.
        var result = sut.KillProcess(int.MaxValue - 1, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ProcessNotFound");
    }

    /// <summary>
    /// Verifies the protected-process set directly instead of calling KillProcess on the real
    /// System, csrss, lsass, svchost and winlogon processes: if the guard ever regressed, that
    /// would try to terminate them, which crashes Windows when the run is elevated.
    /// </summary>
    [Theory]
    [InlineData("System")]
    [InlineData("smss")]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("winlogon")]
    [InlineData("dwm")]
    [InlineData("explorer")]
    public void KillProcess_ProtectedProcessNames_AreInProtectedList(string processName)
    {
        // Act
        var protectedNames = GetProtectedProcessNames();

        // Assert
        protectedNames.Contains(processName).Should().BeTrue();
        protectedNames.Contains(processName.ToUpperInvariant()).Should().BeTrue(
            "process names must match case-insensitively");
    }

    private static ISet<string> GetProtectedProcessNames()
    {
        var field = typeof(ProcessService)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(f => f.Name.Contains("Protected", StringComparison.OrdinalIgnoreCase)
                                  && typeof(ISet<string>).IsAssignableFrom(f.FieldType));

        field.Should().NotBeNull(
            "ProcessService should keep its protected process names in a static ISet<string>; " +
            "update this test if the protected-process check moved");

        return (ISet<string>)field!.GetValue(null)!;
    }

    #endregion

    #region Browser --new-window Injection Tests

    // These tests use the default validation mock, so Process.Start receives an empty file
    // name and throws: no browser or Notepad window is opened. Each test asserts the
    // LaunchException result to prove that nothing was started.

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

        // Act
        var result = sut.LaunchApplication("chrome.exe", ["https://example.com"]);

        // Assert - nothing was launched, and browser detection was consulted
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.LaunchException));
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
        var result = sut.LaunchApplication("notepad.exe", ["https://example.com"]);

        // Assert - nothing was launched; checked for a browser, but no new window flag
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.LaunchException));
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
        var result = sut.LaunchApplication("chrome.exe", ["--incognito"]);

        // Assert - nothing was launched; checked browser and URL, but no flag since no URL
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.LaunchException));
        mockBrowserDetection.Verify(b => b.IsBrowser("chrome.exe"), Times.Once);
        mockBrowserDetection.Verify(b => b.ContainsUrl(It.IsAny<string[]?>()), Times.Once);
        mockBrowserDetection.Verify(b => b.GetNewWindowFlag(It.IsAny<string>()), Times.Never);
    }

    #endregion
}
