using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Review F2: launch_application, launch_applications_batch (and every launch planned the same way) refuse the Chromium and
/// Electron switches that make a program run a command line, load code or open it to remote control
/// (<see cref="ChromiumSwitchGuard"/>), for a program's .exe, a packaged app and a shell:AppsFolder entry alike, and start
/// nothing. launch_browser_with_debug's own --remote-debugging-port still passes. The process seam is a mock: nothing is
/// started.
/// </summary>
public class ChromiumSwitchLaunchTests
{
    private const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";
    private const string Spotify = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify";

    private readonly Mock<IExecutableResolver> _resolver = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Mock<IInstalledAppsService> _installed = new();
    private readonly Mock<INativeAppsWrapper> _apps = new();
    private readonly List<ProcessStartInfo> _started = [];

    public ChromiumSwitchLaunchTests()
    {
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(
                exe.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? Edge
                : exe.Equals("notepad", StringComparison.OrdinalIgnoreCase) ? Notepad : exe,
                exe.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? LaunchTargetKind.Other : LaunchTargetKind.NativeExecutable));
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>())).Callback<ProcessStartInfo>(_started.Add)
            .Returns((IProcessHandle?)null);
        _installed.Setup(i => i.ProgramOf(It.Is<PackagedAppId>(a => a.AppUserModelId == Spotify))).Returns("Spotify.exe");
        _installed.Setup(i => i.FindByAppId("MSEdge")).Returns(new InstalledApp("Microsoft Edge", "MSEdge", false, Edge));
        _installed.Setup(i => i.FindByName(It.IsAny<string>())).Returns([]);
    }

    private ProcessService CreateSut() => new(
        TestDataFactory.CreateMockWindowService().Object,
        new InputValidationService(ProtocolAllowlist.BuiltIn),
        TestDataFactory.CreateMockBrowserDetectionService().Object,
        _resolver.Object,
        _processes.Object,
        appsWrapper: _apps.Object,
        installedApps: _installed.Object);

    private void VerifyNothingStarted()
    {
        _started.Should().BeEmpty();
        _apps.Verify(a => a.ActivateApplication(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData("msedge", "--utility-cmd-prefix=powershell -c calc")]
    [InlineData("msedge", "--gpu-launcher=calc.exe")]
    [InlineData("msedge", "--renderer-cmd-prefix=calc.exe")]
    [InlineData("msedge", "--remote-debugging-port=9222")]
    [InlineData("msedge", @"--load-extension=C:\Temp\ext")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", "--inspect-extensions=9333")]
    [InlineData(@"C:\Users\me\AppData\Local\slack\Update.exe", "--process-start-args=--gpu-launcher=calc.exe")]
    [InlineData("notepad", "--gpu-launcher=calc.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", @"--extensionTestsPath=C:\Temp\tests.js")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe", @"--extensionDevelopmentPath=C:\Temp\ext")]
    [InlineData(@"C:\Users\me\AppData\Local\Discord\Update.exe", @"--process-start-args=""--inspect""")]
    public async Task LaunchApplication_WithASwitchThatRunsACommandOrOpensRemoteControl_StartsNothing(string executable, string argument)
    {
        // Act - a helper or launcher (Squirrel's Update.exe, chrome_proxy.exe) hands its arguments on, so any program counts
        var result = await CreateSut().LaunchApplicationAsync(executable, ["https://example.com/", argument]);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        result.ErrorMessage.Should().Contain("Nothing was launched");
        VerifyNothingStarted();
    }

    [Theory]
    [InlineData(@"""--gpu-launcher=C:\x\e.exe""")]
    [InlineData(@"""--inspect=9229""")]
    [InlineData(@"""--remote-debugging-port=9222""")]
    public async Task LaunchApplication_ALauncherGivenAQuotedSwitchToHandOn_StartsNothing(string handedOn)
    {
        // Act - Squirrel's Update.exe hands --process-start-args to the app as raw text, and the app's command line parser
        // takes the quotes off: the switch must not get through because it holds no white space
        var result = await CreateSut().LaunchApplicationAsync(@"C:\Users\me\AppData\Local\Discord\Update.exe",
            ["--processStart", "Discord.exe", "--process-start-args", handedOn]);

        // Assert
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        result.ErrorMessage.Should().Contain("Nothing was launched");
        VerifyNothingStarted();
    }

    [Fact]
    public void PlanLaunch_TheRealProfileAsUserDataDir_IsRefused()
    {
        var plan = CreateSut().PlanLaunch("msedge",
            ["--remote-debugging-port=9222", @"--user-data-dir=C:\Users\me\AppData\Local\Microsoft\Edge\User Data"]);

        plan.IsValid.Should().BeFalse();
        plan.StartInfo.Should().BeNull();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
    }

    [Fact]
    public void CheckLaunch_RefusesTheSwitch_AsTheLaunchWould()
    {
        // apply_layout and run_steps check a launch with CheckLaunch before anything is started
        CreateSut().CheckLaunch(new LaunchRequest("msedge", ["--gpu-launcher=calc.exe"]))!.ErrorCode
            .Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
    }

    [Fact]
    public void PlanLaunch_OrdinaryBrowserArguments_StillStart()
    {
        var plan = CreateSut().PlanLaunch("msedge", ["--new-window", "--inprivate", "https://example.com/"]);

        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
    }

    [Fact]
    public async Task LaunchApplicationsBatch_RefusesTheItemWithTheSwitch_AndStartsTheOthers()
    {
        // Act
        var results = await CreateSut().LaunchApplicationsBatchAsync(
        [
            new LaunchRequest("notepad", [@"C:\notes\todo.txt"]),
            new LaunchRequest("msedge", ["--remote-allow-origins=*", "--remote-debugging-port=9222"]),
            new LaunchRequest("msedge", ["  /utility-cmd-prefix=calc.exe"])
        ]);

        // Assert
        results[0].Success.Should().BeTrue(results[0].ErrorMessage);
        results[1].ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        results[2].ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        _started.Should().ContainSingle().Which.FileName.Should().Be(Notepad);
    }

    [Fact]
    public void PlanLaunch_APackagedAppGivenTheSwitch_IsRefused()
    {
        // A Store app built on Electron or CEF reads the same switches from its activation's arguments
        var plan = CreateSut().PlanLaunch(Spotify, ["--remote-debugging-port=9222"]);

        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
        CreateSut().PlanLaunch(Spotify, ["spotify:track:1"]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void PlanLaunch_AnAppsFolderEntryGivenTheSwitch_IsRefused()
    {
        var plan = CreateSut().PlanLaunch(@"shell:AppsFolder\MSEdge", ["--gpu-launcher=calc.exe"]);

        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
    }

    [Fact]
    public void PlanLaunch_TheDebugBrowsersOwnArguments_PassOnlyForTheDebugLaunch()
    {
        // launch_browser_with_debug's own start (LaunchRequest.DebugBrowserLaunch) opens the port next to a checked profile
        var arguments = ProcessService.BuildDebugBrowserArguments(0, @"C:\Users\Jane Doe\AppData\Local\Temp\browser-debug-1", "https://example.com/");

        CreateSut().PlanLaunch(Edge, arguments.ToArray(), debugBrowserLaunch: true).IsValid.Should().BeTrue();
        CreateSut().PlanLaunch(Edge, arguments.ToArray()).ErrorCode.Should().Be(nameof(LaunchErrorCode.UnsafeChromiumSwitch));
    }
}
