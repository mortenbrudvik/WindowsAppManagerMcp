using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for ProcessService.
/// Tests T4.1-T4.5 from BACKLOG-TECHNICAL.md.
///
/// Desktop safety: every ProcessService in this class is built by <see cref="CreateSut"/> with a
/// mocked <see cref="INativeProcessWrapper"/>, the only code that starts, closes or kills
/// processes (NativeCallGuardTests enforces that). No test here can launch or terminate a real
/// process. The process listing and lookup tests below only read the real process table.
/// </summary>
public class ProcessServiceTests
{
    private const int LaunchedProcessId = 4321;

    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IInputValidationService> _mockValidationService;
    private readonly Mock<IBrowserDetectionService> _mockBrowserDetection;
    private readonly Mock<IExecutableResolver> _mockResolver = new();
    private readonly Mock<INativeProcessWrapper> _mockProcessWrapper = new();
    private readonly Mock<IProcessHandle> _mockLaunchedProcess = new();
    private ProcessStartInfo? _startedWith;

    public ProcessServiceTests()
    {
        _mockWindowService = TestDataFactory.CreateMockWindowService();
        _mockWindowService.Setup(w => w.FindWindows(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<nint?>(), It.IsAny<bool>()))
            .Returns(Array.Empty<WindowInfo>());
        _mockValidationService = new Mock<IInputValidationService>();
        _mockBrowserDetection = TestDataFactory.CreateMockBrowserDetectionService();

        // Validation accepts executables and working directories unchanged
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe));
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns<string?>(dir => new PathValidationResult(true, SanitizedPath: dir));

        // Every name resolves to itself as a native executable, independent of the machine's PATH
        _mockResolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(exe, LaunchTargetKind.NativeExecutable));

        // The process seam records what would have been started and returns a fake process
        _mockLaunchedProcess.SetupGet(p => p.Id).Returns(LaunchedProcessId);
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(startInfo => _startedWith = startInfo)
            .Returns(_mockLaunchedProcess.Object);
        _mockProcessWrapper.Setup(w => w.OpenProcess(It.IsAny<int>())).Returns((IProcessHandle?)null);
    }

    private ProcessService CreateSut(IBrowserDetectionService? browserDetection = null) => new(
        _mockWindowService.Object,
        _mockValidationService.Object,
        browserDetection ?? _mockBrowserDetection.Object,
        _mockResolver.Object,
        _mockProcessWrapper.Object);

    private void VerifyNothingStarted() =>
        _mockProcessWrapper.Verify(w => w.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);

    #region T4.1: LaunchApplication Tests

    /// <summary>
    /// T4.1.1: Test LaunchApplication with valid executable returns success.
    /// </summary>
    [Fact]
    public void LaunchApplication_WithValidExecutable_ReturnsSuccess()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("notepad.exe", waitForWindow: false);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(LaunchedProcessId);
        result.WindowHandle.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        result.ErrorCode.Should().BeNull();
        _startedWith.Should().NotBeNull();
        _startedWith!.FileName.Should().Be("notepad.exe");
        _startedWith.UseShellExecute.Should().BeTrue();
        _mockLaunchedProcess.Verify(p => p.RefreshMainWindowHandle(), Times.Never);
        _mockLaunchedProcess.Verify(p => p.Dispose(), Times.Once);
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
        VerifyNothingStarted();
    }

    [Fact]
    public void LaunchApplication_WithWaitForWindow_ReturnsWindowHandle()
    {
        // Arrange - the main window appears on the second poll
        _mockLaunchedProcess.SetupSequence(p => p.RefreshMainWindowHandle())
            .Returns(nint.Zero)
            .Returns(new nint(0x1234));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("notepad.exe", waitForWindow: true, waitTimeoutMs: 10000);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(LaunchedProcessId);
        result.WindowHandle.Should().Be(new nint(0x1234));
        _mockLaunchedProcess.Verify(p => p.RefreshMainWindowHandle(), Times.Exactly(2));
    }

    [Fact]
    public void LaunchApplication_WithWaitForWindow_UsesWindowOfProcessWhenNoMainWindow()
    {
        // Arrange - no main window, but the window service sees a window owned by the process
        var window = TestDataFactory.CreateWindowInfo(handleValue: 0x5678, processId: LaunchedProcessId);
        _mockWindowService.Setup(w => w.FindWindows(
                It.IsAny<string?>(), It.IsAny<string?>(), LaunchedProcessId, It.IsAny<nint?>(), It.IsAny<bool>()))
            .Returns([window]);
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("notepad.exe", waitForWindow: true, waitTimeoutMs: 10000);

        // Assert
        result.Success.Should().BeTrue();
        result.WindowHandle.Should().Be(new nint(0x5678));
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
        VerifyNothingStarted();
    }

    [Fact]
    public void LaunchApplication_WhenWorkingDirValidationFails_ReturnsErrorCode()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns(new PathValidationResult(false, Error: "Invalid working directory", ErrorCode: "InvalidWorkingDirectory"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("notepad.exe", workingDirectory: "invalid|path");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid working directory");
        result.ErrorCode.Should().Be("InvalidWorkingDirectory");
        VerifyNothingStarted();
    }

    [Fact]
    public void LaunchApplication_WhenProcessStartThrows_ReturnsLaunchExceptionWithValidationWarnings()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(true, SanitizedExecutable: "app.exe", Warning: "exe warning"));
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns(new PathValidationResult(true, Warning: "dir warning"));
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Throws(new Win32Exception(2, "The system cannot find the file specified."));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("app.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.LaunchException));
        result.ErrorMessage.Should().Be("The system cannot find the file specified.");
        result.Warning.Should().Be("exe warning; dir warning");
    }

    [Fact]
    public void LaunchApplication_WhenNoProcessIsStarted_ReturnsProcessStartFailed()
    {
        // Arrange - ShellExecute handed the request over without creating a process
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>())).Returns((IProcessHandle?)null);
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("app.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.ProcessStartFailed));
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
        // Arrange - the mocked process seam finds no process with this ID
        var sut = CreateSut();

        // Act
        var result = sut.KillProcess(int.MaxValue - 1, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ProcessNotFound");
        _mockProcessWrapper.Verify(w => w.OpenProcess(int.MaxValue - 1), Times.Once);
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

    // The mocked process seam records the start info instead of opening a browser or Notepad.

    [Fact]
    public void LaunchApplication_BrowserWithUrl_CallsBrowserDetectionService()
    {
        // Arrange
        var mockBrowserDetection = new Mock<IBrowserDetectionService>();
        mockBrowserDetection.Setup(b => b.IsBrowser("chrome.exe")).Returns(true);
        mockBrowserDetection.Setup(b => b.ContainsUrl(It.IsAny<string[]>())).Returns(true);
        mockBrowserDetection.Setup(b => b.GetNewWindowFlag("chrome.exe")).Returns("--new-window");
        var sut = CreateSut(mockBrowserDetection.Object);

        // Act
        var result = sut.LaunchApplication("chrome.exe", ["https://example.com"]);

        // Assert - browser detection was consulted and the flag goes before the URL
        result.Success.Should().BeTrue();
        _startedWith!.Arguments.Should().Be("--new-window https://example.com");
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
        var sut = CreateSut(mockBrowserDetection.Object);

        // Act
        var result = sut.LaunchApplication("notepad.exe", ["https://example.com"]);

        // Assert - checked for a browser, but no new window flag
        result.Success.Should().BeTrue();
        _startedWith!.Arguments.Should().Be("https://example.com");
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
        var sut = CreateSut(mockBrowserDetection.Object);

        // Act
        var result = sut.LaunchApplication("chrome.exe", ["--incognito"]);

        // Assert - checked browser and URL, but no flag since no URL
        result.Success.Should().BeTrue();
        _startedWith!.Arguments.Should().Be("--incognito");
        mockBrowserDetection.Verify(b => b.IsBrowser("chrome.exe"), Times.Once);
        mockBrowserDetection.Verify(b => b.ContainsUrl(It.IsAny<string[]?>()), Times.Once);
        mockBrowserDetection.Verify(b => b.GetNewWindowFlag(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region LaunchBrowserWithDebug Tests

    [Fact]
    public void LaunchBrowserWithDebug_UnsupportedBrowser_ReturnsErrorWithoutTouchingProcesses()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.LaunchBrowserWithDebug("firefox");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.UnsupportedBrowser));
        VerifyNothingStarted();
        _mockProcessWrapper.Verify(w => w.FindWithWhereExe(It.IsAny<string>()), Times.Never);
    }

    #endregion
}
