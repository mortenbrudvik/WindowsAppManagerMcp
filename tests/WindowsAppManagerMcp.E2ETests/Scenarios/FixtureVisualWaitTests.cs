using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// wait_for_visual's waits (roadmap M6.4) against the fixture's change panel (<c>--change-after-ms</c>, recolored and
/// relabeled once) and spinner (<c>--animate-for-ms</c>, turning and then still), compared with the rectangles the fixture
/// reports. No-input tier: the waits only read the fixture's pixels; nothing is clicked, typed or moved, and the harness
/// owns and closes the fixture.
/// </summary>
public class FixtureVisualWaitTests
{
    private readonly ITestOutputHelper _output;

    public FixtureVisualWaitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static VisualWaitRequest Request(VisualTarget target, VisualWaitCondition condition = VisualWaitCondition.Changed,
        int timeoutMs = 10_000, int stableMs = 500) => new()
    {
        Target = target,
        Condition = condition,
        TimeoutMs = timeoutMs,
        PollMs = VisualChangeService.DefaultPollMs,
        StableMs = stableMs
    };

    private void Log(VisualWaitResult result) =>
        _output.WriteLine($"{result.Condition}: satisfied {result.Satisfied}, reason {result.Reason}, {result.ElapsedMs} ms, " +
                          $"{result.Checks} checks, {result.ChangedPercent}% in [{string.Join(", ", result.ChangedRegions?.Select(Describe) ?? [])}], " +
                          $"method {result.CaptureMethod}, {result.ErrorCode} {result.Error} " +
                          $"warnings [{string.Join("; ", result.Warnings ?? [])}]");

    private static string Describe(BoundsDto r) => $"({r.X}, {r.Y}, {r.Width}x{r.Height})";

    private static bool Overlaps(BoundsDto region, ScreenRect rect) =>
        region.X < rect.X + rect.Width && rect.X < region.X + region.Width &&
        region.Y < rect.Y + rect.Height && rect.Y < region.Y + region.Height;

    private static ScreenRect RectOf(FixtureEvent fixtureEvent) => ScreenRect.From(fixtureEvent.Data.GetProperty("rect"));

    [DesktopNoInputFact]
    public async Task Changed_OnTheMainWindow_SeesThePanelChange_WhereThePanelIs()
    {
        // Arrange - the panel changes 1.5 s after the ready event, so the wait's first frame comes before it
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { ChangeAfter = TimeSpan.FromMilliseconds(1500) });
        var waits = e2e.Server.Get<IVisualChangeService>();

        // Act
        var result = await waits.WaitAsync(Request(new VisualTarget.Window(e2e.Fixture.MainWindowHandle, IncludeFrame: true)));

        // Assert
        Log(result);
        var panel = RectOf(e2e.Fixture.WaitForEvent(FixtureContract.Events.PanelChanged));
        _output.WriteLine($"panel {panel}");
        result.Satisfied.Should().BeTrue(result.Error);
        result.Reason.Should().Be("pixels");
        result.CaptureMethod.Should().Be("PrintWindow");
        result.ChangedRegions.Should().Contain(region => Overlaps(region, panel), "the panel is what changed");
        result.ChangedRegions.Should().OnlyContain(region => Overlaps(region, Inflate(panel, 16)),
            "nothing else in the window changed (no caret, no status text)");
    }

    [DesktopNoInputFact]
    public async Task Changed_Since_ACaptureBeforeThePanelChange_IsSatisfiedAtTheFirstCheck()
    {
        // Arrange - a metadata capture (no image) before the change, then the change, then the wait
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { ChangeAfter = TimeSpan.FromMilliseconds(1500) });
        var before = e2e.Server.Screenshots.CaptureWindow(e2e.Fixture.MainWindowHandle, includeFrame: true, new CaptureOptions { Output = CaptureOutputMode.Metadata }).Metadata;
        before.Success.Should().BeTrue(before.Error);
        var panel = RectOf(e2e.Fixture.WaitForEvent(FixtureContract.Events.PanelChanged));

        // Act
        var result = await e2e.Server.Get<IVisualChangeService>().WaitAsync(Request(new VisualTarget.Since(before.CaptureId!), timeoutMs: 0));

        // Assert
        Log(result);
        result.Satisfied.Should().BeTrue(result.Error);
        result.Checks.Should().Be(1);
        result.ChangedRegions.Should().Contain(region => Overlaps(region, panel));
        result.CaptureId.Should().NotBeNullOrEmpty();
    }

    [DesktopNoInputFact]
    public async Task Changed_Since_TheLastFrame_OfAStillWindow_TimesOut()
    {
        // Arrange - the window does not change; a first wait's last frame is the second wait's baseline
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var waits = e2e.Server.Get<IVisualChangeService>();
        var first = await waits.WaitAsync(Request(new VisualTarget.Window(e2e.Fixture.MainWindowHandle, IncludeFrame: true), timeoutMs: 1500));
        Log(first);

        // Act
        var second = await waits.WaitAsync(Request(new VisualTarget.Since(first.CaptureId!), timeoutMs: 1000));

        // Assert - no false change from the caret in the focused text box, the clock or the frame
        Log(second);
        first.ErrorCode.Should().Be("WaitTimedOut");
        first.ChangedPercent.Should().Be(0);
        second.ErrorCode.Should().Be("WaitTimedOut");
        second.ChangedPercent.Should().Be(0);
    }

    [DesktopNoInputFact]
    public async Task Stable_IsReached_OnlyAfterTheSpinnerStops()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { AnimateFor = TimeSpan.FromMilliseconds(2000) });
        e2e.Fixture.WaitForEvent(FixtureContract.Events.AnimationStarted);

        // Act
        var result = await e2e.Server.Get<IVisualChangeService>().WaitAsync(
            Request(new VisualTarget.Window(e2e.Fixture.MainWindowHandle, IncludeFrame: true), VisualWaitCondition.Stable, stableMs: 500));

        // Assert - the spinner's stop event was written before the wait saw half a second of stillness
        Log(result);
        result.Satisfied.Should().BeTrue(result.Error);
        result.StableForMs.Should().BeGreaterThanOrEqualTo(500);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.AnimationStopped, timeout: TimeSpan.Zero);
        result.ElapsedMs.Should().BeGreaterThan(1000, "the spinner turned for about 2 s from before the wait started");
    }

    [DesktopNoInputFact]
    public async Task Stable_WhileTheSpinnerTurns_TimesOut_WithTheSpinnerAsWhatKeptMoving()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { AnimateFor = TimeSpan.FromSeconds(8) });
        var spinner = RectOf(e2e.Fixture.WaitForEvent(FixtureContract.Events.AnimationStarted));

        // Act
        var result = await e2e.Server.Get<IVisualChangeService>().WaitAsync(
            Request(new VisualTarget.Window(e2e.Fixture.MainWindowHandle, IncludeFrame: true), VisualWaitCondition.Stable, timeoutMs: 1500));

        // Assert
        Log(result);
        _output.WriteLine($"spinner {spinner}");
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.TimedOut.Should().BeTrue();
        result.ChangedRegions.Should().NotBeNullOrEmpty();
        result.ChangedRegions.Should().OnlyContain(region => Overlaps(region, Inflate(spinner, 16)));
    }

    [DesktopNoInputFact]
    public async Task Changed_OnARegionOverThePanel_SeesTheChange()
    {
        // Arrange - a screen region just around the panel: a copy of the screen, so the fixture must be uncovered there
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { ChangeAfter = TimeSpan.FromMilliseconds(1500) });
        var panel = e2e.Fixture.Ready.Element(FixtureContract.Ids.ChangePanel);
        var area = Inflate(panel, 8);

        // Act
        var result = await e2e.Server.Get<IVisualChangeService>().WaitAsync(
            Request(new VisualTarget.Region(area.X, area.Y, area.Width, area.Height)));

        // Assert
        Log(result);
        result.Satisfied.Should().BeTrue(result.Error);
        result.CaptureMethod.Should().Be("ScreenBitBlt");
        result.ChangedRegions.Should().Contain(region => Overlaps(region, panel));
    }

    private static ScreenRect Inflate(ScreenRect rect, int by) => new(rect.X - by, rect.Y - by, rect.Width + 2 * by, rect.Height + 2 * by);
}
