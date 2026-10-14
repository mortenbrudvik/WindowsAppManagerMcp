using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// launch_application's forceAccessibility (roadmap M3.3): --force-renderer-accessibility is added for a Chromium,
/// Electron or CEF program only, once, in front of the user's arguments, and the result says whether it was. The process
/// seam, the resolver and the file system are fakes: nothing is started and no real file is read.
/// </summary>
public class ForceAccessibilityLaunchTests
{
    private const string Code = @"C:\Apps\VS Code\Code.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";
    private const string CodeCmd = @"C:\Apps\VS Code\bin\code.cmd";
    private const string Flag = ChromiumPrograms.ForceRendererAccessibility;

    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IInputValidationService> _validation = new();
    private readonly Mock<IExecutableResolver> _resolver = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Mock<IProcessHandle> _launched = new();
    private readonly Mock<IWindowWaitService> _windowWait = new();
    private readonly List<WindowInfo> _open = [];
    private readonly List<ProcessStartInfo> _started = [];
    private readonly Dictionary<int, (int ParentProcessId, string ImageName)> _running = [];
    private readonly Dictionary<int, string> _imagePaths = [];

    public ForceAccessibilityLaunchTests()
    {
        _validation.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe));
        _validation.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns<string?>(dir => new PathValidationResult(true, SanitizedPath: dir));
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(exe,
                exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? LaunchTargetKind.BatchFile
                : exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? LaunchTargetKind.NativeExecutable
                : LaunchTargetKind.Other));
        _launched.SetupGet(p => p.Id).Returns(4321);
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Callback((ProcessStartInfo info) => _started.Add(info))
            .Returns(_launched.Object);
        _windows.Setup(w => w.GetApplicationWindows()).Returns(() => _open);
        _processes.Setup(p => p.GetProcessList()).Returns(() => _running);
        _processes.Setup(p => p.GetProcessImagePath(It.IsAny<int>())).Returns((int id) => _imagePaths.GetValueOrDefault(id));
        _windowWait.Setup(w => w.TakeBaseline()).Returns(new WindowBaseline(new Dictionary<nint, string>(), nint.Zero));
    }

    /// <summary>VS Code's folder holds Chromium's resource files; notepad's does not.</summary>
    private static ChromiumPrograms FileSystem() => new(
        path => path.Equals(Code, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(Notepad, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(@"C:\Apps\VS Code\icudtl.dat", StringComparison.OrdinalIgnoreCase) ||
                path.Equals(@"C:\Apps\VS Code\resources.pak", StringComparison.OrdinalIgnoreCase),
        _ => []);

    private ProcessService CreateSut() => new(
        _windows.Object, _validation.Object, TestDataFactory.CreateMockBrowserDetectionService().Object, _resolver.Object,
        _processes.Object, windowWait: _windowWait.Object, chromiumPrograms: FileSystem());

    private Task<LaunchResult> LaunchAsync(string executable, string[]? arguments = null, bool forceAccessibility = true, bool reuseExisting = false) =>
        CreateSut().LaunchApplicationAsync(
            new LaunchRequest(executable, arguments, ReuseExisting: reuseExisting, ForceAccessibility: forceAccessibility), false, 5000);

    [Fact]
    public async Task AnElectronApp_IsStartedWithTheFlag_InFrontOfTheUsersArguments()
    {
        // Act
        var result = await LaunchAsync(Code, ["--", "my file.txt"]);

        // Assert - in front: Chromium reads no switch after "--"
        result.Success.Should().BeTrue();
        result.ForceAccessibilityApplied.Should().BeTrue();
        result.Warning.Should().BeNull();
        _started.Single().Arguments.Should().Be($"{Flag} -- \"my file.txt\"");
    }

    [Fact]
    public async Task AnElectronApp_WithoutArguments_GetsOnlyTheFlag()
    {
        // Act
        var result = await LaunchAsync(Code);

        // Assert
        result.ForceAccessibilityApplied.Should().BeTrue();
        _started.Single().Arguments.Should().Be(Flag);
    }

    [Fact]
    public async Task TheFlag_IsNotAddedTwice()
    {
        // Act
        var result = await LaunchAsync(Code, ["--force-renderer-accessibility=complete", "."]);

        // Assert
        result.ForceAccessibilityApplied.Should().BeTrue();
        _started.Single().Arguments.Should().Be("--force-renderer-accessibility=complete .");
    }

    [Fact]
    public async Task AnotherProgram_IsStartedWithoutTheFlag_AndTheWarningSaysWhy()
    {
        // Act
        var result = await LaunchAsync(Notepad, ["notes.txt"]);

        // Assert
        result.Success.Should().BeTrue("the launch itself still happens");
        result.ForceAccessibilityApplied.Should().BeFalse();
        result.Warning.Should().Contain("forceAccessibility was not applied").And.Contain("icudtl.dat");
        _started.Single().Arguments.Should().Be("notes.txt");
    }

    [Fact]
    public async Task ABatchFile_IsRefusedAsAScript_AndTheErrorNamesTheExe()
    {
        // Act - a batch file shim is a script, which the server does not run (roadmap M3.F8)
        var result = await LaunchAsync(CodeCmd);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        result.ErrorMessage.Should().Contain("code.exe");
        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task AUri_IsStartedWithoutTheFlag()
    {
        // Act
        var result = await LaunchAsync("https://example.com");

        // Assert
        result.ForceAccessibilityApplied.Should().BeFalse();
        result.Warning.Should().Contain("not a program's .exe");
    }

    [Fact]
    public async Task WithoutForceAccessibility_NothingIsAddedOrReported()
    {
        // Act
        var result = await LaunchAsync(Code, ["."], forceAccessibility: false);

        // Assert
        result.ForceAccessibilityApplied.Should().BeNull();
        _started.Single().Arguments.Should().Be(".");
    }

    [Fact]
    public async Task WhenTheLaunchWentToARunningInstance_TheResultWarnsThatItKeepsItsMode()
    {
        // Arrange - Windows started no new process: a handoff
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>())).Returns((IProcessHandle?)null);

        // Act
        var result = await LaunchAsync(Code);

        // Assert - closing the windows is not enough when the browser keeps a process without one
        result.Status.Should().Be(LaunchStatus.PossibleHandoff);
        result.ForceAccessibilityApplied.Should().BeTrue("the flag was on the command line");
        result.Warning.Should().Contain("already running")
            .And.Contain("Startup boost")
            .And.Contain("background apps")
            .And.Contain("list_processes with includeWindowless");
    }

    /// <summary>A running process: <paramref name="processId"/> with its image name and path.</summary>
    private void Running(int processId, string imagePath)
    {
        _running[processId] = (1, Path.GetFileName(imagePath));
        _imagePaths[processId] = imagePath;
    }

    [Fact]
    public async Task WhenTheAppIsAlreadyRunning_ALaunchWithoutAWait_WarnsThatTheSwitchMostLikelyHasNoEffect()
    {
        // Arrange - VS Code runs, with or without a window: the new Code.exe hands its request to it and exits
        Running(900, Code);

        // Act
        var result = await LaunchAsync(Code);

        // Assert
        result.Status.Should().Be(LaunchStatus.Started);
        result.ForceAccessibilityApplied.Should().BeTrue("the flag was on the command line");
        result.Warning.Should().Contain("already running").And.Contain("900").And.Contain("Startup boost");
        _started.Single().Arguments.Should().Be(Flag, "the launch itself still happens");
    }

    [Fact]
    public async Task AnotherProgramWithTheSameImageName_IsNotTheAppRunning()
    {
        // Arrange - a Code.exe of another installation
        Running(900, @"C:\Other\Code.exe");

        // Act
        var result = await LaunchAsync(Code);

        // Assert
        result.ForceAccessibilityApplied.Should().BeTrue();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task InABatch_ASecondLaunchOfTheApp_WarnsThatTheFirstIsAlreadyRunning()
    {
        // Arrange - the first item's process runs once it is started
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Callback((ProcessStartInfo info) =>
            {
                _started.Add(info);
                Running(4321, Code);
            })
            .Returns(_launched.Object);

        // Act
        var results = await CreateSut().LaunchApplicationsBatchAsync(
        [
            new LaunchRequest(Code, ForceAccessibility: true),
            new LaunchRequest(Code, ["."], ForceAccessibility: true)
        ]);

        // Assert
        results[0].Warning.Should().BeNull();
        results[1].Warning.Should().Contain("already running").And.Contain("4321");
    }

    [Fact]
    public async Task WithoutForceAccessibility_TheRunningProcessesAreNotRead()
    {
        // Act
        await LaunchAsync(Code, forceAccessibility: false);

        // Assert
        _processes.Verify(p => p.GetProcessList(), Times.Never);
    }

    [Fact]
    public async Task WhenReuseExistingUsesAnOpenWindow_ItIsNotApplied()
    {
        // Arrange
        _open.Add(TestDataFactory.CreateWindowInfo(handleValue: 0x10, title: "Visual Studio Code", processName: "Code", processId: 900));

        // Act
        var result = await LaunchAsync(Code, reuseExisting: true);

        // Assert
        result.Status.Should().Be(LaunchStatus.AlreadyOpen);
        result.ForceAccessibilityApplied.Should().BeFalse();
        result.Warning.Should().Contain("nothing was started");
        _started.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedLaunch_ReportsNothingAboutTheFlag()
    {
        // Arrange
        _validation.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(false, Error: "bad", ErrorCode: nameof(LaunchErrorCode.InvalidCharacters)));

        // Act
        var result = await LaunchAsync(Code);

        // Assert
        result.Success.Should().BeFalse();
        result.ForceAccessibilityApplied.Should().BeNull();
    }

    [Fact]
    public async Task ABatchOfLaunches_ReportsTheFlagPerItem()
    {
        // Act
        var results = await CreateSut().LaunchApplicationsBatchAsync(
        [
            new LaunchRequest(Code, ForceAccessibility: true),
            new LaunchRequest(Notepad, ForceAccessibility: true),
            new LaunchRequest(Notepad)
        ]);

        // Assert
        results.Select(r => r.ForceAccessibilityApplied).Should().Equal(true, false, null);
        _started.Select(s => s.Arguments).Should().Equal(Flag, string.Empty, string.Empty);
    }
}
