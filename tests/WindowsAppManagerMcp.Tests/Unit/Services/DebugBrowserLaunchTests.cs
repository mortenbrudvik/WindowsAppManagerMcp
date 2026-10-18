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

    /// <summary>The server's profile folders (roadmap M5.4), under a folder of this test's own.</summary>
    private readonly DebugBrowserProfiles _profiles = new(
        Path.Combine(Path.GetTempPath(), $"wam-m54-roots-{Guid.NewGuid():N}", "named"),
        Path.Combine(Path.GetTempPath(), $"wam-m54-roots-{Guid.NewGuid():N}", "temp"));
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
        foreach (var root in new[] { _profiles.NamedRoot, _profiles.TemporaryRoot })
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

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
        debugBrowserReadiness: readiness,
        debugBrowserProfiles: _profiles)
    {
        DevToolsPortTimeout = TimeSpan.FromMilliseconds(300)
    };

    private DebugBrowserLaunchRequest Request(int port = 0, string? url = null, string? executablePath = null) =>
        new(DebugBrowser.Chrome, url, port, _profile, executablePath);

    private void VerifyNothingStarted() => _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);

    [Fact]
    public async Task UnsupportedBrowser_ReturnsErrorWithoutTouchingProcesses()
    {
        // Act - a value outside the enum (the tool's schema refuses any other name before this)
        var result = await CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest((DebugBrowser)99));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.UnsupportedBrowser));
        result.Error.Should().Contain("chrome, edge, brave");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task EveryBrowserTheToolTakes_IsLaunched_NotUnsupported()
    {
        foreach (var browser in Enum.GetValues<DebugBrowser>())
        {
            // Arrange - the browser's own .exe where App Paths names it
            var exe = $@"C:\Browsers\{browser}\{DebugBrowserLocator.ExecutableName(browser)}";
            var locator = new DebugBrowserLocator(path => path == exe, _ => [exe], FakeFolder);
            BrowserReportsPort("50123\n/devtools/browser/B1");

            // Act
            var result = await CreateSut(locator).LaunchBrowserWithDebugAsync(Request() with { Browser = browser });

            // Assert
            result.ErrorCode.Should().NotBe(nameof(BrowserErrorCode.UnsupportedBrowser), browser.ToString());
            result.Success.Should().BeTrue($"{browser}: {result.Error}");
            _started.Last().FileName.Should().Be(exe);
        }
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

        // Assert - the result has no PID of "that browser": a debug browser is reused or closed through the tools that
        // prove it is one (reuseIfRunning, close_debug_browser, roadmap M5.4), never found by name or title (it may be the
        // user's own browser)
        result.Warning.Should().Be("careful");
        result.ProcessId.Should().BeNull();
        result.Error.Should().NotContain("Close that browser")
            .And.Contain("reuseIfRunning=true").And.Contain("close_debug_browser").And.Contain("never close a browser you did not start");
    }

    [Fact]
    public async Task ABrowserThatExitsAtOnceWithANewTemporaryProfile_IsNotBlamedOnAnotherBrowser()
    {
        // Arrange - no other browser can use a folder this launch just named
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        _browser.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome));

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
        result.Error.Should().Contain("ask the user to close it")
            .And.NotContain("or close it with close_debug_browser", "close_debug_browser refuses a userDataDir browser this server run did not start");
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
        var result = await sut.LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: userDataDir));

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
        readiness.Verify(r => r.WaitUntilReadyAsync(new DebugBrowserEndpoint("chrome", BrowserProcessId, 50123, _profile, BrowserStart),
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
            .ReturnsAsync(new DebugBrowserReadiness(Ready: false, Error: "Port belongs to process 999.",
                Reason: DebugBrowserNotReadyReason.PortHeldByOtherProcess));

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request(port));

        // Assert - the port is the other process's, so the result does not offer it as the browser's: only what closes it
        readiness.Verify(r => r.WaitUntilReadyAsync(new DebugBrowserEndpoint("chrome", BrowserProcessId, port, _profile, BrowserStart),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Success.Should().BeFalse();
        result.CdpReady.Should().BeFalse();
        result.Error.Should().Contain("process 999").And.Contain($"processId {BrowserProcessId}) is still running");
        result.CdpPort.Should().BeNull();
        result.ProcessId.Should().Be(BrowserProcessId);
        result.WindowHandle.Should().Be(BrowserWindow.Handle);
        result.UserDataDir.Should().Be(_profile);
    }

    [Fact]
    public async Task APortNotShownToBeTheBrowsers_IsNotReturned()
    {
        // Arrange - the port's owner could not be read, and the profile did not name the URL that answered
        var port = FreeLoopbackPort();
        var readiness = new Mock<IDebugBrowserReadiness>();
        readiness.Setup(r => r.WaitUntilReadyAsync(It.IsAny<DebugBrowserEndpoint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DebugBrowserReadiness(Ready: false, Error: "The owner of the port could not be read.",
                Reason: DebugBrowserNotReadyReason.PortOwnerUnknown));

        // Act
        var result = await CreateSut(readiness: readiness.Object).LaunchBrowserWithDebugAsync(Request(port));

        // Assert
        result.Success.Should().BeFalse();
        result.CdpReady.Should().BeFalse();
        result.CdpPort.Should().BeNull();
        result.ProcessId.Should().Be(BrowserProcessId);
    }

    [Fact]
    public async Task ABrowserThatExitsDuringTheReadinessWait_FailsAtOnce_AsNoLongerRunning()
    {
        // Arrange - the real probe: the port refuses every request once the browser is gone, and it is not asked again
        BrowserReportsPort("50123\n");
        _browserStartTimes.Enqueue(BrowserStart);   // read after the start
        _browserStartTimes.Enqueue(null);           // the probe's check after the refusal
        _browserStartTimes.Enqueue(null);           // the failure's own check
        var cdp = new Mock<ICdpService>(MockBehavior.Strict);
        cdp.Setup(c => c.GetBrowserVersionAsync(50123, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<CdpBrowserVersion>.Fail(BrowserErrorCode.CdpUnavailable, "Could not connect to CDP on 127.0.0.1:50123."));
        var probe = new DebugBrowserReadinessProbe(cdp.Object, new FakePortOwnerResolver(null), _processes.Object);
        var stopwatch = Stopwatch.StartNew();

        // Act - the readiness wait is 10 seconds
        var result = await CreateSut(readiness: probe).LaunchBrowserWithDebugAsync(Request());

        // Assert
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        cdp.Verify(c => c.GetBrowserVersionAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Success.Should().BeFalse();
        result.CdpReady.Should().BeFalse();
        result.Error.Should().Contain("exited before its DevTools endpoint").And.Contain("no longer running")
            .And.NotContain("launch_browser_with_debug");
        result.ProcessId.Should().BeNull();
        result.CdpPort.Should().BeNull();
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

    #region Roadmap M5.4: temporary profiles, the start time and a cancelled launch

    /// <summary>The --user-data-dir the browser was started with.</summary>
    private string StartedProfile() =>
        ChromiumCommandLine.Parse("chrome.exe " + _started.Last().Arguments)["user-data-dir"];

    /// <summary>The fake browser writes DevToolsActivePort into whatever profile it was started with.</summary>
    private void BrowserReportsPortInItsProfile(string content) =>
        _whenStarted = () => File.WriteAllText(DevToolsActivePortFile.PathIn(StartedProfile()), content);

    private IReadOnlyList<string> TemporaryProfilesLeft() =>
        Directory.Exists(_profiles.TemporaryRoot) ? Directory.EnumerateDirectories(_profiles.TemporaryRoot).ToList() : [];

    /// <summary>The window wait is cancelled by the caller while it waits, after the browser started.</summary>
    private CancellationTokenSource CancelDuringTheWindowWait()
    {
        var cancel = new CancellationTokenSource();
        _windowWait.Setup(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
                It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()))
            .Returns<WindowBaseline, Func<WindowCandidate, bool>, WindowWaitOptions, CancellationToken>((_, _, _, token) =>
            {
                cancel.Cancel();
                return Task.FromCanceled<WindowWaitResult>(token);
            });
        return cancel;
    }

    private void BrowserCanBeOpened() =>
        _processes.Setup(p => p.OpenProcess(BrowserProcessId)).Returns(_browser.Object);

    [Fact]
    public async Task ANewTemporaryProfile_IsAFolderOfTheServers_MadeBeforeTheStart_AndTheResultHasTheStartTime()
    {
        // Arrange
        BrowserReportsPortInItsProfile("50123\n");

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome));

        // Assert - its path is what lets close_debug_browser prove it is the server's, even after a restart
        result.Success.Should().BeTrue(result.Error);
        result.UserDataDir.Should().Be(StartedProfile());
        _profiles.KindOf(result.UserDataDir).Should().Be(DebugProfileKind.Temporary);
        Directory.Exists(result.UserDataDir).Should().BeTrue();
        result.StartTime.Should().Be(BrowserStart);
    }

    [Fact]
    public async Task TheTemporaryProfileOfABrowserThatExitedAtOnce_IsDeleted()
    {
        // Arrange
        WaitFinds(WindowWaitOutcome.TimedOut, null);
        _browser.SetupGet(p => p.HasExited).Returns(true);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome));

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.UserDataDir.Should().BeNull("no browser will use it, and it is gone");
        TemporaryProfilesLeft().Should().BeEmpty();
    }

    [Fact]
    public async Task ACallCancelledDuringTheWindowWait_EndsTheBrowserItStarted_AndDeletesItsTemporaryProfile()
    {
        // Arrange - nobody would learn this browser's PID or port, and its debug port has no authentication
        using var cancel = CancelDuringTheWindowWait();
        BrowserCanBeOpened();
        _browser.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        var act = () => CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome), cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _browser.Verify(p => p.Kill(true), Times.Once);
        TemporaryProfilesLeft().Should().BeEmpty();
    }

    [Fact]
    public async Task ACallCancelledDuringTheReadinessCheck_EndsTheBrowserItStarted_ButKeepsAUserDataDir()
    {
        // Arrange
        BrowserReportsPort("50123\n");
        BrowserCanBeOpened();
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
        _browser.Verify(p => p.Kill(true), Times.Once);
        Directory.Exists(_profile).Should().BeTrue("a userDataDir the agent passed is never deleted");
    }

    [Fact]
    public async Task ACancelledCall_WhoseBrowsersPidNowBelongsToAnotherProcess_EndsNothing()
    {
        // Arrange - the start time read at the start, then another one: the browser exited and its PID was reused
        _browserStartTimes.Enqueue(BrowserStart);
        _browserStartTimes.Enqueue(BrowserStart.AddMinutes(1));
        using var cancel = CancelDuringTheWindowWait();
        BrowserCanBeOpened();

        // Act
        var act = () => CreateSut().LaunchBrowserWithDebugAsync(Request(), cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _browser.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ANamedProfileABrowserIsRunningWith_IsRefused_WithCloseDebugBrowserAsAdvice()
    {
        // Arrange - a named profile is the server's own folder, so close_debug_browser accepts its debug browser
        var named = _profiles.NamedPath("work");
        Directory.CreateDirectory(named);
        using var running = new FileStream(Path.Combine(named, ChromiumProfileLock.FileName), FileMode.Create, FileAccess.Write,
            FileShare.Read, 1, FileOptions.DeleteOnClose);

        // Act
        var result = await CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: named));

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain("or close it with close_debug_browser").And.NotContain("ask the user to close it");
        VerifyNothingStarted();
    }

    [Fact]
    public async Task ACallCancelledWhileTheStartIsStillRunning_EndsTheBrowserWhenTheStartReturns_ThenDeletesItsProfile()
    {
        // Arrange - the start (CreateProcess while Defender scans a freshly updated browser) still runs when the caller
        // cancels: the browser it brings would run with an unauthenticated port that no result names
        using var cancel = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _whenStarted = () =>
        {
            cancel.Cancel();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        _browser.Setup(p => p.Kill(true)).Callback(() => killed.TrySetResult());

        // Act
        var act = () => CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome), cancel.Token);

        // Assert - nothing is deleted while the start may still use the profile
        await act.Should().ThrowAsync<OperationCanceledException>();
        TemporaryProfilesLeft().Should().ContainSingle("the browser may still start with it");
        _browser.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);

        // Then the start returns: its browser is ended, and only then is its profile deleted
        release.Set();
        await killed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (TemporaryProfilesLeft().Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        TemporaryProfilesLeft().Should().BeEmpty();
        _browser.Verify(p => p.Dispose(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ACallCancelledBeforeTheStart_StartsNothing_AndLeavesNoTemporaryProfile()
    {
        // Arrange
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        // Act
        var act = () => CreateSut().LaunchBrowserWithDebugAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome), cancel.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        VerifyNothingStarted();
        TemporaryProfilesLeft().Should().BeEmpty();
    }

    #endregion

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
