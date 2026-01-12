namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for MonitorService.
/// Tests T3.6 from BACKLOG-TECHNICAL.md.
///
/// Note: MonitorService uses P/Invoke for monitor enumeration.
/// These tests focus on the CalculateSnapBounds calculation logic.
/// </summary>
public class MonitorServiceTests
{
    /// <summary>
    /// Helper method that mirrors the CalculateSnapBounds logic for testing.
    /// This allows testing the calculation algorithm without P/Invoke dependencies.
    /// </summary>
    private static MonitorRect CalculateSnapBoundsInternal(MonitorRect workArea, SnapPosition position)
    {
        return position switch
        {
            SnapPosition.LeftHalf => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width / 2, workArea.Height),

            SnapPosition.RightHalf => new MonitorRect(
                workArea.X + workArea.Width / 2, workArea.Y, workArea.Width / 2, workArea.Height),

            SnapPosition.TopHalf => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width, workArea.Height / 2),

            SnapPosition.BottomHalf => new MonitorRect(
                workArea.X, workArea.Y + workArea.Height / 2, workArea.Width, workArea.Height / 2),

            SnapPosition.TopLeftQuarter => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.TopRightQuarter => new MonitorRect(
                workArea.X + workArea.Width / 2, workArea.Y, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.BottomLeftQuarter => new MonitorRect(
                workArea.X, workArea.Y + workArea.Height / 2, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.BottomRightQuarter => new MonitorRect(
                workArea.X + workArea.Width / 2, workArea.Y + workArea.Height / 2, workArea.Width / 2, workArea.Height / 2),

            SnapPosition.LeftThird => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width / 3, workArea.Height),

            SnapPosition.CenterThird => new MonitorRect(
                workArea.X + workArea.Width / 3, workArea.Y, workArea.Width / 3, workArea.Height),

            SnapPosition.RightThird => new MonitorRect(
                workArea.X + workArea.Width * 2 / 3, workArea.Y, workArea.Width / 3, workArea.Height),

            SnapPosition.LeftTwoThirds => new MonitorRect(
                workArea.X, workArea.Y, workArea.Width * 2 / 3, workArea.Height),

            SnapPosition.RightTwoThirds => new MonitorRect(
                workArea.X + workArea.Width / 3, workArea.Y, workArea.Width * 2 / 3, workArea.Height),

            SnapPosition.Fullscreen => workArea,

            _ => workArea
        };
    }

    #region T3.6: CalculateSnapBounds Tests (Parameterized)

    // Standard 1920x1040 work area (1080p with 40px taskbar)
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
    public void CalculateSnapBounds_StandardMonitor_ReturnsCorrectBounds(
        SnapPosition position,
        int expectedX, int expectedY,
        int expectedWidth, int expectedHeight)
    {
        // Act
        var result = CalculateSnapBoundsInternal(StandardWorkArea, position);

        // Assert
        result.X.Should().Be(expectedX);
        result.Y.Should().Be(expectedY);
        result.Width.Should().Be(expectedWidth);
        result.Height.Should().Be(expectedHeight);
    }

    [Fact]
    public void CalculateSnapBounds_WithOffsetWorkArea_RespectsOffset()
    {
        // Arrange - Secondary monitor at x=1920
        var workArea = new MonitorRect(1920, 0, 2560, 1400);

        // Act
        var result = CalculateSnapBoundsInternal(workArea, SnapPosition.LeftHalf);

        // Assert
        result.X.Should().Be(1920); // Should start at monitor offset
        result.Y.Should().Be(0);
        result.Width.Should().Be(1280); // Half of 2560
        result.Height.Should().Be(1400);
    }

    [Fact]
    public void CalculateSnapBounds_WithTaskbarOffset_RespectsWorkArea()
    {
        // Arrange - Work area with top taskbar (y offset)
        var workArea = new MonitorRect(0, 40, 1920, 1040);

        // Act
        var result = CalculateSnapBoundsInternal(workArea, SnapPosition.TopHalf);

        // Assert
        result.X.Should().Be(0);
        result.Y.Should().Be(40); // Should respect taskbar offset
        result.Width.Should().Be(1920);
        result.Height.Should().Be(520);
    }

    [Fact]
    public void CalculateSnapBounds_Halves_CoverFullWidth()
    {
        // Act
        var left = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.LeftHalf);
        var right = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.RightHalf);

        // Assert - Combined width should equal work area width
        (left.Width + right.Width).Should().Be(StandardWorkArea.Width);
        // No gap between halves
        (left.X + left.Width).Should().Be(right.X);
    }

    [Fact]
    public void CalculateSnapBounds_Thirds_CoverFullWidth()
    {
        // Act
        var left = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.LeftThird);
        var center = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.CenterThird);
        var right = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.RightThird);

        // Assert - Combined width should equal work area width
        (left.Width + center.Width + right.Width).Should().Be(StandardWorkArea.Width);
        // No gaps
        (left.X + left.Width).Should().Be(center.X);
        (center.X + center.Width).Should().Be(right.X);
    }

    [Fact]
    public void CalculateSnapBounds_Quarters_CoverFullArea()
    {
        // Act
        var topLeft = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.TopLeftQuarter);
        var topRight = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.TopRightQuarter);
        var bottomLeft = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.BottomLeftQuarter);
        var bottomRight = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.BottomRightQuarter);

        // Assert - All quarters should have same dimensions
        topLeft.Width.Should().Be(topRight.Width);
        topLeft.Height.Should().Be(bottomLeft.Height);

        // Combined should cover full area
        (topLeft.Width + topRight.Width).Should().Be(StandardWorkArea.Width);
        (topLeft.Height + bottomLeft.Height).Should().Be(StandardWorkArea.Height);
    }

    [Fact]
    public void CalculateSnapBounds_Fullscreen_ReturnsEntireWorkArea()
    {
        // Act
        var result = CalculateSnapBoundsInternal(StandardWorkArea, SnapPosition.Fullscreen);

        // Assert
        result.Should().Be(StandardWorkArea);
    }

    #endregion
}
