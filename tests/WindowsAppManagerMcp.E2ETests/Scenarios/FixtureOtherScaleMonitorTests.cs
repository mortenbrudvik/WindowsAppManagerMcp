using WindowsAppManagerMcp.E2ETests.Harness;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The fixture on a monitor whose scale differs from the primary's (roadmap M3.F30): the server's coordinates must still be
/// the physical pixels the fixture draws at, and a --no-activate fixture must stay out of the foreground although WPF's DPI
/// change inside Show activates its window. The first desktop run of M3.F2 failed both ways on a 100% monitor beside a 150%
/// primary.
/// </summary>
public class FixtureOtherScaleMonitorTests
{
    private const uint BoundsTolerance = 2;

    private readonly ITestOutputHelper _output;

    public FixtureOtherScaleMonitorTests(ITestOutputHelper output) => _output = output;

    private E2ESession Start() =>
        E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OtherScaleMonitor = true });

    [DesktopNoInputFact(Requires = typeof(MonitorAtAnotherScale))]
    public void TheFixture_OpensAtTheOtherMonitorsScale_AndLeavesTheAgentsHostInFront()
    {
        // Arrange and Act
        using var e2e = Start();
        var ready = e2e.Fixture.Ready;

        // Assert - per-monitor v2 at the other monitor's DPI, not the primary's, and the terminal still in front
        ready.DpiAwareness.Should().Be("perMonitorV2");
        ready.Dpi.Should().NotBe((int)MonitorAtAnotherScale.PrimaryDpi, "the window is on the monitor at another scale");
        e2e.RequireAgentHostInFront();
    }

    [DesktopNoInputFact(Requires = typeof(MonitorAtAnotherScale))]
    public async Task GetUIElements_BoundsMatchWhereTheFixtureDrawsItsElements_OnTheOtherMonitor()
    {
        // Arrange
        using var e2e = Start();
        var ready = e2e.Fixture.Ready;

        // Act
        var result = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);

        // Assert - both sides are per-monitor v2, so both report the physical pixels of the other monitor
        result.Success.Should().BeTrue(result.Error);
        var byId = UiaReads.Flatten(result.Elements)
            .Where(e => e.AutomationId != null)
            .GroupBy(e => e.AutomationId!)
            .ToDictionary(g => g.Key, g => g.First().Bounds);
        foreach (var (id, expected) in ready.Elements)
        {
            byId.Should().ContainKey(id);
            var actual = byId[id]!;
            _output.WriteLine($"{id}: UI Automation ({actual.X}, {actual.Y}, {actual.Width}x{actual.Height}), fixture {expected}");
            actual.X.Should().BeCloseTo(expected.X, BoundsTolerance, "x of '{0}'", id);
            actual.Y.Should().BeCloseTo(expected.Y, BoundsTolerance, "y of '{0}'", id);
            actual.Width.Should().BeCloseTo(expected.Width, BoundsTolerance, "width of '{0}'", id);
            actual.Height.Should().BeCloseTo(expected.Height, BoundsTolerance, "height of '{0}'", id);
        }
    }

    [DesktopNoInputFact(Requires = typeof(MonitorAtAnotherScale))]
    public void CaptureWindow_ClientArea_IsTheFixturesClientArea_WithTheSwatchesAtTheirOffsets_OnTheOtherMonitor()
    {
        // Arrange
        using var e2e = Start();
        var ready = e2e.Fixture.Ready;

        // Act - full resolution and lossless, so image pixels map 1:1 to the other monitor's physical pixels
        var capture = e2e.Server.Screenshots.CaptureWindow(
            e2e.Fixture.MainWindowHandle, includeFrame: false, new CaptureOptions { Format = CaptureFormat.Png, MaxDimension = 0 });

        // Assert - the region captured is where the fixture says its client area is, and the swatches are where it draws them
        var result = capture.Metadata;
        result.Success.Should().BeTrue(result.Error);
        _output.WriteLine($"captured {result.CapturedRegion}, fixture client {ready.ClientRect}");
        result.CapturedRegion.Should().Be(new CapturedRegion(ready.ClientRect.X, ready.ClientRect.Y, ready.ClientRect.Width, ready.ClientRect.Height));
        using var image = CaptureChecks.DecodeImage(capture);
        CaptureChecks.SwatchMismatches(image, ready.ClientRect.X, ready.ClientRect.Y, ready).Should().BeEmpty();
    }
}
