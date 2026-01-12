namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for AppLauncherTools.
/// Tests the MCP tool layer that exposes application launching functionality.
/// </summary>
public class AppLauncherToolsTests
{
    private readonly Mock<IProcessService> _mockProcessService;

    public AppLauncherToolsTests()
    {
        _mockProcessService = new Mock<IProcessService>();
    }

    private AppLauncherTools CreateSut() => new AppLauncherTools(_mockProcessService.Object);

    #region LaunchApplication Tests

    [Fact]
    public void LaunchApplication_CallsServiceWithAllParameters()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplication(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>()))
            .Returns(new LaunchResult(true, 1234, null, null));
        var sut = CreateSut();

        // Act
        sut.LaunchApplication(
            executable: "notepad.exe",
            arguments: new[] { "file.txt" },
            workingDirectory: @"C:\Temp",
            waitForWindow: true,
            waitTimeoutMs: 10000);

        // Assert
        _mockProcessService.Verify(p => p.LaunchApplication(
            "notepad.exe",
            new[] { "file.txt" },
            @"C:\Temp",
            true,
            10000), Times.Once);
    }

    [Fact]
    public void LaunchApplication_WithDefaults_UsesDefaultValues()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplication(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>()))
            .Returns(new LaunchResult(true, 1234, null, null));
        var sut = CreateSut();

        // Act
        sut.LaunchApplication("notepad.exe");

        // Assert
        _mockProcessService.Verify(p => p.LaunchApplication(
            "notepad.exe",
            null,
            null,
            false,
            5000), Times.Once);
    }

    [Fact]
    public void LaunchApplication_ReturnsSuccessResult()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplication(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>()))
            .Returns(new LaunchResult(true, 1234, new nint(5678), null));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("notepad.exe", waitForWindow: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(1234);
        result.WindowHandle.Should().Be(5678);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void LaunchApplication_ReturnsFailureResult()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplication(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>()))
            .Returns(new LaunchResult(false, null, null, "File not found"));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("nonexistent.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.WindowHandle.Should().BeNull();
        result.Error.Should().Be("File not found");
    }

    [Fact]
    public void LaunchApplication_WithNullWindowHandle_ReturnsNullHandle()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplication(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>()))
            .Returns(new LaunchResult(true, 1234, null, null));
        var sut = CreateSut();

        // Act
        var result = sut.LaunchApplication("notepad.exe");

        // Assert
        result.WindowHandle.Should().BeNull();
    }

    #endregion

    #region ListProcesses Tests

    [Fact]
    public void ListProcesses_CallsServiceWithParameters()
    {
        // Arrange
        _mockProcessService.Setup(p => p.GetRunningProcesses(
            It.IsAny<string?>(),
            It.IsAny<bool>()))
            .Returns(new List<ProcessInfo>());
        var sut = CreateSut();

        // Act
        sut.ListProcesses(nameFilter: "chrome", includeWindowless: true);

        // Assert
        _mockProcessService.Verify(p => p.GetRunningProcesses("chrome", true), Times.Once);
    }

    [Fact]
    public void ListProcesses_WithDefaults_UsesDefaultValues()
    {
        // Arrange
        _mockProcessService.Setup(p => p.GetRunningProcesses(
            It.IsAny<string?>(),
            It.IsAny<bool>()))
            .Returns(new List<ProcessInfo>());
        var sut = CreateSut();

        // Act
        sut.ListProcesses();

        // Assert
        _mockProcessService.Verify(p => p.GetRunningProcesses(null, false), Times.Once);
    }

    [Fact]
    public void ListProcesses_ReturnsEmptyList_WhenNoProcesses()
    {
        // Arrange
        _mockProcessService.Setup(p => p.GetRunningProcesses(
            It.IsAny<string?>(),
            It.IsAny<bool>()))
            .Returns(new List<ProcessInfo>());
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ListProcesses_MapsProcessInfoToDto()
    {
        // Arrange
        var processInfo = new ProcessInfo(
            ProcessId: 1234,
            Name: "chrome",
            ExecutablePath: @"C:\Program Files\Google\Chrome\chrome.exe",
            WindowCount: 5,
            MemoryUsageMB: 512.75);
        _mockProcessService.Setup(p => p.GetRunningProcesses(
            It.IsAny<string?>(),
            It.IsAny<bool>()))
            .Returns(new List<ProcessInfo> { processInfo });
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result.Should().HaveCount(1);
        var dto = result[0];
        dto.ProcessId.Should().Be(1234);
        dto.Name.Should().Be("chrome");
        dto.ExecutablePath.Should().Be(@"C:\Program Files\Google\Chrome\chrome.exe");
        dto.WindowCount.Should().Be(5);
        dto.MemoryUsageMB.Should().Be(512.8); // Rounded to 1 decimal
    }

    [Fact]
    public void ListProcesses_RoundsMemoryUsage()
    {
        // Arrange
        var processInfo = new ProcessInfo(
            ProcessId: 1234,
            Name: "app",
            ExecutablePath: null,
            WindowCount: 1,
            MemoryUsageMB: 123.456789);
        _mockProcessService.Setup(p => p.GetRunningProcesses(
            It.IsAny<string?>(),
            It.IsAny<bool>()))
            .Returns(new List<ProcessInfo> { processInfo });
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result[0].MemoryUsageMB.Should().Be(123.5);
    }

    [Fact]
    public void ListProcesses_HandlesNullMemoryUsage()
    {
        // Arrange
        var processInfo = new ProcessInfo(
            ProcessId: 1234,
            Name: "app",
            ExecutablePath: null,
            WindowCount: 0,
            MemoryUsageMB: null);
        _mockProcessService.Setup(p => p.GetRunningProcesses(
            It.IsAny<string?>(),
            It.IsAny<bool>()))
            .Returns(new List<ProcessInfo> { processInfo });
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result[0].MemoryUsageMB.Should().BeNull();
    }

    [Fact]
    public void ListProcesses_ReturnsMultipleProcesses()
    {
        // Arrange
        var processes = new List<ProcessInfo>
        {
            new(1, "app1", null, 1, 100),
            new(2, "app2", null, 2, 200),
            new(3, "app3", null, 3, 300)
        };
        _mockProcessService.Setup(p => p.GetRunningProcesses(
            It.IsAny<string?>(),
            It.IsAny<bool>()))
            .Returns(processes);
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result.Should().HaveCount(3);
    }

    #endregion
}
