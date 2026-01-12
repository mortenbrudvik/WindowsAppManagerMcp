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

    public ProcessServiceTests()
    {
        _mockWindowService = TestDataFactory.CreateMockWindowService();
        _mockValidationService = new Mock<IInputValidationService>();

        // Set up default validation to pass through
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe));
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns<string?>(dir => new PathValidationResult(true, SanitizedPath: dir));
    }

    private ProcessService CreateSut() => new ProcessService(_mockWindowService.Object, _mockValidationService.Object);

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
}
