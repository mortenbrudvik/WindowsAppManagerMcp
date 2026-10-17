namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
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

    /// <summary>The service returns <paramref name="result"/> for any launch request.</summary>
    private void LaunchReturns(LaunchResult result) =>
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task LaunchApplication_CallsServiceWithAllParameters()
    {
        // Arrange
        LaunchReturns(new LaunchResult(true, 1234, Status: LaunchStatus.Started));
        var sut = CreateSut();
        var hint = new LaunchWindowHint(ProcessName: "Notepad", TitleContains: "file.txt");

        // Act
        await sut.LaunchApplication(
            executable: "notepad.exe",
            arguments: new[] { "file.txt" },
            workingDirectory: @"C:\Temp",
            waitForWindow: true,
            waitTimeoutMs: 10000,
            expectWindow: hint);

        // Assert
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(
            It.Is<LaunchRequest>(r => r.Executable == "notepad.exe" && r.Arguments!.SequenceEqual(new[] { "file.txt" }) &&
                                      r.WorkingDirectory == @"C:\Temp" && r.ExpectWindow == hint),
            true,
            10000,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_WithDefaults_UsesDefaultValues()
    {
        // Arrange
        LaunchReturns(new LaunchResult(true, 1234, Status: LaunchStatus.Started));
        var sut = CreateSut();

        // Act
        await sut.LaunchApplication("notepad.exe");

        // Assert
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(
            new LaunchRequest("notepad.exe", null, null, null, false, false),
            false,
            5000,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_PassesForceAccessibility_AndReportsWhetherItWasApplied()
    {
        // Arrange - roadmap M3.3
        LaunchReturns(new LaunchResult(true, 1234, Status: LaunchStatus.Started, ForceAccessibilityApplied: true));

        // Act
        var result = await CreateSut().LaunchApplication(@"C:\Apps\VS Code\Code.exe", forceAccessibility: true);

        // Assert
        result.ForceAccessibilityApplied.Should().BeTrue();
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(
            It.Is<LaunchRequest>(r => r.ForceAccessibility),
            false,
            5000,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_ReturnsTheStatusAndTheWholeWindowItFound()
    {
        // Arrange - the window belongs to the process the launcher handed off to
        var window = TestDataFactory.CreateWindowInfo(handleValue: 5678, title: "Calculator", processName: "CalculatorApp", processId: 4321) with
        {
            ClassName = "ApplicationFrameWindow",
            HostProcessName = "ApplicationFrameHost",
            HostProcessId = 900
        };
        LaunchReturns(new LaunchResult(true, 1234, window.Handle, Status: LaunchStatus.WindowObserved, Window: window, LauncherExited: true));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplication("calc", waitForWindow: true);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(1234);
        result.WindowHandle.Should().Be(5678);
        result.LaunchStatus.Should().Be("windowObserved");
        result.LauncherExited.Should().BeTrue();
        result.Window!.Handle.Should().Be(5678);
        result.Window.ProcessName.Should().Be("CalculatorApp");
        result.Window.ProcessId.Should().Be(4321);
        result.Window.HostProcessName.Should().Be("ApplicationFrameHost");
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(LaunchStatus.Started, "started")]
    [InlineData(LaunchStatus.WindowObserved, "windowObserved")]
    [InlineData(LaunchStatus.ReusedExistingWindow, "reusedExistingWindow")]
    [InlineData(LaunchStatus.OpenedInTab, "openedInTab")]
    [InlineData(LaunchStatus.PossibleHandoff, "possibleHandoff")]
    [InlineData(LaunchStatus.ExitedWithoutWindow, "exitedWithoutWindow")]
    [InlineData(LaunchStatus.TimedOut, "timedOut")]
    [InlineData(LaunchStatus.AwaitingUserConsent, "awaitingUserConsent")]
    public async Task LaunchApplication_ReportsEveryStatusByItsCamelCaseName(LaunchStatus status, string expected)
    {
        // Arrange
        LaunchReturns(new LaunchResult(true, Status: status));

        // Act
        var result = await CreateSut().LaunchApplication("notepad.exe");

        // Assert
        result.LaunchStatus.Should().Be(expected);
    }

    [Fact]
    public async Task LaunchApplication_ReturnsFailureResult()
    {
        // Arrange
        LaunchReturns(new LaunchResult(false, null, null, "File not found", nameof(LaunchErrorCode.ExecutableNotFound)));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplication("nonexistent.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.WindowHandle.Should().BeNull();
        result.Window.Should().BeNull();
        result.LaunchStatus.Should().BeNull();
        result.Error.Should().Be("File not found");
        result.ErrorCode.Should().Be("ExecutableNotFound");
    }

    [Fact]
    public async Task LaunchApplication_WithNullWindowHandle_ReturnsNullHandle()
    {
        // Arrange
        LaunchReturns(new LaunchResult(true, 1234, Status: LaunchStatus.Started));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplication("notepad.exe");

        // Assert
        result.WindowHandle.Should().BeNull();
        result.Window.Should().BeNull();
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(30000, 30000)]
    [InlineData(int.MaxValue, 30000)] // the wait runs inside the input gate: an agent's value must not hold it for days
    [InlineData(-5, 0)]
    public async Task LaunchApplication_WaitsForTheWindowAtMostMaxWaitTimeoutMs(int waitTimeoutMs, int expected)
    {
        LaunchReturns(new LaunchResult(true, 1234, Status: LaunchStatus.TimedOut));
        var sut = CreateSut();

        await sut.LaunchApplication("notepad.exe", waitForWindow: true, waitTimeoutMs: waitTimeoutMs);

        _mockProcessService.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), true, expected, It.IsAny<CancellationToken>()), Times.Once);
        AppLauncherTools.MaxWaitTimeoutMs.Should().Be(30000);
    }

    [Fact]
    public async Task LaunchApplicationsBatch_WaitsForEachWindowAtMostMaxWaitTimeoutMs()
    {
        _mockProcessService.Setup(p => p.LaunchApplicationsBatchAsync(
                It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LaunchResult(true, 1234, null, null)]);
        var sut = CreateSut();

        await sut.LaunchApplicationsBatch([new ApplicationLaunchRequest("notepad.exe")], waitForWindows: true, waitTimeoutMs: int.MaxValue);

        _mockProcessService.Verify(p => p.LaunchApplicationsBatchAsync(
            It.IsAny<IReadOnlyList<LaunchRequest>>(), true, AppLauncherTools.MaxWaitTimeoutMs,
            It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaunchApplicationsBatch_PassesEachItemsExpectWindow_AndReturnsEachItemsStatusAndWindow()
    {
        // Arrange
        IReadOnlyList<LaunchRequest>? requested = null;
        var window = TestDataFactory.CreateWindowInfo(handleValue: 777, title: "Settings", processName: "SystemSettings", processId: 55);
        _mockProcessService.Setup(p => p.LaunchApplicationsBatchAsync(
                It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<LaunchRequest> apps, bool _, int _, IProgress<OperationProgress>? _, CancellationToken _) => requested = apps)
            .ReturnsAsync([
                new LaunchResult(true, null, window.Handle, Status: LaunchStatus.WindowObserved, Window: window),
                new LaunchResult(true, 99, Status: LaunchStatus.TimedOut, LauncherExited: false)
            ]);
        var hint = new LaunchWindowHint(ProcessName: "SystemSettings");

        // Act
        var result = await CreateSut().LaunchApplicationsBatch(
            [new ApplicationLaunchRequest("ms-settings:display", ExpectWindow: hint), new ApplicationLaunchRequest("notepad")], waitForWindows: true);

        // Assert
        requested!.Select(r => r.ExpectWindow).Should().Equal(hint, null);
        result.Results[0].LaunchStatus.Should().Be("windowObserved");
        result.Results[0].Window!.ProcessName.Should().Be("SystemSettings");
        result.Results[0].WindowHandle.Should().Be(777);
        result.Results[1].LaunchStatus.Should().Be("timedOut");
        result.Results[1].LauncherExited.Should().BeFalse();
    }

    [Fact]
    public async Task LaunchApplicationsBatch_PassesEachItemsForceAccessibility_AndReportsWhetherItWasApplied()
    {
        // Arrange - roadmap M3.F25: an item takes forceAccessibility as launch_application does
        IReadOnlyList<LaunchRequest>? requested = null;
        _mockProcessService.Setup(p => p.LaunchApplicationsBatchAsync(
                It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<LaunchRequest> apps, bool _, int _, IProgress<OperationProgress>? _, CancellationToken _) => requested = apps)
            .ReturnsAsync([
                new LaunchResult(true, 1234, Status: LaunchStatus.Started, ForceAccessibilityApplied: true),
                new LaunchResult(true, 1235, Status: LaunchStatus.Started)
            ]);

        // Act
        var result = await CreateSut().LaunchApplicationsBatch(
            [new ApplicationLaunchRequest(@"C:\Apps\VS Code\Code.exe", ForceAccessibility: true), new ApplicationLaunchRequest("notepad")]);

        // Assert
        requested!.Select(r => r.ForceAccessibility).Should().Equal(true, false);
        result.Results[0].ForceAccessibilityApplied.Should().BeTrue();
        result.Results[1].ForceAccessibilityApplied.Should().BeNull("forceAccessibility was not asked for");
    }

    #endregion

    #region ListProcesses Tests

    private ProcessQuery? _processQuery;

    /// <summary>The service lists <paramref name="processes"/> for any query, and records the query.</summary>
    private void ListReturns(params ProcessInfo[] processes) =>
        _mockProcessService.Setup(p => p.GetRunningProcesses(It.IsAny<ProcessQuery>()))
            .Callback<ProcessQuery>(q => _processQuery = q)
            .Returns(processes);

    [Fact]
    public void ListProcesses_CallsServiceWithParameters()
    {
        // Arrange
        ListReturns();
        var sut = CreateSut();

        // Act
        sut.ListProcesses(nameFilter: "chrome", includeWindowless: true, processId: 42, pathContains: @"\Google\",
            sortBy: ProcessSort.StartTime, limit: 3, includeCommandLine: true);

        // Assert
        _processQuery.Should().Be(new ProcessQuery
        {
            NameFilter = "chrome",
            IncludeWindowless = true,
            ProcessId = 42,
            PathContains = @"\Google\",
            Sort = ProcessSort.StartTime,
            Limit = 3,
            IncludeCommandLine = true
        });
    }

    [Fact]
    public void ListProcesses_WithDefaults_UsesDefaultValues()
    {
        // Arrange
        ListReturns();
        var sut = CreateSut();

        // Act
        sut.ListProcesses();

        // Assert
        _processQuery.Should().Be(new ProcessQuery());
    }

    [Fact]
    public void ListProcesses_ReturnsEmptyList_WhenNoProcesses()
    {
        // Arrange
        ListReturns();
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ListProcesses_WithALimitBelowOne_FailsWithInvalidArguments_AndListsNothing(int limit)
    {
        // Arrange
        ListReturns();

        // Act
        var act = () => CreateSut().ListProcesses(limit: limit);

        // Assert
        act.Should().Throw<ToolErrorException>()
            .Where(e => e.ErrorCode == ToolErrorReporting.InvalidArgumentsErrorCode)
            .WithMessage("*limit*at least 1*");
        _mockProcessService.Verify(p => p.GetRunningProcesses(It.IsAny<ProcessQuery>()), Times.Never);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("", 5)]
    [InlineData("e", null)]
    [InlineData("chrome", null)]
    [InlineData("chrome", AppLauncherTools.MaxCommandLineProcesses + 1)]
    public void ListProcesses_WithIncludeCommandLine_WithoutProcessIdOrANameFilterAndASmallLimit_FailsWithInvalidArguments(
        string? nameFilter, int? limit)
    {
        // Arrange - every process's command line in one call is too much to put in front of the agent, and a short name
        // filter ("e") matches most processes, so it bounds nothing without a limit
        ListReturns();

        // Act
        var act = () => CreateSut().ListProcesses(nameFilter: nameFilter, includeWindowless: true, limit: limit, includeCommandLine: true);

        // Assert
        act.Should().Throw<ToolErrorException>()
            .Where(e => e.ErrorCode == ToolErrorReporting.InvalidArgumentsErrorCode)
            .WithMessage("*includeCommandLine*processId*nameFilter*limit*20*");
        _mockProcessService.Verify(p => p.GetRunningProcesses(It.IsAny<ProcessQuery>()), Times.Never);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(AppLauncherTools.MaxCommandLineProcesses)]
    public void ListProcesses_WithIncludeCommandLine_NameFilterAndASmallLimit_ReadsTheCommandLines(int limit)
    {
        // Arrange
        ListReturns();

        // Act
        CreateSut().ListProcesses(nameFilter: "chrome", limit: limit, includeCommandLine: true);

        // Assert
        _processQuery.Should().Be(new ProcessQuery { NameFilter = "chrome", Limit = limit, IncludeCommandLine = true });
    }

    [Fact]
    public void ListProcesses_WithIncludeCommandLineAndProcessId_ListsTheCommandLine()
    {
        // Arrange
        ListReturns(new ProcessInfo(1234, "app", null, 1, 10, CommandLine: "app.exe --password=***", CommandLineRedacted: true));

        // Act
        var dto = CreateSut().ListProcesses(processId: 1234, includeCommandLine: true).Single();

        // Assert
        dto.CommandLine.Should().Be("app.exe --password=***");
        dto.CommandLineRedacted.Should().BeTrue();
    }

    [Fact]
    public void ListProcesses_MapsProcessInfoToDto()
    {
        // Arrange
        var window = TestDataFactory.CreateWindowInfo(0x1A2B, "New Tab - Google Chrome", "chrome", 1234);
        ListReturns(new ProcessInfo(
            ProcessId: 1234,
            Name: "chrome",
            ExecutablePath: @"C:\Program Files\Google\Chrome\chrome.exe",
            WindowCount: 5,
            MemoryUsageMB: 512.75,
            ParentProcessId: 880,
            StartTime: new DateTime(2026, 10, 16, 7, 5, 9, 678, DateTimeKind.Utc),
            Windows: [window],
            Integrity: new ProcessIntegrity(1234, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null)));
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result.Should().ContainSingle().Which.Should().Be(new ProcessInfoDto(
            ProcessId: 1234,
            Name: "chrome",
            ExecutablePath: @"C:\Program Files\Google\Chrome\chrome.exe",
            WindowCount: 5,
            MemoryUsageMB: 512.8, // Rounded to 1 decimal
            ParentProcessId: 880,
            StartTime: "2026-10-16T07:05:09Z",
            IntegrityLevel: IntegrityLevel.Medium,
            Elevated: false,
            AboveServer: null,
            Windows: result[0].Windows,
            CommandLine: null,
            CommandLineRedacted: null));
        result[0].Windows.Should().Equal(new ProcessWindowDto(0x1A2B, "New Tab - Google Chrome"));
    }

    [Fact]
    public void ListProcesses_LeavesOutWhatIsUnknownOrEmpty_AndReportsAboveServerOnlyWhenTrue()
    {
        // Arrange
        ListReturns(
            new ProcessInfo(1, "svchost", null, 0, null),
            new ProcessInfo(2, "mmc", null, 1, 30, Integrity: new ProcessIntegrity(2, IntegrityLevel.LikelyElevated, null, null, "denied"),
                AboveServer: true));

        // Act
        var result = CreateSut().ListProcesses(includeWindowless: true);

        // Assert
        result[0].Should().Be(new ProcessInfoDto(1, "svchost", null, 0, null));
        result[1].IntegrityLevel.Should().Be(IntegrityLevel.LikelyElevated);
        result[1].Elevated.Should().BeNull("the token could not be read");
        result[1].AboveServer.Should().BeTrue();
    }

    [Fact]
    public void ListProcesses_CutsALongWindowTitle()
    {
        // Arrange
        var title = new string('x', 300);
        ListReturns(new ProcessInfo(1, "app", null, 1, 10, Windows: [TestDataFactory.CreateWindowInfo(7, title, "app", 1)]));

        // Act
        var result = CreateSut().ListProcesses();

        // Assert
        result[0].Windows!.Single().Title.Should().Be(AppText.Cut(title));
    }

    [Fact]
    public void ListProcesses_RoundsMemoryUsage()
    {
        // Arrange
        ListReturns(new ProcessInfo(ProcessId: 1234, Name: "app", ExecutablePath: null, WindowCount: 1, MemoryUsageMB: 123.456789));
        var sut = CreateSut();

        // Act
        var result = sut.ListProcesses();

        // Assert
        result[0].MemoryUsageMB.Should().Be(123.5);
    }

    [Fact]
    public void ListProcesses_ReturnsMultipleProcesses()
    {
        // Arrange
        ListReturns(new(1, "app1", null, 1, 100), new(2, "app2", null, 2, 200), new(3, "app3", null, 3, 300));
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
            It.IsAny<string?>(), It.IsAny<KillSafeguards?>(),
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
        _mockProcessService.Verify(p => p.KillProcessAsync(1234, true, true, 2500, "notepad.exe", It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()), Times.Once);
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
        _mockProcessService.Verify(p => p.KillProcessAsync(1234, true, false, 5000, null, It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KillProcess_ReleasesTheInputGate_BeforeTheWaitForTheExit()
    {
        // Arrange - a graceful close waits up to 60 s for an app showing 'Save changes?': no input call must wait that long
        var flow = InputGate.TagFlow();
        bool? heldForTheClose = null;
        bool? heldForTheWait = null;
        KillSafeguards? passed = null;
        _mockProcessService.Setup(p => p.KillProcessAsync(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(),
                It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()))
            .Callback((int _, bool _, bool _, int _, string? _, KillSafeguards? safeguards, CancellationToken _) =>
            {
                passed = safeguards;
                heldForTheClose = InputGate.IsHeldBy(flow);
                safeguards?.BeforeWait?.Invoke();
                heldForTheWait = InputGate.IsHeldBy(flow);
            })
            .ReturnsAsync(new KillResult(true, 1234, "notepad", CloseRequested: true, Exited: true));

        // Act - the model confirmed: the client cannot ask the user
        var result = await CreateSut().KillProcess(processId: 1234, confirm: true);

        // Assert
        result.Success.Should().BeTrue();
        heldForTheClose.Should().BeTrue();
        heldForTheWait.Should().BeFalse();
        passed!.Confirmed.Should().BeNull("the user was not asked, so there is nothing they confirmed to pin");
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
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<KillSafeguards?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KillResult(true, 1234, "notepad", CloseRequested: true, Exited: true));

        // Act
        await CreateSut().KillProcess(processId: 1234, confirm: true, cancellationToken: call.Token);

        // Assert
        _mockProcessService.Verify(p => p.KillProcessAsync(1234, true, false, 5000, null, It.IsAny<KillSafeguards?>(), call.Token), Times.Once);
    }

    #endregion

    #region Packaged apps, runAsAdmin and list_installed_apps (roadmap M6.5)

    [Fact]
    public async Task LaunchApplication_HoldsTheInputGateForTheLaunch_ItTakesItself()
    {
        // Arrange - launch_application is self-gated, so that its runAsAdmin question never holds the gate
        var flow = InputGate.TagFlow();
        bool? held = null;
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => held = InputGate.IsHeldBy(flow))
            .ReturnsAsync(new LaunchResult(true, 1234, Status: LaunchStatus.Started));

        // Act
        await CreateSut().LaunchApplication("notepad.exe");

        // Assert - and released again
        held.Should().BeTrue();
        InputGate.IsHeldBy(flow).Should().BeFalse();
    }

    [Fact]
    public async Task LaunchApplication_WhilePaused_IsPausedByUser_AndStartsNothing()
    {
        using var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);

        var act = () => new AppLauncherTools(_mockProcessService.Object, session: session).LaunchApplication("notepad.exe");

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LaunchApplication_PassesRunAsAdmin_WithoutAnApprovalTheUserDidNotGive()
    {
        // Arrange - the client cannot ask (no server), and Windows would not ask either: the service refuses it
        LaunchReturns(new LaunchResult(false, ErrorMessage: "needs confirmation", ErrorCode: nameof(LaunchErrorCode.ElevationNotConfirmed)));
        _mockProcessService.Setup(p => p.PreviewElevation(It.IsAny<LaunchRequest>()))
            .Returns(new ElevationPreview(true, "User Account Control is off", new ElevationApproval(@"C:\Tools\app.exe", "")));

        // Act
        var result = await CreateSut().LaunchApplication(@"C:\Tools\app.exe", runAsAdmin: true);

        // Assert
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.ElevationNotConfirmed));
        result.Elevation.Should().BeNull();
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(
            It.Is<LaunchRequest>(r => r.RunAsAdmin && r.ApprovedElevation == null), false, 5000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_RunAsAdminWindowsAsksAbout_ReportsElevationUacPrompt()
    {
        // Review M6.5 #8: the result, and so the audit line, say how the elevation was approved
        LaunchReturns(new LaunchResult(true, 1234, Status: LaunchStatus.Started));
        _mockProcessService.Setup(p => p.PreviewElevation(It.IsAny<LaunchRequest>()))
            .Returns(new ElevationPreview(false, null, new ElevationApproval(@"C:\Tools\app.exe", "")));

        var result = await CreateSut().LaunchApplication(@"C:\Tools\app.exe", runAsAdmin: true);

        result.Elevation.Should().Be("uacPrompt");
        (await CreateSut().LaunchApplication(@"C:\Tools\app.exe")).Elevation.Should().BeNull("nothing was elevated");
    }

    [Fact]
    public async Task LaunchApplication_ARunAsAdminThePlanRefuses_IsRefusedBeforeAnyQuestion_AndStartsNothing()
    {
        _mockProcessService.Setup(p => p.PreviewElevation(It.IsAny<LaunchRequest>()))
            .Returns(new ElevationPreview(false, null, null, ToolErrorReporting.InvalidArgumentsErrorCode, "runAsAdmin starts only a program's .exe"));

        var result = await CreateSut().LaunchApplication("ms-settings:", runAsAdmin: true);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LaunchApplication_WhileAnotherCallHoldsTheGate_GivesUpWithInputBusy_AndStartsNothing()
    {
        // Review M6.5, missing test 6: launch_application takes the gate itself, with the filter's InputBusy
        using var session = new InputSession(new FakeInputWrapper());
        var sut = new AppLauncherTools(_mockProcessService.Object, session: session, gateWaitTimeout: TimeSpan.FromMilliseconds(50));
        var held = Task.Run(() => InputGate.Enter());
        using var gate = await held;

        var act = () => sut.LaunchApplication("notepad.exe");

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be("InputBusy");
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LaunchApplication_ReleasesTheGate_WhenTheLaunchThrows()
    {
        _mockProcessService.Setup(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("launch failed"));

        var act = () => CreateSut().LaunchApplication("notepad.exe");

        await act.Should().ThrowAsync<InvalidOperationException>();
        var next = Task.Run(() => InputGate.Enter().Dispose());
        (await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10))) == next).Should().BeTrue("the gate was released");
    }

    [Fact]
    public void ElevationApprovalLost_TellsTheAgentTheUserIsAskedAgain()
    {
        // Review M6.5 #9: an approval is not kept across an InputBusy; the retry asks the user again
        var busy = new ToolErrorException("InputBusy", "Another call held the input for 30 seconds. Nothing was done.");

        var lost = AppLauncherTools.ElevationApprovalLost(busy);

        lost.ErrorCode.Should().Be("InputBusy");
        lost.Message.Should().StartWith(busy.Message).And.Contain("asked again");
    }

    [Fact]
    public async Task LaunchApplication_RunAsAdminWithPlacement_IsInvalidArguments_AndLaunchesNothing()
    {
        var act = () => CreateSut().LaunchApplication(@"C:\Tools\app.exe", placement: new LaunchPlacement(Focus: true), runAsAdmin: true);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _mockProcessService.Verify(p => p.LaunchApplicationAsync(It.IsAny<LaunchRequest>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LaunchApplicationsBatch_PassesEachItemsRunAsAdmin_AndRefusesItWithPlacementAlone()
    {
        // Arrange
        IReadOnlyList<LaunchRequest>? passed = null;
        _mockProcessService.Setup(p => p.LaunchApplicationsBatchAsync(It.IsAny<IReadOnlyList<LaunchRequest>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<LaunchRequest> apps, bool _, int _, IProgress<OperationProgress>? _, CancellationToken _) => passed = apps)
            .ReturnsAsync((IReadOnlyList<LaunchRequest> apps, bool _, int _, IProgress<OperationProgress>? _, CancellationToken _) =>
                apps.Select(_ => new LaunchResult(true, 1, Status: LaunchStatus.Started)).ToList());

        // Act
        var result = await CreateSut().LaunchApplicationsBatch(
        [
            new ApplicationLaunchRequest(@"C:\Tools\app.exe", RunAsAdmin: true),
            new ApplicationLaunchRequest(@"C:\Tools\other.exe", Placement: new LaunchPlacement(Focus: true), RunAsAdmin: true),
            new ApplicationLaunchRequest("notepad.exe")
        ]);

        // Assert
        passed!.Select(r => r.RunAsAdmin).Should().Equal(true, false);
        result.Results[1].ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Succeeded.Should().Be(2);
        result.Results.Select(r => r.Elevation).Should().Equal("uacPrompt", null, null);
    }

    private readonly Mock<IInstalledAppsService> _installedApps = new();

    [Fact]
    public void ListInstalledApps_ReturnsTheAppsWithTheirTotal()
    {
        // Arrange
        _installedApps.Setup(i => i.List("calc", 5)).Returns(new InstalledAppsListing(
            [new InstalledApp("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true, null)], 3, true, false));

        // Act
        var result = new AppLauncherTools(_mockProcessService.Object, installedApps: _installedApps.Object).ListInstalledApps("calc", 5);

        // Assert
        result.Apps.Should().Equal(new InstalledAppDto("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true));
        result.Total.Should().Be(3);
        result.Truncated.Should().BeTrue();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public void ListInstalledApps_AStartMenuThatCannotBeRead_IsAnEmptyListWithAWarning()
    {
        _installedApps.Setup(i => i.List(null, 25)).Returns(new InstalledAppsListing([], 0, false, Unreadable: true));

        var result = new AppLauncherTools(_mockProcessService.Object, installedApps: _installedApps.Object).ListInstalledApps();

        result.Apps.Should().BeEmpty();
        result.Warning.Should().Contain("could not be read");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void ListInstalledApps_ALimitOutOfRange_IsInvalidArguments(int limit)
    {
        var act = () => new AppLauncherTools(_mockProcessService.Object, installedApps: _installedApps.Object).ListInstalledApps(limit: limit);

        act.Should().Throw<ToolErrorException>().Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    #endregion
}
