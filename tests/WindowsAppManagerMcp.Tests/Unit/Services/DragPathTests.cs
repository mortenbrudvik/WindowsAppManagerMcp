namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The points of a drag (roadmap M4.3): they end at the drop, the first one leaves the drag rectangle around the press
/// (twice SM_CXDRAG x SM_CYDRAG) whenever the drag is that long, and the rest follow evenly. Pure.
/// </summary>
public class DragPathTests
{
    [Fact]
    public void Points_ALongDrag_AreEvenSteps_EndingAtTheDrop()
    {
        var points = DragPath.Points(100, 200, 300, 200, steps: 4, threshold: (4, 4));

        points.Should().Equal((150, 200), (200, 200), (250, 200), (300, 200));
    }

    [Fact]
    public void Points_WhenAnEvenStepStaysInsideTheDragRectangle_TheFirstGoesBeyondIt()
    {
        // 100 pixels in 50 steps is 2 pixels a step, inside SM_CXDRAG 4: the drag would not start until the third move,
        // and some applications look only at the first, so the first goes beyond twice the threshold (8 pixels)
        var points = DragPath.Points(0, 0, 100, 0, steps: 50, threshold: (4, 4));

        points.Should().HaveCount(50);
        points[0].X.Should().BeGreaterThan(8);
        points[^1].Should().Be((100, 0));
        points.Select(p => p.X).Should().BeInAscendingOrder();
        points.Should().OnlyContain(p => p.Y == 0);
    }

    [Fact]
    public void Points_VerticalDrag_UsesTheVerticalThreshold()
    {
        var points = DragPath.Points(10, 10, 10, 110, steps: 50, threshold: (4, 10));

        points[0].Y.Should().BeGreaterThan(10 + 20);
        points[0].X.Should().Be(10);
    }

    [Fact]
    public void Points_ADragShorterThanTheMargin_GoesStraightToTheDrop()
    {
        var points = DragPath.Points(50, 50, 55, 50, steps: 3, threshold: (4, 4));

        points.Should().Equal((55, 50), (55, 50), (55, 50));
    }

    [Fact]
    public void Points_OneStep_IsTheDrop()
    {
        DragPath.Points(-500, 20, 700, 900, steps: 1, threshold: null).Should().Equal((700, 900));
    }

    [Fact]
    public void Points_WithoutAThreshold_UseWindowsDefault()
    {
        var points = DragPath.Points(0, 0, 0, 100, steps: 100, threshold: null);

        points[0].Y.Should().BeGreaterThan(DragPath.StartMargin * DragPath.DefaultThreshold);
    }

    [Fact]
    public void Points_NegativeCoordinates_LeftOfThePrimaryMonitor()
    {
        var points = DragPath.Points(-100, -50, -300, -50, steps: 2, threshold: (4, 4));

        points.Should().Equal((-200, -50), (-300, -50));
    }

    [Fact]
    public void Points_NoSteps_Throws()
    {
        var act = () => DragPath.Points(0, 0, 10, 10, steps: 0, threshold: null);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
