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
    private readonly Mock<IWindowWaitService> _mockWindowWait = new();
    private readonly List<string> _calls = [];
    private readonly List<Func<WindowCandidate, bool>> _ownerPredicates = [];
    private readonly List<WindowWaitOptions> _waitOptions = [];
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
            .Callback<ProcessStartInfo>(startInfo =>
            {
                _startedWith = startInfo;
                _calls.Add("start");
            })
            .Returns(_mockLaunchedProcess.Object);
        _mockProcessWrapper.Setup(w => w.OpenProcess(It.IsAny<int>())).Returns((IProcessHandle?)null);

        // The window wait (WindowWaitServiceTests covers it) records the order of the calls
        _mockWindowWait.Setup(w => w.TakeBaseline())
            .Callback(() => _calls.Add("baseline"))
            .Returns(new WindowBaseline(new Dictionary<nint, string>(), nint.Zero));
    }

    private ProcessService CreateSut(IBrowserDetectionService? browserDetection = null) => new(
        _mockWindowService.Object,
        _mockValidationService.Object,
        browserDetection ?? _mockBrowserDetection.Object,
        _mockResolver.Object,
        _mockProcessWrapper.Object,
        windowWait: _mockWindowWait.Object);

    private void VerifyNothingStarted() =>
        _mockProcessWrapper.Verify(w => w.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);

    #region T4.1: LaunchApplication Tests

    /// <summary>A window of another process than the started one: the app the launcher handed off to.</summary>
    private static readonly WindowInfo HandedOffWindow =
        TestDataFactory.CreateWindowInfo(handleValue: 0x5678, title: "Example - Browser", processName: "browser", processId: 7777);

    /// <summary>The wait service finds <paramref name="window"/> with <paramref name="outcome"/>, after recording what it got.</summary>
    private void WaitFinds(WindowWaitOutcome outcome, WindowInfo? window)
    {
        _mockWindowWait.Setup(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
                It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()))
            .Callback<WindowBaseline, Func<WindowCandidate, bool>, WindowWaitOptions, CancellationToken>((_, isOwner, options, _) =>
            {
                _ownerPredicates.Add(isOwner);
                _waitOptions.Add(options);
                _calls.Add("wait");
            })
            .ReturnsAsync(new WindowWaitResult(outcome, window, 300));
    }

    [Fact]
    public async Task LaunchApplication_WithValidExecutable_ReturnsSuccess()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplicationAsync("notepad.exe", waitForWindow: false);

        // Assert
        result.Success.Should().BeTrue();
        result.ProcessId.Should().Be(LaunchedProcessId);
        result.Status.Should().Be(LaunchStatus.Started);
        result.WindowHandle.Should().BeNull();
        result.Window.Should().BeNull();
        result.LauncherExited.Should().BeNull("there was no window wait");
        result.ErrorMessage.Should().BeNull();
        result.ErrorCode.Should().BeNull();
        _startedWith.Should().NotBeNull();
        _startedWith!.FileName.Should().Be("notepad.exe");
        _startedWith.UseShellExecute.Should().BeTrue();
        _mockWindowWait.VerifyNoOtherCalls();
        _mockLaunchedProcess.Verify(p => p.Dispose(), Times.Once);
    }

    /// <summary>
    /// T4.1.2: Test LaunchApplication with invalid executable returns failure.
    /// </summary>
    [Fact]
    public async Task LaunchApplication_WithInvalidExecutable_ReturnsFailure()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(
                false,
                Error: "Executable not found or invalid path",
                ErrorCode: nameof(LaunchErrorCode.InvalidPath)));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplicationAsync("nonexistent_app_xyz123.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.Status.Should().BeNull();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ErrorCode.Should().NotBeNullOrEmpty();
        VerifyNothingStarted();
    }

    [Fact]
    public async Task LaunchApplication_WithWaitForWindow_TakesTheBaselineBeforeTheStart_AndReturnsTheWindowFound()
    {
        // Arrange - the launcher handed off: the window belongs to another process
        WaitFinds(WindowWaitOutcome.NewWindow, HandedOffWindow);
        _mockLaunchedProcess.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("chrome.exe", ["https://example.com/"], waitForWindow: true, waitTimeoutMs: 8000);

        // Assert
        _calls.Should().Equal("baseline", "start", "wait");
        result.Success.Should().BeTrue();
        result.Status.Should().Be(LaunchStatus.WindowObserved);
        result.ProcessId.Should().Be(LaunchedProcessId);
        result.WindowHandle.Should().Be(new nint(0x5678));
        result.Window.Should().Be(HandedOffWindow);
        result.LauncherExited.Should().BeTrue();
        result.Warning.Should().BeNull();
        _waitOptions.Single().TimeoutMs.Should().Be(8000);
        _waitOptions.Single().TryClaim.Should().BeNull();
        _mockLaunchedProcess.Verify(p => p.Dispose(), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_TheWaitsOwnerCheck_AcceptsTheStartedProcessAndTheExpectedWindow()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        await CreateSut().LaunchApplicationAsync(
            new LaunchRequest("app.exe", ExpectWindow: new LaunchWindowHint(TitleContains: "Report")), waitForWindow: true, waitTimeoutMs: 1000);

        // Assert
        var isOwner = _ownerPredicates.Single();
        isOwner(new WindowCandidate(0x1, LaunchedProcessId, null, "Untitled")).Should().BeTrue();
        isOwner(new WindowCandidate(0x2, 999, null, "Quarterly Report")).Should().BeTrue();
        isOwner(new WindowCandidate(0x3, 999, null, "Something else")).Should().BeFalse();
    }

    [Fact]
    public async Task LaunchApplication_GivesTheWaitTheDocumentsOfTheArgumentsWithTheirPaths_ButNoHost()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        await CreateSut().LaunchApplicationAsync("notepad.exe", [@"C:\Projects\README.md", "--flag", "https://www.example.com/page", "notes.txt"],
            @"C:\Work", waitForWindow: true, waitTimeoutMs: 1000);

        // Assert - a web page sets its own title, so a host in it says nothing about the launch
        _waitOptions.Single().DocumentHints.Should().Equal(
            new DocumentHint("README.md", @"C:\Projects\README.md"),
            new DocumentHint("notes.txt", @"C:\Work\notes.txt"));
        _waitOptions.Single().TitleContains.Should().BeNull();
    }

    [Fact]
    public async Task LaunchApplication_GivesTheWaitExpectWindowTitleContains()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        await CreateSut().LaunchApplicationAsync(
            new LaunchRequest("notepad.exe", ["https://www.example.com"], ExpectWindow: new LaunchWindowHint(TitleContains: " Example Domain ")),
            waitForWindow: true, waitTimeoutMs: 1000);

        // Assert
        _waitOptions.Single().TitleContains.Should().Be("Example Domain");
    }

    [Theory]
    [InlineData(WindowWaitOutcome.ReusedExistingWindow, LaunchStatus.ReusedExistingWindow)]
    [InlineData(WindowWaitOutcome.OpenedInTab, LaunchStatus.OpenedInTab)]
    public async Task LaunchApplication_AnExistingWindowTheAppReused_IsReported(WindowWaitOutcome outcome, LaunchStatus expected)
    {
        // Arrange
        WaitFinds(outcome, HandedOffWindow);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("notepad.exe", [@"C:\notes.txt"], waitForWindow: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be(expected);
        result.Window.Should().Be(HandedOffWindow);
    }

    [Fact]
    public async Task LaunchApplication_NoWindowWhileTheProgramRuns_IsTimedOut_WithWhatToDo()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("notepad.exe", waitForWindow: true, waitTimeoutMs: 1500);

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be(LaunchStatus.TimedOut);
        result.LauncherExited.Should().BeFalse();
        result.Window.Should().BeNull();
        result.Warning.Should().Contain("1500 ms").And.Contain("find_windows").And.Contain("do not launch it again");
    }

    [Fact]
    public async Task LaunchApplication_NoWindowAndTheProgramExited_IsExitedWithoutWindow()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        _mockLaunchedProcess.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("launcher.exe", waitForWindow: true);

        // Assert
        result.Status.Should().Be(LaunchStatus.ExitedWithoutWindow);
        result.LauncherExited.Should().BeTrue();
        result.Warning.Should().Contain("expectWindow");
    }

    [Fact]
    public async Task LaunchApplication_AnUntitledMainWindowOfTheProgram_StillCounts()
    {
        // Arrange - the wait (which needs a title) found nothing, but the process has a visible main window
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        var untitled = TestDataFactory.CreateWindowInfo(handleValue: 0x4444, title: "", processId: LaunchedProcessId);
        _mockLaunchedProcess.Setup(p => p.RefreshMainWindowHandle()).Returns(new nint(0x4444));
        _mockWindowService.Setup(w => w.GetWindowInfo(new nint(0x4444))).Returns(untitled);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("game.exe", waitForWindow: true);

        // Assert
        result.Status.Should().Be(LaunchStatus.WindowObserved);
        result.Window.Should().Be(untitled);
    }

    [Fact]
    public async Task LaunchApplication_WhenWindowsStartsNoProcess_IsAHandoff_NotAFailure()
    {
        // Arrange - ShellExecute handed the request to a running app without creating a process
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>())).Returns((IProcessHandle?)null);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("https://example.com/");

        // Assert
        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.ProcessId.Should().BeNull();
        result.Status.Should().Be(LaunchStatus.PossibleHandoff);
        result.Warning.Should().Contain("processId is null");
    }

    [Fact]
    public async Task LaunchApplication_AHandoffWithAWait_ReturnsTheWindowOfTheAppThatGotTheRequest()
    {
        // Arrange
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>())).Returns((IProcessHandle?)null);
        WaitFinds(WindowWaitOutcome.NewWindow, HandedOffWindow);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("https://example.com/", waitForWindow: true);

        // Assert
        result.Status.Should().Be(LaunchStatus.WindowObserved);
        result.ProcessId.Should().BeNull();
        result.Window.Should().Be(HandedOffWindow);
        result.LauncherExited.Should().BeNull("no process was started");
    }

    [Fact]
    public async Task LaunchApplication_AHandoffWithoutAWindow_IsPossibleHandoff()
    {
        // Arrange
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>())).Returns((IProcessHandle?)null);
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        var result = await CreateSut().LaunchApplicationAsync("mailto:someone@example.com", waitForWindow: true);

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be(LaunchStatus.PossibleHandoff);
        result.Warning.Should().Contain("expectWindow");
    }

    [Fact]
    public async Task LaunchApplication_AStartThatDoesNotReturn_IsAwaitingUserConsent_AndItsProcessIsReleasedLater()
    {
        // Arrange - ShellExecute blocks while Windows shows a UAC prompt
        using var userAnswers = new ManualResetEventSlim();
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Returns(() =>
            {
                userAnswers.Wait(TimeSpan.FromSeconds(10));
                return _mockLaunchedProcess.Object;
            });
        var released = new TaskCompletionSource();
        _mockLaunchedProcess.Setup(p => p.Dispose()).Callback(() => released.TrySetResult());
        var sut = new ProcessService(_mockWindowService.Object, _mockValidationService.Object, _mockBrowserDetection.Object,
            _mockResolver.Object, _mockProcessWrapper.Object, windowWait: _mockWindowWait.Object)
        {
            StartTimeout = TimeSpan.FromMilliseconds(100)
        };

        // Act
        var result = await sut.LaunchApplicationAsync("setup.exe", waitForWindow: true);

        // Assert - no window wait; the user approves afterwards and the late process handle is released
        result.Success.Should().BeTrue();
        result.Status.Should().Be(LaunchStatus.AwaitingUserConsent);
        result.ProcessId.Should().BeNull();
        result.Warning.Should().Contain("UAC").And.Contain("do not launch it again");
        _mockWindowWait.Verify(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
            It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        userAnswers.Set();
        (await Task.WhenAny(released.Task, Task.Delay(TimeSpan.FromSeconds(10)))).Should().Be(released.Task);
    }

    [Fact]
    public async Task LaunchApplication_TheUserDeclinesTheUacPrompt_IsElevationCancelled()
    {
        // Arrange
        _mockProcessWrapper.Setup(w => w.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Throws(new Win32Exception(1223, "The operation was canceled by the user."));

        // Act
        var result = await CreateSut().LaunchApplicationAsync("setup.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.ElevationCancelled));
        result.ErrorMessage.Should().Contain("declined");
    }

    [Fact]
    public async Task LaunchApplication_CancelledWhileWaitingForTheWindow_StopsWaitingAndThrows()
    {
        // Arrange - the wait runs until the call is cancelled. The call is cancelled once it waits, not by a timer, so a slow
        // start cannot cancel it before the program is started
        using var call = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockWindowWait.Setup(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
                It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()))
            .Returns<WindowBaseline, Func<WindowCandidate, bool>, WindowWaitOptions, CancellationToken>(async (_, _, _, token) =>
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new WindowWaitResult(WindowWaitOutcome.TimedOut, null, 0);
            });

        // Act
        var launch = CreateSut().LaunchApplicationAsync("notepad.exe", waitForWindow: true, waitTimeoutMs: 30000, cancellationToken: call.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        call.Cancel();

        // Assert - the cancellation is reported as such, not as a LaunchException result; the program was started
        await FluentActions.Awaiting(() => launch).Should().ThrowAsync<OperationCanceledException>();
        _mockProcessWrapper.Verify(w => w.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Once);
        _mockLaunchedProcess.Verify(p => p.Dispose(), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_CancelledBeforeTheStart_StartsNothing()
    {
        // Arrange
        using var call = new CancellationTokenSource();
        call.Cancel();

        // Act
        var act = () => CreateSut().LaunchApplicationAsync("notepad.exe", cancellationToken: call.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        VerifyNothingStarted();
    }

    [Fact]
    public async Task LaunchApplicationsBatch_ReportsProgressAfterEachApplication()
    {
        // Arrange - the second application is refused by validation, the others start
        _mockValidationService.Setup(v => v.ValidateExecutable("bad.exe"))
            .Returns(new ExecutableValidationResult(false, Error: "not allowed", ErrorCode: "InvalidPath"));
        var progress = new RecordingProgress<OperationProgress>();

        // Act
        var results = await CreateSut().LaunchApplicationsBatchAsync(
            [new LaunchRequest("notepad.exe"), new LaunchRequest("bad.exe"), new LaunchRequest("calc.exe")], progress: progress);

        // Assert - "i of n" after every application, started or not
        results.Select(r => r.Success).Should().Equal(true, false, true);
        results.Select(r => r.Status).Should().Equal(LaunchStatus.Started, null, LaunchStatus.Started);
        progress.Reports.Select(p => (p.Completed, p.Total)).Should().Equal((1, 3), (2, 3), (3, 3));
        progress.Reports.Select(p => p.Message).Should().Equal(
            "1 of 3: notepad.exe started",
            "2 of 3: bad.exe was not started (InvalidPath)",
            "3 of 3: calc.exe started");
    }

    [Fact]
    public async Task LaunchApplicationsBatch_WithWaitForWindows_StartsEveryApplicationFirst_ThenWaitsForAllWindows()
    {
        // Arrange - the window of the first application appears; the second never shows one
        _mockWindowWait.Setup(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
                It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()))
            .Callback<WindowBaseline, Func<WindowCandidate, bool>, WindowWaitOptions, CancellationToken>((_, _, options, _) =>
            {
                _waitOptions.Add(options);
                _calls.Add("wait");
            })
            .Returns(() => Task.FromResult(_waitOptions.Count == 1
                ? new WindowWaitResult(WindowWaitOutcome.NewWindow, HandedOffWindow, 400)
                : new WindowWaitResult(WindowWaitOutcome.TimedOut, null, 150)));
        var progress = new RecordingProgress<OperationProgress>();

        // Act
        var results = await CreateSut().LaunchApplicationsBatchAsync(
            [new LaunchRequest("notepad.exe"), new LaunchRequest("calc.exe")], waitForWindows: true, waitTimeoutMs: 150, progress: progress);

        // Assert - both started before either wait; one deadline for both
        _calls.Should().Equal("baseline", "start", "baseline", "start", "wait", "wait");
        _waitOptions.Select(o => o.TimeoutMs).Should().Equal(150, 150);
        results.Select(r => r.Status).Should().Equal(LaunchStatus.WindowObserved, LaunchStatus.TimedOut);
        results[0].Window.Should().Be(HandedOffWindow);
        progress.Reports.Select(p => p.Message).Should().Equal(
            "1 of 2: notepad.exe started, its window appeared",
            "2 of 2: calc.exe started, no window appeared in time");
        _mockLaunchedProcess.Verify(p => p.Dispose(), Times.Exactly(2));
    }

    [Fact]
    public async Task LaunchApplicationsBatch_ANewWindow_GoesToOneLaunchOnly()
    {
        // Arrange - two launches of the same program wait together
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        await CreateSut().LaunchApplicationsBatchAsync([new LaunchRequest("notepad.exe"), new LaunchRequest("notepad.exe")], waitForWindows: true);

        // Assert - they share one claim: the window the first one takes is no longer the second one's
        _waitOptions.Should().HaveCount(2);
        _waitOptions[0].TryClaim!(0x100).Should().BeTrue();
        _waitOptions[1].TryClaim!(0x100).Should().BeFalse();
        _waitOptions[1].TryClaim!(0x101).Should().BeTrue();
    }

    /// <summary>
    /// Two launches of the same program match each other's windows by name and path. A window of the process one of them
    /// started (or of its child) is that launch's, whichever wait sees it first: a wait that polls first must not take
    /// the other launch's document window. A window neither started (a running instance both may hand off to) stays open
    /// to both, and the shared claim gives it to one.
    /// </summary>
    [Fact]
    public async Task LaunchApplicationsBatch_SameProgramTwice_EachLaunchGetsOnlyTheWindowsOfTheProcessItStarted()
    {
        // Arrange - notepad a.txt starts 501, notepad b.txt starts 502; 600 is a child of 502; 700 is an older Notepad
        const string notepad = @"C:\Windows\notepad.exe";
        var launchedAt = DateTime.UtcNow;
        var processList = new Dictionary<int, (int ParentProcessId, string ImageName)>
        {
            [501] = (10, "notepad.exe"),
            [502] = (10, "notepad.exe"),
            [600] = (502, "notepad.exe"),
            [700] = (10, "notepad.exe")
        };
        _mockResolver.Setup(r => r.Resolve("notepad", It.IsAny<string?>()))
            .Returns(new ResolvedLaunchTarget(notepad, LaunchTargetKind.NativeExecutable));
        _mockProcessWrapper.Setup(w => w.GetProcessList()).Returns(processList);
        _mockProcessWrapper.Setup(w => w.GetProcessImagePath(It.IsAny<int>())).Returns(notepad);
        _mockProcessWrapper.Setup(w => w.GetProcessStartTime(It.IsAny<int>()))
            .Returns<int>(id => id == 700 ? launchedAt.AddHours(-1) : launchedAt.AddSeconds(1));
        var first = new Mock<IProcessHandle>();
        first.SetupGet(p => p.Id).Returns(501);
        var second = new Mock<IProcessHandle>();
        second.SetupGet(p => p.Id).Returns(502);
        _mockProcessWrapper.SetupSequence(w => w.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Returns(first.Object)
            .Returns(second.Object);
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        await CreateSut().LaunchApplicationsBatchAsync(
            [
                new LaunchRequest("notepad", ["a.txt"], ExpectWindow: new LaunchWindowHint(TitleContains: ".txt")),
                new LaunchRequest("notepad", ["b.txt"])
            ],
            waitForWindows: true);

        // Assert
        _ownerPredicates.Should().HaveCount(2);
        var (ownsA, ownsB) = (_ownerPredicates[0], _ownerPredicates[1]);
        WindowCandidate Window(int processId, string title) => new(0x100 + processId, processId, null, title);

        ownsA(Window(501, "a.txt - Notepad")).Should().BeTrue();
        ownsA(Window(502, "b.txt - Notepad")).Should().BeFalse("502 is the other launch's process, even though the hint matches");
        ownsA(Window(600, "b.txt - Notepad")).Should().BeFalse("600 is a child of the other launch's process");
        ownsB(Window(502, "b.txt - Notepad")).Should().BeTrue();
        ownsB(Window(600, "b.txt - Notepad")).Should().BeTrue();
        ownsB(Window(501, "a.txt - Notepad")).Should().BeFalse("501 is the other launch's process");

        // A running instance that neither launch started can be either launch's (the shared claim decides)
        ownsA(Window(700, "Notepad")).Should().BeTrue();
        ownsB(Window(700, "Notepad")).Should().BeTrue();
    }

    /// <summary>
    /// A single-instance app (Code, Windows Terminal, an Electron app) started twice in one batch: the second launcher hands
    /// its request to the instance the first launch started and exits, so its window belongs to the first launch's process.
    /// Once the first launch has its window, and the second launcher is gone, the second launch may take that process's
    /// next window; before that, or while the second launcher still runs, it may not.
    /// </summary>
    [Fact]
    public async Task LaunchApplicationsBatch_ALaunchThatHandedOffToAnEarlierItemsInstance_GetsItsWindow_OnceThatItemHasOne()
    {
        // Arrange - launch 1 starts 501, whose child 600 is the app's single instance; launch 2's 502 forwards to it and exits
        const string app = @"C:\Apps\Code\Code.exe";
        var launchedAt = DateTime.UtcNow;
        _mockResolver.Setup(r => r.Resolve("code", It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(app, LaunchTargetKind.NativeExecutable));
        _mockProcessWrapper.Setup(w => w.GetProcessList()).Returns(new Dictionary<int, (int ParentProcessId, string ImageName)>
        {
            [501] = (10, "Code.exe"),
            [502] = (10, "Code.exe"),
            [600] = (501, "Code.exe")
        });
        _mockProcessWrapper.Setup(w => w.GetProcessImagePath(It.IsAny<int>())).Returns(app);
        _mockProcessWrapper.Setup(w => w.GetProcessStartTime(It.IsAny<int>())).Returns(launchedAt.AddSeconds(1));
        var first = new Mock<IProcessHandle>();
        first.SetupGet(p => p.Id).Returns(501);
        var second = new Mock<IProcessHandle>();
        second.SetupGet(p => p.Id).Returns(502);
        var secondExited = false;
        second.SetupGet(p => p.HasExited).Returns(() => secondExited);
        second.SetupGet(p => p.ExitCode).Returns(() => secondExited ? 0 : null);
        _mockProcessWrapper.SetupSequence(w => w.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Returns(first.Object)
            .Returns(second.Object);
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        await CreateSut().LaunchApplicationsBatchAsync([new LaunchRequest("code", ["a"]), new LaunchRequest("code", ["b"])], waitForWindows: true);

        // Assert
        var ownsB = _ownerPredicates[1];
        var handedOffWindow = new WindowCandidate(0x700, 600, null, "b - Visual Studio Code");
        ownsB(handedOffWindow).Should().BeFalse("launch 1 has no window yet: this one may be its own");

        _waitOptions[0].TryClaim!(0x600).Should().BeTrue();
        ownsB(handedOffWindow).Should().BeFalse("launch 2's own process still runs: its window may still come");

        secondExited = true;
        ownsB(handedOffWindow).Should().BeTrue("launch 1 has its window, and launch 2's launcher handed off and exited with 0");
        _ownerPredicates[0](handedOffWindow).Should().BeTrue("the shared claim still decides which launch gets a window");
    }

    /// <summary>
    /// M2 follow-up F3: a launcher that failed (a non-zero exit code) handed nothing off, so it is never credited with a
    /// window of another item's instance, even one that appears after it exited: its launch ends without a window.
    /// </summary>
    [Fact]
    public async Task LaunchApplicationsBatch_ALauncherThatFailed_IsNotCreditedWithAWindowOfAnotherItemsInstance()
    {
        // Arrange
        var (_, second) = SetUpTwoCodeLaunches();
        second.SetupGet(p => p.HasExited).Returns(true);
        second.SetupGet(p => p.ExitCode).Returns(1);

        // Act
        await CreateSut().LaunchApplicationsBatchAsync([new LaunchRequest("code", ["a"]), new LaunchRequest("code", ["b"])], waitForWindows: true);

        // Assert - the window appears after the failed launcher exited, and is still not its
        _waitOptions[0].TryClaim!(0x600).Should().BeTrue();
        _ownerPredicates[1](new WindowCandidate(0x700, 600, null, "a - Visual Studio Code")).Should()
            .BeFalse("launch 2's launcher failed: the window of launch 1's instance is not its");
    }

    /// <summary>
    /// M2 follow-up F3: when the launcher's exit code cannot be read, only a window that appears after its exit was seen
    /// can be the one it handed off; a window of the other item's instance that was already there is not.
    /// </summary>
    [Fact]
    public async Task LaunchApplicationsBatch_ALauncherWithoutAnExitCode_GetsOnlyAWindowThatAppearedAfterItExited()
    {
        // Arrange
        var (_, second) = SetUpTwoCodeLaunches();
        var secondExited = false;
        second.SetupGet(p => p.HasExited).Returns(() => secondExited);
        second.SetupGet(p => p.ExitCode).Returns((int?)null);

        // Act
        await CreateSut().LaunchApplicationsBatchAsync([new LaunchRequest("code", ["a"]), new LaunchRequest("code", ["b"])], waitForWindows: true);

        // Assert
        var ownsB = _ownerPredicates[1];
        var earlier = new WindowCandidate(0x700, 600, null, "Welcome - Visual Studio Code");
        _waitOptions[0].TryClaim!(0x600).Should().BeTrue();
        ownsB(earlier).Should().BeFalse("launch 2's own process still runs");

        secondExited = true;
        ownsB(earlier).Should().BeFalse("this window was there while launch 2's launcher still ran: it was not handed off");
        ownsB(new WindowCandidate(0x800, 600, null, "b - Visual Studio Code")).Should()
            .BeTrue("this window appeared after the launcher exited");
    }

    /// <summary>Two launches of Code: launch 1 starts 501, whose child 600 is the single instance; launch 2 starts 502.</summary>
    private (Mock<IProcessHandle> First, Mock<IProcessHandle> Second) SetUpTwoCodeLaunches()
    {
        const string app = @"C:\Apps\Code\Code.exe";
        var launchedAt = DateTime.UtcNow;
        _mockResolver.Setup(r => r.Resolve("code", It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(app, LaunchTargetKind.NativeExecutable));
        _mockProcessWrapper.Setup(w => w.GetProcessList()).Returns(new Dictionary<int, (int ParentProcessId, string ImageName)>
        {
            [501] = (10, "Code.exe"),
            [502] = (10, "Code.exe"),
            [600] = (501, "Code.exe")
        });
        _mockProcessWrapper.Setup(w => w.GetProcessImagePath(It.IsAny<int>())).Returns(app);
        _mockProcessWrapper.Setup(w => w.GetProcessStartTime(It.IsAny<int>())).Returns(launchedAt.AddSeconds(1));
        var first = new Mock<IProcessHandle>();
        first.SetupGet(p => p.Id).Returns(501);
        var second = new Mock<IProcessHandle>();
        second.SetupGet(p => p.Id).Returns(502);
        _mockProcessWrapper.SetupSequence(w => w.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Returns(first.Object)
            .Returns(second.Object);
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        return (first, second);
    }

    [Fact]
    public async Task LaunchApplicationsBatch_ProgressValues_OnlyIncrease_WhenWaitsEndTogether()
    {
        // Arrange - every wait ends on a thread-pool thread at once
        using var go = new ManualResetEventSlim();
        _mockWindowWait.Setup(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
                It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.Run(() =>
            {
                go.Wait(TimeSpan.FromSeconds(10));
                return new WindowWaitResult(WindowWaitOutcome.TimedOut, null, 100);
            }));
        var progress = new RecordingProgress<OperationProgress>();
        var applications = Enumerable.Range(0, 16).Select(i => new LaunchRequest($"app{i}.exe")).ToList();

        // Act
        var batch = CreateSut().LaunchApplicationsBatchAsync(applications, waitForWindows: true, waitTimeoutMs: 100, progress: progress);
        go.Set();
        await batch;

        // Assert
        progress.Reports.Select(p => p.Completed).Should().Equal(Enumerable.Range(1, 16));
    }

    [Fact]
    public async Task LaunchApplicationsBatch_ExpectWindowOfAnItem_IsUsedForItsWait()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);

        // Act
        await CreateSut().LaunchApplicationsBatchAsync(
            [new LaunchRequest("ms-settings:display", ExpectWindow: new LaunchWindowHint(TitleContains: "Settings"))], waitForWindows: true);

        // Assert
        _ownerPredicates.Single()(new WindowCandidate(0x1, 999, null, "Settings")).Should().BeTrue();
    }

    [Fact]
    public async Task LaunchApplicationsBatch_WhenCancelled_StartsNoFurtherApplication()
    {
        // Arrange - the call is cancelled once the first application has started
        using var call = new CancellationTokenSource();
        var progress = new CancellingProgress(call);

        // Act
        var act = () => CreateSut().LaunchApplicationsBatchAsync(
            [new LaunchRequest("notepad.exe"), new LaunchRequest("calc.exe"), new LaunchRequest("mspaint.exe")],
            progress: progress, cancellationToken: call.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _mockProcessWrapper.Verify(w => w.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Once);
    }

    /// <summary>Cancels <paramref name="call"/> at the first progress report.</summary>
    private sealed class CancellingProgress(CancellationTokenSource call) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => call.Cancel();
    }

    #endregion

    #region LaunchApplication Error Code Tests

    [Fact]
    public async Task LaunchApplication_WhenValidationFails_ReturnsErrorCode()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns(new ExecutableValidationResult(false, Error: "Path traversal detected", ErrorCode: "PathTraversal"));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplicationAsync("..\\..\\cmd.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Path traversal detected");
        result.ErrorCode.Should().Be("PathTraversal");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task LaunchApplication_WhenWorkingDirValidationFails_ReturnsErrorCode()
    {
        // Arrange
        _mockValidationService.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns(new PathValidationResult(false, Error: "Invalid working directory", ErrorCode: "InvalidWorkingDirectory"));
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchApplicationAsync("notepad.exe", workingDirectory: "invalid|path");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid working directory");
        result.ErrorCode.Should().Be("InvalidWorkingDirectory");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task LaunchApplication_WhenProcessStartThrows_ReturnsLaunchExceptionWithValidationWarnings()
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
        var result = await sut.LaunchApplicationAsync("app.exe");

        // Assert
        result.Success.Should().BeFalse();
        result.ProcessId.Should().BeNull();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.LaunchException));
        result.ErrorMessage.Should().Be("The system cannot find the file specified.");
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

    [Fact]
    public void GetRunningProcesses_CountsWindowsOnOtherDesktopsToolAndShellWindowsButNotUntitledOnes()
    {
        // Arrange - the test host stands in for a debug browser whose window the user moved to
        // another virtual desktop: the user-facing listings leave that cloaked window out
        using var self = Process.GetCurrentProcess();
        WindowQuery? query = null;
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Callback<WindowQuery>(q => query = q)
            .Returns(
            [
                TestDataFactory.CreateWindowInfo(1, "Debug browser", self.ProcessName, self.Id) with { IsCloaked = true },
                TestDataFactory.CreateWindowInfo(2, "Find", self.ProcessName, self.Id),
                TestDataFactory.CreateWindowInfo(3, "", self.ProcessName, self.Id),
            ]);

        // Act
        var result = CreateSut().GetRunningProcesses(nameFilter: self.ProcessName);

        // Assert
        query.Should().NotBeNull();
        query!.IncludeShell.Should().BeTrue("cloaked, tool and shell windows mean the process still has a window");
        result.Should().ContainSingle(p => p.ProcessId == self.Id).Which.WindowCount.Should().Be(2);
        _mockWindowService.Verify(w => w.GetAllWindows(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void GetRunningProcesses_CountsAUwpWindowForTheAppAndForItsFrameHost()
    {
        // Arrange - the test host plays the ApplicationFrameHost of a UWP app's window
        using var self = Process.GetCurrentProcess();
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(
            [
                TestDataFactory.CreateWindowInfo(1, "Calculator", "CalculatorApp", processId: int.MaxValue) with
                {
                    HostProcessName = self.ProcessName,
                    HostProcessId = self.Id
                },
            ]);

        // Act
        var result = CreateSut().GetRunningProcesses(nameFilter: self.ProcessName);

        // Assert - the frame window belongs to ApplicationFrameHost, so the host still counts it
        result.Should().ContainSingle(p => p.ProcessId == self.Id).Which.WindowCount.Should().Be(1);
    }

    [Fact]
    public void GetRunningProcesses_ByDefault_LeavesOutProcessesWithoutATitledWindow()
    {
        // Arrange
        using var self = Process.GetCurrentProcess();
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns([TestDataFactory.CreateWindowInfo(1, "", self.ProcessName, self.Id)]);

        // Act
        var result = CreateSut().GetRunningProcesses(nameFilter: self.ProcessName);

        // Assert
        result.Should().NotContain(p => p.ProcessId == self.Id);
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
    public async Task KillProcess_WithoutConfirmation_ReturnsConfirmationRequired()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcessAsync(1234, confirm: false);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ConfirmationRequired");
        result.ErrorMessage.Should().Contain("Confirmation required");
    }

    [Fact]
    public async Task KillProcess_WithZeroProcessId_ReturnsInvalidProcessId()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcessAsync(0, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidProcessId");
    }

    [Fact]
    public async Task KillProcess_WithNegativeProcessId_ReturnsInvalidProcessId()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcessAsync(-1, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidProcessId");
    }

    [Fact]
    public async Task KillProcess_WithCurrentProcess_ReturnsCannotTerminateSelf()
    {
        // Arrange
        var sut = CreateSut();
        var currentPid = Environment.ProcessId;

        // Act
        var result = await sut.KillProcessAsync(currentPid, confirm: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("CannotTerminateSelf");
        result.ErrorMessage.Should().Contain("Cannot terminate the current process");
    }

    [Fact]
    public async Task KillProcess_WithNonExistentProcess_ReturnsProcessNotFound()
    {
        // Arrange - the mocked process seam finds no process with this ID
        var sut = CreateSut();

        // Act
        var result = await sut.KillProcessAsync(int.MaxValue - 1, confirm: true);

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
    public async Task LaunchApplication_BrowserWithUrl_CallsBrowserDetectionService()
    {
        // Arrange
        var mockBrowserDetection = new Mock<IBrowserDetectionService>();
        mockBrowserDetection.Setup(b => b.IsBrowser("chrome.exe")).Returns(true);
        mockBrowserDetection.Setup(b => b.ContainsUrl(It.IsAny<string[]>())).Returns(true);
        mockBrowserDetection.Setup(b => b.GetNewWindowFlag("chrome.exe")).Returns("--new-window");
        var sut = CreateSut(mockBrowserDetection.Object);

        // Act
        var result = await sut.LaunchApplicationAsync("chrome.exe", ["https://example.com"]);

        // Assert - browser detection was consulted and the flag goes before the URL
        result.Success.Should().BeTrue();
        _startedWith!.Arguments.Should().Be("--new-window https://example.com");
        mockBrowserDetection.Verify(b => b.IsBrowser("chrome.exe"), Times.Once);
        mockBrowserDetection.Verify(b => b.ContainsUrl(It.IsAny<string[]>()), Times.Once);
        mockBrowserDetection.Verify(b => b.GetNewWindowFlag("chrome.exe"), Times.Once);
    }

    [Fact]
    public async Task LaunchApplication_NonBrowserWithUrl_DoesNotGetNewWindowFlag()
    {
        // Arrange
        var mockBrowserDetection = new Mock<IBrowserDetectionService>();
        mockBrowserDetection.Setup(b => b.IsBrowser("notepad.exe")).Returns(false);
        var sut = CreateSut(mockBrowserDetection.Object);

        // Act
        var result = await sut.LaunchApplicationAsync("notepad.exe", ["https://example.com"]);

        // Assert - checked for a browser, but no new window flag
        result.Success.Should().BeTrue();
        _startedWith!.Arguments.Should().Be("https://example.com");
        mockBrowserDetection.Verify(b => b.IsBrowser("notepad.exe"), Times.Once);
        mockBrowserDetection.Verify(b => b.GetNewWindowFlag(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task LaunchApplication_BrowserWithoutUrl_DoesNotGetNewWindowFlag()
    {
        // Arrange
        var mockBrowserDetection = new Mock<IBrowserDetectionService>();
        mockBrowserDetection.Setup(b => b.IsBrowser("chrome.exe")).Returns(true);
        mockBrowserDetection.Setup(b => b.ContainsUrl(It.IsAny<string[]?>())).Returns(false);
        var sut = CreateSut(mockBrowserDetection.Object);

        // Act
        var result = await sut.LaunchApplicationAsync("chrome.exe", ["--incognito"]);

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
    public async Task LaunchBrowserWithDebug_UnsupportedBrowser_ReturnsErrorWithoutTouchingProcesses()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchBrowserWithDebugAsync("firefox");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.UnsupportedBrowser));
        VerifyNothingStarted();
        _mockProcessWrapper.Verify(w => w.FindWithWhereExe(It.IsAny<string>()), Times.Never);
    }

    #endregion
}
