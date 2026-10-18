namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Windows.Graphics.Capture against the fixture (roadmap M7.1), in the no-input tier: it only reads. The real wrapper
/// captures the fixture's window through the compositor, with the visible frame's origin and size PrintWindow reports, and
/// pixels that match PrintWindow's away from the edges, both with and without the frame.
/// </summary>
public class FixtureGraphicsCaptureTests
{
    private readonly ITestOutputHelper _output;

    public FixtureGraphicsCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wgc_CapturesTheFixture_WhereAndAsPrintWindowDoes(bool includeFrame)
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var graphics = e2e.Server.Get<IGraphicsCaptureWrapper>();
        graphics.Availability.Should().Be(GraphicsCaptureAvailability.Available, "Windows.Graphics.Capture should be available on a Windows 10 1903+ desktop");
        var screenshots = e2e.Server.Get<IScreenshotService>();

        // Act
        var printed = screenshots.CapturePixels(new CaptureTarget.Window(main, includeFrame, WindowCaptureMethod.PrintWindow));
        var composed = screenshots.CapturePixels(new CaptureTarget.Window(main, includeFrame, WindowCaptureMethod.Wgc));
        _output.WriteLine($"PrintWindow: {printed.CapturedRegion} {printed.Pixels.Method}; WGC: {composed.CapturedRegion} {composed.Pixels.Method}");

        // Assert - the same place on the screen, give or take a pixel of rounding at the frame's edge
        composed.Pixels.Method.Should().Be(IGraphicsCaptureWrapper.Method);
        composed.CapturedRegion.X.Should().BeCloseTo(printed.CapturedRegion.X, 1);
        composed.CapturedRegion.Y.Should().BeCloseTo(printed.CapturedRegion.Y, 1);
        composed.CapturedRegion.Width.Should().BeCloseTo(printed.CapturedRegion.Width, 2);
        composed.CapturedRegion.Height.Should().BeCloseTo(printed.CapturedRegion.Height, 2);
        composed.Warnings.Should().NotContain(w => w.StartsWith("ImageBlank", StringComparison.Ordinal));

        // The same pixels in the middle of the window (the edges differ: rounded corners and the frame's shadow)
        var difference = MeanDifference(printed.Pixels, composed.Pixels);
        _output.WriteLine($"Mean channel difference in the middle: {difference:0.00}");
        difference.Should().BeLessThan(12, "both read the same window content");
    }

    /// <summary>The mean absolute difference of the blue, green and red channels over the overlap's middle half.</summary>
    private static double MeanDifference(CaptureResult a, CaptureResult b)
    {
        var left = Math.Max(a.ScreenOriginX, b.ScreenOriginX);
        var top = Math.Max(a.ScreenOriginY, b.ScreenOriginY);
        var right = Math.Min(a.ScreenOriginX + a.Width, b.ScreenOriginX + b.Width);
        var bottom = Math.Min(a.ScreenOriginY + a.Height, b.ScreenOriginY + b.Height);
        var (width, height) = (right - left, bottom - top);
        long total = 0, count = 0;
        for (var y = top + height / 4; y < bottom - height / 4; y += 3)
        {
            for (var x = left + width / 4; x < right - width / 4; x += 3)
            {
                var ia = ((y - a.ScreenOriginY) * a.Width + (x - a.ScreenOriginX)) * 4;
                var ib = ((y - b.ScreenOriginY) * b.Width + (x - b.ScreenOriginX)) * 4;
                for (var c = 0; c < 3; c++)
                {
                    total += Math.Abs(a.Bgra[ia + c] - b.Bgra[ib + c]);
                    count++;
                }
            }
        }

        return count == 0 ? double.MaxValue : (double)total / count;
    }
}
