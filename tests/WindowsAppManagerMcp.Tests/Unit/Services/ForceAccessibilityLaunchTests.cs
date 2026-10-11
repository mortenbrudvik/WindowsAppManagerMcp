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
    public async Task ABatchFile_IsStartedWithoutTheFlag_AndTheWarningNamesTheExe()
    {
        // Act
        var result = await LaunchAsync(CodeCmd);

        // Assert
        result.ForceAccessibilityApplied.Should().BeFalse();
        result.Warning.Should().Contain("batch file").And.Contain("Code.exe");
        _started.Single().Arguments.Should().BeEmpty();
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

        // Assert
        result.Status.Should().Be(LaunchStatus.PossibleHandoff);
        result.ForceAccessibilityApplied.Should().BeTrue("the flag was on the command line");
        result.Warning.Should().Contain("already running").And.Contain("close every window");
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
