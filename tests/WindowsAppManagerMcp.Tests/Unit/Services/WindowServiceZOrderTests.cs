namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// bringToFront in move_window, set_window_bounds, snap_window and move_window_to_monitor raises the
/// window in the z-order without activating it (D-WIN-16): SetWindowPos with HWND_TOP and
/// SWP_NOACTIVATE only. BringWindowToTop activates the window, so keyboard input would follow it
/// whenever the server happened to have foreground rights; activating is focus_window's job.
/// Every window is a fake on a mocked <see cref="INativeWindowWrapper"/>, so nothing real moves.
/// </summary>
public class WindowServiceZOrderTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwRestore = 9;

    private static readonly nint Window = 0x7000;

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly List<uint> _setWindowPosFlags = [];

    public WindowServiceZOrderTests()
    {
        _native.Setup(n => n.IsWindow(Window)).Returns(true);
        _native.Setup(n => n.GetWindowRect(Window)).Returns((100, 100, 900, 700));
        _native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Callback<nint, int, int, int, int, uint>((_, _, _, _, _, flags) => _setWindowPosFlags.Add(flags))
            .Returns(Win32CallResult.Ok);
        _monitors.Setup(m => m.CalculateSnapBounds(It.IsAny<int>(), It.IsAny<SnapPosition>()))
            .Returns(new MonitorRect(0, 0, 960, 1040));
    }

    private WindowService CreateSut() => new(_monitors.Object, _native.Object, Mock.Of<IHostGuard>());

    public static TheoryData<string> Operations => new()
    {
        "move_window",
        "set_window_bounds",
        "snap_window",
        "move_window_to_monitor",
        "move_window_to_monitor:restore",
    };

    private bool Run(string operation, bool bringToFront) => (operation switch
    {
        "move_window" => CreateSut().MoveWindow(Window, 10, 20, bringToFront),
        "set_window_bounds" => CreateSut().SetWindowBounds(Window, 10, 20, 800, 600, bringToFront),
        "snap_window" => CreateSut().SnapWindow(Window, SnapPosition.LeftHalf, monitorIndex: 0, bringToFront),
        "move_window_to_monitor" => CreateSut().MoveWindowToMonitor(Window, 0, MonitorPositioning.Center, bringToFront),
        "move_window_to_monitor:restore" => CreateSut().MoveWindowToMonitor(Window, 0, MonitorPositioning.Restore, bringToFront),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
    }).Success;

    [Theory]
    [MemberData(nameof(Operations))]
    public void BringToFront_RaisesTheWindowWithoutActivatingIt(string operation)
    {
        // Act
        var result = Run(operation, bringToFront: true);

        // Assert - one SetWindowPos that changes the z-order (no SWP_NOZORDER: HWND_TOP) but not the activation
        result.Should().BeTrue();
        _setWindowPosFlags.Should().ContainSingle();
        (_setWindowPosFlags[0] & SwpNoZOrder).Should().Be(0u, "bringToFront puts the window on top of the z-order");
        (_setWindowPosFlags[0] & SwpNoActivate).Should().Be(SwpNoActivate, "bringToFront must not activate the window");
        _native.Verify(n => n.BringWindowToTop(It.IsAny<nint>()), Times.Never,
            "BringWindowToTop activates the window, so keyboard input would follow it");
        _native.Verify(n => n.SetForegroundWindow(It.IsAny<nint>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void WithoutBringToFront_KeepsTheZOrderAndTheActivation(string operation)
    {
        // Act
        var result = Run(operation, bringToFront: false);

        // Assert
        result.Should().BeTrue();
        _setWindowPosFlags.Should().ContainSingle().Which.Should().Be(SwpNoZOrder | SwpNoActivate);
        _native.Verify(n => n.BringWindowToTop(It.IsAny<nint>()), Times.Never);
        _native.Verify(n => n.SetForegroundWindow(It.IsAny<nint>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void WhenSetWindowPosFails_ReportsFailure_AndDoesNothingElse(string operation)
    {
        // Arrange
        _native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(Win32CallResult.Failure(0));

        // Act
        var result = Run(operation, bringToFront: true);

        // Assert
        result.Should().BeFalse();
        _native.Verify(n => n.BringWindowToTop(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void SetWindowBounds_OfAMinimizedWindow_RestoresItBeforePlacingIt()
    {
        // Arrange - the restore is unchanged: SW_RESTORE (M2.3 makes geometry changes non-activating)
        _native.Setup(n => n.IsIconic(Window)).Returns(true);

        // Act
        var result = CreateSut().SetWindowBounds(Window, 10, 20, 800, 600, bringToFront: true);

        // Assert
        result.Success.Should().BeTrue();
        _native.Verify(n => n.ShowWindow(Window, SwRestore), Times.Once);
        _native.Verify(n => n.BringWindowToTop(It.IsAny<nint>()), Times.Never);
    }
}
