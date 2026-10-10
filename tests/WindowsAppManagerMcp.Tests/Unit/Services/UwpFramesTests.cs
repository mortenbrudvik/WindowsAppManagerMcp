namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// <see cref="UwpFrames"/>: a UWP (Store) app's top-level window is an ApplicationFrameWindow of the
/// shared ApplicationFrameHost.exe, while the app runs in the process of the CoreWindow the frame hosts.
/// Every window is a fake on a mocked <see cref="INativeWindowWrapper"/>.
/// </summary>
public class UwpFramesTests
{
    private const uint FrameHostPid = 6000;
    private const uint CalculatorPid = 7000;

    private static readonly nint Frame = 0x6000;
    private static readonly nint TitleBar = 0x6001;
    private static readonly nint CoreWindow = 0x6002;
    private static readonly nint Notepad = 0x6100;

    private readonly Mock<INativeWindowWrapper> _native = new();

    public UwpFramesTests()
    {
        Window(Frame, "ApplicationFrameWindow", FrameHostPid);
        Window(TitleBar, "ApplicationFrameTitleBarWindow", FrameHostPid);
        Window(CoreWindow, "Windows.UI.Core.CoreWindow", CalculatorPid);
        Window(Notepad, "Notepad", 4242);
    }

    private void Window(nint handle, string className, uint processId)
    {
        _native.Setup(n => n.GetClassName(handle)).Returns(className);
        _native.Setup(n => n.GetWindowThreadProcessId(handle)).Returns(processId);
    }

    private void Children(nint parent, params nint[] children) =>
        _native.Setup(n => n.EnumChildWindows(parent, It.IsAny<Func<nint, bool>>()))
            .Callback<nint, Func<nint, bool>>((_, callback) =>
            {
                foreach (var child in children)
                {
                    if (!callback(child))
                        break;
                }
            });

    [Fact]
    public void AppProcessId_OfAnOrdinaryWindow_IsItsOwnProcess_WithoutListingChildren()
    {
        UwpFrames.AppProcessId(_native.Object, Notepad).Should().Be(4242u);
        _native.Verify(n => n.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Fact]
    public void AppProcessId_OfAUwpFrame_IsTheProcessOfTheCoreWindowItHosts()
    {
        // Arrange
        Children(Frame, TitleBar, CoreWindow);

        // Act & Assert
        UwpFrames.AppProcessId(_native.Object, Frame).Should().Be(CalculatorPid);
    }

    [Fact]
    public void AppProcessId_OfAFrameWithoutItsCoreWindow_IsTheFrameHost()
    {
        // Arrange - Windows moves the CoreWindow of a minimized or suspended app out of the frame; the
        // frame is then all that a read of it reaches
        Children(Frame, TitleBar);

        // Act & Assert
        UwpFrames.AppProcessId(_native.Object, Frame).Should().Be(FrameHostPid);
    }

    [Fact]
    public void AppProcessId_OfAWindowThatIsGone_IsZero()
    {
        UwpFrames.AppProcessId(_native.Object, new nint(0x9999)).Should().Be(0u);
    }

    [Fact]
    public void FindHostedAppProcessId_SkipsCoreWindowsOfTheFrameHostAndOfNoProcess()
    {
        // Arrange
        var hostCoreWindow = new nint(0x6003);
        var goneCoreWindow = new nint(0x6004);
        Window(hostCoreWindow, "Windows.UI.Core.CoreWindow", FrameHostPid);
        Window(goneCoreWindow, "Windows.UI.Core.CoreWindow", 0);
        Children(Frame, hostCoreWindow, goneCoreWindow, CoreWindow);

        // Act & Assert
        UwpFrames.FindHostedAppProcessId(_native.Object, Frame, FrameHostPid).Should().Be(CalculatorPid);
    }

    [Fact]
    public void FindHostedAppProcessId_WithNoCoreWindowChild_IsNull()
    {
        // Arrange
        Children(Frame, TitleBar);

        // Act & Assert
        UwpFrames.FindHostedAppProcessId(_native.Object, Frame, FrameHostPid).Should().BeNull();
    }
}
