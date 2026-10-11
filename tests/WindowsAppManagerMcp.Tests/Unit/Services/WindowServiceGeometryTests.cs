namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The geometry fixes of roadmap M2.3, against a fake window on a mocked <see cref="INativeWindowWrapper"/> (nothing real
/// moves): a minimized or maximized window is restored without activation before it is measured or placed, the visible
/// frame is placed (the invisible resize borders are added), move keeps the size and resize the position, a window is
/// moved to another monitor at its normal size clamped to the work area, in two steps across a DPI change and without
/// scaling the size in advance, and 'maximize' happens there, verified.
/// </summary>
public class WindowServiceGeometryTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const int SwShowNormal = 1, SwMaximize = 3, SwShowNoActivate = 4, SwRestore = 9;

    private static readonly nint Window = 0x4242;

    private readonly Mock<INativeWindowWrapper> _native = new();
    private readonly Mock<IMonitorService> _monitors = new();
    private readonly FakeWindow _window;
    private List<MonitorInfo> _monitorList =
    [
        TestDataFactory.CreateMonitorInfo(index: 0),
        TestDataFactory.CreateMonitorInfo(index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: false,
            boundsX: 1920, boundsWidth: 2560, boundsHeight: 1440, workAreaX: 1920, workAreaWidth: 2560, workAreaHeight: 1400)
    ];

    public WindowServiceGeometryTests()
    {
        _window = new FakeWindow(this);
        _native.Setup(n => n.IsWindow(Window)).Returns(true);
        _monitors.Setup(m => m.GetAllMonitors()).Returns(() => _monitorList);
        _monitors.Setup(m => m.GetMonitorIndex(Window)).Returns(() => _window.MonitorIndex);
        _monitors.Setup(m => m.CalculateSnapBounds(It.IsAny<int>(), It.IsAny<SnapPosition>()))
            .Returns((int index, SnapPosition position) => SnapMath.Calculate(_monitorList[index].WorkArea, position));
    }

    private WindowService CreateSut() =>
        new(_monitors.Object, _native.Object, Mock.Of<IHostGuard>(), stateChangeTimeout: TimeSpan.Zero);

    private void UseScales(double monitor0, double monitor1) =>
        _monitorList = [_monitorList[0] with { ScaleFactor = monitor0 }, _monitorList[1] with { ScaleFactor = monitor1 }];

    #region The visible frame is placed

    [Fact]
    public void SetWindowFrame_GrowsTheTargetByTheInvisibleBorders()
    {
        // Act
        var result = CreateSut().SetWindowFrame(Window, 0, 0, 960, 1040, bringToFront: false);

        // Assert - GetWindowRect is 7 px wider on each side and 7 px taller; the visible frame is exactly the target
        result.Success.Should().BeTrue(result.Error);
        _window.Calls.Should().ContainSingle().Which.Should().Be(new Placement(-7, 0, 974, 1047, SwpNoZOrder | SwpNoActivate));
        _window.Visible.Should().Be(new WindowRect(0, 0, 960, 1040));
    }

    [Fact]
    public void SetWindowBounds_Outer_PlacesTheWindowRectAsGiven()
    {
        // Layouts save and apply GetWindowRect, so their placements stay where they were saved
        CreateSut().SetWindowBounds(Window, 0, 0, 960, 1040, bringToFront: false).Success.Should().BeTrue();

        _window.Calls.Should().ContainSingle().Which.Should().Be(new Placement(0, 0, 960, 1040, SwpNoZOrder | SwpNoActivate));
    }

    [Fact]
    public void SetWindowFrame_OfAWindowWithoutAVisibleFrame_PlacesTheWindowRect()
    {
        // Arrange - DWM cannot report the frame
        _window.HasFrame = false;

        // Act
        CreateSut().SetWindowFrame(Window, 0, 0, 960, 1040, bringToFront: false);

        // Assert
        _window.Calls.Should().ContainSingle().Which.Should().Be(new Placement(0, 0, 960, 1040, SwpNoZOrder | SwpNoActivate));
    }

    [Fact]
    public void MoveWindow_MovesTheVisibleFrame_AndKeepsTheSize()
    {
        // Act
        var result = CreateSut().MoveWindow(Window, 300, 200, bringToFront: false);

        // Assert - SWP_NOSIZE: the window keeps the size it has, whatever GetWindowRect said a moment ago
        result.Success.Should().BeTrue(result.Error);
        var call = _window.Calls.Should().ContainSingle().Subject;
        (call.Flags & SwpNoSize).Should().Be(SwpNoSize);
        (call.Flags & SwpNoMove).Should().Be(0u);
        (call.X, call.Y).Should().Be((293, 200));
        _window.Visible.Should().Be(new WindowRect(300, 200, 800, 600));
    }

    [Fact]
    public void ResizeWindow_ResizesTheVisibleFrame_AndKeepsThePosition()
    {
        // Act
        var result = CreateSut().ResizeWindow(Window, 1000, 700);

        // Assert - SWP_NOMOVE, and never raised
        result.Success.Should().BeTrue(result.Error);
        var call = _window.Calls.Should().ContainSingle().Subject;
        call.Flags.Should().Be(SwpNoMove | SwpNoZOrder | SwpNoActivate);
        (call.Width, call.Height).Should().Be((1014, 707));
        _window.Visible.Should().Be(new WindowRect(100, 100, 1000, 700));
    }

    [Fact]
    public void SnapWindow_PlacesTheVisibleFrame_SoHalvesMeetEdgeToEdge()
    {
        // Act
        CreateSut().SnapWindow(Window, SnapPosition.RightHalf, monitorIndex: 0).Success.Should().BeTrue();

        // Assert - no gap at the work area's right and bottom edges, nor at the middle
        _window.Visible.Should().Be(new WindowRect(960, 0, 960, 1040));
        _window.Outer.Should().Be(new WindowRect(953, 0, 974, 1047));
    }

    #endregion

    #region A minimized or maximized window is restored without activation first

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("bounds")]
    [InlineData("snap")]
    [InlineData("monitor")]
    public void APlacement_OfAMaximizedWindow_RestoresItWithoutActivation_BeforePlacingIt(string operation)
    {
        // Arrange - placing a window that keeps WS_MAXIMIZE leaves it 'maximized' at the new size
        _window.State = WindowState.Maximized;

        // Act
        var result = Place(operation);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _window.State.Should().Be(WindowState.Normal);
        _window.Calls.Should().NotBeEmpty().And.OnlyContain(c => c.StateWhenPlaced == WindowState.Normal);
        _native.Verify(n => n.ShowWindow(Window, SwShowNoActivate), Times.Once);
        _native.Verify(n => n.ShowWindow(Window, SwRestore), Times.Never, "SW_RESTORE activates the window");
        _native.Verify(n => n.ShowWindow(Window, SwShowNormal), Times.Never, "SW_SHOWNORMAL activates the window");
        _native.Verify(n => n.SetForegroundWindow(It.IsAny<nint>()), Times.Never);
        _native.Verify(n => n.BringWindowToTop(It.IsAny<nint>()), Times.Never);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("monitor")]
    public void APlacement_OfAWindowMinimizedFromMaximized_RestoresItToNormal_InAtMostTwoSteps(string operation)
    {
        // Arrange - the first restore of such a window returns it to maximized
        _window.State = WindowState.Minimized;
        _window.RestoresToMaximized = true;

        // Act
        var result = Place(operation);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _window.State.Should().Be(WindowState.Normal);
        _native.Verify(n => n.ShowWindow(Window, SwShowNoActivate), Times.Exactly(2));
        _window.Calls.Should().OnlyContain(c => c.StateWhenPlaced == WindowState.Normal);
    }

    [Fact]
    public void APlacement_OfAWindowThatStaysMaximized_IsMoveFailed_AndPlacesNothing()
    {
        // Arrange - the application ignores the restore
        _window.State = WindowState.Maximized;
        _window.IgnoresShowCommands = true;

        // Act
        var result = CreateSut().SnapWindow(Window, SnapPosition.LeftHalf, monitorIndex: 0);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
        result.Error.Should().Contain("maximized").And.Contain("restore");
        _window.Calls.Should().BeEmpty();
        _native.Verify(n => n.ShowWindow(Window, SwShowNoActivate), Times.Exactly(2), "at most twice");
    }

    [Fact]
    public void ResizeWindow_OfAWindowThatStaysMinimized_IsResizeFailed()
    {
        _window.State = WindowState.Minimized;
        _window.IgnoresShowCommands = true;

        CreateSut().ResizeWindow(Window, 640, 480).ErrorCode.Should().Be(WindowErrorCode.ResizeFailed);
    }

    [Fact]
    public void MoveWindowToMonitor_OfAMinimizedWindow_MeasuresItAfterTheRestore()
    {
        // Arrange - a minimized window's rect is the 160x28 icon at -32000; measured first, it shrank to that size
        _window.State = WindowState.Minimized;

        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 0, MonitorPositioning.Center);

        // Assert - centered at its normal size
        result.Success.Should().BeTrue(result.Error);
        _window.Visible.Should().Be(new WindowRect(560, 220, 800, 600));
    }

    #endregion

    #region move_window_to_monitor

    [Fact]
    public void MoveWindowToMonitor_AWindowLargerThanTheTarget_IsClampedToItsWorkArea()
    {
        // Arrange - 3000 x 2000 does not fit the 1920 x 1040 work area of monitor 0
        _window.Visible = new WindowRect(2000, 0, 3000, 2000);

        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 0, MonitorPositioning.Center);

        // Assert - the whole visible frame, title bar included, is inside the work area
        result.Success.Should().BeTrue(result.Error);
        _window.Visible.Should().Be(new WindowRect(0, 0, 1920, 1040));
    }

    [Theory]
    [InlineData(MonitorPositioning.TopLeft, 1920, 0)]
    [InlineData(MonitorPositioning.TopRight, 3680, 0)]
    [InlineData(MonitorPositioning.BottomLeft, 1920, 800)]
    [InlineData(MonitorPositioning.BottomRight, 3680, 800)]
    [InlineData(MonitorPositioning.Center, 2800, 400)]
    [InlineData(MonitorPositioning.Restore, 1920, 0)]
    public void MoveWindowToMonitor_PositionsTheVisibleFrameInTheTargetWorkArea(MonitorPositioning positioning, int x, int y)
    {
        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 1, positioning);

        // Assert - 800 x 600, edges flush with the work area's edges
        result.Success.Should().BeTrue(result.Error);
        _window.Visible.Should().Be(new WindowRect(x, y, 800, 600));
    }

    [Fact]
    public void MoveWindowToMonitor_ToAMonitorWithTheSameScale_PlacesTheWindowOnce()
    {
        CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Center).Success.Should().BeTrue();

        _window.Calls.Should().ContainSingle();
    }

    [Fact]
    public void MoveWindowToMonitor_ToAMonitorWithAnotherScale_MovesFirst_ThenSizesFromTheSizeTheAppChose()
    {
        // Arrange - 100% to 150%: a per-monitor-aware app rescales itself to 1200 x 900 when it arrives (WM_DPICHANGED)
        UseScales(1.0, 1.5);
        _window.RescalesOnDpiChange = true;

        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Center);

        // Assert - step one only moves (SWP_NOSIZE): the size is never scaled in advance, or the app scales it again
        result.Success.Should().BeTrue(result.Error);
        _window.Calls.Should().HaveCount(2);
        (_window.Calls[0].Flags & SwpNoSize).Should().Be(SwpNoSize);
        _window.Calls[0].MonitorWhenPlaced.Should().Be(0);
        (_window.Calls[1].Flags & SwpNoSize).Should().Be(0u);
        _window.Visible.Should().Be(new WindowRect(1920 + (2560 - 1200) / 2, (1400 - 900) / 2, 1200, 900));
        _window.Insets.Should().Be(new FrameInsets(11, 0, 11, 11), "the borders grew with the scale and were measured again");
    }

    [Fact]
    public void MoveWindowToMonitor_AcrossADpiChange_ClampsTheSizeTheAppChose()
    {
        // Arrange - 1800 x 1000 at 100% becomes 2700 x 1500 at 150%, more than the 2560 x 1400 work area
        UseScales(1.0, 1.5);
        _window.RescalesOnDpiChange = true;
        _window.Visible = new WindowRect(0, 0, 1800, 1000);

        // Act
        CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Center).Success.Should().BeTrue();

        // Assert
        _window.Visible.Should().Be(new WindowRect(1920, 0, 2560, 1400));
    }

    [Fact]
    public void MoveWindowToMonitor_ThatLeavesTheWindowOnAnotherMonitor_IsMoveFailed()
    {
        // Arrange - an app that keeps its position
        _window.IgnoresPlacement = true;

        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Center);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
        result.Error.Should().Contain("on monitor 0, not on monitor 1");
    }

    [Fact]
    public void MoveWindowToMonitor_Maximize_OfAWindowMaximizedOnAnotherMonitor_RestoresMovesThenMaximizesItThere()
    {
        // Arrange - SW_MAXIMIZE alone would maximize it on monitor 0 again
        _window.State = WindowState.Maximized;

        // Act
        var result = CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Maximize);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _window.Calls.Should().ContainSingle().Which.StateWhenPlaced.Should().Be(WindowState.Normal);
        _window.Calls[0].MonitorWhenPlaced.Should().Be(0);
        _window.State.Should().Be(WindowState.Maximized);
        _window.MonitorIndex.Should().Be(1);
        _native.Verify(n => n.ShowWindow(Window, SwShowNoActivate), Times.Once);
        _native.Verify(n => n.ShowWindow(Window, SwMaximize), Times.Once);
    }

    [Fact]
    public void MoveWindowToMonitor_Maximize_ThatEndsOnAnotherMonitor_IsMoveFailed()
    {
        _window.IgnoresPlacement = true;

        var result = CreateSut().MoveWindowToMonitor(Window, 1, MonitorPositioning.Maximize);

        result.ErrorCode.Should().Be(WindowErrorCode.MoveFailed);
        result.Error.Should().Contain("maximized on monitor 0, not on monitor 1");
    }

    #endregion

    #region A placement that crosses a DPI change is applied again

    [Fact]
    public void SnapWindow_ToAMonitorWithAnotherScale_PlacesTheTargetAgain_AfterTheAppRescaledItself()
    {
        // Arrange - the first SetWindowPos takes the window to 150%, where the app applies WM_DPICHANGED's rect
        UseScales(1.0, 1.5);
        _window.RescalesOnDpiChange = true;

        // Act
        var result = CreateSut().SnapWindow(Window, SnapPosition.LeftHalf, monitorIndex: 1, bringToFront: false);

        // Assert - as FancyZones does: the snap rect is placed again, with the borders measured at the new scale
        result.Success.Should().BeTrue(result.Error);
        _window.Calls.Should().HaveCount(2);
        _window.Visible.Should().Be(new WindowRect(1920, 0, 1280, 1400));
    }

    [Fact]
    public void MoveWindow_ToAMonitorWithAnotherScale_KeepsTheSizeTheAppChose_AndAlignsTheVisibleFrame()
    {
        // Arrange
        UseScales(1.0, 1.5);
        _window.RescalesOnDpiChange = true;

        // Act
        var result = CreateSut().MoveWindow(Window, 2000, 100, bringToFront: false);

        // Assert - move keeps the app's own size (SWP_NOSIZE both times); the frame ends at (2000, 100)
        result.Success.Should().BeTrue(result.Error);
        _window.Calls.Should().HaveCount(2).And.OnlyContain(c => (c.Flags & SwpNoSize) == SwpNoSize);
        _window.Visible.Should().Be(new WindowRect(2000, 100, 1200, 900));
    }

    #endregion

    #region Settling and reading

    [Fact]
    public void WaitForSettledFrame_ReturnsOnceTwoReadsAreEqual()
    {
        // Arrange - the app moves the window during the first reads
        var reads = 0;
        _native.Setup(n => n.GetWindowRect(Window)).Returns(() => ++reads < 4 ? (reads * 10, 0, 800, 600) : (40, 0, 800, 600));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        CreateSut().WaitForSettledFrame(Window, TimeSpan.FromSeconds(5));

        // Assert
        reads.Should().BeInRange(5, 6);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void WaitForSettledFrame_OfAWindowThatKeepsMoving_StopsAtTheTimeout()
    {
        var reads = 0;
        _native.Setup(n => n.GetWindowRect(Window)).Returns(() => (++reads, 0, 800, 600));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        CreateSut().WaitForSettledFrame(Window, TimeSpan.FromMilliseconds(100));

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        reads.Should().BeGreaterThan(1);
    }

    [Fact]
    public void WaitForSettledFrame_WithZero_ReadsNothing()
    {
        CreateSut().WaitForSettledFrame(Window, TimeSpan.Zero);

        _native.Verify(n => n.GetWindowRect(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void GetWindowInfo_ReportsTheVisibleFrame_NextToTheWindowRect()
    {
        var info = CreateSut().GetWindowInfo(Window);

        info!.Bounds.Should().Be(new WindowRect(93, 100, 814, 607));
        info.VisibleBounds.Should().Be(new WindowRect(100, 100, 800, 600));
    }

    [Fact]
    public void GetWindowInfo_WithoutAVisibleFrame_HasNoVisibleBounds()
    {
        _window.HasFrame = false;

        CreateSut().GetWindowInfo(Window)!.VisibleBounds.Should().BeNull();
    }

    [Theory]
    [InlineData(250, 250)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(int.MaxValue, 5000)]
    public void SettleDelay_IsBetweenZeroAndMaxSettleMs(int settleMs, int expected)
    {
        WindowService.SettleDelay(settleMs).Should().Be(TimeSpan.FromMilliseconds(expected));
    }

    #endregion

    private WindowChangeResult Place(string operation) => operation switch
    {
        "move" => CreateSut().MoveWindow(Window, 10, 20),
        "resize" => CreateSut().ResizeWindow(Window, 640, 480),
        "bounds" => CreateSut().SetWindowFrame(Window, 10, 20, 640, 480),
        "snap" => CreateSut().SnapWindow(Window, SnapPosition.LeftHalf, monitorIndex: 0),
        _ => CreateSut().MoveWindowToMonitor(Window, 0, MonitorPositioning.Center)
    };

    private sealed record Placement(int X, int Y, int Width, int Height, uint Flags)
    {
        public WindowState StateWhenPlaced { get; init; }

        public int MonitorWhenPlaced { get; init; }

        // Calls compare by their arguments
        public bool Equals(Placement? other) =>
            other is not null && (X, Y, Width, Height, Flags) == (other.X, other.Y, other.Width, other.Height, other.Flags);

        public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height, Flags);
    }

    /// <summary>
    /// A window as Windows keeps it: a normal rect (the visible frame plus borders of 7 px at 100%, scaled with the
    /// monitor's scale), a show state, and a 160x28 icon rect at -32000 while minimized. Its monitor is the one that
    /// holds the centre of its rect. SetWindowPos honours SWP_NOSIZE and SWP_NOMOVE; with RescalesOnDpiChange the
    /// window, like a per-monitor-aware app handling WM_DPICHANGED, scales itself by the ratio of the two scales when a
    /// placement takes it to a monitor with another scale.
    /// </summary>
    private sealed class FakeWindow
    {
        private readonly WindowServiceGeometryTests _test;

        public FakeWindow(WindowServiceGeometryTests test)
        {
            _test = test;
            var native = test._native;
            native.Setup(n => n.IsIconic(Window)).Returns(() => State == WindowState.Minimized);
            native.Setup(n => n.IsZoomed(Window)).Returns(() => State == WindowState.Maximized);
            native.Setup(n => n.IsWindowVisible(Window)).Returns(true);
            native.Setup(n => n.GetWindowRect(Window)).Returns(() => Ltrb(CurrentOuter));
            native.Setup(n => n.GetExtendedFrameBounds(Window)).Returns(() => HasFrame ? Ltrb(CurrentVisible) : null);
            native.Setup(n => n.ShowWindow(Window, It.IsAny<int>())).Returns((nint _, int command) => Show(command));
            native.Setup(n => n.SetWindowPos(Window, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
                .Returns((nint _, int x, int y, int width, int height, uint flags) => SetPos(x, y, width, height, flags));
        }

        public WindowState State { get; set; } = WindowState.Normal;

        public bool RestoresToMaximized { get; set; }

        public bool IgnoresShowCommands { get; set; }

        public bool IgnoresPlacement { get; set; }

        public bool RescalesOnDpiChange { get; set; }

        public bool HasFrame { get; set; } = true;

        /// <summary>The visible frame of the normal (restored) window.</summary>
        public WindowRect Visible { get; set; } = new(100, 100, 800, 600);

        public List<Placement> Calls { get; } = [];

        public FrameInsets Insets
        {
            get
            {
                var border = (int)Math.Round(7 * Scale(MonitorOf(Visible)), MidpointRounding.AwayFromZero);
                return HasFrame ? new FrameInsets(border, 0, border, border) : FrameInsets.None;
            }
        }

        public WindowRect Outer => Insets.Outer(Visible);

        public int MonitorIndex => MonitorOf(Visible);

        private WindowRect CurrentOuter => State switch
        {
            WindowState.Minimized => new WindowRect(-32000, -32000, 160, 28),
            WindowState.Maximized => Grow(_test._monitorList[MonitorIndex].WorkArea, 8),
            _ => Outer
        };

        private WindowRect CurrentVisible => State switch
        {
            WindowState.Minimized => new WindowRect(-32000, -32000, 160, 28),
            WindowState.Maximized => Grow(_test._monitorList[MonitorIndex].WorkArea, 0),
            _ => Visible
        };

        private bool Show(int command)
        {
            if (IgnoresShowCommands)
                return true;

            switch (command)
            {
                case SwMaximize:
                    State = WindowState.Maximized;
                    break;
                case SwShowNoActivate:
                case SwRestore:
                    State = State == WindowState.Minimized && RestoresToMaximized ? WindowState.Maximized : WindowState.Normal;
                    RestoresToMaximized = false;
                    break;
            }

            return true;
        }

        private Win32CallResult SetPos(int x, int y, int width, int height, uint flags)
        {
            Calls.Add(new Placement(x, y, width, height, flags) { StateWhenPlaced = State, MonitorWhenPlaced = MonitorIndex });
            if (IgnoresPlacement)
                return Win32CallResult.Ok;

            var before = MonitorIndex;
            var insets = Insets;
            var outer = Outer;
            var newOuter = new WindowRect(
                (flags & SwpNoMove) != 0 ? outer.X : x,
                (flags & SwpNoMove) != 0 ? outer.Y : y,
                (flags & SwpNoSize) != 0 ? outer.Width : width,
                (flags & SwpNoSize) != 0 ? outer.Height : height);
            Visible = new WindowRect(newOuter.X + insets.Left, newOuter.Y + insets.Top,
                newOuter.Width - insets.Left - insets.Right, newOuter.Height - insets.Top - insets.Bottom);

            var after = MonitorIndex;
            if (RescalesOnDpiChange && Scale(after) != Scale(before))
            {
                // WM_DPICHANGED: the app keeps its top-left corner and scales its size
                var ratio = Scale(after) / Scale(before);
                Visible = Visible with { Width = (int)Math.Round(Visible.Width * ratio), Height = (int)Math.Round(Visible.Height * ratio) };
            }

            return Win32CallResult.Ok;
        }

        private double Scale(int monitor) => _test._monitorList[monitor].ScaleFactor;

        private int MonitorOf(WindowRect rect)
        {
            var centerX = rect.X + rect.Width / 2;
            return centerX >= 1920 ? 1 : 0;
        }

        private static WindowRect Grow(MonitorRect area, int by) =>
            new(area.X - by, area.Y - by, area.Width + 2 * by, area.Height + 2 * by);

        private static (int, int, int, int) Ltrb(WindowRect rect) => (rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}
