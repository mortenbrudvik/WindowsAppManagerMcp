namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// WindowZones, the pure read behind a window row's zone and spansMonitors (issue #12): the snap_window position whose
/// rectangle (SnapMath, floor rounding included) the visible frame fills on its monitor's work area, within about 2 px times
/// the monitor's scale, "fullscreen" for a maximized window by its state, "monitor" for a frame over the whole monitor, and
/// nothing for a minimized window or a frame that fills no zone.
/// </summary>
public class WindowZonesTests
{
    // 2560 x 1440 at 125% with a 48 px taskbar at the bottom: odd thirds (853, 853, 854) and a work area unlike the bounds
    private static readonly MonitorInfo Primary = new(0, @"\\.\DISPLAY1", true,
        new MonitorRect(0, 0, 2560, 1440), new MonitorRect(0, 0, 2560, 1392), 1.25);

    // 1920 x 1080 at 100% left of the primary, its taskbar on the left: a negative origin and a work area that does not
    // start at the monitor's corner
    private static readonly MonitorInfo Left = new(1, @"\\.\DISPLAY2", false,
        new MonitorRect(-1920, 180, 1920, 1080), new MonitorRect(-1872, 180, 1872, 1080), 1.0);

    private static WindowRect Frame(MonitorRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    public static TheoryData<SnapPosition, string> Positions() => new()
    {
        { SnapPosition.LeftHalf, "left_half" },
        { SnapPosition.RightHalf, "right_half" },
        { SnapPosition.TopHalf, "top_half" },
        { SnapPosition.BottomHalf, "bottom_half" },
        { SnapPosition.TopLeftQuarter, "top_left_quarter" },
        { SnapPosition.TopRightQuarter, "top_right_quarter" },
        { SnapPosition.BottomLeftQuarter, "bottom_left_quarter" },
        { SnapPosition.BottomRightQuarter, "bottom_right_quarter" },
        { SnapPosition.LeftThird, "left_third" },
        { SnapPosition.CenterThird, "center_third" },
        { SnapPosition.RightThird, "right_third" },
        { SnapPosition.LeftTwoThirds, "left_two_thirds" },
        { SnapPosition.RightTwoThirds, "right_two_thirds" },
        { SnapPosition.Fullscreen, "fullscreen" },
    };

    #region Zone

    [Theory]
    [MemberData(nameof(Positions))]
    public void AFrameSnapWindowPlaced_IsThatPosition(SnapPosition position, string zone)
    {
        var frame = Frame(SnapMath.Calculate(Primary.WorkArea, position));

        WindowZones.Match(frame, WindowState.Normal, Primary).Should().Be(zone);
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void AFrameSnapWindowPlaced_OnAMonitorWithANegativeOriginAndALeftTaskbar_IsThatPosition(SnapPosition position, string zone)
    {
        var frame = Frame(SnapMath.Calculate(Left.WorkArea, position));

        WindowZones.Match(frame, WindowState.Normal, Left).Should().Be(zone);
    }

    [Fact]
    public void AMaximizedWindow_IsFullscreen_ByItsState()
    {
        // Its window rect overhangs the work area by the invisible borders; the state decides, not the rectangle
        WindowZones.Match(new WindowRect(-9, -9, 2578, 1410), WindowState.Maximized, Primary).Should().Be("fullscreen");
    }

    [Fact]
    public void AMinimizedWindow_HasNoZone()
    {
        // Its frame is the icon at -32000, and a frame that happened to fit a zone would still say nothing about it
        WindowZones.Match(Frame(Primary.WorkArea), WindowState.Minimized, Primary).Should().BeNull();
        WindowZones.Match(new WindowRect(-32000, -32000, 160, 28), WindowState.Minimized, Primary).Should().BeNull();
    }

    [Fact]
    public void AFrameOverTheWholeMonitor_TaskbarIncluded_IsMonitor()
    {
        // A borderless fullscreen video or game: the monitor's bounds, not its work area
        WindowZones.Match(Frame(Primary.Bounds), WindowState.Normal, Primary).Should().Be("monitor");
    }

    [Fact]
    public void OnAMonitorWhoseWorkAreaIsTheWholeMonitor_AFrameOverIt_IsFullscreen()
    {
        // An auto-hide taskbar, or a monitor without one: both match, and the snap_window name wins
        var noTaskbar = Primary with { WorkArea = Primary.Bounds };

        WindowZones.Match(Frame(noTaskbar.Bounds), WindowState.Normal, noTaskbar).Should().Be("fullscreen");
    }

    [Fact]
    public void AFrameThatFillsNoZone_HasNone()
    {
        WindowZones.Match(new WindowRect(200, 150, 1100, 800), WindowState.Normal, Primary).Should().BeNull();
    }

    [Fact]
    public void AFrameOnAnotherMonitorThanItsOwn_HasNoZone()
    {
        // The left half of the left monitor, matched against the primary: zones are of the window's own monitor
        var frame = Frame(SnapMath.Calculate(Left.WorkArea, SnapPosition.LeftHalf));

        WindowZones.Match(frame, WindowState.Normal, Primary).Should().BeNull();
    }

    [Fact]
    public void AWindowManagerThatRoundsThirdsTheOtherWay_StillMatches()
    {
        // SnapMath floors each edge: thirds of 2560 are 853, 853 and 854 wide. Another window manager rounds to 854, 853, 853
        WindowZones.Match(new WindowRect(0, 0, 854, 1392), WindowState.Normal, Primary).Should().Be("left_third");
        WindowZones.Match(new WindowRect(854, 0, 853, 1392), WindowState.Normal, Primary).Should().Be("center_third");
        WindowZones.Match(new WindowRect(1707, 0, 853, 1392), WindowState.Normal, Primary).Should().Be("right_third");
    }

    [Theory]
    [InlineData(1.0, 2, "right_half")]
    [InlineData(1.0, 3, null)]
    [InlineData(1.25, 3, "right_half")]
    [InlineData(1.25, 4, null)]
    [InlineData(2.0, 4, "right_half")]
    [InlineData(2.0, 5, null)]
    public void AnEdgeOffTheZone_Matches_WithinAbout2PixelsTimesTheMonitorsScale(double scale, int offBy, string? zone)
    {
        // Arrange - the right half with its left edge offBy pixels to the right
        var monitor = Primary with { ScaleFactor = scale };
        var half = SnapMath.Calculate(monitor.WorkArea, SnapPosition.RightHalf);
        var frame = new WindowRect(half.X + offBy, half.Y, half.Width - offBy, half.Height);

        // Act & Assert
        WindowZones.Match(frame, WindowState.Normal, monitor).Should().Be(zone);
        WindowZones.Tolerance(monitor).Should().Be((int)Math.Ceiling(2 * scale));
    }

    [Fact]
    public void ATolerance_AppliesToEveryEdge()
    {
        // Arrange - the left half of a 100% monitor, each edge in turn 3 px off (the tolerance is 2)
        var monitor = Primary with { ScaleFactor = 1.0 };
        var half = Frame(SnapMath.Calculate(monitor.WorkArea, SnapPosition.LeftHalf));

        // Act & Assert
        WindowZones.Match(half with { X = half.X - 3, Width = half.Width + 3 }, WindowState.Normal, monitor).Should().BeNull("left");
        WindowZones.Match(half with { Y = half.Y + 3, Height = half.Height - 3 }, WindowState.Normal, monitor).Should().BeNull("top");
        WindowZones.Match(half with { Width = half.Width + 3 }, WindowState.Normal, monitor).Should().BeNull("right");
        WindowZones.Match(half with { Height = half.Height - 3 }, WindowState.Normal, monitor).Should().BeNull("bottom");
        WindowZones.Match(half with { X = half.X + 2, Y = half.Y + 2, Width = half.Width - 4, Height = half.Height - 4 },
            WindowState.Normal, monitor).Should().Be("left_half", "every edge within 2 px");
    }

    [Fact]
    public void AMonitorWhoseScaleCouldNotBeRead_UsesTheToleranceAt100Percent()
    {
        // MonitorInfo then has ScaleFactor 1 (DpiUnknown)
        WindowZones.Tolerance(Primary with { ScaleFactor = 1.0, DpiUnknown = true }).Should().Be(2);
    }

    #endregion

    #region Spanning monitors

    [Fact]
    public void AFrameAcrossTwoMonitors_Spans()
    {
        // Mostly on the primary, 400 px onto the left monitor
        var frame = new WindowRect(-400, 300, 1600, 900);

        WindowZones.Spans(frame, WindowState.Normal, Primary, () => [Primary, Left]).Should().BeTrue();
    }

    [Fact]
    public void AFrameOnItsOwnMonitor_DoesNotSpan_AndTheMonitorsAreNotRead()
    {
        var read = false;

        var spans = WindowZones.Spans(Frame(Primary.WorkArea), WindowState.Normal, Primary, () =>
        {
            read = true;
            return [Primary, Left];
        });

        spans.Should().BeFalse();
        read.Should().BeFalse("a frame inside its monitor needs no list of the others");
    }

    [Fact]
    public void AFrameThatReachesOntoANeighbourByNoMoreThanTheTolerance_DoesNotSpan()
    {
        // A window manager that rounds an edge 2 px past the monitor's edge; the tolerance at 125% is 3
        var frame = new WindowRect(-2, 300, 1282, 900);

        WindowZones.Spans(frame, WindowState.Normal, Primary, () => [Primary, Left]).Should().BeFalse();
    }

    [Fact]
    public void AFramePartlyOffScreen_OnOneMonitor_DoesNotSpan()
    {
        // Half above the primary, where no monitor is: it leaves its monitor, but covers only one
        var frame = new WindowRect(100, -500, 800, 900);

        WindowZones.Spans(frame, WindowState.Normal, Primary, () => [Primary, Left]).Should().BeFalse();
    }

    [Fact]
    public void AMinimizedWindow_DoesNotSpan()
    {
        WindowZones.Spans(new WindowRect(-32000, -32000, 160, 28), WindowState.Minimized, Primary, () => [Primary, Left]).Should().BeFalse();
    }

    #endregion
}
