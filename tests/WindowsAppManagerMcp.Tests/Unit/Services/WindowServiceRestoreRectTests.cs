namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// A window's restore rectangle (roadmap M6.8): <see cref="WindowService.GetNormalBounds"/> reads it through
/// GetWindowPlacement in screen coordinates, and <see cref="WindowService.SetMinimizedBounds"/> gives a minimized window a
/// new one through SetWindowPlacement with SW_SHOWMINNOACTIVE, so it stays minimized and is never shown or activated. All on
/// a mocked <see cref="INativeWindowWrapper"/>: nothing real changes.
/// </summary>
public class WindowServiceRestoreRectTests
{
    private const int SwShowMinimized = 2, SwShowMinNoActive = 7, WpfSetMinPosition = 1, WpfRestoreToMaximized = 2;
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;

    private static readonly nint Window = 0x5151;

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IMonitorService> _monitors = new();
    private NativeWindowPlacement _placement = new(SwShowMinimized, WpfSetMinPosition | WpfRestoreToMaximized, (-32000, -32000), (-1, -1),
        (100, 100, 900, 700));
    private readonly List<NativeWindowPlacement> _set = [];
    private Win32CallResult _setResult = Win32CallResult.Ok;
    private bool _applySet = true;

    public WindowServiceRestoreRectTests()
    {
        // Taskbar on the left: workspace x 0 is screen x 48
        _monitors.Setup(m => m.GetAllMonitors()).Returns([
            new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(48, 0, 1872, 1080), 1.0)
        ]);
        _native.Setup(n => n.IsWindow(Window)).Returns(true);
        _native.Setup(n => n.IsWindowVisible(Window)).Returns(true);
        _native.Setup(n => n.IsIconic(Window)).Returns(true);
        _native.Setup(n => n.GetWindowPlacement(Window)).Returns(() => _placement);
        _native.Setup(n => n.SetWindowPlacement(Window, It.IsAny<NativeWindowPlacement>()))
            .Returns((nint _, NativeWindowPlacement placement) =>
            {
                _set.Add(placement);
                if (_setResult.Succeeded && _applySet)
                {
                    _placement = _placement with { NormalPosition = placement.NormalPosition };
                }

                return _setResult;
            });
    }

    private WindowService CreateSut() => new(_monitors.Object, _native.Object, Mock.Of<IHostGuard>(), stateChangeTimeout: TimeSpan.Zero);

    [Fact]
    public void GetNormalBounds_IsTheRestoreRectangle_InScreenCoordinates()
    {
        CreateSut().GetNormalBounds(Window).Should().Be(new WindowRect(148, 100, 800, 600));
    }

    [Fact]
    public void GetNormalBounds_OfAToolWindow_IsAlreadyInScreenCoordinates()
    {
        _native.Setup(n => n.GetWindowLongPtr(Window, GwlExStyle)).Returns(WsExToolWindow);

        CreateSut().GetNormalBounds(Window).Should().Be(new WindowRect(100, 100, 800, 600));
    }

    [Fact]
    public void GetNormalBounds_IsNullWhenThePlacementCannotBeRead()
    {
        _native.Setup(n => n.GetWindowPlacement(Window)).Returns((NativeWindowPlacement?)null);

        CreateSut().GetNormalBounds(Window).Should().BeNull();
    }

    [Fact]
    public void SetMinimizedBounds_SetsTheRestoreRectangle_AndKeepsTheWindowMinimized_WithoutShowingIt()
    {
        var result = CreateSut().SetMinimizedBounds(Window, 248, 50, 1000, 800);

        result.Success.Should().BeTrue(result.Error);
        var set = _set.Should().ContainSingle().Subject;
        set.ShowCmd.Should().Be(SwShowMinNoActive, "SW_SHOWMINNOACTIVE leaves the window minimized and does not activate it");
        set.NormalPosition.Should().Be((200, 50, 1200, 850), "the rectangle goes in as workspace coordinates");
        set.Flags.Should().Be(WpfRestoreToMaximized, "whether it restores to maximized is the window's own, and no icon position is set");
        _native.Verify(n => n.ShowWindow(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        _native.Verify(n => n.SetWindowPos(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()),
            Times.Never);
    }

    [Fact]
    public void SetMinimizedBounds_OfAWindowThatIsNotMinimized_IsRefused_AndNothingIsSent()
    {
        _native.Setup(n => n.IsIconic(Window)).Returns(false);

        var result = CreateSut().SetMinimizedBounds(Window, 248, 50, 1000, 800);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
        _set.Should().BeEmpty();
    }

    [Fact]
    public void SetMinimizedBounds_OfAHiddenWindow_IsRefused_SoItIsNotShown()
    {
        // A tray app hides its window after minimizing it; SW_SHOWMINNOACTIVE would show it on the taskbar
        _native.Setup(n => n.IsWindowVisible(Window)).Returns(false);

        var result = CreateSut().SetMinimizedBounds(Window, 248, 50, 1000, 800);

        result.ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
        result.Error.Should().Contain("hidden");
        _set.Should().BeEmpty();
    }

    [Fact]
    public void SetMinimizedBounds_ThatDoesNotTakeHold_IsMoveFailed()
    {
        // The read-back shows the old rectangle
        _applySet = false;

        var result = CreateSut().SetMinimizedBounds(Window, 248, 50, 1000, 800);

        result.ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
    }

    [Fact]
    public void SetMinimizedBounds_RefusedByWindows_IsAccessDenied()
    {
        _setResult = Win32CallResult.Failure(5);

        CreateSut().SetMinimizedBounds(Window, 248, 50, 1000, 800).ErrorCode.Should().Be(WindowErrorCode.AccessDenied);
    }

    [Fact]
    public void SetMinimizedBounds_OfAWindowThatIsNoWindow_IsInvalidHandle()
    {
        _native.Setup(n => n.IsWindow(Window)).Returns(false);

        CreateSut().SetMinimizedBounds(Window, 248, 50, 1000, 800).ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        _set.Should().BeEmpty();
    }
}
