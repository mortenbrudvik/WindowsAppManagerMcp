using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// DebugBrowserLifecycle (roadmap M5.4): named profiles, reuseIfRunning, the browsers the server started, and
/// close_debug_browser's identity checks and close. The processes, their command lines, the port lookup and CDP are fakes,
/// so nothing is started, closed or connected to; the profile roots are real folders under %TEMP% that each test makes
/// and removes. The user's own browser is modelled as a chrome.exe without a debug port, or with one on a profile that is
/// not the server's: it must never be closed.
/// </summary>
public sealed class DebugBrowserLifecycleTests : IDisposable
{
    private const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const int DebugPid = 4321;
    private const int UsersPid = 777;
    private const int Port = 50123;

    private static readonly DateTime DebugStart = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wam-m54-lifecycle-{Guid.NewGuid():N}");
    private readonly DebugBrowserProfiles _profiles;
    private readonly Mock<IProcessService> _processes = new();
    private readonly Mock<INativeProcessWrapper> _native = new();
    private readonly Mock<IDebugPortLocator> _ports = new();
    private readonly Mock<ICdpService> _cdp = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<ITargetPolicy> _policy = new();
    private readonly Mock<IHostGuard> _host = new();
    private readonly Dictionary<int, (int Parent, string Image)> _processList = new();
    private readonly Dictionary<int, string?> _commandLines = new();
    private readonly Dictionary<int, DateTime?> _startTimes = new();
    private readonly Dictionary<int, Mock<IProcessHandle>> _handles = new();
    private readonly List<(int ProcessId, string ImageName)> _descendants = [];
    private int? _portOwner = DebugPid;
    private int _gateEntries;

    public DebugBrowserLifecycleTests()
    {
        _profiles = new DebugBrowserProfiles(Path.Combine(_root, "named"), Path.Combine(_root, "temp"));
        _native.Setup(n => n.GetProcessList()).Returns(() => _processList.ToDictionary(p => p.Key, p => p.Value));
        _native.Setup(n => n.GetProcessCommandLine(It.IsAny<int>())).Returns((int pid) => _commandLines.GetValueOrDefault(pid));
        _native.Setup(n => n.GetProcessStartTime(It.IsAny<int>())).Returns((int pid) => _startTimes.GetValueOrDefault(pid));
        _native.Setup(n => n.OpenProcess(It.IsAny<int>())).Returns((int pid) => _handles.TryGetValue(pid, out var h) ? h.Object : null);
        _native.Setup(n => n.GetDescendantsOf(It.IsAny<int>())).Returns(() => _descendants.ToList());
        _ports.Setup(p => p.Find(It.IsAny<int>())).Returns((int pid) => new DebugPortLookup(DebugPortLookupStatus.Found, Port, $"process {pid} listens on {Port}"));
        _cdp.Setup(c => c.GetBrowserVersionAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int port, TimeSpan _, CancellationToken _) => CdpResult<CdpBrowserVersion>.Ok(
                new CdpBrowserVersion("Chrome/155.0.0.0", "1.3", new Uri($"ws://127.0.0.1:{port}/devtools/browser/B1"), port)));
        _cdp.Setup(c => c.CloseBrowserAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<bool>.Ok(true));
        _windows.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([]);
        _policy.Setup(p => p.Evaluate(It.IsAny<IReadOnlyCollection<string?>>())).Returns(TargetPolicyVerdict.Allow);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private DebugBrowserLifecycle CreateSut() => new(
        _processes.Object, _native.Object, _ports.Object, new FakePortOwnerResolver(_portOwner), _cdp.Object, _windows.Object,
        _profiles, _policy.Object, _host.Object);

    private string TemporaryProfile()
    {
        var folder = _profiles.NewTemporaryPath();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Cookies"), "session");
        return folder;
    }

    private static string CommandLine(string? profile, bool debugPort = true, string? type = null) =>
        $"\"{ChromeExe}\"" +
        (type != null ? $" --type={type}" : string.Empty) +
        (debugPort ? " --remote-debugging-port=0" : string.Empty) +
        (profile != null ? $" --user-data-dir=\"{profile}\"" : string.Empty) +
        " --no-first-run about:blank";

    /// <summary>A running process: its image, command line, start time and a handle that can be waited for and ended.</summary>
    private Mock<IProcessHandle> Running(int pid, string? commandLine, string image = "chrome.exe", DateTime? start = null, bool exitsWhenClosed = true)
    {
        _processList[pid] = (1, image);
        _commandLines[pid] = commandLine;
        _startTimes[pid] = start ?? DebugStart;
        var handle = new Mock<IProcessHandle>();
        var exited = false;
        handle.SetupGet(h => h.Id).Returns(pid);
        handle.SetupGet(h => h.HasExited).Returns(() => exited);
        handle.Setup(h => h.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => exited);
        handle.Setup(h => h.Kill(It.IsAny<bool>())).Callback(() => Exit(pid, () => exited = true));
        _handles[pid] = handle;
        if (exitsWhenClosed)
        {
            _cdp.Setup(c => c.CloseBrowserAsync(It.IsAny<int>(), pid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback(() => Exit(pid, () => exited = true))
                .ReturnsAsync(CdpResult<bool>.Ok(true));
        }

        return handle;
    }

    private void Exit(int pid, Action markExited)
    {
        markExited();
        _processList.Remove(pid);
        _startTimes[pid] = null;
    }

    private Task<IDisposable> EnterGate(CancellationToken _)
    {
        _gateEntries++;
        return Task.FromResult<IDisposable>(new NoopDisposable());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static FileStream HoldLock(string profile)
    {
        Directory.CreateDirectory(profile);
        return new FileStream(Path.Combine(profile, ChromiumProfileLock.FileName), FileMode.Create, FileAccess.Write, FileShare.Read, 1,
            FileOptions.DeleteOnClose);
    }

    private void LaunchReturns(DebugBrowserLaunchResult result) =>
        _processes.Setup(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private static DebugBrowserLaunchResult Started(int pid, string profile) =>
        new DebugBrowserLaunchResult(true, pid, 0x5678, Port, profile, null, null, LaunchStatus: LaunchStatus.WindowObserved, CdpReady: true)
        {
            StartTime = DebugStart
        };

    #region Launch: named profiles and reuseIfRunning

    [Fact]
    public async Task ANamedProfile_IsLaunchedFromItsFolderUnderTheServersRoot()
    {
        // Arrange
        DebugBrowserLaunchRequest? asked = null;
        _processes.Setup(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DebugBrowserLaunchRequest r, CancellationToken _) => asked = r)
            .ReturnsAsync(Started(DebugPid, Path.Combine(_profiles.NamedRoot, "work")));

        // Act
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome) { ProfileName = "work" });

        // Assert
        result.Success.Should().BeTrue();
        asked!.UserDataDir.Should().Be(Path.Combine(_profiles.NamedRoot, "work"));
        Directory.Exists(asked.UserDataDir).Should().BeTrue("the folder is made before the browser starts");
    }

    [Theory]
    [InlineData(@"..\User Data")]
    [InlineData("CON")]
    [InlineData("")]
    public async Task ABadProfileName_IsRefused_AndNothingIsLaunched(string name)
    {
        // Act
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome) { ProfileName = name });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AProfileAndAUserDataDir_AreRefused_AndNothingIsLaunched()
    {
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: @"C:\Profiles\x") { ProfileName = "work" });

        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReuseIfRunning_WithANewTemporaryProfile_IsRefused()
    {
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome) { ReuseIfRunning = true });

        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Error.Should().Contain("reuseIfRunning needs profile or userDataDir");
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReuseIfRunning_ReturnsTheDebugBrowserRunningWithTheProfile_FoundByItsCommandLineAndPort()
    {
        // Arrange - the debug browser, its renderer (the same profile, with --type) and the user's own Chrome
        var profile = Path.Combine(_profiles.NamedRoot, "work");
        using var held = HoldLock(profile);
        Running(DebugPid, CommandLine(profile));
        Running(DebugPid + 1, CommandLine(profile, type: "renderer"));
        Running(UsersPid, CommandLine(null, debugPort: false));
        var window = TestDataFactory.CreateWindowInfo(handleValue: 0x5678, title: "Inbox - Google Chrome", processName: "chrome", processId: DebugPid);
        _windows.Setup(w => w.FindWindows(It.Is<WindowQuery>(q => q.ProcessId == DebugPid))).Returns([window]);

        // Act
        var result = await CreateSut().LaunchAsync(
            new DebugBrowserLaunchRequest(DebugBrowser.Chrome, Url: "https://example.com/") { ProfileName = "work", ReuseIfRunning = true });

        // Assert
        result.Success.Should().BeTrue();
        result.LaunchStatus.Should().Be(LaunchStatus.AlreadyRunning);
        result.ProcessId.Should().Be(DebugPid);
        result.WindowHandle.Should().Be(window.Handle);
        result.CdpPort.Should().Be(Port);
        result.CdpReady.Should().BeTrue();
        result.CdpEndpoint.Should().Be($"http://127.0.0.1:{Port}");
        result.BrowserWsEndpoint.Should().Be($"ws://127.0.0.1:{Port}/devtools/browser/B1");
        result.UserDataDir.Should().Be(profile);
        result.Warning.Should().Contain("url was not opened");
        _processes.Verify(p => p.LaunchBrowserWithDebugAsync(It.IsAny<DebugBrowserLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _ports.Verify(p => p.Find(DebugPid), Times.Once);
    }

    [Fact]
    public async Task ReuseIfRunning_ABrowserWithoutADebugPort_IsNotReused_NorNamed()
    {
        // Arrange - the user's own browser runs with the folder the agent passed
        var profile = Path.Combine(_root, "MyProfile");
        using var held = HoldLock(profile);
        Running(UsersPid, CommandLine(profile, debugPort: false));

        // Act
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile) { ReuseIfRunning = true });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.ProcessId.Should().BeNull("it may be the user's browser: no PID that invites closing it");
        result.Error.Should().Contain("without a DevTools port");
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReuseIfRunning_ADebugBrowserWhosePortIsNotItsOwn_FailsWithItsProcessId()
    {
        // Arrange
        var profile = Path.Combine(_profiles.NamedRoot, "work");
        using var held = HoldLock(profile);
        Running(DebugPid, CommandLine(profile));
        _ports.Setup(p => p.Find(DebugPid)).Returns(new DebugPortLookup(DebugPortLookupStatus.HeldByOtherProcess, null, "process 99 listens on it."));

        // Act
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome) { ProfileName = "work", ReuseIfRunning = true });

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain("process 99 listens on it").And.Contain("close_debug_browser");
        result.ProcessId.Should().Be(DebugPid);
        _cdp.Verify(c => c.GetBrowserVersionAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReuseIfRunning_AnotherBrowsersExe_IsNotReused()
    {
        var profile = Path.Combine(_profiles.NamedRoot, "work");
        using var held = HoldLock(profile);
        Running(DebugPid, CommandLine(profile), image: "msedge.exe");

        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome) { ProfileName = "work", ReuseIfRunning = true });

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().Contain("msedge.exe").And.Contain("not chrome.exe");
    }

    [Fact]
    public async Task ReuseIfRunning_WhenNoBrowserHoldsTheProfile_Launches()
    {
        // Arrange - no lockfile: no process list is read
        LaunchReturns(Started(DebugPid, Path.Combine(_profiles.NamedRoot, "work")));

        // Act
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome) { ProfileName = "work", ReuseIfRunning = true });

        // Assert
        result.LaunchStatus.Should().Be(LaunchStatus.WindowObserved);
        _native.Verify(n => n.GetProcessList(), Times.Never);
    }

    [Fact]
    public async Task ALaunchWithANewTemporaryProfile_SweepsWhatEarlierBrowsersLeft_ButNotTheProfileOfOneItStarted()
    {
        // Arrange - an old profile of a browser this server started and that still runs (its lockfile is not held in this
        // fake), and an old one nobody uses
        var inUse = TemporaryProfile();
        var left = TemporaryProfile();
        Directory.SetCreationTimeUtc(inUse, DateTime.UtcNow - TimeSpan.FromHours(2));
        Directory.SetCreationTimeUtc(left, DateTime.UtcNow - TimeSpan.FromHours(2));
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, inUse));
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: inUse));
        Running(DebugPid, CommandLine(inUse));
        LaunchReturns(Started(DebugPid + 10, _profiles.NewTemporaryPath()));

        // Act
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome));

        // Assert
        Directory.Exists(left).Should().BeFalse();
        Directory.Exists(inUse).Should().BeTrue();
    }

    [Fact]
    public async Task ALaunchWithANewTemporaryProfile_SweepsTheProfileOfABrowserItStartedThatHasExited()
    {
        // Arrange - a browser this server started, closed by hand (or crashed, or ended with kill_process) without
        // close_debug_browser: its record must not keep its folder for the rest of the server's run
        var exited = TemporaryProfile();
        Directory.SetCreationTimeUtc(exited, DateTime.UtcNow - TimeSpan.FromHours(2));
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, exited));
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: exited));
        LaunchReturns(Started(DebugPid + 10, _profiles.NewTemporaryPath()));

        // Act
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome));

        // Assert
        Directory.Exists(exited).Should().BeFalse();
    }

    [Fact]
    public async Task ReuseIfRunning_ABrowserThePolicyDenies_IsRefused_WithoutItsProcessIdOrPort()
    {
        // Arrange - a port the server finds by itself is the target policy's to refuse (roadmap M5.3)
        var profile = Path.Combine(_profiles.NamedRoot, "work");
        using var held = HoldLock(profile);
        Running(DebugPid, CommandLine(profile));
        _policy.Setup(p => p.Evaluate(It.IsAny<IReadOnlyCollection<string?>>())).Returns(TargetPolicyVerdict.Deny("chrome", "chrome is on the deny list"));

        // Act
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome) { ProfileName = "work", ReuseIfRunning = true });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("chrome is on the deny list").And.Contain("Nothing was launched");
        result.ProcessId.Should().BeNull();
        result.CdpPort.Should().BeNull();
        result.BrowserWsEndpoint.Should().BeNull();
        _ports.Verify(p => p.Find(It.IsAny<int>()), Times.Never);
        _processes.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReuseIfRunning_AnUnusableDebugBrowserThisServerDidNotStart_OnAUserDataDir_IsNotNamedForClosing(bool portFound)
    {
        // Arrange - the user's own debug browser on a folder of theirs: close_debug_browser would refuse it
        var profile = Path.Combine(_root, "MyProfile");
        using var held = HoldLock(profile);
        Running(UsersPid, CommandLine(profile));
        if (portFound)
        {
            _cdp.Setup(c => c.GetBrowserVersionAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CdpResult<CdpBrowserVersion>.Fail(BrowserErrorCode.CdpTimeout, "no answer"));
        }
        else
        {
            _ports.Setup(p => p.Find(UsersPid)).Returns(new DebugPortLookup(DebugPortLookupStatus.HeldByOtherProcess, null, "process 99 listens on it."));
        }

        // Act
        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile) { ReuseIfRunning = true });

        // Assert
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DebugLaunchFailed));
        result.Error.Should().NotContain("Close it with close_debug_browser").And.Contain("ask the user to close it");
        result.ProcessId.Should().BeNull("it may be the user's browser: no PID that invites closing it");
    }

    [Fact]
    public async Task ReuseIfRunning_AnUnusableDebugBrowserThisServerStarted_OnAUserDataDir_IsNamedForClosing()
    {
        // Arrange
        var profile = Path.Combine(_root, "MyProfile");
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, profile));
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile));
        using var held = HoldLock(profile);
        Running(DebugPid, CommandLine(profile));
        _ports.Setup(p => p.Find(DebugPid)).Returns(new DebugPortLookup(DebugPortLookupStatus.HeldByOtherProcess, null, "process 99 listens on it."));

        // Act
        var result = await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile) { ReuseIfRunning = true });

        // Assert
        result.Error.Should().Contain("Close it with close_debug_browser");
        result.ProcessId.Should().Be(DebugPid);
    }

    [Fact]
    public async Task ReuseIfRunning_ADebugBrowserThisServerDidNotStart_OnAUserDataDir_IsReturned_WithAWarningThatItMayBeTheUsers()
    {
        var profile = Path.Combine(_root, "MyProfile");
        using var held = HoldLock(profile);
        Running(UsersPid, CommandLine(profile));

        var result = await CreateSut().LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile) { ReuseIfRunning = true });

        result.LaunchStatus.Should().Be(LaunchStatus.AlreadyRunning);
        result.Warning.Should().Contain("not started by this server").And.Contain("close_debug_browser does not close it");
    }

    [Fact]
    public async Task Abandon_EndsTheBrowserTheServerStarted_AndDeletesItsTemporaryProfile()
    {
        // Arrange - a launch whose call was cancelled after it returned (during the placement): no result names it
        var profile = TemporaryProfile();
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, profile));
        var launched = await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile));
        var browser = Running(DebugPid, CommandLine(profile));

        // Act
        await sut.AbandonAsync(launched);

        // Assert - the browser process alone, never its process tree: close_debug_browser's rule, since a tree holds what
        // the browser opened (a download's program, the user's own browser that a link started)
        browser.Verify(h => h.Kill(false), Times.Once);
        browser.Verify(h => h.Kill(true), Times.Never);
        Directory.Exists(profile).Should().BeFalse();
    }

    [Fact]
    public async Task Abandon_EndsWhatIsLeftOfTheBrowsersOwnProcesses_NeverAProgramItOpened()
    {
        // Arrange - a renderer that outlives the browser, a program a download opened, and the user's own browser that a
        // link started (the same chrome.exe, a descendant, but on the user's profile)
        var profile = TemporaryProfile();
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, profile));
        var launched = await sut.LaunchAsync(new DebugBrowserLaunchRequest(UserDataDir: profile));
        var browser = Running(DebugPid, CommandLine(profile));
        var helper = Running(DebugPid + 1, CommandLine(profile, type: "renderer"), start: DebugStart.AddSeconds(1));
        var opened = Running(9000, "\"C:\\Windows\\notepad.exe\" C:\\Users\\me\\Downloads\\a.txt", image: "notepad.exe");
        var users = Running(UsersPid, CommandLine(null, debugPort: false), start: DebugStart.AddMinutes(1));
        _descendants.AddRange([(DebugPid + 1, "chrome.exe"), (9000, "notepad.exe"), (UsersPid, "chrome.exe")]);

        // Act
        await sut.AbandonAsync(launched);

        // Assert - what close_debug_browser would end, and only that
        browser.Verify(h => h.Kill(false), Times.Once);
        helper.Verify(h => h.Kill(false), Times.Once);
        helper.Verify(h => h.Kill(true), Times.Never);
        opened.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        users.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Abandon_APidThatNowBelongsToAnotherProcess_EndsNothing()
    {
        var profile = TemporaryProfile();
        var other = Running(DebugPid, CommandLine(profile), start: DebugStart.AddMinutes(1));

        await CreateSut().AbandonAsync(Started(DebugPid, profile));

        other.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Abandon_AReusedBrowser_EndsNothing_AndKeepsItsProfile()
    {
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile));

        await CreateSut().AbandonAsync(Started(DebugPid, profile) with { LaunchStatus = LaunchStatus.AlreadyRunning });

        browser.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        Directory.Exists(profile).Should().BeTrue();
    }

    #endregion

    #region close_debug_browser: which browsers may be closed

    [Fact]
    public void CheckClose_ABrowserTheServerStarted_IsAccepted_WithItsHelpers()
    {
        // Arrange
        var profile = TemporaryProfile();
        Running(DebugPid, CommandLine(profile));
        Running(DebugPid + 1, CommandLine(profile, type: "renderer"), start: DebugStart.AddSeconds(1));
        Running(9000, "\"C:\\Windows\\notepad.exe\" C:\\Users\\me\\Downloads\\a.txt");
        _descendants.AddRange([(DebugPid + 1, "chrome.exe"), (9000, "notepad.exe")]);

        // Act
        var check = CreateSut().CheckClose(DebugPid, null, deleteProfile: false);

        // Assert
        check.Refusal.Should().BeNull();
        var target = check.Target!;
        target.ProcessId.Should().Be(DebugPid);
        target.ProcessName.Should().Be("chrome");
        target.UserDataDir.Should().Be(profile);
        target.ProfileKind.Should().Be(DebugProfileKind.Temporary);
        target.DeletesProfile.Should().BeTrue("a temporary profile is always deleted");
        target.Helpers.Should().Equal([(DebugPid + 1, (DateTime?)DebugStart.AddSeconds(1))], "only the browser's own processes, never a program it opened");
    }

    [Fact]
    public void CheckClose_TheUsersOwnBrowser_ThatTheDebugBrowserStarted_IsNotAHelper()
    {
        // Arrange - a link or a download in the debug browser started the default browser (the same chrome.exe) while the
        // user's was not running: it is the user's main browser on their default profile, a child of the debug browser
        var profile = TemporaryProfile();
        Running(DebugPid, CommandLine(profile));
        Running(DebugPid + 1, CommandLine(profile, type: "renderer"), start: DebugStart.AddSeconds(1));
        Running(UsersPid, CommandLine(null, debugPort: false), start: DebugStart.AddMinutes(1));
        Running(UsersPid + 1, CommandLine(null, debugPort: false, type: "renderer"), start: DebugStart.AddMinutes(1));
        Running(UsersPid + 2, CommandLine(@"D:\Profiles\Other", type: "renderer"), start: DebugStart.AddMinutes(1));
        Running(UsersPid + 3, null, start: DebugStart.AddMinutes(1));
        _descendants.AddRange([(DebugPid + 1, "chrome.exe"), (UsersPid, "chrome.exe"), (UsersPid + 1, "chrome.exe"),
            (UsersPid + 2, "chrome.exe"), (UsersPid + 3, "chrome.exe")]);

        // Act
        var check = CreateSut().CheckClose(DebugPid, null, deleteProfile: false);

        // Assert - only a child process (--type) with the browser's own --user-data-dir
        check.Target!.Helpers.Should().Equal([(DebugPid + 1, (DateTime?)DebugStart.AddSeconds(1))]);
    }

    [Fact]
    public void CheckClose_TheUsersBrowser_WithoutADebugPort_IsNotADebugBrowser()
    {
        Running(UsersPid, CommandLine(@"C:\Users\me\AppData\Local\Google\Chrome\User Data", debugPort: false));

        var check = CreateSut().CheckClose(UsersPid, null, false);

        check.Target.Should().BeNull();
        check.Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
        check.Refusal.Error.Should().Contain("may be the user's own browser");
    }

    [Fact]
    public void CheckClose_ADebugBrowserTheServerDidNotStart_WithAProfileOutsideItsFolders_IsNotADebugBrowser()
    {
        // The user's own debug browser (they started it with a port and a folder of their own)
        Running(UsersPid, CommandLine(@"D:\Profiles\Testing"));

        var check = CreateSut().CheckClose(UsersPid, null, false);

        check.Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
        check.Refusal.Error.Should().Contain("this server did not start it").And.Contain("ask the user");
    }

    [Fact]
    public void CheckClose_ADebugBrowserWithAServerProfile_IsAccepted_WithoutBeingTracked()
    {
        // A browser a previous run of the server started (or a launch whose call was refused before it returned)
        Running(DebugPid, CommandLine(Path.Combine(_profiles.NamedRoot, "work")));

        var check = CreateSut().CheckClose(DebugPid, null, deleteProfile: true);

        check.Target!.StartedByServer.Should().BeFalse();
        check.Target.ProfileKind.Should().Be(DebugProfileKind.Named);
        check.Target.DeletesProfile.Should().BeTrue("deleteProfile asks for a named profile's delete");
    }

    [Fact]
    public void CheckClose_ANamedProfile_IsKept_WithoutDeleteProfile()
    {
        Running(DebugPid, CommandLine(Path.Combine(_profiles.NamedRoot, "work")));

        CreateSut().CheckClose(DebugPid, null, deleteProfile: false).Target!.DeletesProfile.Should().BeFalse();
    }

    [Fact]
    public async Task CheckClose_ABrowserTheServerStartedWithAUserDataDir_IsAccepted_ButItsFolderIsNeverDeleted()
    {
        // Arrange
        var profile = Path.Combine(_root, "MyProfile");
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, profile));
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile));
        Running(DebugPid, CommandLine(profile));

        // Act
        var check = sut.CheckClose(DebugPid, null, deleteProfile: true);

        // Assert
        check.Target!.StartedByServer.Should().BeTrue();
        check.Target.ProfileKind.Should().Be(DebugProfileKind.Custom);
        check.Target.DeletesProfile.Should().BeFalse();
        check.Target.Warning.Should().Contain("deleteProfile does not apply");
    }

    [Fact]
    public async Task CheckClose_APidTheServerStartedThatNowBelongsToAnotherProcess_IsNotTrusted()
    {
        // Arrange - the server's browser exited; the user's own debug browser got its PID
        var profile = Path.Combine(_root, "MyProfile");
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, profile));
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile));
        Running(DebugPid, CommandLine(profile), start: DebugStart.AddMinutes(5));

        // Act
        var check = sut.CheckClose(DebugPid, null, false);

        // Assert
        check.Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
    }

    [Fact]
    public async Task CheckClose_ABrowserTheServerStarted_ThatNoLongerNamesItsProfile_IsNotADebugBrowser()
    {
        var profile = TemporaryProfile();
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, profile));
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile));
        Running(DebugPid, CommandLine(_profiles.NewTemporaryPath()));

        var check = sut.CheckClose(DebugPid, null, false);

        check.Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
        check.Refusal.Error.Should().Contain("no longer runs with the profile");
    }

    [Theory]
    [InlineData("renderer")]
    [InlineData("gpu-process")]
    public void CheckClose_AHelperProcess_IsNotTheBrowser(string type)
    {
        Running(DebugPid + 1, CommandLine(TemporaryProfile(), type: type));

        var check = CreateSut().CheckClose(DebugPid + 1, null, false);

        check.Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
        check.Refusal.Error.Should().Contain("helper process");
    }

    [Fact]
    public void CheckClose_AProcessThatIsNotABrowser_IsNotADebugBrowser()
    {
        Running(9000, $"\"C:\\tools\\fake.exe\" --remote-debugging-port=0 --user-data-dir=\"{TemporaryProfile()}\"", image: "fake.exe");

        CreateSut().CheckClose(9000, null, false).Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
    }

    [Fact]
    public void CheckClose_AnUnreadableCommandLine_IsNotADebugBrowser()
    {
        Running(DebugPid, null);

        CreateSut().CheckClose(DebugPid, null, false).Refusal!.Error.Should().Contain("could not be read");
    }

    [Fact]
    public void CheckClose_AProcessThatIsNotRunning_IsProcessNotFound()
    {
        CreateSut().CheckClose(DebugPid, null, false).Refusal!.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNotFound));
    }

    [Fact]
    public void CheckClose_ByCdpPort_ClosesTheBrowserListeningOnIt()
    {
        Running(DebugPid, CommandLine(TemporaryProfile()));

        CreateSut().CheckClose(null, Port, false).Target!.ProcessId.Should().Be(DebugPid);
    }

    [Fact]
    public void CheckClose_AProcessIdAndAPortOfAnotherProcess_AreRefused()
    {
        Running(DebugPid, CommandLine(TemporaryProfile()));
        _portOwner = UsersPid;

        var check = CreateSut().CheckClose(DebugPid, Port, false);

        check.Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
        check.Refusal.Error.Should().Contain($"belongs to process {UsersPid}");
    }

    [Fact]
    public void CheckClose_APortNothingListensOn_IsProcessNotFound()
    {
        _portOwner = null;

        CreateSut().CheckClose(null, Port, false).Refusal!.ErrorCode.Should().Be(nameof(ProcessErrorCode.ProcessNotFound));
    }

    [Fact]
    public void CheckClose_ABrowserThePolicyDenies_IsRefused()
    {
        Running(DebugPid, CommandLine(TemporaryProfile()));
        _policy.Setup(p => p.Evaluate(It.IsAny<IReadOnlyCollection<string?>>())).Returns(TargetPolicyVerdict.Deny("chrome", "chrome is on the deny list"));

        CreateSut().CheckClose(DebugPid, null, false).Refusal!.ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
    }

    [Fact]
    public void CheckClose_TheAgentsHost_IsRefused()
    {
        Running(DebugPid, CommandLine(TemporaryProfile()));
        _host.Setup(h => h.IsHostProcess(DebugPid)).Returns(true);

        CreateSut().CheckClose(DebugPid, null, false).Refusal!.ErrorCode.Should().Be(nameof(ProcessErrorCode.CannotTerminateHost));
    }

    #endregion

    #region close_debug_browser: the close

    [Fact]
    public async Task Close_SendsBrowserClose_InsideTheGate_WaitsForTheExit_AndDeletesTheTemporaryProfile()
    {
        // Arrange
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile));
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(5), EnterGate);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.ClosedGracefully.Should().BeTrue();
        result.Forced.Should().BeFalse();
        result.Exited.Should().BeTrue();
        result.ProfileDeleted.Should().BeTrue();
        result.ProfileKind.Should().Be("temporary");
        Directory.Exists(profile).Should().BeFalse();
        _gateEntries.Should().Be(1);
        _cdp.Verify(c => c.CloseBrowserAsync(Port, DebugPid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        browser.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Close_ABrowserThatDoesNotExitAfterBrowserClose_IsEnded_InsideTheGate()
    {
        // Arrange
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile), exitsWhenClosed: false);
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(1), EnterGate);

        // Assert
        result.Success.Should().BeTrue();
        result.ClosedGracefully.Should().BeTrue();
        result.Forced.Should().BeTrue();
        result.Warning.Should().Contain("did not exit within 1000 ms");
        browser.Verify(h => h.Kill(false), Times.Once, "the browser alone; its own helpers are ended one by one");
        _gateEntries.Should().Be(2);
        Directory.Exists(profile).Should().BeFalse();
    }

    [Fact]
    public async Task Close_WhenBrowserCloseIsSentButNotAnswered_StillWaitsForTheExit_BeforeEndingIt()
    {
        // Arrange - a busy browser got Browser.close but did not answer in time; it exits during the wait
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile), exitsWhenClosed: false);
        _cdp.Setup(c => c.CloseBrowserAsync(It.IsAny<int>(), DebugPid, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CdpResult<bool>.Ok(false));
        var waited = new List<int>();
        browser.Setup(h => h.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int ms, CancellationToken _) =>
            {
                waited.Add(ms);
                browser.SetupGet(h => h.HasExited).Returns(true);
                _processList.Remove(DebugPid);
                _startTimes[DebugPid] = null;
                return true;
            });
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(7), EnterGate);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Forced.Should().BeFalse();
        result.ClosedGracefully.Should().BeTrue("it exited after Browser.close");
        waited.Should().Equal([7000]);
        browser.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        _gateEntries.Should().Be(1);
        Directory.Exists(profile).Should().BeFalse();
    }

    [Fact]
    public async Task Close_WhenTheGateIsRefusedBeforeTheEnd_SaysBrowserCloseWasSent_AndKeepsTheProfile()
    {
        // Arrange - Browser.close was accepted, the browser is still closing, and the user pauses input during the wait
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile), exitsWhenClosed: false);
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;
        var entries = 0;
        Task<IDisposable> PausedTheSecondTime(CancellationToken _) => ++entries == 1
            ? Task.FromResult<IDisposable>(new NoopDisposable())
            : Task.FromException<IDisposable>(new ToolErrorException(nameof(WindowErrorCode.PausedByUser), "Nothing was done: input is paused."));

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(1), PausedTheSecondTime);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Error.Should().Contain("Browser.close was sent").And.NotContain("Nothing was done");
        result.ClosedGracefully.Should().BeTrue();
        result.Forced.Should().BeFalse();
        result.Exited.Should().BeFalse();
        browser.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        Directory.Exists(profile).Should().BeTrue();
    }

    [Fact]
    public async Task Close_WhenTheGateIsRefusedBeforeTheEnd_ButTheBrowserHasExitedMeanwhile_Succeeds()
    {
        // Arrange
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile), exitsWhenClosed: false);
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;
        var entries = 0;
        Task<IDisposable> BusyTheSecondTime(CancellationToken _)
        {
            if (++entries == 1)
                return Task.FromResult<IDisposable>(new NoopDisposable());

            browser.SetupGet(h => h.HasExited).Returns(true);
            return Task.FromException<IDisposable>(new ToolErrorException(nameof(InputErrorCode.InputBusy), "Nothing was done: busy."));
        }

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(1), BusyTheSecondTime);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Exited.Should().BeTrue();
        result.Forced.Should().BeFalse();
        Directory.Exists(profile).Should().BeFalse();
    }

    [Fact]
    public async Task Close_WithoutItsDebugPort_EndsTheBrowser_AndSaysWhy()
    {
        var profile = TemporaryProfile();
        Running(DebugPid, CommandLine(profile));
        _ports.Setup(p => p.Find(DebugPid)).Returns(new DebugPortLookup(DebugPortLookupStatus.NotListening, null, "No listener was found."));
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;

        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(1), EnterGate);

        result.ClosedGracefully.Should().BeFalse();
        result.Forced.Should().BeTrue();
        result.Warning.Should().Contain("without Browser.close").And.Contain("No listener was found.");
        _cdp.Verify(c => c.CloseBrowserAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_EndsTheBrowsersOwnProcessesLeftAfterItExited_OnlyThoseThatKeptTheirStartTime()
    {
        // Arrange - a renderer that keeps running, and a PID of a former helper that now belongs to another process
        var profile = TemporaryProfile();
        Running(DebugPid, CommandLine(profile));
        var helper = Running(DebugPid + 1, CommandLine(profile, type: "renderer"), start: DebugStart.AddSeconds(1), exitsWhenClosed: false);
        var reused = Running(DebugPid + 2, CommandLine(profile, type: "gpu-process"), start: DebugStart.AddSeconds(2), exitsWhenClosed: false);
        _descendants.AddRange([(DebugPid + 1, "chrome.exe"), (DebugPid + 2, "chrome.exe")]);
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;
        _startTimes[DebugPid + 2] = DebugStart.AddMinutes(3);

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(5), EnterGate);

        // Assert
        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("1 of its own processes still ran");
        helper.Verify(h => h.Kill(false), Times.Once);
        reused.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Close_ABrowserWhosePidWentToAnotherProcessMeanwhile_IsNotTouched()
    {
        // Arrange - the browser exited while the user was asked, and another process got its PID
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile));
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;
        _startTimes[DebugPid] = DebugStart.AddMinutes(1);

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(5), EnterGate);

        // Assert
        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("already exited");
        browser.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        _cdp.Verify(c => c.CloseBrowserAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        _gateEntries.Should().Be(0);
        Directory.Exists(profile).Should().BeFalse("the browser that used it is gone");
    }

    [Fact]
    public async Task Close_ABrowserWhoseProfileChangedMeanwhile_IsNotClosed()
    {
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile));
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;
        _commandLines[DebugPid] = CommandLine(@"C:\Users\me\AppData\Local\Google\Chrome\User Data");

        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(5), EnterGate);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
        browser.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        Directory.Exists(profile).Should().BeTrue();
    }

    [Fact]
    public async Task Close_ANamedProfileWithoutDeleteProfile_IsKept()
    {
        var profile = Path.Combine(_profiles.NamedRoot, "work");
        Directory.CreateDirectory(profile);
        Running(DebugPid, CommandLine(profile));
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, deleteProfile: false).Target!;

        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(5), EnterGate);

        result.Success.Should().BeTrue();
        result.ProfileDeleted.Should().BeNull();
        result.ProfileKind.Should().Be("named");
        Directory.Exists(profile).Should().BeTrue();
    }

    [Fact]
    public async Task Close_ABrowserThatCannotBeEnded_FailsAndKeepsItsProfile()
    {
        // Arrange
        var profile = TemporaryProfile();
        var browser = Running(DebugPid, CommandLine(profile), exitsWhenClosed: false);
        browser.Setup(h => h.Kill(It.IsAny<bool>())).Throws(new System.ComponentModel.Win32Exception(5, "Access is denied"));
        var sut = CreateSut();
        var target = sut.CheckClose(DebugPid, null, false).Target!;

        // Act
        var result = await sut.CloseAsync(target, TimeSpan.FromSeconds(1), EnterGate);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TerminationException));
        result.Exited.Should().BeFalse();
        result.Forced.Should().BeFalse("the end was refused");
        Directory.Exists(profile).Should().BeTrue();
    }

    [Fact]
    public async Task Close_ForgetsTheBrowser_SoItsPidIsNotTrustedAgain()
    {
        // Arrange - started by the server with a userDataDir: trusted only while tracked
        var profile = Path.Combine(_root, "MyProfile");
        var sut = CreateSut();
        LaunchReturns(Started(DebugPid, profile));
        await sut.LaunchAsync(new DebugBrowserLaunchRequest(DebugBrowser.Chrome, UserDataDir: profile));
        Running(DebugPid, CommandLine(profile));
        await sut.CloseAsync(sut.CheckClose(DebugPid, null, false).Target!, TimeSpan.FromSeconds(5), EnterGate);

        // Act - the same PID and start time again (a fake that cannot happen on Windows, but the record must be gone)
        Running(DebugPid, CommandLine(profile));
        var check = sut.CheckClose(DebugPid, null, false);

        // Assert
        check.Refusal!.ErrorCode.Should().Be(nameof(BrowserErrorCode.NotADebugBrowser));
    }

    #endregion
}
