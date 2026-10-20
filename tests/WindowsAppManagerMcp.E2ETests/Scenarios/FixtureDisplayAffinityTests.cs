using System.Drawing;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Captures and the windows that keep themselves out of them (issue #17), in the no-input tier: they only read the screen.
/// <para>
/// Part 1: the fixture's overlay sets WDA_EXCLUDEFROMCAPTURE on itself (--exclude-overlay-from-capture), as an app's toast or
/// overlay does to stay out of screen shares. A copy of the screen over it lists it in excludedFromCapture with the affinity
/// the server reads back, get_window_info's detail 'full' reports that affinity, and capturing the overlay itself warns on
/// every capture method. The tests check the value read back, not missing pixels; they log what each method's image held.
/// </para>
/// <para>
/// Part 2, a regression guard for the 1.15.0 report of a monitor capture that showed only the wallpaper: a copy of the screen
/// (capture_region, and take_screenshot with monitor 'all') over the fixture's WebView2 window, drawn by Chromium through
/// DirectComposition, shows what Windows.Graphics.Capture's frame of the window shows. The no-input tier cannot bring the
/// window to the front, so a window that is covered on the screen is reported and the comparison left out (inconclusive).
/// Skipped, with the reason, on a machine without the WebView2 runtime.
/// </para>
/// </summary>
public class FixtureDisplayAffinityTests
{
    private const uint WdaExcludeFromCapture = 0x11;

    /// <summary>How long WebView2 gets to start and load the page: its first start on a machine can take a while.</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(60);

    private static readonly CaptureOptions Metadata = new() { Output = CaptureOutputMode.Metadata };

    private readonly ITestOutputHelper _output;

    public FixtureDisplayAffinityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>The overlay, once it has rendered with WDA_EXCLUDEFROMCAPTURE set on itself, as the fixture reports it.</summary>
    private nint WaitForTheExcludedOverlay(E2ESession e2e)
    {
        var opened = e2e.Fixture.WaitForEvent(FixtureContract.Events.OverlayOpened);
        _output.WriteLine($"Overlay: {opened}");
        opened.GetBoolean("affinitySet").Should().BeTrue("Windows 10 2004 and later take WDA_EXCLUDEFROMCAPTURE");
        opened.GetInt64("displayAffinity").Should().Be(WdaExcludeFromCapture, "the fixture's own read of its affinity");
        return opened.GetHandle("hwnd");
    }

    private void Log(string what, ScreenshotResult metadata)
    {
        var excluded = metadata.ExcludedFromCapture?.Select(w => $"0x{w.Handle:X} {w.Affinity} '{w.Title}' ({w.X}, {w.Y}, {w.Width}x{w.Height})");
        _output.WriteLine($"{what}: success={metadata.Success} method={metadata.CaptureMethod} region={metadata.CapturedRegion} " +
                          $"excluded=[{string.Join(", ", excluded ?? [])}] warnings=[{string.Join(" | ", metadata.Warnings ?? [])}] {metadata.Error}");
    }

    [DesktopNoInputFact]
    public void ACopyOfTheScreen_OverTheExcludedOverlay_ListsIt_AndGetWindowInfoReportsItsAffinity()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { ExcludeOverlayFromCapture = true });
        var overlay = WaitForTheExcludedOverlay(e2e);
        var frame = e2e.Server.Windows.GetWindowInfo(overlay)!.VisibleBounds ?? e2e.Server.Windows.GetWindowInfo(overlay)!.Bounds;
        var screenshots = e2e.Server.Screenshots;

        // Act - a region around the overlay, its monitor (the primary), and the overlay read in full
        var region = screenshots.CaptureRegion(frame.X - 20, frame.Y - 20, frame.Width + 40, frame.Height + 40, Metadata).Metadata;
        var monitor = screenshots.CaptureMonitor(screenshots.ListScreens().PrimaryIndex, Metadata).Metadata;
        var info = e2e.Server.Get<WindowFinderTools>().GetWindowInfo(handle: overlay, detail: WindowDetail.Full);
        Log("capture_region", region);
        Log("take_screenshot", monitor);
        _output.WriteLine($"get_window_info: displayAffinity={info.DisplayAffinity} title='{info.Title}' process={info.ProcessName}");

        // Assert - the affinity the server read back, with the overlay's own process and title (the fixture is not denied)
        foreach (var capture in new[] { region, monitor })
        {
            capture.Success.Should().BeTrue(capture.Error);
            var listed = capture.ExcludedFromCapture.Should().ContainSingle(w => w.Handle == (long)overlay,
                "the overlay is on the screen there but not in the copy").Which;
            listed.Affinity.Should().Be(DisplayAffinity.ExcludeFromCapture);
            listed.Title.Should().Be(FixtureContract.OverlayTitle(e2e.Fixture.Instance));
            listed.ProcessName.Should().Be(info.ProcessName);
            new Rectangle(listed.X, listed.Y, listed.Width, listed.Height).IntersectsWith(
                new Rectangle(frame.X, frame.Y, frame.Width, frame.Height)).Should().BeTrue("it is listed where it is");
        }

        info.Success.Should().BeTrue(info.Error);
        info.DisplayAffinity.Should().Be(DisplayAffinity.ExcludeFromCapture);
    }

    [DesktopNoInputFact]
    public void CapturingTheExcludedOverlayItself_WarnsDisplayAffinity_WithEveryMethod()
    {
        // Arrange - what PrintWindow and Windows.Graphics.Capture return for such a window is not documented: the log says
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { ExcludeOverlayFromCapture = true });
        var overlay = WaitForTheExcludedOverlay(e2e);
        var screenshots = e2e.Server.Screenshots;

        foreach (var method in new[] { WindowCaptureMethod.Auto, WindowCaptureMethod.PrintWindow, WindowCaptureMethod.Wgc, WindowCaptureMethod.Screen })
        {
            // Act
            var capture = screenshots.CaptureWindow(overlay, includeFrame: true, Metadata with { CaptureMethod = method }).Metadata;
            Log($"capture_window {method}", capture);

            // Assert - a refusal (wgc where Windows.Graphics.Capture is unavailable) has nothing to warn about
            if (!capture.Success)
            {
                capture.ErrorCode.Should().Be(nameof(ScreenshotErrorCode.CaptureFailed), "only wgc may be unavailable");
                method.Should().Be(WindowCaptureMethod.Wgc);
                continue;
            }

            capture.Warnings.Should().ContainSingle(w => w.StartsWith("DisplayAffinity: ", StringComparison.Ordinal),
                "{0} reads a window that keeps itself out of captures", method).Which.Should().Contain("excludeFromCapture");
            capture.ExcludedFromCapture?.Should().NotContain(w => w.Handle == (long)overlay, "the warning reports the window itself");
        }
    }

    [DesktopNoInputFact(Requires = typeof(WebView2Runtime))]
    public void ACopyOfTheScreen_OverTheWebView2Window_ShowsWhatItsCompositorFrameShows()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenWebView = true });
        var window = WaitForThePage(e2e);
        var graphics = e2e.Server.Get<IGraphicsCaptureWrapper>();
        graphics.Availability.Should().Be(GraphicsCaptureAvailability.Available, "Windows.Graphics.Capture should be available on a Windows 10 1903+ desktop");
        var screenshots = e2e.Server.Screenshots;

        // Its frame as the compositor has it: the window's own content, whatever covers it
        var composed = screenshots.CapturePixels(new CaptureTarget.Window(window, IncludeFrame: true, WindowCaptureMethod.Wgc));
        _output.WriteLine($"WGC: {composed.CapturedRegion} {composed.Pixels.Method} warnings=[{string.Join(" | ", composed.Warnings)}]");
        composed.Warnings.Should().NotContain(w => w.StartsWith("ImageBlank", StringComparison.Ordinal), "the page has been drawn");
        var frame = new Rectangle(composed.Pixels.ScreenOriginX, composed.Pixels.ScreenOriginY, composed.Pixels.Width, composed.Pixels.Height);

        // A copy of the screen shows other windows: compare only when nothing that shows in it lies over the window
        if (CoveringWindows(e2e, window, frame) is { Count: > 0 } covering)
        {
            _output.WriteLine($"Inconclusive: the WebView2 window is covered on the screen by {string.Join(", ", covering)}, and the " +
                              "no-input tier cannot bring it to the front. Nothing was compared.");
            return;
        }

        // Act - capture_region over the window, and take_screenshot with monitor 'all'
        var region = screenshots.CapturePixels(new CaptureTarget.Region(frame.X, frame.Y, frame.Width, frame.Height));
        var everything = screenshots.CapturePixels(new CaptureTarget.Monitor(ScreenshotService.VirtualScreenIndex));
        _output.WriteLine($"capture_region: {region.CapturedRegion} {region.Pixels.Method} warnings=[{string.Join(" | ", region.Warnings)}]");
        _output.WriteLine($"take_screenshot all: {everything.CapturedRegion} {everything.Pixels.Method} warnings=[{string.Join(" | ", everything.Warnings)}]");

        // Assert - the same content in the middle of the window (the edges differ: rounded corners and the frame's shadow)
        var fromRegion = MeanDifference(composed.Pixels, region.Pixels);
        var fromAll = MeanDifference(composed.Pixels, everything.Pixels);
        _output.WriteLine($"Mean channel difference in the middle: capture_region {fromRegion:0.00}, take_screenshot all {fromAll:0.00}");
        region.Warnings.Should().NotContain(w => w.StartsWith("ImageBlank", StringComparison.Ordinal));
        fromRegion.Should().BeLessThan(12, "a copy of the screen shows the DirectComposition window as the compositor draws it");
        fromAll.Should().BeLessThan(12, "a capture of every monitor shows it too, not the wallpaper behind it");
    }

    /// <summary>The WebView2 window, once its page has loaded; fails with the fixture's reason when it could not load it.</summary>
    private nint WaitForThePage(E2ESession e2e)
    {
        var outcome = e2e.Fixture.WaitForAnyEvent([FixtureContract.Events.WebViewReady, FixtureContract.Events.WebViewFailed], PageTimeout);
        outcome.Name.Should().Be(FixtureContract.Events.WebViewReady,
            "the WebView2 window should show its page, but it could not (runtime missing: {0}): {1}",
            outcome.GetBoolean("runtimeMissing"), outcome.GetString("message"));
        var window = outcome.GetHandle("hwnd");
        _output.WriteLine($"WebView2 window 0x{window:X}, runtime {outcome.GetString("runtimeVersion")}.");
        return window;
    }

    /// <summary>
    /// The drawn top-level windows above <paramref name="window"/> whose frame overlaps <paramref name="frame"/>, as the server
    /// walks them (<see cref="DrawnWindows"/>), less those that keep themselves out of captures (no copy of the screen shows
    /// them); empty when the window is uncovered.
    /// </summary>
    private static List<string> CoveringWindows(E2ESession e2e, nint window, Rectangle frame)
    {
        var windows = e2e.Server.Get<INativeWindowWrapper>();
        var covering = new List<string>();
        foreach (var above in DrawnWindows.FrontToBack(windows))
        {
            if (above == window)
            {
                return covering;
            }

            if (windows.GetWindowDisplayAffinity(above) == WdaExcludeFromCapture ||
                (windows.GetExtendedFrameBounds(above) ?? windows.GetWindowRect(above)) is not { } rect ||
                !Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom).IntersectsWith(frame))
            {
                continue;
            }

            covering.Add($"0x{above:X} '{windows.GetWindowText(above)}' ({windows.GetClassName(above)})");
        }

        // Not drawn at all: nothing of it can be compared
        covering.Add("nothing: the window is not drawn");
        return covering;
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
                var ia = ((long)(y - a.ScreenOriginY) * a.Width + (x - a.ScreenOriginX)) * 4;
                var ib = ((long)(y - b.ScreenOriginY) * b.Width + (x - b.ScreenOriginX)) * 4;
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
