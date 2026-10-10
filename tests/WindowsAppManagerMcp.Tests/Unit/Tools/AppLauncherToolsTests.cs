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
    public async Task LaunchApplication_CallsServiceWithAllParameters()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, 1234, null, null));
        var sut = CreateSut();

        // Act
        await sut.LaunchApplication(
            executable: "notepad.exe",
            arguments: new[] { "file.txt" },
            workingDirectory: @"C:\Temp",
            waitForWindow: true,
            waitTimeoutMs: 10000);

        // Assert
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(
            "notepad.exe",
            new[] { "file.txt" },
            @"C:\Temp",
            true,
            10000,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_WithDefaults_UsesDefaultValues()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, 1234, null, null));
        var sut = CreateSut();

        // Act
        await sut.LaunchApplication("notepad.exe");

        // Assert
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(
            "notepad.exe",
            null,
            null,
            false,
            5000,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_ReturnsSuccessResult()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, 1234, new nint(5678), null));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplication("notepad.exe", waitForWindow: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(1234);
        result.WindowHandle.Should().Be(5678);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task LaunchApplication_ReturnsFailureResult()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(false, null, null, "File not found"));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplication("nonexistent.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.WindowHandle.Should().BeNull();
        result.Error.Should().Be("File not found");
    }

    [Fact]
    public async Task LaunchApplication_WithNullWindowHandle_ReturnsNullHandle()
    {
        // Arrange
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, 1234, null, null));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplication("notepad.exe");

        // Assert
        result.WindowHandle.Should().BeNull();
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(30000, 30000)]
    [InlineData(int.MaxValue, 30000)] // the wait runs inside the input gate: an agent's value must not hold it for days
    [InlineData(-5, 0)]
    public async Task LaunchApplication_WaitsForTheWindowAtMostMaxWaitTimeoutMs(int waitTimeoutMs, int expected)
    {
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(It.IsAny<string>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LaunchResult(true, 1234, null, null));
        var sut = CreateSut();

        await sut.LaunchApplication("notepad.exe", waitForWindow: true, waitTimeoutMs: waitTimeoutMs);

        _mockProcessService.Verify(p => p.LaunchApplicationAsync("notepad.exe", null, null, true, expected, It.IsAny<CancellationToken>()), Times.Once);
        AppLauncherTools.MaxWaitTimeoutMs.Should().Be(30000);
    }

    [Fact]
    public async Task LaunchApplicationsBatch_WaitsForEachWindowAtMostMaxWaitTimeoutMs()
    {
        _mockProcessService.Setup(p => p.LaunchApplicationsBatchAsync(
                It.IsAny<IReadOnlyList<(string Executable, string[]? Arguments, string? WorkingDirectory)>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LaunchResult(true, 1234, null, null)]);
        var sut = CreateSut();

        await sut.LaunchApplicationsBatch([new ApplicationLaunchRequest("notepad.exe")], waitForWindows: true, waitTimeoutMs: int.MaxValue);

        _mockProcessService.Verify(p => p.LaunchApplicationsBatchAsync(
            It.IsAny<IReadOnlyList<(string Executable, string[]? Arguments, string? WorkingDirectory)>>(), true, AppLauncherTools.MaxWaitTimeoutMs,
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
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

    #region KillProcess Tests

    private void SetupKill(KillResult result)
    {
        _mockProcessService.Setup(p => p.KillProcessAsync(
            It.IsAny<int>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<int>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    [Fact]
    public async Task KillProcess_CallsServiceWithAllParameters()
    {
        // Arrange
        SetupKill(new KillResult(true, 1234, "notepad"));
        var sut = CreateSut();

        // Act
        await sut.KillProcess(processId: 1234, confirm: true, forceKill: true, gracefulTimeoutMs: 2500, expectedProcessName: "notepad.exe");

        // Assert
        _mockProcessService.Verify(p => p.KillProcessAsync(1234, true, true, 2500, "notepad.exe", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KillProcess_WithDefaults_UsesDefaultForceKill()
    {
        // Arrange
        SetupKill(new KillResult(true, 1234, "notepad"));
        var sut = CreateSut();

        // Act
        await sut.KillProcess(processId: 1234, confirm: true);

        // Assert - graceful by default, 5 s timeout, no name guard
        _mockProcessService.Verify(p => p.KillProcessAsync(1234, true, false, 5000, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KillProcess_ReturnsSuccessResult()
    {
        // Arrange
        SetupKill(new KillResult(true, 1234, "notepad", CloseRequested: true, Exited: true));
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcess(processId: 1234, confirm: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(1234);
        result.ProcessName.Should().Be("notepad");
        result.Error.Should().BeNull();
        result.ErrorCode.Should().BeNull();
        result.CloseRequested.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.Forced.Should().BeFalse();
    }

    [Fact]
    public async Task KillProcess_MapsForcedExitAndWarning()
    {
        // Arrange
        SetupKill(new KillResult(true, 1234, "node", Forced: true, Exited: true, Warning: "Some child processes could not be terminated"));
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcess(processId: 1234, confirm: true, forceKill: true);

        // Assert
        result.Forced.Should().BeTrue();
        result.Exited.Should().BeTrue();
        result.CloseRequested.Should().BeFalse();
        result.Warning.Should().Be("Some child processes could not be terminated");
    }

    [Fact]
    public async Task KillProcess_GracefulTimeout_MapsStillRunningResult()
    {
        // Arrange
        SetupKill(new KillResult(
            false, 1234, "notepad",
            ErrorMessage: "still running",
            ErrorCode: nameof(ProcessErrorCode.GracefulCloseTimedOut),
            CloseRequested: true));
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcess(processId: 1234, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("GracefulCloseTimedOut");
        result.CloseRequested.Should().BeTrue();
        result.Forced.Should().BeFalse();
        result.Exited.Should().BeFalse();
    }

    [Fact]
    public async Task KillProcess_ReturnsFailureResult()
    {
        // Arrange
        SetupKill(new KillResult(false, 1234, null, "Process not found", "ProcessNotFound"));
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcess(processId: 1234, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().Be(1234);
        result.ProcessName.Should().BeNull();
        result.Error.Should().Be("Process not found");
        result.ErrorCode.Should().Be("ProcessNotFound");
    }

    [Fact]
    public async Task KillProcess_WithConfirmFalse_ReturnsConfirmationRequired()
    {
        // Arrange
        SetupKill(new KillResult(false, 1234, null, "Confirmation required", "ConfirmationRequired"));
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcess(processId: 1234, confirm: false);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ConfirmationRequired");
    }

    [Fact]
    public async Task KillProcess_WithProtectedProcess_ReturnsProtectedProcess()
    {
        // Arrange
        SetupKill(new KillResult(false, 4, "System", "Cannot terminate protected system process", "ProtectedProcess"));
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcess(processId: 4, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessName.Should().Be("System");
        result.ErrorCode.Should().Be("ProtectedProcess");
    }

    [Fact]
    public async Task KillProcess_PassesTheCallsToken_SoTheClientCanCancelTheWaitForTheExit()
    {
        // Arrange
        using var call = new CancellationTokenSource();
        _mockProcessService.Setup(p => p.KillProcessAsync(
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(true, 1234, "notepad", CloseRequested: true, Exited: true));

        // Act
        await CreateSut().KillProcess(processId: 1234, confirm: true, cancellationToken: call.Token);

        // Assert
        _mockProcessService.Verify(p => p.KillProcessAsync(1234, true, false, 5000, null, call.Token), Times.Once);
    }

    #endregion
}
