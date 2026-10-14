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
    private const uint CredDialogPid = 8000;

    private static readonly nint Frame = 0x6000;
    private static readonly nint TitleBar = 0x6001;
    private static readonly nint CoreWindow = 0x6002;
    private static readonly nint Notepad = 0x6100;
    private static readonly nint PromptFrame = 0x6200;      // a window of ApplicationFrameHost of another class
    private static readonly nint PromptCoreWindow = 0x6201; // CredDialogHost's CoreWindow inside it

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
    public void AppProcessId_OfAUwpFrame_DoesNotLookUpTheFrameHostsName()
    {
        // Arrange - the class alone makes it a frame, as before roadmap M3.F7
        Children(Frame, TitleBar, CoreWindow);

        // Act & Assert
        UwpFrames.AppProcessId(_native.Object, Frame).Should().Be(CalculatorPid);
        _native.Verify(n => n.GetProcessName(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public void AppProcessId_OfAFrameHostWindowOfAnotherClass_IsTheProcessOfTheCoreWindowItHosts()
    {
        // Arrange - a prompt may come up in a window of ApplicationFrameHost of another class (roadmap M3.F7)
        Window(PromptFrame, "PromptFrameWindow", FrameHostPid);
        Window(PromptCoreWindow, "Windows.UI.Core.CoreWindow", CredDialogPid);
        _native.Setup(n => n.GetProcessName(FrameHostPid)).Returns("ApplicationFrameHost");
        Children(PromptFrame, PromptCoreWindow);

        // Act & Assert
        UwpFrames.AppProcessId(_native.Object, PromptFrame).Should().Be(CredDialogPid);
    }

    [Fact]
    public void AppProcessId_OfAWindowOfAnotherProcess_IsItsOwnProcess_WithoutListingChildren()
    {
        // Arrange - a CoreWindow of another process under a window that ApplicationFrameHost does not own is no hosted app
        _native.Setup(n => n.GetProcessName(4242u)).Returns("notepad");
        Children(Notepad, CoreWindow);

        // Act & Assert
        UwpFrames.AppProcessId(_native.Object, Notepad).Should().Be(4242u);
        _native.Verify(n => n.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Theory]
    [InlineData("ApplicationFrameHost", true)]
    [InlineData("applicationframehost.exe", true)]
    [InlineData(@"C:\Windows\System32\ApplicationFrameHost.exe", true)]
    [InlineData("ApplicationFrameHostHelper", false)]
    [InlineData("CalculatorApp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFrameHostProcess_MatchesApplicationFrameHostOnly(string? processName, bool expected)
    {
        UwpFrames.IsFrameHostProcess(processName).Should().Be(expected);
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
