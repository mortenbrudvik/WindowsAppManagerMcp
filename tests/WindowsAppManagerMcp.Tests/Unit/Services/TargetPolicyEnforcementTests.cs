using ModelContextProtocol.Protocol;
using System.Text.Json;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The target policy (roadmap M1.12) outside the input path: close_window and close_windows_batch,
/// kill_process, capture_window and capture_with_elements refuse a denied process with
/// TargetDeniedByPolicy, and no tool argument (force, forceKill) overrides it. Windows, processes and
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

    private WindowService CreateWindowService(ITargetPolicy? policy) =>
        new(TestDataFactory.CreateMockMonitorService().Object, _native.Object, Mock.Of<IHostGuard>(), policy);

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
    public void CloseWindowsBatch_RefusesTheDeniedWindow_AndClosesTheOthers()
    {
        // Arrange
        var notepad = AddWindow(0x200, "Notepad", 2001, "notepad");
        var keePass = AddWindow(0x300, "KeePassWindow", 3001, "KeePass");

        // Act
        var results = CreateWindowService(Policy()).CloseWindowsBatch([notepad, keePass], force: false, delayBetweenMs: 0);

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

    #endregion

    #region kill_process

    private const int KeePassPid = 3001;

    private (ProcessService Service, Mock<IProcessHandle> Process) CreateProcessService(string processName, ITargetPolicy policy)
    {
        var wrapper = new Mock<INativeProcessWrapper>();
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
    public void KillProcess_DeniedProcess_IsRefused_EvenWithForceKill(bool forceKill)
    {
        // Arrange
        var (sut, process) = CreateProcessService("KeePass", Policy());

        // Act
        var result = sut.KillProcess(KeePassPid, confirm: true, forceKill: forceKill);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
        result.ProcessName.Should().Be("KeePass");
        result.ErrorMessage.Should().Contain(TargetPolicySettings.DenyVariable);
        process.Verify(p => p.Kill(It.IsAny<bool>()), Times.Never);
        process.Verify(p => p.CloseMainWindow(), Times.Never);
    }

    [Fact]
    public void KillProcess_AllowList_RefusesUnlistedProcesses()
    {
        var (sut, process) = CreateProcessService("notepad", Policy(allow: "mspaint"));

        sut.KillProcess(KeePassPid, confirm: true).ErrorCode.Should().Be(nameof(ProcessErrorCode.TargetDeniedByPolicy));
        process.Verify(p => p.CloseMainWindow(), Times.Never);
    }

    #endregion

    #region capture_window, capture_with_elements

    private readonly Mock<IScreenshotService> _screenshots = TestDataFactory.CreateMockScreenshotService();
    private readonly Mock<IUIElementService> _elements = TestDataFactory.CreateMockUIElementService();
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService();

    private ScreenshotTools CreateScreenshotTools(nint handle, string processName)
    {
        _windows.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo((int)handle, processName + " window", processName, 3001));
        return new ScreenshotTools(_screenshots.Object, _elements.Object, _windows.Object, Policy());
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement;

    [Fact]
    public void CaptureWindow_DeniedProcess_IsRefused_AndNothingIsCaptured()
    {
        // Arrange
        var sut = CreateScreenshotTools(0x300, "KeePass");

        // Act
        var result = sut.CaptureWindow(0x300);

        // Assert
        result.IsError.Should().BeTrue();
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _screenshots.Verify(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public void CaptureWithElements_DeniedProcess_IsRefused_BeforeItsElementsAreRead()
    {
        // Arrange
        var sut = CreateScreenshotTools(0x300, "KeePass");

        // Act
        var result = sut.CaptureWithElements(0x300);

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(nameof(ScreenshotErrorCode.TargetDeniedByPolicy));
        _elements.Verify(e => e.GetUIElements(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<UIElementFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
        _screenshots.Verify(s => s.CaptureWindowWithElements(It.IsAny<nint>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<UIElementInfo>>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()), Times.Never);
    }

    [Fact]
    public void CaptureWindow_AllowedProcess_IsCaptured()
    {
        var sut = CreateScreenshotTools(0x200, "notepad");

        sut.CaptureWindow(0x200);

        _screenshots.Verify(s => s.CaptureWindow(new nint(0x200), true, It.IsAny<CaptureOptions>()), Times.Once);
    }

    #endregion
}
