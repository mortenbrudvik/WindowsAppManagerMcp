using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// ProcessService.LaunchBrowserWithDebugAsync (roadmap M5.1): finding the browser, the first-run switches, port 0 and
/// the DevToolsActivePort file, a launch that handed off to a browser already running, the window owner limited to the
/// started process, the launch status and warning, and the readiness hook. The process seam, the window wait and the
/// browser locator are fakes, so nothing is started; the profile is a real folder under %TEMP% that the fake "browser"
/// writes its DevToolsActivePort into. An explicit port is bound only on the loopback address, to check PortInUse.
/// </summary>
public sealed class DebugBrowserLaunchTests : IDisposable
{
    private const string Chrome = @"C:\Browsers\Chrome\chrome.exe";
    private const int BrowserProcessId = 4321;
    private const int UsersChromeProcessId = 777;

    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService();
    private readonly Mock<IInputValidationService> _validation = new();
    private readonly Mock<IExecutableResolver> _resolver = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Mock<IProcessHandle> _browser = new();
    private readonly Mock<IWindowWaitService> _windowWait = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<Func<WindowCandidate, bool>> _owners = [];
    private readonly string _profile = Path.Combine(Path.GetTempPath(), $"wam-m51-test-{Guid.NewGuid():N}");
    private Action? _whenStarted;

    /// <summary>The started browser's start time, as each read of it finds it (then <see cref="BrowserStart"/>).</summary>
    private readonly Queue<DateTime?> _browserStartTimes = new();
    private static readonly DateTime BrowserStart = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private static readonly WindowInfo BrowserWindow =
        TestDataFactory.CreateWindowInfo(handleValue: 0x5678, title: "New Tab - Google Chrome", processName: "chrome", processId: BrowserProcessId);

    public DebugBrowserLaunchTests()
    {
        _validation.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe));
        _validation.Setup(v => v.ValidateWorkingDirectory(It.IsAny<string?>()))
            .Returns<string?>(dir => new PathValidationResult(true, SanitizedPath: dir));
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(exe, LaunchTargetKind.NativeExecutable));
        _browser.SetupGet(p => p.Id).Returns(BrowserProcessId);
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Callback((ProcessStartInfo info) =>
            {
                _started.Add(info);
                _whenStarted?.Invoke();
            })
            .Returns(_browser.Object);
        _processes.Setup(p => p.GetProcessList()).Returns(new Dictionary<int, (int, string)>
        {
            [BrowserProcessId] = (1, "chrome.exe"),
            [UsersChromeProcessId] = (1, "chrome.exe")
        });
        _processes.Setup(p => p.GetProcessImagePath(It.IsAny<int>())).Returns(Chrome);
        _processes.Setup(p => p.GetProcessStartTime(BrowserProcessId))
            .Returns(() => _browserStartTimes.Count > 0 ? _browserStartTimes.Dequeue() : BrowserStart);
        _windowWait.Setup(w => w.TakeBaseline()).Returns(new WindowBaseline(new Dictionary<nint, string>(), nint.Zero));
        WaitFinds(WindowWaitOutcome.NewWindow, BrowserWindow);
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_profile))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(_profile, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Holds the profile's lockfile open as a running Chromium does on Windows (its process singleton): for writing,
    /// shared for reading only, deleted when closed.
    /// </summary>
    private FileStream HoldProfileLock() => new(
        Path.Combine(_profile, ChromiumProfileLock.FileName), FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose);

    private void WaitFinds(WindowWaitOutcome outcome, WindowInfo? window) =>
        _windowWait.Setup(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
                It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()))
            .Callback<WindowBaseline, Func<WindowCandidate, bool>, WindowWaitOptions, CancellationToken>((_, isOwner, _, _) => _owners.Add(isOwner))
            .ReturnsAsync(new WindowWaitResult(outcome, window, 300));

    /// <summary>The fake browser writes DevToolsActivePort when it starts, as Chromium does for port 0 once it listens.</summary>
    private void BrowserReportsPort(string content) =>
        _whenStarted = () => File.WriteAllText(DevToolsActivePortFile.PathIn(_profile), content);

    private static DebugBrowserLocator Finds(params string[] existing) =>
        new(path => existing.Contains(path, StringComparer.OrdinalIgnoreCase), _ => [], FakeFolder);

    private static string FakeFolder(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
        Environment.SpecialFolder.ProgramFilesX86 => @"C:\Program Files (x86)",
        Environment.SpecialFolder.LocalApplicationData => @"C:\Users\me\AppData\Local",
        _ => string.Empty
    };

    private ProcessService CreateSut(DebugBrowserLocator? locator = null, IDebugBrowserReadiness? readiness = null) => new(
        _windows.Object, _validation.Object, TestDataFactory.CreateMockBrowserDetectionService().Object, _resolver.Object,
        _processes.Object, windowWait: _windowWait.Object,
        debugBrowserLocator: locator ?? new DebugBrowserLocator(path => path == Chrome, _ => [Chrome], FakeFolder),
        debugBrowserReadiness: readiness)
    {
        DevToolsPortTimeout = TimeSpan.FromMilliseconds(300)
    };

    private DebugBrowserLaunchRequest Request(int port = 0, string? url = null, string? executablePath = null) =>
        new("chrome", url, port, _profile, executablePath);

    private void VerifyNothingStarted() => _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);

    [Fact]
    public async Task UnsupportedBrowser_ReturnsErrorWithoutTouchingProcesses()
    {
        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest("firefox"));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.UnsupportedBrowser));
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ABrowserThatIsNotFound_ListsThePathsTried_AndStartsNothing()
    {
        // Act
        var result = await CreateSut(Finds()).LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.BrowserNotFound));
        result.Error.Should().Contain(@"App Paths\chrome.exe")
            .And.Contain(@"C:\Program Files\Google\Chrome\Application\chrome.exe")
            .And.Contain(@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")
            .And.Contain(@"C:\Users\me\AppData\Local\Google\Chrome\Application\chrome.exe")
            .And.Contain("executablePath");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task AnExecutablePathThatDoesNotExist_IsBrowserNotFound_AndNothingElseIsTried()
    {
        // Act
        var result = await CreateSut(Finds(Chrome)).LaunchBrowserWithDebugAsync(Request(executablePath: @"D:\Portable\chrome.exe"));

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.BrowserNotFound));
        result.Error.Should().Contain(@"D:\Portable\chrome.exe").And.Contain("does not exist");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task TheBrowser_StartsWithTheFirstRunSwitches_PortZero_AndItsOwnProfile()
    {
        // Arrange
        BrowserReportsPort("50123\n/devtools/browser/0a1b2c3d\n");

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request(url: "https://example.com/"));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        var started = _started.Should().ContainSingle().Subject;
        started.FileName.Should().Be(Chrome);
        started.Arguments.Should().Be(
            "--no-first-run --no-default-browser-check --disable-search-engine-choice-screen --disable-sync " +
            $"--remote-debugging-port=0 --user-data-dir={_profile} https://example.com/");
    }

    [Fact]
    public async Task PortZero_ReportsThePortTheBrowserWroteToDevToolsActivePort()
    {
        // Arrange
        BrowserReportsPort("50123\n/devtools/browser/0a1b2c3d\n");

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.CdpPort.Should().Be(50123);
        result.ProcessId.Should().Be(BrowserProcessId);
        result.WindowHandle.Should().Be(BrowserWindow.Handle);
        result.UserDataDir.Should().Be(_profile);
        result.LaunchStatus.Should().Be(LaunchStatus.WindowObserved);
        result.CdpReady.Should().BeNull("this server has no readiness check, so nothing says whether the endpoint answers");
        result.CdpEndpoint.Should().BeNull();
    }

    [Fact]
    public async Task AStaleDevToolsActivePort_IsDeletedBeforeTheStart_SoItsPortIsNeverReported()
    {
        // Arrange - an earlier run of the profile left port 9999; this browser writes nothing
        File.WriteAllText(DevToolsActivePortFile.PathIn(_profile), "9999\n/devtools/browser/old\n");
        bool? staleFileAtStart = null;
        _whenStarted = () => staleFileAtStart = File.Exists(DevToolsActivePortFile.PathIn(_profile));

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert - the browser runs, but its port is not known: a failure that still names it, so it can be closed
        staleFileAtStart.Should().BeFalse();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.CdpPort.Should().BeNull();
        result.Error.Should().Contain("did not write its DevTools port").And.Contain("still running");
        result.ProcessId.Should().Be(BrowserProcessId);
        result.WindowHandle.Should().Be(BrowserWindow.Handle);
        result.UserDataDir.Should().Be(_profile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a port\n")]
    [InlineData("0\n/devtools/browser/x")]
    [InlineData("70000\n")]
    public void DevToolsActivePort_WithoutAPortOnItsFirstLine_HasNoPort(string content)
    {
        DevToolsActivePortFile.ParsePort(content).Should().BeNull();
    }

    [Theory]
    [InlineData("50123\n/devtools/browser/abc", 50123)]
    [InlineData("9222\r\n/devtools/browser/abc\r\n", 9222)]
    [InlineData(" 1025 ", 1025)]
    public void DevToolsActivePort_ItsFirstLineIsThePort(string content, int port)
    {
        DevToolsActivePortFile.ParsePort(content).Should().Be(port);
    }

    [Fact]
    public async Task OnlyWindowsOfTheStartedProcess_CountAsTheDebugBrowsers()
    {
        // Arrange
        BrowserReportsPort("50123\n");

        // Act
        await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert - the user's own Chrome (same name and path) opening a window during the wait is not the debug browser
        var isOwner = _owners.Should().ContainSingle().Subject;
        isOwner(new WindowCandidate(0x100, BrowserProcessId, null, "New Tab")).Should().BeTrue();
        isOwner(new WindowCandidate(0x200, UsersChromeProcessId, null, "Inbox - Google Chrome")).Should().BeFalse();
    }

    [Fact]
    public async Task ALaunchThatHandedOffToARunningBrowser_FailsWithDebugLaunchFailed()
    {
        // Arrange - Chrome passed the request to the instance that already uses the profile and exited; no window of its
        // own. Its lockfile was not seen before the start (the browser started meanwhile): the backstop after the start
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        _browser.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.LaunchStatus.Should().Be(LaunchStatus.ExitedWithoutWindow);
        result.Error.Should().Contain("already running with this userDataDir").And.Contain("ignores the debug switches")
            .And.Contain("opened the url").And.Contain("could not start");
        result.ProcessId.Should().BeNull("the started process exited");
        result.CdpPort.Should().BeNull();
        result.UserDataDir.Should().Be(_profile);
    }

    [Fact]
    public async Task AHandoff_NamesNoBrowserToClose_AndKeepsOnlyTheLaunchsOwnWarning()
    {
        // Arrange - launch_application's note for a launch without a window says to pass expectWindow, which this tool
        // does not have, and not to launch again, which the error's advice contradicts
        _validation.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe, Warning: "careful"));
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        _browser.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert - the result has no PID of "that browser": close only a debug browser by the processId its own launch
        // returned, never one found by name or title (it may be the user's own browser)
        result.Warning.Should().Be("careful");
        result.Error.Should().NotContain("Close that browser")
            .And.Contain("processId its own launch returned").And.Contain("never close a browser you did not start");
    }

    [Fact]
    public async Task ABrowserThatExitsAtOnceWithANewTemporaryProfile_IsNotBlamedOnAnotherBrowser()
    {
        // Arrange - no other browser can use a folder this launch just named
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        _browser.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest("chrome"));

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain("exited without a window of its own").And.Contain("could not start")
            .And.NotContain("already running with this userDataDir").And.NotContain("leave userDataDir out");
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task AProfileABrowserIsRunningWith_IsRefusedBeforeAnythingIsDeletedOrStarted()
    {
        // Arrange - a debug browser an earlier call started on this profile: its port file, and its lockfile held open
        File.WriteAllText(DevToolsActivePortFile.PathIn(_profile), "50123\n/devtools/browser/running\n");
        using var running = HoldProfileLock();

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request(url: "https://example.com/"));

        // Assert - the url is not handed to that browser, and its port stays readable from the profile
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain("already running with this userDataDir").And.Contain("Nothing was launched")
            .And.Contain("never close a browser you did not start").And.Contain("leave userDataDir out");
        result.ProcessId.Should().BeNull();
        result.UserDataDir.Should().Be(_profile);
        VerifyNothingStarted();
        _windowWait.Verify(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
            It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        File.ReadAllText(DevToolsActivePortFile.PathIn(_profile)).Should().StartWith("50123");
    }

    [Fact]
    public async Task ALockfileNoBrowserHolds_DoesNotStopTheLaunch()
    {
        // Arrange - left behind (a copied profile); a running Chromium deletes its lockfile when it closes it
        File.WriteAllText(Path.Combine(_profile, ChromiumProfileLock.FileName), string.Empty);
        BrowserReportsPort("50123\n");

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.CdpPort.Should().Be(50123);
    }

    [Fact]
    public void ChromiumProfileLock_IsHeldOnlyWhileTheLockfileIsOpen()
    {
        ChromiumProfileLock.HeldReason(_profile).Should().BeNull("there is no lockfile");

        using (HoldProfileLock())
        {
            ChromiumProfileLock.HeldReason(_profile).Should().NotBeNull();
        }

        ChromiumProfileLock.HeldReason(_profile).Should().BeNull("Chromium's lockfile is deleted when it is closed");
    }

    [Fact]
    public async Task AStalePortFileThatCannotBeDeleted_StartsNothing_AndDoesNotBlameABrowser()
    {
        // Arrange - Chromium writes DevToolsActivePort and closes it, so a running browser does not stop the delete;
        // a read-only file (or the folder's permissions) does
        var portFile = DevToolsActivePortFile.PathIn(_profile);
        File.WriteAllText(portFile, "9999\n");
        File.SetAttributes(portFile, FileAttributes.ReadOnly);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain("could not be deleted").And.Contain("Nothing was launched").And.Contain("permissions")
            .And.NotContain("Close that browser");
        VerifyNothingStarted();
    }

    [Theory]
    [InlineData("wam-profile")]
    [InlineData(@"profiles\debug")]
    [InlineData(@"\profiles\debug")]
    [InlineData(@"C:profile")]
    public async Task ARelativeUserDataDir_IsRefused_AndNothingIsStarted(string userDataDir)
    {
        // Arrange - the server would resolve it against its own current directory, the browser against its install folder
        var sut = CreateSut();

        // Act
        var result = await sut.LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest("chrome", UserDataDir: userDataDir));

        // Assert
        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Error.Should().Contain("full path").And.Contain("Nothing was launched");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ABrowserThatExitsWhileItsPortIsAwaited_IsNotNamed_SoNoOtherProcessIsClosedByItsPid()
    {
        // Arrange - the browser started and showed its window, then exited before writing DevToolsActivePort
        _browserStartTimes.Enqueue(BrowserStart);
        _browserStartTimes.Enqueue(null);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain("did not write its DevTools port").And.Contain("no longer running").And.NotContain("still running");
        result.ProcessId.Should().BeNull();
        result.WindowHandle.Should().BeNull();
        result.UserDataDir.Should().Be(_profile);
    }

    [Fact]
    public async Task ABrowserWhosePidWasReusedWhileItsPortIsAwaited_IsNotNamed()
    {
        // Arrange - another process now has the PID: it started at another time
        _browserStartTimes.Enqueue(BrowserStart);
        _browserStartTimes.Enqueue(BrowserStart.AddSeconds(5));

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Error.Should().Contain("no longer running");
        result.ProcessId.Should().BeNull();
        result.WindowHandle.Should().BeNull();
    }

    [Fact]
    public async Task ABrowserStillRunningWithoutAWindow_Succeeds_WithTheLaunchStatusAndWarning()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        _browser.SetupGet(p => p.HasExited).Returns(false);
        var port = FreeLoopbackPort();

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request(port: port));

        // Assert - an explicit port is not read from DevToolsActivePort (Chromium writes it only for port 0)
        result.Success.Should().BeTrue(result.Error);
        result.CdpPort.Should().Be(port);
        result.WindowHandle.Should().BeNull();
        result.LaunchStatus.Should().Be(LaunchStatus.TimedOut);
        result.Warning.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AnExplicitPortInUse_IsPortInUse_AndNothingIsStarted()
    {
        // Arrange
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // Act
            var result = await CreateSut().LaunchBrowserWithDebugAsync(Request(port: port));

            // Assert
            result.ErrorCode.Should().Be(nameof(BrowserErrorCode.PortInUse));
            result.Error.Should().Contain($"Port {port} is already in use").And.Contain("Nothing was launched");
            VerifyNothingStarted();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ALaunchWindowsRefused_KeepsItsErrorCodeAndWarning()
    {
        // Arrange - the browser was deleted between the lookup and the launch's own check
        _validation.Setup(v => v.ValidateExecutable(It.IsAny<string>()))
            .Returns<string>(exe => new ExecutableValidationResult(true, SanitizedExecutable: exe, Warning: "careful"));
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(exe, LaunchTargetKind.NativeExecutable, NotFound: true));

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.ExecutableNotFound));
        result.Warning.Should().Be("careful");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task TheReadinessHook_GetsTheStartedBrowsersEndpoint_AndItsAnswerDecides()
    {
        // Arrange
        BrowserReportsPort("50123\n");
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserReadiness(Ready: false, Error: "/json/version did not answer within 10 seconds."));

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request());

        // Assert
        readiness.Verify(r => r.WaitUntilReadyAsync(new DebugBrowserEndpoint("chrome", BrowserProcessId, 50123, _profile),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().StartWith("/json/version did not answer").And.Contain("still running");
        result.ProcessId.Should().Be(BrowserProcessId);
        result.CdpPort.Should().Be(50123);
        result.CdpReady.Should().BeFalse();
        result.CdpEndpoint.Should().BeNull();
    }

    [Fact]
    public async Task AReadyEndpoint_IsReported_WithTheBrowserVersionAndItsEndpoints()
    {
        // Arrange
        BrowserReportsPort("50123\n");
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserReadiness(Ready: true, BrowserVersion: "Chrome/141.0.7390.55",
                CdpEndpoint: "http://127.0.0.1:50123", BrowserWebSocketUrl: "ws://127.0.0.1:50123/devtools/browser/0a1b"));

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.CdpReady.Should().BeTrue();
        result.CdpPort.Should().Be(50123);
        result.BrowserVersion.Should().Be("Chrome/141.0.7390.55");
        result.CdpEndpoint.Should().Be("http://127.0.0.1:50123");
        result.BrowserWsEndpoint.Should().Be("ws://127.0.0.1:50123/devtools/browser/0a1b");
        result.ProcessId.Should().Be(BrowserProcessId);
    }

    [Fact]
    public async Task TheReadinessCheck_IsBoundedByTheReadinessTimeout()
    {
        // Arrange
        BrowserReportsPort("50123\n");
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserReadiness(Ready: true));

        // Act
        await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request());

        // Assert - about 10 seconds by default, as the port file's wait
        readiness.Verify(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), TimeSpan.FromSeconds(10), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AnExplicitPort_IsCheckedForReadiness_Too()
    {
        // Arrange - the browser writes no port file for an explicit port to be read; the endpoint is the requested port
        var port = FreeLoopbackPort();
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserReadiness(Ready: false, Error: "Port belongs to process 999."));

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request(port));

        // Assert
        readiness.Verify(r => r.WaitUntilReadyAsync(new DebugBrowserEndpoint("chrome", BrowserProcessId, port, _profile),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Success.Should().BeFalse();
        result.CdpReady.Should().BeFalse();
        result.Error.Should().Contain("process 999").And.Contain($"processId {BrowserProcessId}) is still running");
    }

    public static TheoryData<Exception> HookFailures() => new()
    {
        new HttpRequestException("No connection could be made because the target machine actively refused it."),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.")
    };

    [Theory]
    [MemberData(nameof(HookFailures))]
    public async Task AReadinessHookThatThrows_IsANotReadyAnswer_ThatStillNamesTheRunningBrowser(Exception failure)
    {
        // Arrange - the hook's contract is to report, not throw; its own timeout is not the caller's cancellation
        BrowserReportsPort("50123\n");
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain(failure.Message).And.Contain("still running");
        result.ProcessId.Should().Be(BrowserProcessId);
        result.WindowHandle.Should().Be(BrowserWindow.Handle);
        result.CdpPort.Should().Be(50123);
        result.UserDataDir.Should().Be(_profile);
    }

    [Fact]
    public async Task TheCallersCancellation_DuringTheReadinessCheck_IsThrown()
    {
        // Arrange
        BrowserReportsPort("50123\n");
        using var cancel = new CancellationTokenSource();
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns<DebugBrowserEndpoint, TimeSpan, CancellationToken>((_, _, token) =>
            {
                cancel.Cancel();
                return Task.FromCanceled<DebugBrowserReadiness>(token);
            });

        // Act
        var act = () => CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request(), cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ABrowserThatExitedBeforeItsEndpointAnswered_IsNotNamed()
    {
        // Arrange
        BrowserReportsPort("50123\n");
        _browserStartTimes.Enqueue(BrowserStart);
        _browserStartTimes.Enqueue(null);
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserReadiness(Ready: false, Error: "/json/version did not answer within 10 seconds."));

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.Error.Should().StartWith("/json/version did not answer").And.Contain("no longer running");
        result.ProcessId.Should().BeNull();
        result.WindowHandle.Should().BeNull();
        result.CdpPort.Should().BeNull();
    }

    [Fact]
    public async Task TheReadinessHook_IsNotAsked_WhenThePortIsNotKnown()
    {
        // Arrange - the browser never writes DevToolsActivePort
        var readiness = new Mock<IDebugBrowserReadiness>(MockBehavior.Strict);

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request());

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        readiness.VerifyNoOtherCalls();
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
