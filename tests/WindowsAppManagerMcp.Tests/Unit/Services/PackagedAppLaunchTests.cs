using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Packaged app launch, friendly names and runAsAdmin (roadmap M6.5, owner decision D11): ProcessService plans an
/// AppUserModelID as an activation, activates it through INativeAppsWrapper, finds its window by package family, refuses
/// arguments an interpreter package could run, resolves an installed app's exact display name, and starts an .exe elevated.
/// Desktop safety: the activation, the Applications folder, the process seam and the window wait are all fakes, so no
/// test activates, starts or looks for a real app.
/// </summary>
public class PackagedAppLaunchTests
{
    private const string Calculator = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string CalculatorFamily = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
    private const string CalculatorTarget = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string Notepad = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";
    private const string Ubuntu = "CanonicalGroupLimited.UbuntuonWindows_79rhkp1fndgsc!ubuntuonwindows";
    private const int ActivatedProcessId = 5000;
    private const int StartedProcessId = 4321;

    private readonly Mock<IExecutableResolver> _resolver = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Mock<INativeAppsWrapper> _apps = new();
    private readonly Mock<IInstalledAppsService> _installed = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();
    private readonly Mock<IWindowWaitService> _windowWait = new();
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService();
    private readonly Mock<IProcessHandle> _activated = new();
    private readonly Mock<IProcessHandle> _started = new();
    private readonly List<ProcessStartInfo> _startedWith = [];
    private readonly List<(string AppId, string? Arguments)> _activations = [];
    private readonly List<(Func<WindowCandidate, bool> IsOwner, WindowWaitOptions Options)> _waits = [];

    public PackagedAppLaunchTests()
    {
        // Every bare name resolves to itself as a native .exe, unless a test says otherwise
        _resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((exe, _) => new ResolvedLaunchTarget(exe, LaunchTargetKind.NativeExecutable));

        _activated.SetupGet(p => p.Id).Returns(ActivatedProcessId);
        _started.SetupGet(p => p.Id).Returns(StartedProcessId);
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>()))
            .Callback<ProcessStartInfo>(_startedWith.Add)
            .Returns(_started.Object);
        _processes.Setup(p => p.OpenProcess(ActivatedProcessId)).Returns(_activated.Object);

        _apps.Setup(a => a.ActivateApplication(It.IsAny<string>(), It.IsAny<string?>()))
            .Callback<string, string?>((id, arguments) => _activations.Add((id, arguments)))
            .Returns(new AppActivationResult(0, ActivatedProcessId));

        _installed.Setup(i => i.ProgramOf(It.Is<PackagedAppId>(a => a.AppUserModelId == Calculator))).Returns("CalculatorApp.exe");
        _installed.Setup(i => i.ProgramOf(It.Is<PackagedAppId>(a => a.AppUserModelId == Notepad))).Returns("Notepad.exe");
        _installed.Setup(i => i.ProgramOf(It.Is<PackagedAppId>(a => a.AppUserModelId == Ubuntu))).Returns("ubuntu.exe");
        _installed.Setup(i => i.FindByName(It.IsAny<string>())).Returns([]);

        _security.SetupGet(s => s.ServerIntegrity).Returns(new ProcessIntegrity(Environment.ProcessId, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null));
        _security.Setup(s => s.GetElevationPrompt()).Returns(ElevationPrompt.Prompts);

        _windowWait.Setup(w => w.TakeBaseline()).Returns(new WindowBaseline(new Dictionary<nint, string>(), nint.Zero));
    }

    private ProcessService CreateSut(bool withApps = true, TimeSpan? activationTimeout = null) => new(
        _windows.Object,
        new InputValidationService(ProtocolAllowlist.BuiltIn),
        TestDataFactory.CreateMockBrowserDetectionService().Object,
        _resolver.Object,
        _processes.Object,
        windowWait: _windowWait.Object,
        securityProbe: _security.Object,
        appsWrapper: withApps ? _apps.Object : null,
        installedApps: withApps ? _installed.Object : null)
    {
        ActivationTimeout = activationTimeout ?? ProcessService.DefaultActivationTimeout
    };

    private void WaitFinds(WindowWaitOutcome outcome, WindowInfo? window) =>
        _windowWait.Setup(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
                It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()))
            .Callback<WindowBaseline, Func<WindowCandidate, bool>, WindowWaitOptions, CancellationToken>((_, isOwner, options, _) =>
                _waits.Add((isOwner, options)))
            .ReturnsAsync(new WindowWaitResult(outcome, window, 300));

    private void VerifyNothingStarted()
    {
        _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);
        _apps.Verify(a => a.ActivateApplication(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    private static readonly WindowInfo CalculatorWindow =
        TestDataFactory.CreateWindowInfo(handleValue: 0x700, title: "Kalkulator", processName: "CalculatorApp", processId: ActivatedProcessId);

    #region Planning an AppUserModelID

    [Theory]
    [InlineData(Calculator)]
    [InlineData(CalculatorTarget)]
    public void PlanLaunch_AnAppUserModelId_IsAPackagedApp_WithoutAskingTheResolver(string executable)
    {
        var plan = CreateSut().PlanLaunch(executable);

        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.TargetKind.Should().Be(LaunchTargetKind.PackagedApp);
        plan.Target!.App.Should().Be(new PackagedAppId(CalculatorFamily, "App"));
        plan.StartInfo!.FileName.Should().Be(CalculatorTarget, "the shell fallback starts the same app");
        plan.StartInfo.Arguments.Should().BeEmpty();
        _resolver.Verify(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void PlanLaunch_ArgumentsToAnAppThatIsNoInterpreter_AreQuotedForTheAppAlone()
    {
        // Activation hands the command line to the app itself, so cmd.exe's characters need no refusal
        var plan = CreateSut().PlanLaunch(Notepad, [@"C:\notes\R&D 100%.txt"]);

        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.Arguments.Should().Be("\"C:\\notes\\R&D 100%.txt\"");
    }

    [Fact]
    public void PlanLaunch_ArgumentsToAnInterpreterPackage_AreRefused()
    {
        var plan = CreateSut().PlanLaunch(Ubuntu, ["-c", "rm -rf ~"]);

        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        plan.ErrorMessage.Should().Contain("ubuntu");
    }

    [Fact]
    public void PlanLaunch_ArgumentsToAnAppWhoseProgramIsNotKnown_AreRefused()
    {
        var plan = CreateSut().PlanLaunch("Vendor.Unknown_8wekyb3d8bbwe!App", ["x"]);

        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Fact]
    public void PlanLaunch_ArgumentsToAPackagedApp_AreRefused_WithoutTheInstalledAppsService()
    {
        var plan = CreateSut(withApps: false).PlanLaunch(Notepad, ["todo.txt"]);

        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments), "the program cannot be told, so it fails closed");
    }

    [Fact]
    public void PlanLaunch_AnInterpreterPackageWithoutArguments_IsAllowed()
    {
        CreateSut().PlanLaunch(Ubuntu).IsValid.Should().BeTrue();
    }

    [Fact]
    public void PlanLaunch_ForceAccessibilityOnAPackagedApp_IsNotApplied()
    {
        var plan = CreateSut().PlanLaunch(Calculator, forceAccessibility: true);

        plan.IsValid.Should().BeTrue();
        plan.ForceAccessibilityApplied.Should().BeFalse();
        plan.Warning.Should().Contain("forceAccessibility was not applied");
    }

    #endregion

    #region shell:AppsFolder entries of desktop apps with arguments (closes the InterpreterGuard gap)

    [Fact]
    public void PlanLaunch_ADesktopAppEntryWithArguments_IsCheckedByItsProgram()
    {
        const string entry = @"shell:AppsFolder\PowerShellSeven";
        _resolver.Setup(r => r.Resolve(entry, It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(entry, LaunchTargetKind.Other));
        _installed.Setup(i => i.FindByAppId("PowerShellSeven")).Returns(new InstalledApp("PowerShell 7", "PowerShellSeven", false, @"C:\Program Files\PowerShell\7\pwsh.exe"));

        var plan = CreateSut().PlanLaunch(entry, ["-c", "Get-Secret"]);

        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Fact]
    public void PlanLaunch_ADesktopAppEntryWithArguments_IsAllowed_WhenItsProgramIsNoInterpreter()
    {
        const string entry = @"shell:AppsFolder\MSEdge";
        _resolver.Setup(r => r.Resolve(entry, It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(entry, LaunchTargetKind.Other));
        _installed.Setup(i => i.FindByAppId("MSEdge")).Returns(new InstalledApp("Microsoft Edge", "MSEdge", false, @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"));

        CreateSut().PlanLaunch(entry, ["https://example.com"]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void PlanLaunch_AnUnknownDesktopAppEntryWithArguments_IsRefused()
    {
        const string entry = @"shell:AppsFolder\Something.Else";
        _resolver.Setup(r => r.Resolve(entry, It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(entry, LaunchTargetKind.Other));

        CreateSut().PlanLaunch(entry, ["x"]).ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        CreateSut().PlanLaunch(entry).IsValid.Should().BeTrue("without arguments any entry starts as before");
    }

    #endregion

    #region Friendly names

    private void NotOnDisk(string name) =>
        _resolver.Setup(r => r.Resolve(name, It.IsAny<string?>())).Returns(new ResolvedLaunchTarget(name, LaunchTargetKind.Other, NotFound: true));

    [Fact]
    public void PlanLaunch_TheExactNameOfAPackagedApp_PlansThatApp_WithAWarning()
    {
        NotOnDisk("Kalkulator");
        _installed.Setup(i => i.FindByName("Kalkulator")).Returns([new InstalledApp("Kalkulator", Calculator, true, null)]);

        var plan = CreateSut().PlanLaunch("Kalkulator");

        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.TargetKind.Should().Be(LaunchTargetKind.PackagedApp);
        plan.StartInfo!.FileName.Should().Be(CalculatorTarget);
        plan.Warning.Should().Contain("'Kalkulator'").And.Contain(Calculator);
    }

    [Fact]
    public void PlanLaunch_TheExactNameOfADesktopApp_PlansItsProgram_ThroughTheResolverAndTheGuards()
    {
        const string word = @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE";
        NotOnDisk("Word");
        _installed.Setup(i => i.FindByName("Word")).Returns([new InstalledApp("Word", "Microsoft.Office.WINWORD.EXE.15", false, word)]);

        var plan = CreateSut().PlanLaunch("Word", [@"C:\docs\report.docx"]);

        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.StartInfo!.FileName.Should().Be(word);
        plan.TargetKind.Should().Be(LaunchTargetKind.NativeExecutable);
        plan.Warning.Should().Contain("'Word'").And.Contain(word);
        _resolver.Verify(r => r.Resolve(word, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void PlanLaunch_AFriendlyNameThatIsAnInterpreter_IsRefusedWithArguments()
    {
        const string pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
        NotOnDisk("Shell");
        _installed.Setup(i => i.FindByName("Shell")).Returns([new InstalledApp("Shell", "Microsoft.AutoGenerated.{X}", false, pwsh)]);

        var plan = CreateSut().PlanLaunch("Shell", ["-c", "x"]);

        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Fact]
    public void PlanLaunch_AFriendlyNameThatIsAScript_IsRefused()
    {
        NotOnDisk("Tool");
        _installed.Setup(i => i.FindByName("Tool")).Returns([new InstalledApp("Tool", "Tool", false, @"C:\Tools\tool.ps1")]);

        CreateSut().PlanLaunch("Tool").ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
    }

    [Fact]
    public void PlanLaunch_ANameOfSeveralApps_IsAmbiguousApp_ListingThem()
    {
        NotOnDisk("Settings");
        _installed.Setup(i => i.FindByName("Settings")).Returns(
        [
            new InstalledApp("Settings", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", true, null),
            new InstalledApp("Settings", "Microsoft.AutoGenerated.{8E66}", false, @"C:\Program Files\TortoiseGit\bin\TortoiseGitProc.exe")
        ]);

        var plan = CreateSut().PlanLaunch("Settings");

        plan.IsValid.Should().BeFalse();
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.AmbiguousApp));
        plan.ErrorMessage.Should().Contain("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel")
            .And.Contain("TortoiseGitProc.exe").And.Contain("Nothing was started");
    }

    [Fact]
    public void PlanLaunch_ANameOfNoApp_IsExecutableNotFound_PointingToListInstalledApps()
    {
        NotOnDisk("Calculator");

        var plan = CreateSut().PlanLaunch("Calculator");

        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.ExecutableNotFound));
        plan.ErrorMessage.Should().Contain("list_installed_apps");
    }

    [Fact]
    public void PlanLaunch_AnAppWithoutAProgram_IsNotLaunchedByName()
    {
        NotOnDisk("Thing");
        _installed.Setup(i => i.FindByName("Thing")).Returns([new InstalledApp("Thing", "Thing.Id", false, null)]);

        CreateSut().PlanLaunch("Thing").ErrorCode.Should().Be(nameof(LaunchErrorCode.ExecutableNotFound));
    }

    #endregion

    #region Activation

    [Fact]
    public async Task Launch_APackagedApp_IsActivated_AndReturnsTheAppsProcessId()
    {
        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(Notepad, [@"C:\notes\todo.txt"]), false, 5000);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Status.Should().Be(LaunchStatus.Started);
        result.ProcessId.Should().Be(ActivatedProcessId);
        _activations.Should().Equal((Notepad, @"C:\notes\todo.txt"));
        _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);
        _activated.Verify(p => p.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Launch_APackagedAppWithAWait_FindsItsWindow_ByTheActivatedProcessOrItsPackage()
    {
        WaitFinds(WindowWaitOutcome.NewWindow, CalculatorWindow);
        _apps.Setup(a => a.GetPackageFamilyName(7000)).Returns(CalculatorFamily);
        _apps.Setup(a => a.GetPackageFamilyName(8000)).Returns("Other.App_8wekyb3d8bbwe");

        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(Calculator), true, 5000);

        result.Status.Should().Be(LaunchStatus.WindowObserved);
        result.ProcessId.Should().Be(ActivatedProcessId);
        result.Window.Should().Be(CalculatorWindow);
        var (isOwner, options) = _waits.Single();
        isOwner(new WindowCandidate(0x1, ActivatedProcessId, null, "Kalkulator")).Should().BeTrue();
        isOwner(new WindowCandidate(0x2, 7000, 9000, "Kalkulator")).Should().BeTrue("a process of the same package shows the app too");
        isOwner(new WindowCandidate(0x3, 8000, null, "Other")).Should().BeFalse();
        options.WasCloakedOwner.Should().NotBeNull("a UWP frame is created cloaked, or reused from a suspended app");
        options.WasCloakedOwner!(new WindowCandidate(0x1, ActivatedProcessId, 9000, "Kalkulator")).Should().BeTrue();
        options.WasCloakedOwner(new WindowCandidate(0x2, 7000, 9000, "Kalkulator")).Should().BeFalse("only the activated process revives a cloaked frame");
    }

    [Fact]
    public async Task Launch_AnActivationWhoseProcessExitedAtOnce_HasNoProcessId_AndStillFindsTheWindowByPackage()
    {
        _processes.Setup(p => p.OpenProcess(ActivatedProcessId)).Returns((IProcessHandle?)null);
        WaitFinds(WindowWaitOutcome.ReusedExistingWindow, CalculatorWindow);

        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(Calculator), true, 5000);

        result.Status.Should().Be(LaunchStatus.ReusedExistingWindow);
        result.ProcessId.Should().BeNull();
        result.Window.Should().Be(CalculatorWindow);
    }

    [Fact]
    public async Task Launch_AFailedActivation_IsActivationFailed_WithTheHResult()
    {
        _apps.Setup(a => a.ActivateApplication(It.IsAny<string>(), It.IsAny<string?>())).Returns(new AppActivationResult(unchecked((int)0x80270254), null));

        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(Calculator), true, 5000);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(LaunchErrorCode.ActivationFailed));
        result.ErrorMessage.Should().Contain("0x80270254").And.Contain("list_installed_apps");
        _processes.Verify(p => p.StartProcess(It.IsAny<ProcessStartInfo>()), Times.Never);
        _windowWait.Verify(w => w.WaitForNewWindowAsync(It.IsAny<WindowBaseline>(), It.IsAny<Func<WindowCandidate, bool>>(),
            It.IsAny<WindowWaitOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(unchecked((int)0x80270251))] // E_ELEVATED_ACTIVATION_NOT_SUPPORTED
    [InlineData(unchecked((int)0x80270252))] // E_UAC_DISABLED
    [InlineData(unchecked((int)0x80270253))] // E_FULL_ADMIN_NOT_SUPPORTED
    public async Task Launch_AnActivationWindowsRefusesForElevation_FallsBackToTheShell_WithAWarning(int hresult)
    {
        _apps.Setup(a => a.ActivateApplication(It.IsAny<string>(), It.IsAny<string?>())).Returns(new AppActivationResult(hresult, null));

        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(Calculator), false, 5000);

        result.Success.Should().BeTrue();
        _startedWith.Single().FileName.Should().Be(CalculatorTarget);
        result.Warning.Should().Contain("processId");
    }

    [Fact]
    public async Task Launch_FromAnElevatedServer_UsesTheShell_WithoutTryingToActivate()
    {
        _security.SetupGet(s => s.ServerIntegrity).Returns(new ProcessIntegrity(Environment.ProcessId, IntegrityLevel.High, ProcessIntegrity.HighRid, true, null));
        _processes.Setup(p => p.StartProcess(It.IsAny<ProcessStartInfo>())).Callback<ProcessStartInfo>(_startedWith.Add).Returns((IProcessHandle?)null);
        WaitFinds(WindowWaitOutcome.NewWindow, CalculatorWindow);
        _apps.Setup(a => a.GetPackageFamilyName(ActivatedProcessId)).Returns(CalculatorFamily);

        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(Calculator), true, 5000);

        _apps.Verify(a => a.ActivateApplication(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _startedWith.Single().FileName.Should().Be(CalculatorTarget);
        result.Status.Should().Be(LaunchStatus.WindowObserved);
        result.ProcessId.Should().BeNull();
        result.Warning.Should().Contain("elevated").And.Contain("processId");
        _waits.Single().IsOwner(new WindowCandidate(0x700, ActivatedProcessId, null, "Kalkulator")).Should().BeTrue("the package tells the window");
    }

    [Fact]
    public async Task Launch_AnActivationThatDoesNotReturn_IsTimedOut_NotAwaitingConsent()
    {
        using var release = new ManualResetEventSlim();
        _apps.Setup(a => a.ActivateApplication(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(() =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return new AppActivationResult(0, ActivatedProcessId);
            });

        var result = await CreateSut(activationTimeout: TimeSpan.FromMilliseconds(50)).LaunchApplicationAsync(new LaunchRequest(Calculator), true, 5000);
        release.Set();

        result.Success.Should().BeTrue();
        result.Status.Should().Be(LaunchStatus.TimedOut);
        result.ProcessId.Should().BeNull();
        result.Warning.Should().Contain("did not return").And.Contain("find_windows");
    }

    [Fact]
    public async Task Launch_WithoutTheAppsWrapper_StartsThePackagedAppThroughTheShell()
    {
        var result = await CreateSut(withApps: false).LaunchApplicationAsync(new LaunchRequest(Calculator), false, 5000);

        result.Success.Should().BeTrue();
        _startedWith.Single().FileName.Should().Be(CalculatorTarget);
    }

    [Fact]
    public async Task Batch_APackagedAppItem_IsActivatedToo()
    {
        var results = await CreateSut().LaunchApplicationsBatchAsync([new LaunchRequest(Calculator), new LaunchRequest("notepad.exe")]);

        results.Select(r => r.ProcessId).Should().Equal(ActivatedProcessId, StartedProcessId);
        _activations.Should().ContainSingle();
    }

    [Fact]
    public async Task ReuseExisting_FindsAnOpenWindowOfThePackage_WithoutExpectWindow()
    {
        var open = TestDataFactory.CreateWindowInfo(handleValue: 0x700, title: "Kalkulator", processName: "CalculatorApp", processId: 6000);
        _windows.Setup(w => w.GetApplicationWindows()).Returns([open]);
        _apps.Setup(a => a.GetPackageFamilyName(6000)).Returns(CalculatorFamily);

        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(Calculator, ReuseExisting: true), false, 5000);

        result.Status.Should().Be(LaunchStatus.AlreadyOpen);
        result.Window.Should().Be(open);
        VerifyNothingStarted();
    }

    #endregion

    #region runAsAdmin

    [Fact]
    public async Task RunAsAdmin_StartsTheExeWithTheRunasVerb_AndWarnsThatTheServerCannotDriveIt()
    {
        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest(@"C:\Tools\app.exe") { RunAsAdmin = true }, false, 5000);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _startedWith.Single().Verb.Should().Be("runas");
        result.Warning.Should().Contain("TargetElevated").And.Contain("close it");
    }

    [Fact]
    public void RunAsAdmin_AnythingButAnExe_IsInvalidArguments()
    {
        _resolver.Setup(r => r.Resolve("ms-settings:", It.IsAny<string?>())).Returns(new ResolvedLaunchTarget("ms-settings:", LaunchTargetKind.Other));

        CreateSut().PlanLaunch("ms-settings:", runAsAdmin: true).ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        CreateSut().PlanLaunch(Calculator, runAsAdmin: true).ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task RunAsAdmin_WithReuseExisting_IsInvalidArguments()
    {
        var result = await CreateSut().LaunchApplicationAsync(new LaunchRequest("notepad.exe", ReuseExisting: true) { RunAsAdmin = true }, false, 5000);

        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        VerifyNothingStarted();
    }

    [Theory]
    [InlineData(ElevationPrompt.Silent)]
    [InlineData(ElevationPrompt.Unknown)]
    public async Task RunAsAdmin_WhereWindowsWouldNotAsk_NeedsTheUsersConfirmation(ElevationPrompt prompt)
    {
        _security.Setup(s => s.GetElevationPrompt()).Returns(prompt);
        var sut = CreateSut();

        sut.ElevationNeedsConfirmation().Should().BeTrue();
        var refused = await sut.LaunchApplicationAsync(new LaunchRequest("app.exe") { RunAsAdmin = true }, false, 5000);
        refused.ErrorCode.Should().Be(nameof(LaunchErrorCode.ElevationNotConfirmed));
        VerifyNothingStarted();

        var confirmed = await sut.LaunchApplicationAsync(new LaunchRequest("app.exe") { RunAsAdmin = true, ElevationConfirmed = true }, false, 5000);
        confirmed.Success.Should().BeTrue();
        _startedWith.Single().Verb.Should().Be("runas");
    }

    [Fact]
    public void ElevationNeedsConfirmation_IsFalse_WhenWindowsAsks()
    {
        CreateSut().ElevationNeedsConfirmation().Should().BeFalse();
    }

    [Fact]
    public async Task WithoutRunAsAdmin_NoVerbIsSet()
    {
        await CreateSut().LaunchApplicationAsync(new LaunchRequest("app.exe"), false, 5000);

        _startedWith.Single().Verb.Should().BeEmpty();
    }

    #endregion
}
