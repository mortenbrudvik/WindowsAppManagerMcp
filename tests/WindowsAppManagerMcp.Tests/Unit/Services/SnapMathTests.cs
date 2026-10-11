namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// SnapMath, the pure snap calculation behind MonitorService.CalculateSnapBounds (roadmap M2.3): every edge is at
/// X + W*k/n, so the parts of a split tile the work area exactly, without the lost last pixel of odd sizes, also on
/// monitors with a negative origin (left of or above the primary monitor). The tests call the production code; the old
/// test copy of the algorithm (CalculateSnapBoundsInternal) is gone.
/// </summary>
public class SnapMathTests
{
    // 1080p with a 40 px taskbar
    private static readonly MonitorRect StandardWorkArea = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(SnapPosition.LeftHalf, 0, 0, 960, 1040)]
    [InlineData(SnapPosition.RightHalf, 960, 0, 960, 1040)]
    [InlineData(SnapPosition.TopHalf, 0, 0, 1920, 520)]
    [InlineData(SnapPosition.BottomHalf, 0, 520, 1920, 520)]
    [InlineData(SnapPosition.TopLeftQuarter, 0, 0, 960, 520)]
    [InlineData(SnapPosition.TopRightQuarter, 960, 0, 960, 520)]
    [InlineData(SnapPosition.BottomLeftQuarter, 0, 520, 960, 520)]
    [InlineData(SnapPosition.BottomRightQuarter, 960, 520, 960, 520)]
    [InlineData(SnapPosition.LeftThird, 0, 0, 640, 1040)]
    [InlineData(SnapPosition.CenterThird, 640, 0, 640, 1040)]
    [InlineData(SnapPosition.RightThird, 1280, 0, 640, 1040)]
    [InlineData(SnapPosition.LeftTwoThirds, 0, 0, 1280, 1040)]
    [InlineData(SnapPosition.RightTwoThirds, 640, 0, 1280, 1040)]
    [InlineData(SnapPosition.Fullscreen, 0, 0, 1920, 1040)]
    public void Calculate_OnAStandardMonitor_ReturnsTheExpectedRect(SnapPosition position, int x, int y, int width, int height)
    {
        SnapMath.Calculate(StandardWorkArea, position).Should().Be(new MonitorRect(x, y, width, height));
    }

    [Fact]
    public void Calculate_ThirdsOfAnOddWidth_TileTheWorkAreaExactly()
    {
        // Arrange - 2560 is not divisible by 3: integer thirds of 853 left the last pixel uncovered
        var workArea = new MonitorRect(0, 0, 2560, 1400);

        // Act
        var left = SnapMath.Calculate(workArea, SnapPosition.LeftThird);
        var center = SnapMath.Calculate(workArea, SnapPosition.CenterThird);
        var right = SnapMath.Calculate(workArea, SnapPosition.RightThird);

        // Assert - edges at X + W*k/3
        left.Should().Be(new MonitorRect(0, 0, 853, 1400));
        center.Should().Be(new MonitorRect(853, 0, 853, 1400));
        right.Should().Be(new MonitorRect(1706, 0, 854, 1400));
        (right.X + right.Width).Should().Be(2560, "the last third ends at the work area's right edge");
    }

    [Fact]
    public void Calculate_HalvesOfAnOddWidthAndHeight_TileTheWorkAreaExactly()
    {
        var workArea = new MonitorRect(0, 0, 1921, 1041);

        var left = SnapMath.Calculate(workArea, SnapPosition.LeftHalf);
        var right = SnapMath.Calculate(workArea, SnapPosition.RightHalf);
        var top = SnapMath.Calculate(workArea, SnapPosition.TopHalf);
        var bottom = SnapMath.Calculate(workArea, SnapPosition.BottomHalf);

        (left.Width + right.Width).Should().Be(1921);
        (left.X + left.Width).Should().Be(right.X);
        (top.Height + bottom.Height).Should().Be(1041);
        (top.Y + top.Height).Should().Be(bottom.Y);
    }

    [Fact]
    public void Calculate_TwoThirdsAndTheThirdBesideThem_MeetWithoutAGapOrOverlap()
    {
        var workArea = new MonitorRect(0, 0, 2560, 1400);

        var leftTwo = SnapMath.Calculate(workArea, SnapPosition.LeftTwoThirds);
        var rightThird = SnapMath.Calculate(workArea, SnapPosition.RightThird);
        var leftThird = SnapMath.Calculate(workArea, SnapPosition.LeftThird);
        var rightTwo = SnapMath.Calculate(workArea, SnapPosition.RightTwoThirds);

        (leftTwo.X + leftTwo.Width).Should().Be(rightThird.X);
        (rightThird.X + rightThird.Width).Should().Be(2560);
        (leftThird.X + leftThird.Width).Should().Be(rightTwo.X);
        (rightTwo.X + rightTwo.Width).Should().Be(2560);
    }

    [Theory]
    [InlineData(-1920, 180, 1920, 1040)]
    [InlineData(-2561, -1441, 2561, 1401)]
    [InlineData(1920, 0, 2560, 1400)]
    [InlineData(0, 40, 1919, 1039)]
    public void Calculate_QuartersOnAnyWorkArea_TileItExactly(int x, int y, int width, int height)
    {
        // Arrange - including monitors left of and above the primary one (negative origin) and odd sizes
        var workArea = new MonitorRect(x, y, width, height);

        // Act
        var topLeft = SnapMath.Calculate(workArea, SnapPosition.TopLeftQuarter);
        var topRight = SnapMath.Calculate(workArea, SnapPosition.TopRightQuarter);
        var bottomLeft = SnapMath.Calculate(workArea, SnapPosition.BottomLeftQuarter);
        var bottomRight = SnapMath.Calculate(workArea, SnapPosition.BottomRightQuarter);

        // Assert - the four quarters cover every pixel once
        topLeft.X.Should().Be(x);
        topLeft.Y.Should().Be(y);
        (topLeft.X + topLeft.Width).Should().Be(topRight.X);
        (topLeft.Y + topLeft.Height).Should().Be(bottomLeft.Y);
        (bottomRight.X + bottomRight.Width).Should().Be(x + width);
        (bottomRight.Y + bottomRight.Height).Should().Be(y + height);
        (topLeft.Width * topLeft.Height + topRight.Width * topRight.Height +
         bottomLeft.Width * bottomLeft.Height + bottomRight.Width * bottomRight.Height).Should().Be(width * height);
    }

    [Fact]
    public void Calculate_RespectsTheWorkAreasOffset()
    {
        // A taskbar at the top: the work area starts at y=40
        SnapMath.Calculate(new MonitorRect(0, 40, 1920, 1040), SnapPosition.TopHalf).Should().Be(new MonitorRect(0, 40, 1920, 520));
        SnapMath.Calculate(new MonitorRect(1920, 0, 2560, 1400), SnapPosition.LeftHalf).Should().Be(new MonitorRect(1920, 0, 1280, 1400));
    }

    [Fact]
    public void Calculate_OfAValueThatIsNotASnapPosition_Throws()
    {
        var act = () => SnapMath.Calculate(StandardWorkArea, (SnapPosition)99);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("position");
    }

    [Fact]
    public void MonitorService_CalculateSnapBounds_UsesSnapMath()
    {
        // Arrange - an odd-width monitor left of the primary one
        var monitors = new Mock<INativeMonitorWrapper>();
        monitors.Setup(m => m.EnumerateMonitors()).Returns(
        [
            (new nint(0x10001), TestDataFactory.CreateMonitorInfo()),
            (new nint(0x10002), TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: false, workAreaX: -2561, workAreaWidth: 2561))
        ]);
        var sut = new MonitorService(monitors.Object);

        // Act & Assert
        sut.CalculateSnapBounds(1, SnapPosition.RightThird)
            .Should().Be(SnapMath.Calculate(new MonitorRect(-2561, 0, 2561, 1040), SnapPosition.RightThird));
    }
}
