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
        result.CapturedRegion.Should().Be(new CapturedRegion(
            ready.ClientRect.X, ready.ClientRect.Y, ready.ClientRect.Width, ready.ClientRect.Height));
        result.Width.Should().Be(ready.ClientRect.Width);
        result.Height.Should().Be(ready.ClientRect.Height);
        mismatches.Should().BeEmpty();
    }

    [DesktopNoInputFact]
    public void CaptureWindow_WithFrame_HasTheVisibleFrameAndTheSwatchesAtTheirOffsetsFromIt()
    {
        // Arrange - the capture is cut to DWMWA_EXTENDED_FRAME_BOUNDS, without the invisible resize borders that
        // GetWindowRect includes (roadmap M2.9); the server's own wrapper reads them, which the guard allows
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var handle = e2e.Fixture.MainWindowHandle;
        var frame = e2e.Server.Get<INativeWindowWrapper>().GetExtendedFrameBounds(handle)
                    ?? throw new InvalidOperationException("The fixture window has no DWM frame bounds.");
        var visible = new ScreenRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);

        // Act - image pixel (0, 0) is the visible frame's top-left corner
        var (result, mismatches) = CaptureUntilSwatchesMatch(e2e, includeFrame: true, visible.X, visible.Y);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.CapturedRegion.Should().Be(new CapturedRegion(visible.X, visible.Y, visible.Width, visible.Height));
        result.Width.Should().Be(visible.Width);
        result.Height.Should().Be(visible.Height);
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
                // Full resolution, lossless: image pixels map 1:1 to screen pixels from the capture origin
                var capture = e2e.Server.Screenshots.CaptureWindow(
                    e2e.Fixture.MainWindowHandle, includeFrame, new CaptureOptions { Format = CaptureFormat.Png, MaxDimension = 0 });
                result = capture.Metadata;
                if (!result.Success)
                {
                    return false;
                }
                using var image = CaptureChecks.DecodeImage(capture);
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
