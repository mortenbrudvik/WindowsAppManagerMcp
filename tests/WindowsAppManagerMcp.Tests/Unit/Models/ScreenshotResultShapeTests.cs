namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// Positional construction keeps Error in the slot that existed before FilePath was added.
/// </summary>
public class ScreenshotResultShapeTests
{
    [Fact]
    public void ScreenshotResult_PositionalError_StaysOnError()
    {
        // Arrange
        var region = new CapturedRegion(0, 0, 1, 1);

        // Act
        var result = new ScreenshotResult(false, null, "png", 0, 0, region, null, 1.0, "boom");

        // Assert
        result.Error.Should().Be("boom");
        result.FilePath.Should().BeNull();
        result.OriginalWidth.Should().BeNull();
        result.OriginalHeight.Should().BeNull();
    }

    [Fact]
    public void AnnotatedScreenshotResult_PositionalError_StaysOnError()
    {
        // Act
        var result = new AnnotatedScreenshotResult(
            false, null, "png", 0, 0, 1L, null, null, 0, 0, 0, null, "boom");

        // Assert
        result.Error.Should().Be("boom");
        result.FilePath.Should().BeNull();
        result.OriginalWidth.Should().BeNull();
        result.OriginalHeight.Should().BeNull();
    }
}
