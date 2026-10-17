using ModelContextProtocol.Protocol;
using System.Text.Json;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The target policy (roadmap M1.12) outside the input path: close_window and close_windows_batch,
/// kill_process, capture_window and capture_with_elements refuse a denied process with
/// TargetDeniedByPolicy, and no tool argument (force, forceKill) overrides it. A window whose process
/// cannot be named is refused as well while the policy denies anything. Windows, processes and
/// captures are mocks; nothing is closed, killed or captured.
/// </summary>
public class TargetPolicyEnforcementTests
{
    private const uint WmClose = 0x0010;

    private static TargetPolicy Policy(string? deny = null, string? allow = null) =>
        new(TargetPolicySettings.FromEnvironment(name => name switch
        {
            TargetPolicySettings.DenyVariable => deny,
            TargetPolicySettings.AllowVariable => allow,
            _ => null
        }));

    #region close_window, close_windows_batch

    private readonly Mock<INativeWindowWrapper> _native = new();

    private nint AddWindow(nint handle, string className, uint processId, string processName)
    {
        _native.Setup(n => n.IsWindow(handle)).Returns(true);
        _native.Setup(n => n.GetClassName(handle)).Returns(className);
        _native.Setup(n => n.GetWindowText(handle)).Returns(processName + " window");
        _native.Setup(n => n.GetAncestor(handle, It.IsAny<WindowAncestor>())).Returns(handle);
        _native.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(processId);
        _native.Setup(n => n.GetProcessName(processId)).Returns(processName);
        _native.Setup(n => n.PostMessage(handle, WmClose, nint.Zero, nint.Zero)).Returns(Win32CallResult.Ok);
        return handle;
    }

    /// <summary>
    /// A window whose process the server cannot name (it may run elevated): GetProcessName fails, so the window cannot be
    /// checked against the policy.
    /// </summary>
    private nint AddUnnamedWindow(nint handle, uint processId)
    {
        AddWindow(handle, "VaultWindow", processId, "Vault");
        _native.Setup(n => n.GetProcessName(processId)).Returns((string?)null);
        return handle;
    }

    private WindowService CreateWindowService(ITargetPolicy? policy)
    {
        var monitors = TestDataFactory.CreateMockMonitorService();
        monitors.Setup(m => m.CalculateSnapBounds(It.IsAny<int>(), It.IsAny<SnapPosition>())).Returns(new MonitorRect(0, 0, 960, 1040));
        return new(monitors.Object, _native.Object, Mock.Of<IHostGuard>(), policy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseWindow_DeniedProcess_IsRefused_EvenWithForce(bool force)
    {
        // Arrange
        var keePass = AddWindow(0x300, "WindowsForms10.Window.8.app.0.1", 3001, "KeePass");

        // Act
        var result = CreateWindowService(Policy()).CloseWindow(keePass, force);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("KeePass").And.Contain("force does not override it");
        _native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_RefusesTheDeniedWindow_AndClosesTheOthers()
    {
        // Arrange
        var notepad = AddWindow(0x200, "Notepad", 2001, "notepad");
        var keePass = AddWindow(0x300, "KeePassWindow", 3001, "KeePass");

        // Act
        var results = await CreateWindowService(Policy()).CloseWindowsBatchAsync([notepad, keePass], force: false, delayBetweenMs: 0);

        // Assert
        results[0].Success.Should().BeTrue();
        results[1].ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        _native.Verify(n => n.PostMessage(notepad, WmClose, nint.Zero, nint.Zero), Times.Once);
        _native.Verify(n => n.PostMessage(keePass, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void CloseWindow_UwpFrameOfADeniedApp_IsRefused()
    {
        // Arrange - the frame belongs to ApplicationFrameHost; the hosted CoreWindow to the denied app
        var frame = AddWindow(0x400, "ApplicationFrameWindow", 500, "ApplicationFrameHost");
        var coreWindow = new nint(0x401);
        _native.Setup(n => n.EnumChildWindows(frame, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> callback) => callback(coreWindow));
        _native.Setup(n => n.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        _native.Setup(n => n.GetWindowThreadProcessId(coreWindow)).Returns(600u);
        _native.Setup(n => n.GetProcessName(600u)).Returns("LastPass");

        // Act & Assert
        CreateWindowService(Policy()).CloseWindow(frame, force: true).ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
    }

    [Theory]
    [InlineData("SecHealthUI")]
    [InlineData("CHXSmartScreen")]
    public void EvaluateTargetPolicy_UwpFrameOfTheWindowsSecurityAppOrTheSmartScreenPrompt_IsDeniedByDefault(string app)
    {
        // Arrange - the Windows Security app and the "Windows protected your PC" prompt are UWP apps: their window is a frame
        // of ApplicationFrameHost that hosts the app's CoreWindow
        var frame = AddWindow(0x400, "ApplicationFrameWindow", 500, "ApplicationFrameHost");
        var coreWindow = new nint(0x401);
        _native.Setup(n => n.EnumChildWindows(frame, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> callback) => callback(coreWindow));
        _native.Setup(n => n.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        _native.Setup(n => n.GetWindowThreadProcessId(coreWindow)).Returns(600u);
        _native.Setup(n => n.GetProcessName(600u)).Returns(app);
        var sut = CreateWindowService(new TargetPolicy(TargetPolicySettings.Default));

        // Act
        var verdict = sut.EvaluateTargetPolicy(frame);

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.ProcessName.Should().Be(app);
        sut.CloseWindow(frame, force: true).ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        _native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void EvaluateTargetPolicy_FrameHostWindowOfAnotherClassThatHostsADeniedPrompt_IsDeniedByDefault()
    {
        // Arrange - a prompt may come up in a window of ApplicationFrameHost of another class than ApplicationFrameWindow,
        // which hosts the prompt's CoreWindow (roadmap M3.F7)
        var frame = AddWindow(0x400, "PromptFrameWindow", 500, "ApplicationFrameHost");
        var coreWindow = new nint(0x401);
        _native.Setup(n => n.EnumChildWindows(frame, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> callback) => callback(coreWindow));
        _native.Setup(n => n.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        _native.Setup(n => n.GetWindowThreadProcessId(coreWindow)).Returns(600u);
        _native.Setup(n => n.GetProcessName(600u)).Returns("CredDialogHost");
        var sut = CreateWindowService(new TargetPolicy(TargetPolicySettings.Default));

        // Act
        var verdict = sut.EvaluateTargetPolicy(frame);

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.ProcessName.Should().Be("CredDialogHost");
        sut.CloseWindow(frame, force: true).ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        _native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    /// <summary>
    /// Windows' "Open File - Security Warning" in explorer.exe, which the deny list cannot name (roadmap M3.F5): the policy
    /// recognizes it by its controls (<see cref="SecurityPromptWindows"/>).
    /// </summary>
    private nint AddSecurityPromptOfExplorer()
    {
        var prompt = AddWindow(0x900, "#32770", 5001, "explorer");
        SecurityPromptWindows.Add(_native, prompt);
        return prompt;
    }

    [Fact]
    public void EvaluateTargetPolicy_TheSecurityPromptOfExplorer_IsDeniedByDefault_AndNotClosed()
    {
        // Arrange
        var prompt = AddSecurityPromptOfExplorer();
        var explorer = AddWindow(0x500, "CabinetWClass", 5001, "explorer");
        var sut = CreateWindowService(new TargetPolicy(TargetPolicySettings.Default, _native.Object));

        // Act
        var verdict = sut.EvaluateTargetPolicy(prompt);

        // Assert - File Explorer's own window is not denied; its prompt is, and closing it would answer it
        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("Open File - Security Warning");
        sut.EvaluateTargetPolicy(explorer).Allowed.Should().BeTrue();
        var closed = sut.CloseWindow(prompt, force: true);
        closed.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        closed.Error.Should().Contain("Open File - Security Warning");
        _native.Verify(n => n.PostMessage(prompt, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void CloseWindow_AllowList_RefusesUnlistedWindows()
    {
        // Arrange
        var notepad = AddWindow(0x200, "Notepad", 2001, "notepad");
        var paint = AddWindow(0x210, "MSPaintApp", 2101, "mspaint");
        var sut = CreateWindowService(Policy(allow: "notepad"));

        // Act & Assert
        sut.CloseWindow(notepad, force: false).Success.Should().BeTrue();
        var refused = sut.CloseWindow(paint, force: false);
        refused.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        refused.Error.Should().Contain(TargetPolicySettings.AllowVariable);
    }

    [Fact]
    public void CloseWindow_WithoutAPolicy_RefusesNothingByPolicy()
    {
        var keePass = AddWindow(0x300, "KeePassWindow", 3001, "KeePass");

        CreateWindowService(policy: null).CloseWindow(keePass, force: false).Success.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bitwarden")]
    public void EvaluateTargetPolicy_AWindowWhoseProcessCannotBeNamed_IsRefused_WhileThePolicyDeniesAnything(string? deny)
    {
        // Arrange - the server cannot tell whether the window belongs to a password manager on the deny list
        var vault = AddUnnamedWindow(0x500, 5001);

        // Act
        var verdict = CreateWindowService(Policy(deny)).EvaluateTargetPolicy(vault);

        // Assert - as the input guard, the captures and the UI Automation reads decide
        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("cannot be read");
    }

    [Fact]
    public void EvaluateTargetPolicy_AWindowWhoseProcessCannotBeNamed_IsAllowed_WhenThePolicyDeniesNothing()
    {
        var vault = AddUnnamedWindow(0x500, 5001);

        CreateWindowService(Policy(deny: "none")).EvaluateTargetPolicy(vault).Allowed.Should().BeTrue();
    }

    [Fact]
    public void EvaluateTargetPolicy_UwpFrameWhoseAppCannotBeNamed_IsRefused_WhileThePolicyDeniesAnything()
    {
        // Arrange - the frame host is named, but the app whose CoreWindow it hosts is not
        var frame = AddWindow(0x400, "ApplicationFrameWindow", 500, "ApplicationFrameHost");
        var coreWindow = new nint(0x401);
        _native.Setup(n => n.EnumChildWindows(frame, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> callback) => callback(coreWindow));
        _native.Setup(n => n.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        _native.Setup(n => n.GetWindowThreadProcessId(coreWindow)).Returns(600u);
        _native.Setup(n => n.GetProcessName(600u)).Returns((string?)null);

        // Act
        var verdict = CreateWindowService(Policy()).EvaluateTargetPolicy(frame);

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("cannot be read");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseWindow_AWindowWhoseProcessCannotBeNamed_IsRefused_EvenWithForce(bool force)
    {
        // Arrange
        var vault = AddUnnamedWindow(0x500, 5001);

        // Act
        var result = CreateWindowService(Policy()).CloseWindow(vault, force);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("cannot be read").And.Contain("force does not override it");
        _native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_RefusesAWindowWhoseProcessCannotBeNamed_AndClosesTheOthers()
    {
        // Arrange
        var notepad = AddWindow(0x200, "Notepad", 2001, "notepad");
        var vault = AddUnnamedWindow(0x500, 5001);

        // Act
        var results = await CreateWindowService(Policy()).CloseWindowsBatchAsync([notepad, vault], force: false, delayBetweenMs: 0);

        // Assert
        results[0].Success.Should().BeTrue();
        results[1].ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        _native.Verify(n => n.PostMessage(notepad, WmClose, nint.Zero, nint.Zero), Times.Once);
        _native.Verify(n => n.PostMessage(vault, It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void CloseWindow_AWindowWhoseProcessCannotBeNamed_IsClosed_WhenThePolicyDeniesNothing()
    {
        var vault = AddUnnamedWindow(0x500, 5001);

        CreateWindowService(Policy(deny: "none")).CloseWindow(vault, force: false).Success.Should().BeTrue();
        _native.Verify(n => n.PostMessage(vault, WmClose, nint.Zero, nint.Zero), Times.Once);
    }

    #endregion

    #region Window changes: move, resize, bounds, state, snap, monitor and their batches

    /// <summary>Every window change of WindowService that can restore, raise or move a window.</summary>
    public static TheoryData<string> WindowChanges() =>
        new() { "move", "resize", "bounds", "minimize", "restore", "maximize", "snap", "monitor" };

    private static WindowChangeResult Change(WindowService sut, nint handle, string change) => change switch
    {
        "move" => sut.MoveWindow(handle, 10, 10),
        "resize" => sut.ResizeWindow(handle, 400, 300),
        "bounds" => sut.SetWindowBounds(handle, 10, 10, 400, 300),
        "minimize" => sut.SetWindowState(handle, WindowStateRequest.Minimize),
        "restore" => sut.SetWindowState(handle, WindowStateRequest.Restore),
        "maximize" => sut.SetWindowState(handle, WindowStateRequest.Maximize),
        "snap" => sut.SnapWindow(handle, SnapPosition.LeftHalf, monitorIndex: 0),
        _ => sut.MoveWindowToMonitor(handle, 0, MonitorPositioning.Restore)
    };

    private void VerifyWindowUntouched()
    {
        _native.Verify(n => n.SetWindowPos(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
        _native.Verify(n => n.ShowWindow(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(WindowChanges))]
    public void WindowChange_DeniedProcess_IsRefused_AndTheWindowIsNotTouched(string change)
    {
        // Arrange - a minimized password manager: restoring or raising it would put it in a screenshot
        var keePass = AddWindow(0x300, "KeePassWindow", 3001, "KeePass");
        _native.Setup(n => n.IsIconic(keePass)).Returns(true);
        _native.Setup(n => n.GetWindowRect(keePass)).Returns((0, 0, 800, 600));

        // Act
        var changed = Change(CreateWindowService(Policy()), keePass, change);

        // Assert
        changed.Success.Should().BeFalse();
        changed.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        VerifyWindowUntouched();
    }

    [Theory]
    [MemberData(nameof(WindowChanges))]
    public void WindowChange_TheSecurityPromptOfExplorer_IsRefused_AndTheWindowIsNotTouched(string change)
    {
        // Arrange
        var prompt = AddSecurityPromptOfExplorer();
        _native.Setup(n => n.GetWindowRect(prompt)).Returns((0, 0, 400, 300));

        // Act
        var changed = Change(CreateWindowService(new TargetPolicy(TargetPolicySettings.Default, _native.Object)), prompt, change);

        // Assert
        changed.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        VerifyWindowUntouched();
    }

    [Theory]
    [MemberData(nameof(WindowChanges))]
    public void WindowChange_AllowedProcess_IsDone(string change)
    {
        // Arrange
        var notepad = AddWindow(0x200, "Notepad", 2001, "notepad");
        _native.Setup(n => n.GetWindowRect(notepad)).Returns((0, 0, 800, 600));
        _native.Setup(n => n.SetWindowPos(notepad, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>())).Returns(Win32CallResult.Ok);
        TestDataFactory.SimulateShowStates(_native, notepad);

        // Act & Assert
        Change(CreateWindowService(Policy()), notepad, change).Success.Should().BeTrue();
    }

    [Fact]
    public async Task WindowTools_DeniedWindow_ReturnTargetDeniedByPolicy()
    {
        // Arrange
        var keePass = AddWindow(0x300, "KeePassWindow", 3001, "KeePass");
        _native.Setup(n => n.GetWindowRect(keePass)).Returns((0, 0, 800, 600));
        var tools = new WindowManagerTools(CreateWindowService(Policy()), Mock.Of<IForegroundService>());

        // Act
        var results = new[]
        {
            await tools.SetWindowBounds(10, 10, 400, 300, handle: keePass),
            await tools.SetWindowState(WindowStateRequest.Restore, handle: keePass),
            await tools.SnapWindow(SnapPosition.LeftHalf, handle: keePass),
            await tools.MoveWindowToMonitor(MonitorSelector.OfIndex(0), handle: keePass)
        };

        // Assert
        results.Should().OnlyContain(r => !r.Success && r.ErrorCode == nameof(WindowErrorCode.TargetDeniedByPolicy) &&
                                          r.Error!.Contains("KeePass"));
        VerifyWindowUntouched();
    }

    [Fact]
    public async Task WindowBatchTools_RefuseTheDeniedWindow_AndChangeTheOthers()
    {
        // Arrange
        var notepad = AddWindow(0x200, "Notepad", 2001, "notepad");
        var keePass = AddWindow(0x300, "KeePassWindow", 3001, "KeePass");
        _native.Setup(n => n.GetWindowRect(It.IsAny<nint>())).Returns((0, 0, 800, 600));
        _native.Setup(n => n.SetWindowPos(notepad, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>())).Returns(Win32CallResult.Ok);
        TestDataFactory.SimulateShowStates(_native, notepad);
        var tools = new WindowManagerTools(CreateWindowService(Policy()), Mock.Of<IForegroundService>());

        // Act
        var bounds = await tools.SetWindowsBoundsBatch(
            [new WindowBoundsPlacement(0, 0, 400, 300, Handle: notepad), new WindowBoundsPlacement(0, 0, 400, 300, Handle: keePass)], delayBetweenMs: 0);
        var snaps = await tools.SnapWindowsBatch(
            [new WindowSnapPlacement("left_half", Handle: keePass, Monitor: MonitorSelector.OfIndex(0)), new WindowSnapPlacement("right_half", Handle: notepad, Monitor: MonitorSelector.OfIndex(0))], delayBetweenMs: 0);
        var states = await tools.SetWindowsStateBatch(
            [new WindowStateChange("maximize", Handle: notepad), new WindowStateChange("restore", Handle: keePass)], delayBetweenMs: 0);

        // Assert
        foreach (var batch in new[] { bounds, snaps, states })
        {
            batch.Succeeded.Should().Be(1);
            var refused = batch.Results.Single(r => r.Handle == keePass);
            refused.Success.Should().BeFalse();
            refused.ErrorCode.Should().Be(nameof(WindowErrorCode.TargetDeniedByPolicy));
            refused.Error.Should().Contain("KeePass");
            batch.Results.Single(r => r.Handle == notepad).Success.Should().BeTrue();
        }

        _native.Verify(n => n.SetWindowPos(keePass, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
        _native.Verify(n => n.ShowWindow(keePass, It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(WindowChanges))]
    public void WindowChange_AWindowWhoseProcessCannotBeNamed_IsRefused_AndTheWindowIsNotTouched(string change)
    {
        // Arrange - a minimized window that cannot be checked against the policy: it may be a denied password manager
        var vault = AddUnnamedWindow(0x500, 5001);
        _native.Setup(n => n.IsIconic(vault)).Returns(true);
        _native.Setup(n => n.GetWindowRect(vault)).Returns((0, 0, 800, 600));

        // Act
        var changed = Change(CreateWindowService(Policy()), vault, change);

        // Assert
        changed.Success.Should().BeFalse();
        changed.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        changed.Error.Should().Contain("cannot be read");
        VerifyWindowUntouched();
    }

    [Theory]
    [MemberData(nameof(WindowChanges))]
    public void WindowChange_AWindowWhoseProcessCannotBeNamed_IsDone_WhenThePolicyDeniesNothing(string change)
    {
        // Arrange
        var vault = AddUnnamedWindow(0x500, 5001);
        _native.Setup(n => n.GetWindowRect(vault)).Returns((0, 0, 800, 600));
        _native.Setup(n => n.SetWindowPos(vault, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>())).Returns(Win32CallResult.Ok);
        TestDataFactory.SimulateShowStates(_native, vault);

        // Act & Assert
        Change(CreateWindowService(Policy(deny: "none")), vault, change).Success.Should().BeTrue();
    }

    [Fact]
    public async Task WindowTools_AndTheirBatches_RefuseAWindowWhoseProcessCannotBeNamed()
    {
        // Arrange
        var notepad = AddWindow(0x200, "Notepad", 2001, "notepad");
        var vault = AddUnnamedWindow(0x500, 5001);
        _native.Setup(n => n.GetWindowRect(It.IsAny<nint>())).Returns((0, 0, 800, 600));
        _native.Setup(n => n.SetWindowPos(notepad, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>())).Returns(Win32CallResult.Ok);
        TestDataFactory.SimulateShowStates(_native, notepad);
        var tools = new WindowManagerTools(CreateWindowService(Policy()), Mock.Of<IForegroundService>());

        // Act
        var results = new[]
        {
            await tools.SetWindowBounds(10, 10, 400, 300, handle: vault),
            await tools.SetWindowState(WindowStateRequest.Restore, handle: vault),
            await tools.SnapWindow(SnapPosition.LeftHalf, handle: vault),
            await tools.MoveWindowToMonitor(MonitorSelector.OfIndex(0), handle: vault)
        };
        var batch = await tools.SetWindowsBoundsBatch(
            [new WindowBoundsPlacement(0, 0, 400, 300, Handle: notepad), new WindowBoundsPlacement(0, 0, 400, 300, Handle: vault)], delayBetweenMs: 0);

        // Assert
        results.Should().OnlyContain(r => !r.Success && r.ErrorCode == nameof(WindowErrorCode.TargetDeniedByPolicy) &&
                                          r.Error!.Contains("cannot be read"));
        batch.Succeeded.Should().Be(1);
        batch.Results.Single(r => r.Handle == vault).ErrorCode.Should().Be(nameof(WindowErrorCode.TargetDeniedByPolicy));
        _native.Verify(n => n.SetWindowPos(vault, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
        _native.Verify(n => n.ShowWindow(vault, It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region kill_process

    private const int KeePassPid = 3001;

    private readonly Mock<INativeProcessWrapper> _processWrapper = new();

    private (ProcessService Service, Mock<IProcessHandle> Process) CreateProcessService(string processName, ITargetPolicy policy)
    {
        var wrapper = _processWrapper;
        var process = new Mock<IProcessHandle>();
        process.SetupGet(p => p.Id).Returns(KeePassPid);
        process.SetupGet(p => p.ProcessName).Returns(processName);
        wrapper.Setup(w => w.OpenProcess(KeePassPid)).Returns(process.Object);
        var service = new ProcessService(
            TestDataFactory.CreateMockWindowService().Object,
            new Mock<IInputValidationService>().Object,
            TestDataFactory.CreateMockBrowserDetectionService().Object,
            new Mock<IExecutableResolver>().Object,
            wrapper.Object,
            policy);
        return (service, process);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillProcess_DeniedProcess_IsRefused_EvenWithForceKill(bool forceKill)
    {
        // Arrange
        var (sut, process) = CreateProcessService("KeePass", Policy());

        // Act
        var result = await sut.KillProcessAsync(KeePassPid, confirm: true, forceKill: forceKill);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
        result.ProcessName.Should().Be("KeePass");
        result.ErrorMessage.Should().Contain(TargetPolicySettings.DenyVariable);
        process.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);
        process.Verify(p => p.CloseMainWindow(), Times.Never);
    }

    [Fact]
    public async Task KillProcess_AllowList_RefusesUnlistedProcesses()
    {
        var (sut, process) = CreateProcessService("notepad", Policy(allow: "mspaint"));

        (await sut.KillProcessAsync(KeePassPid, confirm: true)).ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
        process.Verify(p => p.CloseMainWindow(), Times.Never);
    }

    [Fact]
    public async Task KillProcess_ForceKill_ADeniedChildProcess_RefusesTheWholeTree()
    {
        // Arrange - KeePass was started from a terminal; forceKill ends the terminal's whole tree
        var (sut, process) = CreateProcessService("cmd", Policy());
        _processWrapper.Setup(w => w.GetDescendantsOf(KeePassPid)).Returns([(5001, "conhost.exe"), (5002, "KeePass.exe")]);

        // Act
        var result = await sut.KillProcessAsync(KeePassPid, confirm: true, forceKill: true);

        // Assert
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
        result.ErrorMessage.Should().Contain("KeePass").And.Contain("5002").And.Contain("child process");
        process.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task KillProcess_ForceKill_AllowList_EveryProcessOfTheTreeMustBeAllowed()
    {
        var (sut, process) = CreateProcessService("notepad", Policy(allow: "notepad"));
        _processWrapper.Setup(w => w.GetDescendantsOf(KeePassPid)).Returns([(5003, "mspaint.exe")]);

        (await sut.KillProcessAsync(KeePassPid, confirm: true, forceKill: true)).ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
        process.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task KillProcess_ForceKill_AllowedTree_IsKilled()
    {
        // Arrange
        var (sut, process) = CreateProcessService("cmd", Policy());
        _processWrapper.Setup(w => w.GetDescendantsOf(KeePassPid)).Returns([(5001, "conhost.exe")]);
        process.SetupGet(p => p.HasExited).Returns(true);
        process.Setup(p => p.WaitForExitAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        await sut.KillProcessAsync(KeePassPid, confirm: true, forceKill: true);

        // Assert
        process.Verify(p => p.Kill(true), Times.Once);
    }

    [Fact]
    public async Task KillProcess_WithoutForceKill_DoesNotReadTheTree()
    {
        // The graceful close asks only the process's main window to close; its children live on
        var (sut, _) = CreateProcessService("cmd", Policy());

        await sut.KillProcessAsync(KeePassPid, confirm: true, forceKill: false);

        _processWrapper.Verify(w => w.GetDescendantsOf(It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region capture_window, capture_with_elements

    private readonly Mock<IScreenshotService> _screenshots = TestDataFactory.CreateMockScreenshotService();
    private readonly Mock<IUIElementService> _elements = TestDataFactory.CreateMockUIElementService();
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService();

    private ScreenshotTools CreateScreenshotTools(nint handle, string processName, TargetPolicy? policy = null)
    {
        _windows.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo((int)handle, processName + " window", processName, 3001));
        return new ScreenshotTools(_screenshots.Object, _elements.Object, _windows.Object, policy ?? Policy());
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    [Fact]
    public async Task CaptureWindow_DeniedProcess_IsRefused_AndNothingIsCaptured()
    {
        // Arrange
        var sut = CreateScreenshotTools(0x300, "KeePass");

        // Act
        var result = await sut.CaptureWindow(0x300);

        // Assert
        result.IsError.Should().BeTrue();
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWithElements_DeniedProcess_IsRefused_BeforeItsElementsAreRead()
    {
        // Arrange
        var sut = CreateScreenshotTools(0x300, "KeePass");

        // Act
        var result = await sut.CaptureWithElements(0x300);

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _elements.Verify(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),It.IsAny<IProgress<OperationProgress>?>(),It.IsAny<CancellationToken>()), Times.Never);
        _screenshots.Verify(s => s.CaptureWindowWithElements(It.IsAny<nint>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<UIElementInfo>>(),
            It.IsAny<bool>(), It.IsAny<ElementOverlayOptions>(), It.IsAny<CaptureOptions>(), It.IsAny<CapturedWindow?>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindow_AndCaptureWithElements_ByASelector_CheckThePolicyOnTheResolvedWindow()
    {
        // Arrange - the selector names KeePass, which the policy denies (roadmap M2.4: a selector never gets past it)
        var sut = CreateScreenshotTools(0x300, "KeePass");
        var keePass = new WindowSelector { ProcessName = "KeePass" };
        _windows.Setup(w => w.ResolveWindow(keePass))
            .Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo(0x300, "KeePass window", "KeePass", 3001)));

        // Act
        var capture = await sut.CaptureWindow(window: keePass);
        var annotated = await sut.CaptureWithElements(window: keePass);

        // Assert
        Payload(capture).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        Payload(annotated).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
        _elements.Verify(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindow_AndCaptureWithElements_OfAWindowWhoseProcessCannotBeNamed_AreRefused_WhileThePolicyDeniesAnything()
    {
        // Arrange - the window service reports a process whose name it cannot read as "Unknown"
        var sut = CreateScreenshotTools(0x500, WindowInfo.UnknownProcessName);

        // Act
        var capture = await sut.CaptureWindow(0x500);
        var annotated = await sut.CaptureWithElements(0x500);

        // Assert - as the UI Automation reads: a window that cannot be checked is not captured
        Payload(capture).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        Payload(capture).GetProperty("error").GetString().Should().Contain("cannot be read");
        Payload(annotated).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
        _elements.Verify(e => e.GetUIElementsAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
            It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindow_OfAWindowWhoseProcessCannotBeNamed_IsCaptured_WhenThePolicyDeniesNothing()
    {
        var sut = CreateScreenshotTools(0x500, WindowInfo.UnknownProcessName, Policy(deny: "none"));

        await sut.CaptureWindow(0x500);

        _screenshots.Verify(s => s.CaptureWindow(new nint(0x500), true, It.IsAny<CaptureOptions>()), Times.Once);
    }

    [Fact]
    public async Task CaptureWithElements_OfAValidWindowTheWindowServiceCannotRead_IsRefused()
    {
        // Arrange - a window that exists, but whose window information (process included) cannot be read
        var sut = CreateScreenshotTools(0x510, "notepad");
        _windows.Setup(w => w.GetWindowInfo(0x510)).Returns((WindowInfo?)null);

        // Act
        var annotated = await sut.CaptureWithElements(0x510);
        var capture = await sut.CaptureWindow(0x510);

        // Assert
        Payload(annotated).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        Payload(capture).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindow_AndCaptureWithElements_OfTheSecurityPromptOfExplorer_AreRefused()
    {
        // Arrange - the prompt's window information names explorer, which the policy does not deny (roadmap M3.F5)
        var sut = CreateScreenshotTools(0x900, "explorer", new TargetPolicy(TargetPolicySettings.Default, _native.Object));
        SecurityPromptWindows.Add(_native, 0x900);

        // Act
        var capture = await sut.CaptureWindow(0x900);
        var annotated = await sut.CaptureWithElements(0x900);

        // Assert
        Payload(capture).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        Payload(capture).GetProperty("error").GetString().Should().Contain("Open File - Security Warning");
        Payload(annotated).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public async Task CaptureWindow_AllowedProcess_IsCaptured()
    {
        var sut = CreateScreenshotTools(0x200, "notepad");

        await sut.CaptureWindow(0x200);

        _screenshots.Verify(s => s.CaptureWindow(new nint(0x200), true, It.IsAny<CaptureOptions>()), Times.Once);
    }

    #endregion
}
