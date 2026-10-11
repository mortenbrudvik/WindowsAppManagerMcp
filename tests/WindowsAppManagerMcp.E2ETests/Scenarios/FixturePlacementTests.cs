namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The launch tools' placement (roadmap M3.13) against the fixture: the full (input) tier, since the placement moves the
/// fixture's window and brings it to the front. The harness's guard lets the server act only on fixture windows (see
/// FixtureOnlyCallPolicy). The fixture is started by the harness, so this checks the placement path the launch tools take
/// once a launch has its window, not the launch itself.
/// </summary>
public class FixturePlacementTests
{
    private readonly ITestOutputHelper _output;

    public FixturePlacementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public async Task LaunchPlacement_PlacesTheFixtureAtItsBounds_RechecksIt_AndBringsItToTheFront()
    {
        // Arrange - a frame inside the primary monitor's work area
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var workArea = e2e.Server.Monitors.GetPrimaryMonitor().WorkArea;
        var target = new BoundsDto(workArea.X + 40, workArea.Y + 40, 640, 480);
        LaunchPlacementPlan.TryCreate(new LaunchPlacement(Bounds: target, Focus: true), out var plan, out _, out var error)
            .Should().BeTrue(error);

        // Act
        var outcome = (await e2e.Server.Get<ILaunchPlacementService>().PlaceAsync([(main, plan!)])).Single();
        _output.WriteLine(
            $"placement: errorCode={outcome.ErrorCode}, error={outcome.Error}, frame={outcome.Window?.VisibleBounds}, " +
            $"state={outcome.Window?.State}, focused={outcome.Focused}, reapplied={outcome.Reapplied}");

        // Assert - the server's report
        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Window!.State.Should().Be(WindowState.Normal);
        outcome.Window.VisibleBounds.Should().Be(new WindowRect(target.X, target.Y, target.Width, target.Height),
            "the placement puts the visible frame exactly at the bounds");
        outcome.Reapplied.Should().BeFalse("the fixture does not restore saved bounds");
        outcome.Focused.Should().BeTrue();

        // ... agrees with the desktop
        E2ENative.GetAncestor(E2ENative.GetForegroundWindow(), E2ENative.GA_ROOTOWNER).Should().Be(main);
    }
}
