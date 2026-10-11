using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// reuseExisting (roadmap M3.13): which open window counts as an open copy of the launched app, so that nothing is
/// started. Every ProcessService here has a mocked process seam and window service: nothing is started or read on the
/// real desktop.
/// </summary>
public class ProcessServiceReuseTests
{
    private const int LaunchedProcessId = 4321;

    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IInputValidationService> _validation = new();
    private readonly Mock<IExecutableResolver> _resolver = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Mock<IProcessHandle> _launched = new();
    private readonly Mock<IWindowWaitService> _windowWait = new();
    private readonly List<WindowInfo> _open = [];

    public ProcessServiceReuseTests()
    {
        _validation.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe));
        _validation.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns<string?>(dir => new PathValidationResult(true, SanitizedPath: dir));
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(exe, LaunchTargetKind.NativeExecutable));
        _launched.SetupGet(p => p.Id).Returns(LaunchedProcessId);
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>())).Returns(_launched.Object);
        _windows.Setup(w => w.GetApplicationWindows()).Returns(() => _open);
        _windowWait.Setup(w => w.TakeBaseline()).Returns(new WindowBaseline(new Dictionary<nint, string>(), nint.Zero));
    }

    private ProcessService CreateSut() => new(
        _windows.Object, _validation.Object, TestDataFactory.CreateMockBrowserDetectionService().Object, _resolver.Object,
        _processes.Object, windowWait: _windowWait.Object);

    private WindowInfo Open(int handle, string process, string title, int processId = 900, bool host = false)
    {
        var window = TestDataFactory.CreateWindowInfo(handleValue: handle, title: title, processName: process, processId: processId) with
        {
            IsAgentHost = host
        };
        _open.Add(window);
        return window;
    }

    private Task<LaunchResult> LaunchAsync(string executable, string[]? arguments = null, LaunchWindowHint? expectWindow = null) =>
        CreateSut().LaunchApplicationAsync(new LaunchRequest(executable, arguments, ExpectWindow: expectWindow, ReuseExisting: true), false, 5000);

    private void VerifyNothingStarted() => _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);

    private void VerifyStarted() => _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Once);

    [Fact]
    public async Task AnOpenWindowOfTheProgram_IsUsed_AndNothingIsStarted()
    {
        var notepad = Open(0x10, "Notepad", "Untitled - Notepad");

        var result = await LaunchAsync("notepad.exe");

        result.Success.Should().BeTrue();
        result.Status.Should().Be(LaunchStatus.AlreadyOpen);
        result.Status!.Value.ToWireName().Should().Be("alreadyOpen");
        result.Window.Should().Be(notepad);
        result.WindowHandle.Should().Be(notepad.Handle);
        result.ProcessId.Should().BeNull("nothing was started");
        result.Warning.Should().BeNull();
        VerifyNothingStarted();
        _windowWait.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WithoutReuseExisting_OpenWindowsAreNotLookedAt()
    {
        Open(0x10, "Notepad", "Untitled - Notepad");

        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest("notepad.exe"), false, 5000);

        result.Status.Should().Be(LaunchStatus.Started);
        VerifyStarted();
        _windows.Verify(w => w.GetApplicationWindows(), Times.Never);
    }

    [Fact]
    public async Task TheAgentsOwnHostAndOtherApps_AreNeverReused()
    {
        Open(0x10, "notepad", "notes - Notepad", host: true);
        Open(0x11, "Code", "Visual Studio Code");

        var result = await LaunchAsync("notepad.exe");

        result.Status.Should().Be(LaunchStatus.Started);
        VerifyStarted();
    }

    [Fact]
    public async Task ASameAppHandoffName_Counts()
    {
        var calculator = Open(0x10, "CalculatorApp", "Calculator");

        var result = await LaunchAsync("calc.exe");

        result.Window.Should().Be(calculator);
        VerifyNothingStarted();
    }

    [Theory]
    [InlineData("cmd.exe", "WindowsTerminal")]
    [InlineData("powershell.exe", "WindowsTerminal")]
    public async Task AConsoleProgram_NeverReusesATerminalWindow(string executable, string host)
    {
        Open(0x10, host, "Windows PowerShell");

        var result = await LaunchAsync(executable);

        result.Status.Should().Be(LaunchStatus.Started);
        VerifyStarted();
    }

    [Fact]
    public async Task TheSameProgramPath_Counts_WhateverTheProcessIsCalled()
    {
        const string path = @"C:\Apps\Tool\tool.exe";
        _resolver.Setup(r => r.Resolve("tool", It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(path, LaunchTargetKind.NativeExecutable));
        _processes.Setup(p => p.GetProcessImagePath(777)).Returns(path);
        var tool = Open(0x10, "tool-x64", "Tool", processId: 777);

        var result = await LaunchAsync("tool");

        result.Window.Should().Be(tool);
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ALaunchThatOpensAFile_ReusesOnlyAWindowWhoseTitleShowsIt()
    {
        Open(0x10, "Notepad", "other.txt - Notepad");
        var todo = Open(0x11, "Notepad", "todo.txt - Notepad");

        var result = await LaunchAsync("notepad.exe", [@"C:\notes\todo.txt"]);

        result.Window.Should().Be(todo);
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ALaunchThatOpensAFileNoWindowShows_IsStarted()
    {
        Open(0x10, "Notepad", "other.txt - Notepad");

        var result = await LaunchAsync("notepad.exe", [@"C:\notes\todo.txt"]);

        result.Status.Should().Be(LaunchStatus.Started);
        result.Warning.Should().BeNull("the server looked and found no window");
        VerifyStarted();
    }

    [Fact]
    public async Task ALaunchThatOpensSomethingNoTitleWouldShow_IsStarted_AndTheWarningSaysWhy()
    {
        Open(0x10, "Notepad", "Untitled - Notepad");

        var result = await LaunchAsync("notepad.exe", ["x"]);

        result.Status.Should().Be(LaunchStatus.Started);
        result.Warning.Should().Contain("expectWindow.titleContains");
        VerifyStarted();
    }

    [Fact]
    public async Task OptionsAlone_DoNotNeedATitle()
    {
        var notepad = Open(0x10, "Notepad", "Untitled - Notepad");

        var result = await LaunchAsync("notepad.exe", ["--some-option"]);

        result.Window.Should().Be(notepad);
    }

    [Fact]
    public async Task ExpectWindowTitleContains_NamesTheWindowToReuse()
    {
        Open(0x10, "Code", "a.cs - other - Visual Studio Code");
        var project = Open(0x11, "Code", "b.cs - MyProject - Visual Studio Code");

        var result = await LaunchAsync("code.cmd", [@"C:\src\my-project"], new LaunchWindowHint(TitleContains: "MyProject"));

        result.Window.Should().Be(project);
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ADocumentWhoseAppIsUnknown_NeedsExpectWindowProcessName()
    {
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(exe, LaunchTargetKind.Other));
        var word = Open(0x10, "WINWORD", "report.docx - Word");
        Open(0x11, "msedge", "report.docx - Microsoft Edge");

        var unknown = await LaunchAsync(@"C:\docs\report.docx");
        unknown.Status.Should().Be(LaunchStatus.Started);
        unknown.Warning.Should().Contain("expectWindow.processName");

        var named = await LaunchAsync(@"C:\docs\report.docx", expectWindow: new LaunchWindowHint(ProcessName: "winword"));
        named.Status.Should().Be(LaunchStatus.AlreadyOpen);
        named.Window.Should().Be(word);
    }

    [Fact]
    public async Task ExpectWindowProcessName_NarrowsTheProgramsWindows()
    {
        Open(0x10, "Notepad", "Untitled - Notepad");

        var result = await LaunchAsync("notepad.exe", expectWindow: new LaunchWindowHint(ProcessName: "Notepad++"));

        result.Status.Should().Be(LaunchStatus.Started);
    }

    [Fact]
    public async Task SeveralOpenWindows_TheFrontMostIsUsed_AndTheWarningSaysHowMany()
    {
        var front = Open(0x10, "Notepad", "a - Notepad");
        Open(0x11, "Notepad", "b - Notepad");

        var result = await LaunchAsync("notepad.exe");

        result.Window.Should().Be(front);
        result.Warning.Should().Contain("2 open windows matched");
    }

    [Fact]
    public async Task AnInvalidRequest_IsRefused_EvenWithAWindowOpen()
    {
        Open(0x10, "Notepad", "Untitled - Notepad");
        _validation.Setup(v => v.ValidateExecutable("notepad.exe"))
            .Returns(new ExecutableValidationResult(false, Error: "no", ErrorCode: nameof(LaunchErrorCode.InvalidPath)));

        var result = await LaunchAsync("notepad.exe");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.InvalidPath));
    }

    [Fact]
    public async Task InABatch_AnOpenWindowIsReusedByOneItemOnly()
    {
        var notepad = Open(0x10, "Notepad", "Untitled - Notepad");

        var results = await CreateSut().LaunchApplicationsBatchAsync(
            [new LaunchRequest("notepad.exe", ReuseExisting: true), new LaunchRequest("notepad.exe", ReuseExisting: true)]);

        results[0].Status.Should().Be(LaunchStatus.AlreadyOpen);
        results[0].Window.Should().Be(notepad);
        results[1].Status.Should().Be(LaunchStatus.Started);
        VerifyStarted();
    }
}
