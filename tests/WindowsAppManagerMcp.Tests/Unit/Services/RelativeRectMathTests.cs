namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// RelativeRectMath, the pure conversion of percentages (M6.7's set_window_bounds unit=percent) and fractions (layout
/// presets, M6.8) of a monitor's work area or full bounds into virtual-screen pixels. Every edge goes through SnapMath's
/// edge rule, so rectangles that share an edge in percent share it in pixels (no gap, no overlap), and the percentages
/// of a snap position give exactly that snap position's rectangle.
/// </summary>
public class RelativeRectMathTests
{
    #region FromPercent

    [Theory]
    // A 1080p work area with a 40 px taskbar
    [InlineData(0, 0, 1920, 1040, 0, 0, 100, 100, 0, 0, 1920, 1040)]
    [InlineData(0, 0, 1920, 1040, 50, 0, 50, 100, 960, 0, 960, 1040)]
    [InlineData(0, 0, 1920, 1040, 25, 25, 50, 50, 480, 260, 960, 520)]
    // A monitor left of the primary: the origin is negative, the arithmetic is the same
    [InlineData(-2560, 0, 2560, 1400, 0, 0, 50, 100, -2560, 0, 1280, 1400)]
    [InlineData(-2560, 0, 2560, 1400, 50, 0, 50, 100, -1280, 0, 1280, 1400)]
    [InlineData(-2560, 0, 2560, 1400, 25, 50, 50, 50, -1920, 700, 1280, 700)]
    // A monitor above and left of the primary
    [InlineData(-1920, -1080, 1920, 1040, 10, 10, 80, 80, -1728, -976, 1536, 832)]
    // Odd sizes: edges floor, so the halves of 1921 are 960 and 961 wide, as snap_window's are
    [InlineData(0, 0, 1921, 1041, 0, 0, 50, 50, 0, 0, 960, 520)]
    [InlineData(0, 0, 1921, 1041, 50, 50, 50, 50, 960, 520, 961, 521)]
    [InlineData(-1921, 0, 1921, 1041, 50, 0, 50, 100, -961, 0, 961, 1041)]
    public void FromPercent_ReturnsTheExpectedRect(
        int areaX, int areaY, int areaWidth, int areaHeight, double x, double y, double width, double height,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        RelativeRectMath.FromPercent(new MonitorRect(areaX, areaY, areaWidth, areaHeight), x, y, width, height)
            .Should().Be(new MonitorRect(expectedX, expectedY, expectedWidth, expectedHeight));
    }

    [Fact]
    public void FromPercent_ThirdsOfAnOddWidth_TileTheAreaExactly_AsSnapWindowsThirdsDo()
    {
        // Arrange - 2560 is not divisible by 3, and a third is not exact in binary
        var area = new MonitorRect(0, 0, 2560, 1400);
        const double third = 100 / 3.0;

        // Act
        var left = RelativeRectMath.FromPercent(area, 0, 0, third, 100);
        var centre = RelativeRectMath.FromPercent(area, third, 0, third, 100);
        var right = RelativeRectMath.FromPercent(area, 2 * third, 0, third, 100);

        // Assert
        left.Should().Be(new MonitorRect(0, 0, 853, 1400));
        centre.Should().Be(new MonitorRect(853, 0, 853, 1400));
        right.Should().Be(new MonitorRect(1706, 0, 854, 1400));
        left.Should().Be(SnapMath.Calculate(area, SnapPosition.LeftThird));
        centre.Should().Be(SnapMath.Calculate(area, SnapPosition.CenterThird));
        right.Should().Be(SnapMath.Calculate(area, SnapPosition.RightThird));
    }

    public static TheoryData<SnapPosition, double, double, double, double> SnapPositionsInPercent => new()
    {
        { SnapPosition.LeftHalf, 0, 0, 50, 100 },
        { SnapPosition.RightHalf, 50, 0, 50, 100 },
        { SnapPosition.TopHalf, 0, 0, 100, 50 },
        { SnapPosition.BottomHalf, 0, 50, 100, 50 },
        { SnapPosition.TopLeftQuarter, 0, 0, 50, 50 },
        { SnapPosition.TopRightQuarter, 50, 0, 50, 50 },
        { SnapPosition.BottomLeftQuarter, 0, 50, 50, 50 },
        { SnapPosition.BottomRightQuarter, 50, 50, 50, 50 },
        { SnapPosition.LeftThird, 0, 0, 100 / 3.0, 100 },
        { SnapPosition.CenterThird, 100 / 3.0, 0, 100 / 3.0, 100 },
        { SnapPosition.RightThird, 200 / 3.0, 0, 100 / 3.0, 100 },
        { SnapPosition.LeftTwoThirds, 0, 0, 200 / 3.0, 100 },
        { SnapPosition.RightTwoThirds, 100 / 3.0, 0, 200 / 3.0, 100 },
        { SnapPosition.Fullscreen, 0, 0, 100, 100 }
    };

    // Work areas with negative origins, odd sizes and the sizes of common mixed-DPI setups
    private static readonly MonitorRect[] WorkAreas =
    [
        new(0, 0, 1920, 1040),
        new(0, 0, 2560, 1400),
        new(0, 0, 3840, 2088),
        new(-1920, 0, 1920, 1040),
        new(-2560, -360, 2560, 1400),
        new(1920, -1080, 1707, 961),
        new(-3, -7, 1921, 1041),
        new(0, 0, 1, 1),
        new(0, 0, 2, 3),
        new(-32000, -32000, 29999, 17777)
    ];

    [Theory]
    [MemberData(nameof(SnapPositionsInPercent))]
    public void FromPercent_TheSnapPositionsPercentages_GiveExactlyTheSnapRect(
        SnapPosition position, double x, double y, double width, double height)
    {
        foreach (var area in WorkAreas)
        {
            var snapRect = SnapMath.Calculate(area, position);

            // On the 1x1 and 2x3 areas a half or a third is 0 px, which is not a window: refused, not returned
            if (snapRect.Width == 0 || snapRect.Height == 0)
                UnlessUnderOnePixel(() => RelativeRectMath.FromPercent(area, x, y, width, height)).Should().BeNull();
            else
                RelativeRectMath.FromPercent(area, x, y, width, height)
                    .Should().Be(snapRect, "the percentages of {0} on {1}", position, area);
        }
    }

    [Theory]
    // Products that are whole in decimal but a hair below in binary floating point: 0.29 * 100 is 28.999999999999996
    [InlineData(100, 29, 29)]
    [InlineData(100, 57, 57)]
    [InlineData(1000, 30, 300)]
    [InlineData(1000, 70, 700)]
    [InlineData(1920, 10, 192)]
    [InlineData(1920, 1, 19)]   // 19.2: floors
    [InlineData(1920, 99.99, 1919)]
    [InlineData(1920, 100, 1920)]
    [InlineData(1920, 0, 0)]
    public void FromPercent_AnEdgeThatIsWholeInDecimal_DoesNotLoseAPixelToFloatingPoint(int length, double percent, int expectedOffset)
    {
        // Arrange - the edge is the right edge of a rect ending at percent, and the left edge of one starting there
        var area = new MonitorRect(-5000, 0, length, 100);

        // Act and assert
        if (percent > 0)
        {
            var before = RelativeRectMath.FromPercent(area, 0, 0, percent, 100);
            (before.X + before.Width).Should().Be(-5000 + expectedOffset);
        }

        if (percent < 100)
        {
            var after = RelativeRectMath.FromPercent(area, percent, 0, 100 - percent, 100);
            after.X.Should().Be(-5000 + expectedOffset);
        }
    }

    [Fact]
    public void FromPercent_RectsThatShareAnEdgeInPercent_ShareItInPixels()
    {
        // Arrange - cuts an agent might type, including ones that are not whole pixels anywhere, and ones less than a
        // pixel apart (33.33 and 33.333 are 0.0576 px apart on 1920), whose tile is refused rather than 0 px wide
        double[] cuts = [0, 12.5, 33.33, 33.333, 41.7, 50, 66.666, 66.67, 87.49, 100];
        foreach (var area in WorkAreas)
        {
            var previousRight = area.X;
            for (var i = 0; i + 1 < cuts.Length; i++)
            {
                // Act
                var rect = UnlessUnderOnePixel(() => RelativeRectMath.FromPercent(area, cuts[i], 0, cuts[i + 1] - cuts[i], 100));

                // Assert - no gap, no overlap, and the last one ends at the area's right edge
                if (rect is null)
                    continue;
                rect.X.Should().Be(previousRight, "rect {0} on {1} starts where the one before it ends", i, area);
                rect.Width.Should().BeGreaterThanOrEqualTo(1);
                previousRight = rect.X + rect.Width;
            }

            previousRight.Should().Be(area.X + area.Width);
        }
    }

    [Fact]
    public void FromPercent_SumsThatOvershoot100ByFloatingPointError_AreAccepted_AndEndAtTheEdge()
    {
        // Arrange - x is one floating-point step above 70, so x + width is 100.00000000000001
        var x = Math.BitIncrement(70.0);
        (x + 30).Should().BeGreaterThan(100);

        // Act
        var rect = RelativeRectMath.FromPercent(new MonitorRect(0, 0, 2560, 1400), x, x, 30, 30);

        // Assert
        rect.Should().Be(new MonitorRect(1792, 980, 768, 420));
        (rect.X + rect.Width).Should().Be(2560);
        (rect.Y + rect.Height).Should().Be(1400);
    }

    [Fact]
    public void FromPercent_OnMonitorsAtDifferentScales_IsAShareOfEachMonitorsOwnPhysicalPixels()
    {
        // Arrange - a 4K primary at 150 % and a 1080p monitor at 100 % left of it; virtual-screen coordinates are physical
        // pixels, so the scale factor does not enter the conversion
        var primary = TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 3840, 2160, 0, 0, 3840, 2088, 1.5);
        var left = TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, -1920, 0, 1920, 1080, -1920, 0, 1920, 1040, 1.0);

        // Act
        var onPrimary = RelativeRectMath.FromPercent(primary, RelativeArea.WorkArea, 50, 0, 50, 100);
        var onLeft = RelativeRectMath.FromPercent(left, RelativeArea.WorkArea, 50, 0, 50, 100);

        // Assert
        onPrimary.Should().Be(new MonitorRect(1920, 0, 1920, 2088));
        onLeft.Should().Be(new MonitorRect(-960, 0, 960, 1040));
    }

    [Fact]
    public void FromPercent_RelativeToTheMonitor_UsesItsFullBounds_AndToTheWorkArea_LeavesOutTheTaskbar()
    {
        // Arrange - a monitor above the primary, with the taskbar on its left edge
        var monitor = TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 0, -1440, 2560, 1440, 62, -1440, 2498, 1440, 1.25);

        // Act
        var ofWorkArea = RelativeRectMath.FromPercent(monitor, RelativeArea.WorkArea, 0, 0, 50, 100);
        var ofMonitor = RelativeRectMath.FromPercent(monitor, RelativeArea.Monitor, 0, 0, 50, 100);

        // Assert
        ofWorkArea.Should().Be(new MonitorRect(62, -1440, 1249, 1440));
        ofMonitor.Should().Be(new MonitorRect(0, -1440, 1280, 1440));
        RelativeRectMath.AreaOf(monitor, RelativeArea.WorkArea).Should().Be(monitor.WorkArea);
        RelativeRectMath.AreaOf(monitor, RelativeArea.Monitor).Should().Be(monitor.Bounds);
    }

    [Fact]
    public void AreaOf_AnUnknownArea_Throws()
    {
        var act = () => RelativeRectMath.AreaOf(TestDataFactory.CreateMonitorInfo(), (RelativeArea)7);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region CheckPercent

    [Theory]
    [InlineData(0, 0, 100, 100)]
    [InlineData(50, 50, 50, 50)]
    [InlineData(99.5, 0, 0.5, 100)]
    [InlineData(70.00000000000001, 0, 30, 100)] // x + width overshoots 100 by floating-point error only
    public void CheckPercent_AcceptsARectInsideTheArea(double x, double y, double width, double height)
    {
        RelativeRectMath.CheckPercent(x, y, width, height).Should().BeNull();
    }

    [Theory]
    [InlineData(double.NaN, 0, 50, 50, "x")]
    [InlineData(0, double.PositiveInfinity, 50, 50, "y")]
    [InlineData(-1, 0, 50, 50, "x")]
    [InlineData(0, -0.01, 50, 50, "y")]
    [InlineData(100.5, 0, 1, 50, "x")]
    [InlineData(0, 0, 0, 50, "width")]
    [InlineData(0, 0, -10, 50, "width")]
    [InlineData(0, 0, 50, 0, "height")]
    [InlineData(0, 0, 100.01, 50, "width")]
    [InlineData(60, 0, 50, 50, "x + width")]
    [InlineData(0, 50.5, 50, 50, "y + height")]
    public void CheckPercent_RefusesARectOutsideTheArea_NamingTheValue(double x, double y, double width, double height, string named)
    {
        RelativeRectMath.CheckPercent(x, y, width, height).Should().NotBeNull().And.StartWith(named + " ");
    }

    [Fact]
    public void FromPercent_ARectOutsideTheArea_ThrowsWithTheCheckMessage()
    {
        var act = () => RelativeRectMath.FromPercent(new MonitorRect(0, 0, 1920, 1040), 60, 0, 50, 100);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("x + width *");
    }

    [Theory]
    // Valid percentages whose edges floor to the same pixel: 99.99 % and 99.995 % of 1920 are both 1919
    [InlineData(99.99, 0, 0.005, 100, "width")]
    [InlineData(0, 99.99, 100, 0.005, "height")]
    // Smaller than one pixel at the start of the area: 0.01 % of 1920 floors to 0
    [InlineData(0, 0, 0.01, 100, "width")]
    public void FromPercent_ASizeThatRoundsToZeroPixels_Throws_RatherThanReturningAZeroSizedWindow(
        double x, double y, double width, double height, string named)
    {
        RelativeRectMath.CheckPercent(x, y, width, height).Should().BeNull("the percentages themselves are valid");

        var act = () => RelativeRectMath.FromPercent(new MonitorRect(0, 0, 1920, 1920), x, y, width, height);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage($"The {named} is less than one pixel on this area*")
            .Which.ParamName.Should().Be(named);
    }

    #endregion

    #region FromFractions

    [Theory]
    [InlineData(0, 0, 1920, 1040, 0, 0, 0.5, 1, 0, 0, 960, 1040)]
    [InlineData(-2560, 0, 2560, 1400, 1 / 3.0, 0, 1 / 3.0, 1, -1707, 0, 853, 1400)]
    [InlineData(1920, -1080, 1920, 1040, 0.25, 0.5, 0.5, 0.5, 2400, -560, 960, 520)]
    // A saved window that hangs off the work area keeps doing so: fractions are not limited to the area
    [InlineData(0, 0, 1920, 1040, 0.9, 0.8, 0.5, 0.5, 1728, 832, 960, 520)]
    [InlineData(0, 0, 1920, 1040, -0.01, 0, 0.5, 1, -20, 0, 960, 1040)]
    public void FromFractions_ReturnsTheExpectedRect(
        int areaX, int areaY, int areaWidth, int areaHeight, double x, double y, double width, double height,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        var area = new MonitorRect(areaX, areaY, areaWidth, areaHeight);
        var expected = new MonitorRect(expectedX, expectedY, expectedWidth, expectedHeight);

        RelativeRectMath.FromFractions(area, x, y, width, height).Should().Be(expected);
        RelativeRectMath.FromFractions(area, new RelativePosition(x, y, width, height)).Should().Be(expected);
    }

    [Fact]
    public void FromFractions_NeighbouringThirdsOnAMonitorLeftOfThePrimary_Abut_WhereApplyLayoutsTruncationLeavesAGap()
    {
        // Arrange
        var area = new MonitorRect(-2560, 0, 2560, 1400);

        // Act
        var left = RelativeRectMath.FromFractions(area, 0, 0, 1 / 3.0, 1);
        var centre = RelativeRectMath.FromFractions(area, 1 / 3.0, 0, 1 / 3.0, 1);
        var truncatedLeft = LayoutPlanner.TargetOf(new RelativePosition(0, 0, 1 / 3.0, 1), area);
        var truncatedCentre = LayoutPlanner.TargetOf(new RelativePosition(1 / 3.0, 0, 1 / 3.0, 1), area);

        // Assert - the change M6.8 can make is at most one pixel per edge
        (left.X + left.Width).Should().Be(centre.X);
        (truncatedLeft.X + truncatedLeft.Width).Should().Be(truncatedCentre.X - 1);
        Math.Abs(centre.X - truncatedCentre.X).Should().BeLessThanOrEqualTo(1);
        Math.Abs(centre.Width - truncatedCentre.Width).Should().BeLessThanOrEqualTo(1);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0.5, 1)]
    [InlineData(0, double.NegativeInfinity, 0.5, 1)]
    [InlineData(1e12, 0, 0.5, 1)]
    [InlineData(0, 0, -1e12, 1)]
    // Both edges fit in int, but the distance between them does not (3.84e9 px wide, 2.288e9 px high)
    [InlineData(-1e6, 0, 2e6, 1)]
    [InlineData(0, -1.1e6, 1, 2.2e6)]
    // A size that is not above 0 is not a window, even one hanging off the area
    [InlineData(0.5, 0, -0.25, 1)]
    [InlineData(0, 0.5, 1, -0.25)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    // Above 0 as a fraction but 0 px once the edges floor: 1e-4 of 1920 and of 1040 is less than one pixel
    [InlineData(0, 0, 1e-4, 1)]
    [InlineData(0, 0, 1, 1e-4)]
    [InlineData(0.5, 0.5, 1e-4, 1e-4)]
    public void FromFractions_AValueThatIsNotARealPositionOnScreen_Throws(double x, double y, double width, double height)
    {
        var act = () => RelativeRectMath.FromFractions(new MonitorRect(0, 0, 1920, 1040), x, y, width, height);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FromFractions_ASizeOfOnePixel_IsStillAWindow()
    {
        // 1 / 1920 of 1920 is exactly one pixel: the smallest size that is accepted
        RelativeRectMath.FromFractions(new MonitorRect(0, 0, 1920, 1040), 0, 0, 1 / 1920.0, 1)
            .Should().Be(new MonitorRect(0, 0, 1, 1040));
    }

    [Fact]
    public void FromFractions_ASizeThatRoundsToZeroPixels_SaysSo()
    {
        var act = () => RelativeRectMath.FromFractions(new MonitorRect(0, 0, 1920, 1040), 0, 0, 1e-4, 1);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("The width is less than one pixel on this area*")
            .Which.ParamName.Should().Be("width");
    }

    [Fact]
    public void FromFractions_AgreesWithFromPercent()
    {
        foreach (var area in WorkAreas)
        {
            // Including on the 1x1 area, where both refuse the 0-px height
            UnlessUnderOnePixel(() => RelativeRectMath.FromFractions(area, 0.125, 1 / 3.0, 0.5, 1 / 3.0))
                .Should().Be(UnlessUnderOnePixel(() => RelativeRectMath.FromPercent(area, 12.5, 100 / 3.0, 50, 100 / 3.0)), "on {0}", area);
        }
    }

    #endregion

    #region A position or a size alone (set_window_bounds with one pair, roadmap M6.7)

    [Fact]
    public void PointFromPercent_IsTheCornerFromPercentWouldGive()
    {
        var area = new MonitorRect(-2560, 100, 2560, 1400);

        RelativeRectMath.PointFromPercent(area, 100 / 3.0, 50).Should().Be((-2560 + 853, 800));
        RelativeRectMath.PointFromPercent(area, 0, 0).Should().Be((-2560, 100));
        RelativeRectMath.PointFromPercent(area, 100, 100).Should().Be((0, 1500), "100 % is the area's far edge, where a window may start");
    }

    [Fact]
    public void SizeFromPercent_IsTheSizeFromPercentWouldGiveFromTheAreasCorner()
    {
        var area = new MonitorRect(1920, 0, 2560, 1400);

        RelativeRectMath.SizeFromPercent(area, 100 / 3.0, 100).Should().Be((853, 1400));
        RelativeRectMath.SizeFromPercent(area, 25, 50).Should().Be((640, 700));
    }

    [Theory]
    [InlineData(-0.5, 0, "x must be from 0 to 100")]
    [InlineData(0, 100.5, "y must be from 0 to 100")]
    [InlineData(double.NaN, 0, "x must be a number")]
    public void PointFromPercent_OutOfRange_IsRefusedWithCheckPercentPositionsMessage(double x, double y, string message)
    {
        RelativeRectMath.CheckPercentPosition(x, y).Should().StartWith(message);
        var act = () => RelativeRectMath.PointFromPercent(new MonitorRect(0, 0, 1920, 1080), x, y);
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage(message + "*");
    }

    [Theory]
    [InlineData(0, 50, "width must be above 0")]
    [InlineData(50, 100.5, "height must be above 0 and at most 100")]
    public void SizeFromPercent_OutOfRange_IsRefusedWithCheckPercentSizesMessage(double width, double height, string message)
    {
        RelativeRectMath.CheckPercentSize(width, height).Should().StartWith(message);
        var act = () => RelativeRectMath.SizeFromPercent(new MonitorRect(0, 0, 1920, 1080), width, height);
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage(message + "*");
    }

    [Fact]
    public void SizeFromPercent_UnderOnePixel_IsRefused()
    {
        var act = () => RelativeRectMath.SizeFromPercent(new MonitorRect(0, 0, 1920, 1080), 0.01, 50);

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("The width is less than one pixel on this area*");
    }

    #endregion

    /// <summary>The rectangle, or null when it is refused for being less than one pixel wide or high.</summary>
    private static MonitorRect? UnlessUnderOnePixel(Func<MonitorRect> rect)
    {
        try
        {
            return rect();
        }
        catch (ArgumentOutOfRangeException e) when (e.Message.Contains("less than one pixel", StringComparison.Ordinal))
        {
            return null;
        }
    }
}
