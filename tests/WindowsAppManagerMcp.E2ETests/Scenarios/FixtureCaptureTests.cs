namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// capture_window against the fixture: the image has the window's (or client area's) size, and the
/// fixture's color swatches appear where the fixture drew them, which pins down the capture origin.
/// No-input tier: capturing does not need the window in front.
/// </summary>
public class FixtureCaptureTests
{
    /// <summary>The first frame can reach the compositor slightly after the ready event.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public void CaptureWindow_ClientArea_HasTheClientSizeAndTheSwatchesAtTheirClientOffsets()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var ready = e2e.Fixture.Ready;

        // Act - image pixel (0, 0) is the client area's top-left corner
        var (result, mismatches) = CaptureUntilSwatchesMatch(
            e2e, includeFrame: false, ready.ClientRect.X, ready.ClientRect.Y);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Width.Should().Be(ready.ClientRect.Width);
        result.Height.Should().Be(ready.ClientRect.Height);
        mismatches.Should().BeEmpty();
    }

    [DesktopNoInputFact]
    public void CaptureWindow_WithFrame_HasTheWindowSizeAndTheSwatchesAtTheirWindowOffsets()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var ready = e2e.Fixture.Ready;

        // Act - image pixel (0, 0) is the GetWindowRect top-left corner, invisible resize borders included
        var (result, mismatches) = CaptureUntilSwatchesMatch(
            e2e, includeFrame: true, ready.WindowRect.X, ready.WindowRect.Y);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Width.Should().Be(ready.WindowRect.Width);
        result.Height.Should().Be(ready.WindowRect.Height);
        mismatches.Should().BeEmpty();
    }

    private (ScreenshotResult Result, IReadOnlyList<string> Mismatches) CaptureUntilSwatchesMatch(
        E2ESession e2e, bool includeFrame, int originX, int originY)
    {
        ScreenshotResult? result = null;
        IReadOnlyList<string> mismatches = ["not captured"];
        Wait.Until(
            () =>
            {
                result = e2e.Server.Screenshots.CaptureWindow(e2e.Fixture.MainWindowHandle, includeFrame, format: "png");
                if (!result.Success)
                {
                    return false;
                }
                using var image = CaptureChecks.DecodeImage(result);
                mismatches = CaptureChecks.SwatchMismatches(image, originX, originY, e2e.Fixture.Ready);
                return mismatches.Count == 0;
            },
            SettleTimeout,
            TimeSpan.FromMilliseconds(250));

        _output.WriteLine(
            $"capture_window(includeFrame: {includeFrame}): {result!.Width}x{result.Height}, " +
            $"origin used ({originX}, {originY}), mismatches: {(mismatches.Count == 0 ? "none" : string.Join(" ", mismatches))}");
        return (result, mismatches);
    }
}
