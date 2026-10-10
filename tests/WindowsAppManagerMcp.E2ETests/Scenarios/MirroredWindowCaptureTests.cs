namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// capture_window without the frame of a right-to-left mirrored window (WS_EX_LAYOUTRTL, as in an
/// Arabic or Hebrew UI). Client x = 0 is the client area's right edge there, so the capture origin
/// must come from the whole client rectangle, not from ClientToScreen(0, 0). The window is the test's
/// own borderless popup (<see cref="MirroredTestWindow"/>), shown without activation. No-input tier.
/// </summary>
public class MirroredWindowCaptureTests
{
    private readonly ITestOutputHelper _output;

    public MirroredWindowCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public void CaptureWindow_ClientAreaOfAMirroredWindow_StartsAtItsLeftEdge()
    {
        // Arrange - physical pixels, as in the server; the server reads only (capture)
        _output.WriteLine($"Test host DPI awareness: {E2EProcess.EnsureServerDpiAwareness()}.");
        using var server = ServerHarness.Create(DesktopTier.NoInput, fixtureProcessId: 0);
        using var window = MirroredTestWindow.Show(x: 120, y: 140, width: 240, height: 160);
        var (left, top, right, bottom) = window.WindowRect;
        _output.WriteLine($"Mirrored window 0x{window.Handle:X}: ({left}, {top}) - ({right}, {bottom}), client x=0 at {window.ClientZeroOnScreenX}.");

        window.IsMirrored.Should().BeTrue("the test needs a really mirrored window");
        window.ClientZeroOnScreenX.Should().BeGreaterThan(left + ((right - left) / 2),
            "ClientToScreen(0, 0) lands on the right edge of a mirrored window, the origin this test guards against");

        // Act
        var capture = server.Screenshots.CaptureWindow(
            window.Handle, includeFrame: false, new CaptureOptions { Format = "png", MaxDimension = 0, Output = "metadata" });

        // Assert - a borderless popup's client area is its window rectangle
        capture.Metadata.Success.Should().BeTrue(capture.Metadata.Error);
        capture.Metadata.CapturedRegion.Should().Be(new CapturedRegion(left, top, right - left, bottom - top));
        capture.Metadata.ImageToScreen!.OffsetX.Should().Be(left);
    }
}
