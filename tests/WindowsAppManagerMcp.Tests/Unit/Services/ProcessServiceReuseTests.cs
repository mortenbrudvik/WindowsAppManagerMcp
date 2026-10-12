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
    public async Task ALaunchThatOpensAFileNoWindowShows_IsStarted_AndTheWarningSaysSo()
    {
        Open(0x10, "Notepad", "other.txt - Notepad");

        var result = await LaunchAsync("notepad.exe", [@"C:\notes\todo.txt"]);

        result.Status.Should().Be(LaunchStatus.Started);
        result.Warning.Should().Contain("1 open window(s) of the app").And.Contain("'todo.txt'");
        VerifyStarted();
    }

    [Fact]
    public async Task NoWindowOfTheAppOpen_IsStarted_WithoutAWarning()
    {
        Open(0x10, "Code", "todo.txt - Visual Studio Code");

        var result = await LaunchAsync("notepad.exe", [@"C:\notes\todo.txt"]);

        result.Status.Should().Be(LaunchStatus.Started);
        result.Warning.Should().BeNull("the server looked and no window of the app was open");
        VerifyStarted();
    }

    [Theory]
    [InlineData("old-todo.txt - Notepad")]
    [InlineData("todo.txt.bak - Notepad")]
    [InlineData("old_todo.txt - Notepad")]
    [InlineData("my todo.txt - Notepad")]
    [InlineData("mytodo.txt - Notepad")]
    public async Task AWindowShowingANameThatOnlyContainsTheFilesName_IsNotReused(string title)
    {
        Open(0x10, "Notepad", title);

        var result = await LaunchAsync("notepad.exe", [@"C:\notes\todo.txt"]);

        result.Status.Should().Be(LaunchStatus.Started, $"'{title}' shows another file than todo.txt");
        VerifyStarted();
    }

    [Theory]
    [InlineData("todo.txt - Notepad")]
    [InlineData("*todo.txt - Notepad")]
    [InlineData("TODO.TXT - Notepad")]
    [InlineData(@"C:\notes\todo.txt - Notepad")]
    [InlineData("old-todo.txt | todo.txt - Notepad")]
    public async Task AWindowShowingTheFilesWholeName_IsReused(string title)
    {
        var window = Open(0x10, "Notepad", title);

        var result = await LaunchAsync("notepad.exe", [@"C:\notes\todo.txt"]);

        result.Window.Should().Be(window);
        VerifyNothingStarted();
    }

    [Theory]
    [InlineData("Program.cs - MyApp2 - Visual Studio Code", false)]
    [InlineData("Program.cs - MyApp.Tests - Visual Studio Code", false)]
    [InlineData("Program.cs - MyApp Tests - Visual Studio Code", false)]
    [InlineData("Program.cs - MyApp - Visual Studio Code", true)]
    [InlineData("MyApp (Workspace) - Visual Studio Code", true)]
    public async Task AFolder_IsReusedOnlyFromAWindowShowingItsWholeName(string title, bool reused)
    {
        Open(0x10, "Code", title);

        var result = await LaunchAsync("code.cmd", [@"C:\Projects\MyApp"]);

        result.Status.Should().Be(reused ? LaunchStatus.AlreadyOpen : LaunchStatus.Started);
    }

    [Theory]
    [InlineData("todo.txt - Notepad", "todo.txt", true)]
    [InlineData("todo.txt", "todo.txt", true)]
    [InlineData("[todo.txt]", "todo.txt", true)]
    [InlineData("todo.txt: edited", "todo.txt", true)]
    [InlineData("old-todo.txt - Notepad", "todo.txt", false)]
    [InlineData("todo.txt.bak", "todo.txt", false)]
    [InlineData("todo.txt2", "todo.txt", false)]
    [InlineData("old todo.txt", "todo.txt", false)]
    [InlineData("todo.txt old", "todo.txt", false)]
    [InlineData("api-gateway - Code", "api", false)]
    [InlineData("api - Code", "api", true)]
    [InlineData("hub.com - Edge", "github.com", false)]
    [InlineData("", "todo.txt", false)]
    [InlineData("todo.txt", "", false)]
    public void ShowsName_MatchesWholeNamesOnly(string title, string name, bool expected) =>
        LaunchTitles.ShowsName(title, name).Should().Be(expected);

    [Theory]
    [InlineData(@"C:\notes\todo.txt - Notepad++", TitleShows.FullPath)]
    [InlineData(@"*C:\notes\todo.txt - Notepad++", TitleShows.FullPath)]
    [InlineData(@"c:/NOTES/todo.txt - Notepad++", TitleShows.FullPath)]
    [InlineData(@"Edited: C:\notes\todo.txt", TitleShows.FullPath)]
    [InlineData(@"todo.txt - C:\notes\todo.txt", TitleShows.FullPath)]
    [InlineData("todo.txt - Notepad", TitleShows.NameOnly)]
    [InlineData(@"C:\other\todo.txt - Notepad++", TitleShows.OtherPath)]
    [InlineData(@"D:\notes\todo.txt - Notepad++", TitleShows.OtherPath)]
    [InlineData(@"X:\C:\notes\todo.txt", TitleShows.OtherPath)]
    [InlineData(@"notes\todo.txt - Notepad++", TitleShows.OtherPath)]
    [InlineData(@"todo.txt | C:\other\todo.txt - Notepad++", TitleShows.OtherPath)]
    [InlineData("old-todo.txt - Notepad", TitleShows.Nothing)]
    public void Shows_TellsTheLaunchedPathFromAnotherPathAndFromTheNameAlone(string title, TitleShows expected) =>
        LaunchTitles.Shows(title, new DocumentHint("todo.txt", @"C:\notes\todo.txt")).Should().Be(expected);

    [Fact]
    public void Shows_ANameAfterAPath_WhenTheFullPathIsNotKnown_IsAnotherPath() =>
        LaunchTitles.Shows(@"C:\notes\todo.txt - Notepad++", new DocumentHint("todo.txt")).Should().Be(TitleShows.OtherPath);

    [Theory]
    [InlineData(@"C:\other\todo.txt - Notepad++")]
    [InlineData(@"D:\notes\todo.txt - Notepad++")]
    [InlineData(@"C:\notes\old\todo.txt - Notepad++")]
    [InlineData(@"other\todo.txt - Notepad++")]
    public async Task AWindowShowingTheNameAfterAnotherPath_IsNotReused(string title)
    {
        // Arrange - the title shows another file of the same name
        Open(0x10, "notepad++", title);

        // Act
        var result = await LaunchAsync("notepad++.exe", [@"C:\notes\todo.txt"]);

        // Assert
        result.Status.Should().Be(LaunchStatus.Started, $"'{title}' shows another file than C:\\notes\\todo.txt");
        VerifyStarted();
    }

    [Theory]
    [InlineData(@"C:\notes\todo.txt - Notepad++")]
    [InlineData(@"*c:\NOTES\todo.txt - Notepad++")]
    public async Task AWindowShowingTheFullPath_IsReused_WithoutAWarning(string title)
    {
        // Arrange
        var window = Open(0x10, "notepad++", title);

        // Act
        var result = await LaunchAsync("notepad++.exe", [@"C:\notes\todo.txt"]);

        // Assert
        result.Status.Should().Be(LaunchStatus.AlreadyOpen);
        result.Window.Should().Be(window);
        result.Warning.Should().BeNull("the title shows the launched file itself");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ARelativeArgument_IsComparedAsAPathInTheWorkingDirectory()
    {
        // Arrange - the app takes todo.txt in its working directory, C:\notes
        Open(0x10, "notepad++", @"C:\other\todo.txt - Notepad++");
        var window = Open(0x11, "notepad++", @"C:\notes\todo.txt - Notepad++");

        // Act
        var result = await CreateSut().LaunchApplicationAsync(
            new LaunchRequest("notepad++.exe", ["todo.txt"], @"C:\notes", ReuseExisting: true), false, 5000);

        // Assert
        result.Window.Should().Be(window);
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task AWindowShowingOnlyTheName_IsReused_AndTheWarningNamesTheTitleAndSaysToCheck()
    {
        // Arrange - another project's README.md: the title shows the name only
        var window = Open(0x10, "Code", "README.md - ProjectB - Visual Studio Code");

        // Act
        var result = await LaunchAsync("code.cmd", [@"C:\Projects\A\README.md"]);

        // Assert
        result.Status.Should().Be(LaunchStatus.AlreadyOpen);
        result.Window.Should().Be(window);
        result.Warning.Should().Contain("'README.md - ProjectB - Visual Studio Code'")
            .And.Contain("only the name")
            .And.Contain("same name")
            .And.Contain(@"C:\Projects\A\README.md")
            .And.Contain("before acting");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task AWindowShowingTheFullPath_IsPreferredOverAFrontWindowShowingOnlyTheName()
    {
        // Arrange
        Open(0x10, "notepad++", "todo.txt - Notepad++");
        var exact = Open(0x11, "notepad++", @"C:\notes\todo.txt - Notepad++");

        // Act
        var result = await LaunchAsync("notepad++.exe", [@"C:\notes\todo.txt"]);

        // Assert
        result.Window.Should().Be(exact);
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task AWebAddress_NeverReusesABrowserWindowBecauseItsTitleShowsTheHost()
    {
        // Arrange - a search page for the host: the page sets that title
        Open(0x10, "msedge", "bank.example.com - Search - Microsoft Edge");

        // Act
        var result = await LaunchAsync("msedge.exe", ["https://bank.example.com"]);

        // Assert
        result.Status.Should().Be(LaunchStatus.Started);
        result.Warning.Should().Contain("web address").And.Contain("expectWindow.titleContains");
        VerifyStarted();
    }

    [Fact]
    public async Task AWebAddressLaunchedDirectly_NeverReusesABrowserWindowBecauseItsTitleShowsTheHost()
    {
        // Arrange
        _resolver.Setup(r => r.Resolve("https://bank.example.com", It.IsAny<string?>()))
            .Returns(new ResolvedLaunchTarget("https://bank.example.com", LaunchTargetKind.Other));
        Open(0x10, "msedge", "bank.example.com - Microsoft Edge");

        // Act
        var result = await LaunchAsync("https://bank.example.com", expectWindow: new LaunchWindowHint(ProcessName: "msedge"));

        // Assert
        result.Status.Should().Be(LaunchStatus.Started);
        result.Warning.Should().Contain("expectWindow.titleContains");
    }

    [Fact]
    public async Task AWebAddress_ReusesTheWindowExpectWindowTitleContainsNames_AndWarnsThatThePageSetsTheTitle()
    {
        // Arrange
        var window = Open(0x10, "msedge", "Online Banking - Microsoft Edge");

        // Act
        var result = await LaunchAsync("msedge.exe", ["https://bank.example.com"], new LaunchWindowHint(TitleContains: "Online Banking"));

        // Assert
        result.Status.Should().Be(LaunchStatus.AlreadyOpen);
        result.Window.Should().Be(window);
        result.Warning.Should().Contain("'Online Banking - Microsoft Edge'").And.Contain("a title the page controls").And.Contain("https://bank.example.com").And.Contain("before acting");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task AStoreApp_IsReusedByProcessName_WithoutATitle()
    {
        const string aumid = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
        _resolver.Setup(r => r.Resolve(aumid, It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(aumid, LaunchTargetKind.Other));
        var calculator = Open(0x10, "CalculatorApp", "Calculator");

        var result = await LaunchAsync(aumid, expectWindow: new LaunchWindowHint(ProcessName: "CalculatorApp"));

        result.Status.Should().Be(LaunchStatus.AlreadyOpen);
        result.Window.Should().Be(calculator);
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ABareSettingsUri_ReusesTheSettingsWindow()
    {
        _resolver.Setup(r => r.Resolve("ms-settings:", It.IsAny<string?>())).Returns(new ResolvedLaunchTarget("ms-settings:", LaunchTargetKind.Other));
        var settings = Open(0x10, "SystemSettings", "Settings");

        var result = await LaunchAsync("ms-settings:");

        result.Window.Should().Be(settings);
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ASettingsPage_IsStarted_UnlessExpectWindowTitleContainsNamesIt()
    {
        _resolver.Setup(r => r.Resolve("ms-settings:display", It.IsAny<string?>()))
            .Returns(new ResolvedLaunchTarget("ms-settings:display", LaunchTargetKind.Other));
        var settings = Open(0x10, "SystemSettings", "Settings");

        var page = await LaunchAsync("ms-settings:display");
        page.Status.Should().Be(LaunchStatus.Started, "the open Settings window may show another page");
        page.Warning.Should().Contain("expectWindow.titleContains");

        var named = await LaunchAsync("ms-settings:display", expectWindow: new LaunchWindowHint(TitleContains: "Settings"));
        named.Window.Should().Be(settings);
    }

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", 0)]
    [InlineData("ms-settings:", 0)]
    [InlineData("ms-settings://", 0)]
    [InlineData("ms-settings:display", -1)]
    [InlineData("mailto:someone@example.com", -1)]
    [InlineData(@"C:\docs\report.docx", 1)]
    [InlineData("https://www.github.com/x", -1)]
    public void ReuseTitleNames_ForATargetLaunchedDirectly(string fileName, int expected)
    {
        var names = ProcessService.ReuseTitleNames(new ResolvedLaunchTarget(fileName, LaunchTargetKind.Other), null);

        if (expected < 0)
            names.Should().BeNull("no title can be expected to show it");
        else
            names.Should().HaveCount(expected);
    }

    [Fact]
    public async Task AWindowTheTargetPolicyDenies_IsNotReused()
    {
        var denied = Open(0x10, "Notepad", "Untitled - Notepad");
        var allowed = Open(0x11, "Notepad", "Other - Notepad");
        _windows.Setup(w => w.EvaluateTargetPolicy(denied.Handle)).Returns(new TargetPolicyVerdict(false, "notepad", "denied"));

        var result = await LaunchAsync("notepad.exe");

        result.Window.Should().Be(allowed);
    }

    [Fact]
    public async Task InABatch_AWindowAnEarlierItemStarted_IsNotReused()
    {
        // The first item's window appears as soon as it is started, before the second item looks for an open window
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>())).Returns(() =>
        {
            Open(0x20, "Notepad", "Untitled - Notepad", processId: LaunchedProcessId);
            return _launched.Object;
        });

        var results = await CreateSut().LaunchApplicationsBatchAsync(
            [new LaunchRequest("notepad.exe"), new LaunchRequest("notepad.exe", ReuseExisting: true)]);

        results[0].Status.Should().Be(LaunchStatus.Started);
        results[1].Status.Should().Be(LaunchStatus.Started, "the only Notepad window was started by this batch");
        _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Exactly(2));
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
