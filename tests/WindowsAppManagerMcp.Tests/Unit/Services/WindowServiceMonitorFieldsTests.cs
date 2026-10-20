namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// A window row's monitor (issue #12): its index and id come from the one MonitorFromWindow lookup, for a minimized window
/// the monitor it restores to, and are left out when that monitor cannot be read instead of reading monitor 0; its zone and
/// spansMonitors come from its visible frame on that monitor (<see cref="WindowZones"/>); and get_window_info's 'full' read
/// tells where a minimized window comes back to. One fake window on a mocked <see cref="INativeWindowWrapper"/>.
/// </summary>
public class WindowServiceMonitorFieldsTests
{
    private const int GwlExStyle = -20, SwShowMinimized = 2, WpfRestoreToMaximized = 2;
    private const nint WsExToolWindow = 0x80;

    private static readonly nint Window = 0x6161;

    // The primary at 125% with its taskbar on the left, and a 1080p monitor to its right
    private static readonly MonitorInfo Primary = new(0, @"\\.\DISPLAY1", true,
        new MonitorRect(0, 0, 2560, 1440), new MonitorRect(48, 0, 2512, 1440), 1.25) { Id = "m-0d6fc36f" };

    private static readonly MonitorInfo Right = new(1, @"\\.\DISPLAY2", false,
        new MonitorRect(2560, 0, 1920, 1080), new MonitorRect(2560, 0, 1920, 1040), 1.0) { Id = "m-81c07e5b" };

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IMonitorService> _monitors = new();
    private MonitorInfo? _monitor = Primary;
    private WindowRect _frame = new(48, 0, 1256, 1440);
    private bool _minimized;
    private NativeWindowPlacement? _placement;

    public WindowServiceMonitorFieldsTests()
    {
        _native.Setup(n => n.IsWindow(Window)).Returns(true);
        _native.Setup(n => n.IsWindowVisible(Window)).Returns(true);
        _native.Setup(n => n.GetClassName(Window)).Returns("Notepad");
        _native.Setup(n => n.GetWindowText(Window)).Returns("notes.txt - Notepad");
        _native.Setup(n => n.GetWindowThreadProcessId(Window)).Returns(100u);
        _native.Setup(n => n.GetProcessName(100u)).Returns("notepad");
        _native.Setup(n => n.IsIconic(Window)).Returns(() => _minimized);
        _native.Setup(n => n.GetWindowRect(Window)).Returns(() => _minimized
            ? (-32000, -32000, -31840, -31972)
            : (_frame.Left - 9, _frame.Top, _frame.Right + 9, _frame.Bottom + 9));
        _native.Setup(n => n.GetExtendedFrameBounds(Window)).Returns(() => _minimized
            ? (-32000, -32000, -31840, -31972)
            : (_frame.Left, _frame.Top, _frame.Right, _frame.Bottom));
        _native.Setup(n => n.GetWindowPlacement(Window)).Returns(() => _placement);
        _monitors.Setup(m => m.GetMonitorForWindow(Window)).Returns(() => _monitor);
        _monitors.Setup(m => m.GetAllMonitors()).Returns([Primary, Right]);
    }

    private WindowService CreateSut() => new(_monitors.Object, _native.Object, Mock.Of<IHostGuard>());

    #region The monitor

    [Fact]
    public void ARow_NamesItsMonitor_ByIndexAndId_FromOneLookup()
    {
        // Arrange
        _monitor = Right;
        _frame = new WindowRect(2700, 100, 800, 600);

        // Act
        var info = CreateSut().GetWindowInfo(Window)!;

        // Assert - the id comes with the index: no second native call
        info.MonitorIndex.Should().Be(1);
        info.MonitorId.Should().Be("m-81c07e5b");
        _monitors.Verify(m => m.GetMonitorForWindow(Window), Times.Once);
        _monitors.Verify(m => m.GetMonitorIndex(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void ARowWhoseMonitorCannotBeRead_HasNoMonitorFields_NotMonitor0()
    {
        _monitor = null;

        var info = CreateSut().GetWindowInfo(Window)!;

        info.MonitorIndex.Should().BeNull("it used to say 0, a monitor the window may not be on");
        info.MonitorId.Should().BeNull();
        info.Zone.Should().BeNull();
        info.SpansMonitors.Should().BeFalse();
    }

    [Fact]
    public void AMonitorWithoutAnId_GivesTheIndexAlone()
    {
        // The display configuration could not be read, or reports no device path (a remote or virtual display)
        _monitor = Primary with { Id = null };

        var info = CreateSut().GetWindowInfo(Window)!;

        info.MonitorIndex.Should().Be(0);
        info.MonitorId.Should().BeNull();
    }

    [Fact]
    public void AMinimizedWindow_ReportsTheMonitorItRestoresTo_AndNoZone()
    {
        // Arrange - its rect is the icon at -32000, nearest the primary; MonitorFromWindow uses the rect from before the minimize
        _minimized = true;
        _monitor = Right;

        // Act
        var info = CreateSut().GetWindowInfo(Window)!;

        // Assert
        info.State.Should().Be(WindowState.Minimized);
        info.MonitorIndex.Should().Be(1);
        info.MonitorId.Should().Be("m-81c07e5b");
        info.Zone.Should().BeNull();
        info.SpansMonitors.Should().BeFalse();
    }

    #endregion

    #region The zone

    [Fact]
    public void ARow_ReportsTheZoneItsVisibleFrameFills()
    {
        // The left half of the primary's work area (the taskbar on the left), as another program left it
        _frame = new WindowRect(48, 0, 1256, 1440);

        CreateSut().GetWindowInfo(Window)!.Zone.Should().Be("left_half");
    }

    [Fact]
    public void AWindowWithoutAVisibleFrame_IsMatchedByItsWindowRect()
    {
        // DWM cannot report the frame; the window rect (here without borders) is what the geometry results report then
        _native.Setup(n => n.GetExtendedFrameBounds(Window)).Returns(((int, int, int, int)?)null);
        _native.Setup(n => n.GetWindowRect(Window)).Returns((2560, 0, 4480, 1040));
        _monitor = Right;

        CreateSut().GetWindowInfo(Window)!.Zone.Should().Be("fullscreen");
    }

    [Fact]
    public void AWindowAcrossTwoMonitors_SpansMonitors_AndFillsNoZone()
    {
        _frame = new WindowRect(2000, 100, 1200, 800);

        var info = CreateSut().GetWindowInfo(Window)!;

        info.SpansMonitors.Should().BeTrue();
        info.Zone.Should().BeNull();
    }

    #endregion

    #region Where a minimized window comes back to (get_window_info 'full')

    [Fact]
    public void AMinimizedWindow_InFull_TellsWhereItComesBack_InScreenCoordinates()
    {
        // Arrange - the restore rect is in workspace coordinates: x 0 is screen x 48, beside the taskbar on the left
        _minimized = true;
        _placement = new NativeWindowPlacement(SwShowMinimized, 0, (-32000, -32000), (-1, -1), (100, 50, 900, 650));

        // Act
        var restore = CreateSut().GetWindowDetails(Window)!.Restore;

        // Assert
        restore.Should().NotBeNull();
        restore!.State.Should().Be(WindowState.Normal);
        restore.Bounds.Should().Be(new WindowRect(148, 50, 800, 600));
        restore.Monitor.Should().Be(Primary);
    }

    [Fact]
    public void AMinimizedWindowThatComesBackMaximized_SaysSo()
    {
        _minimized = true;
        _placement = new NativeWindowPlacement(SwShowMinimized, WpfRestoreToMaximized, (-32000, -32000), (-1, -1), (100, 50, 900, 650));

        CreateSut().GetWindowDetails(Window)!.Restore!.State.Should().Be(WindowState.Maximized);
    }

    [Fact]
    public void TheRestoreMonitor_IsTheMonitorOfTheRestoreRectangle()
    {
        // A rect on the right monitor (whose work area starts at its corner)
        _minimized = true;
        _placement = new NativeWindowPlacement(SwShowMinimized, 0, (-32000, -32000), (-1, -1), (2700, 100, 3500, 700));

        var restore = CreateSut().GetWindowDetails(Window)!.Restore!;

        restore.Bounds.Should().Be(new WindowRect(2700, 100, 800, 600));
        restore.Monitor.Should().Be(Right);
    }

    [Fact]
    public void AToolWindowsRestoreRectangle_IsAlreadyInScreenCoordinates()
    {
        _minimized = true;
        _native.Setup(n => n.GetWindowLongPtr(Window, GwlExStyle)).Returns(WsExToolWindow);
        _placement = new NativeWindowPlacement(SwShowMinimized, 0, (-32000, -32000), (-1, -1), (100, 50, 900, 650));

        CreateSut().GetWindowDetails(Window)!.Restore!.Bounds.Should().Be(new WindowRect(100, 50, 800, 600));
    }

    [Fact]
    public void AWindowThatIsNotMinimized_HasNoRestore_AndItsPlacementIsNotRead()
    {
        _placement = new NativeWindowPlacement(1, 0, (-1, -1), (-1, -1), (100, 50, 900, 650));

        CreateSut().GetWindowDetails(Window)!.Restore.Should().BeNull();
        _native.Verify(n => n.GetWindowPlacement(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void AMinimizedWindowWhosePlacementCannotBeRead_HasNoRestore()
    {
        _minimized = true;
        _placement = null;

        CreateSut().GetWindowDetails(Window)!.Restore.Should().BeNull();
    }

    [Fact]
    public void TheListingsDoNotReadTheRestore()
    {
        // Only get_window_info's 'full' read pays for GetWindowPlacement; a listing row of a minimized window does not
        _minimized = true;
        _placement = new NativeWindowPlacement(SwShowMinimized, 0, (-32000, -32000), (-1, -1), (100, 50, 900, 650));

        CreateSut().GetWindowInfo(Window);

        _native.Verify(n => n.GetWindowPlacement(It.IsAny<nint>()), Times.Never);
    }

    #endregion
}
