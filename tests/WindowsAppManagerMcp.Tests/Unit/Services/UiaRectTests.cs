namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="UiaRect.ToBounds"/>, which turns UI Automation's bounding rectangles
/// into the integer bounds the tools return.
/// </summary>
public class UiaRectTests
{
    [Fact]
    public void ToBounds_EmptyRectangle_IsNull()
    {
        // UI Automation reports Rect.Empty (infinite position, negative infinite size) for elements without geometry
        UiaRect.Empty.ToBounds().Should().BeNull();
    }

    [Theory]
    [InlineData(double.NaN, 0, 10, 10)]
    [InlineData(0, double.NaN, 10, 10)]
    [InlineData(0, 0, double.PositiveInfinity, 10)]
    [InlineData(0, 0, 10, double.NegativeInfinity)]
    [InlineData(double.NegativeInfinity, 0, 10, 10)]
    [InlineData(0, 0, -1, 10)]
    [InlineData(0, 0, 10, -0.5)]
    [InlineData(3e9, 0, 10, 10)]
    [InlineData(0, -3e9, 10, 10)]
    public void ToBounds_NonFiniteNegativeOrHugeRectangle_IsNull(double x, double y, double width, double height)
    {
        new UiaRect(x, y, width, height).ToBounds().Should().BeNull();
    }

    [Theory]
    [InlineData(10, 20, 30, 40, 10, 20, 30, 40)]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 0)]
    [InlineData(-1920, -200, 1920, 1080, -1920, -200, 1920, 1080)]
    [InlineData(10.5, 20.5, 30, 40, 10, 20, 31, 41)]
    [InlineData(-10.5, -0.25, 20.2, 10, -11, -1, 21, 11)]
    [InlineData(10.2, 20.7, 0.1, 0.1, 10, 20, 1, 1)]
    public void ToBounds_CoversTheRectangleWithWholePixels(
        double x, double y, double width, double height,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        new UiaRect(x, y, width, height).ToBounds().Should().Be(new BoundsDto(expectedX, expectedY, expectedWidth, expectedHeight));
    }
}
