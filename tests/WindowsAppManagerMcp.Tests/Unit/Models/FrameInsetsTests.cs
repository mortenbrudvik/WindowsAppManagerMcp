namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// FrameInsets (roadmap M2.3): the invisible resize borders between a window's GetWindowRect and its visible frame
/// (DWMWA_EXTENDED_FRAME_BOUNDS). The geometry tools place the visible frame, so they grow the target by the insets.
/// </summary>
public class FrameInsetsTests
{
    [Fact]
    public void Between_ATypicalWindows11Window_HasBordersLeftRightAndBottom()
    {
        // Arrange - GetWindowRect includes 7 px of invisible border on three sides at 100%
        var outer = (Left: 93, Top: 100, Right: 907, Bottom: 707);
        var visible = (Left: 100, Top: 100, Right: 900, Bottom: 700);

        // Act
        var insets = FrameInsets.Between(outer, visible);

        // Assert
        insets.Should().Be(new FrameInsets(7, 0, 7, 7));
    }

    [Fact]
    public void Between_WithoutAVisibleFrame_IsNone()
    {
        FrameInsets.Between((0, 0, 800, 600), null).Should().Be(FrameInsets.None);
    }

    [Fact]
    public void Between_AFrameThatIsNotInsideTheWindowRect_GivesNoNegativeInset()
    {
        // A frame that DWM reports larger than the window (or a frame read while the window moved) is not a border
        FrameInsets.Between((100, 100, 900, 700), (95, 100, 905, 710)).Should().Be(FrameInsets.None);
    }

    [Fact]
    public void Between_AnImplausiblyLargeInset_IsNone()
    {
        // A frame read in the middle of a move can be far from the rect; such a read is not used to place the window
        FrameInsets.Between((0, 0, 2000, 1200), (500, 0, 1500, 1200)).Should().Be(FrameInsets.None);
    }

    [Fact]
    public void Outer_GrowsTheVisibleRectByTheInsets()
    {
        new FrameInsets(7, 0, 7, 7).Outer(new WindowRect(0, 0, 960, 1040)).Should().Be(new WindowRect(-7, 0, 974, 1047));
    }

    [Fact]
    public void Outer_OfNone_IsTheSameRect()
    {
        FrameInsets.None.Outer(new WindowRect(10, 20, 30, 40)).Should().Be(new WindowRect(10, 20, 30, 40));
    }
}
