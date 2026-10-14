using WindowsAppManagerMcp.E2ETests.Harness;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Mouse correctness against the fixture (roadmap M4.2): the full (input) tier. A click lands on the exact pixel it was
/// aimed at, on the 150% primary and on a monitor at another scale; click and double_click press the user's primary
/// button and right_click the secondary one (the fixture logs the button as Windows hands it to the window, after the
/// user's swap, so this holds with swapped buttons too, without changing the setting); a double click is one double click.
/// The clicks go on the fixture's colour swatches, which do nothing when clicked. The fixture logs every press with the
/// point of its button message (<see cref="FixtureContract.Events.MouseDown"/>).
/// </summary>
public class FixtureMouseTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureMouseTests(ITestOutputHelper output) => _output = output;

    [DesktopFact]
    public void Click_LandsOnTheExactPixel_AndPressesThePrimaryButton()
    {
        using var e2e = StartInFront();

        ClickEveryCornerAndCentreOfTheSwatches(e2e);
    }

    [DesktopFact(Requires = typeof(MonitorAtAnotherScale))]
    public void Click_LandsOnTheExactPixel_OnAMonitorAtAnotherScale()
    {
        using var e2e = StartInFront(new FixtureLaunchOptions { OtherScaleMonitor = true });
        e2e.Fixture.Ready.Dpi.Should().NotBe((int)MonitorAtAnotherScale.PrimaryDpi, "the fixture is on the monitor at another scale");

        ClickEveryCornerAndCentreOfTheSwatches(e2e);
    }

    [DesktopFact]
    public void RightClick_PressesTheSecondaryButton_AtTheExactPixel()
    {
        using var e2e = StartInFront();
        var swatch = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchGreen);
        var after = LastSequence(e2e);

        var result = e2e.Server.Input.RightClick(swatch.CenterX, swatch.CenterY, Target(e2e), releaseHeldModifiers: false, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        var press = NextPress(e2e, after);
        (press.GetString("button"), press.GetInt64("x"), press.GetInt64("y")).Should().Be(("right", swatch.CenterX, swatch.CenterY));
    }

    [DesktopFact]
    public void DoubleClick_IsOneDoubleClick_AtTheExactPixel()
    {
        using var e2e = StartInFront();
        var swatch = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchBlue);
        var (x, y) = (swatch.X + 3, swatch.Y + swatch.Height - 4);
        var after = LastSequence(e2e);

        var result = e2e.Server.Input.DoubleClick(x, y, Target(e2e), releaseHeldModifiers: false, CancellationToken.None);

        // Both presses at the pixel, and the window counts the second as the double click
        result.Success.Should().BeTrue(result.Error);
        var first = NextPress(e2e, after);
        var second = NextPress(e2e, first.Sequence);
        _output.WriteLine($"presses: {first}, {second}");
        (first.GetString("button"), first.GetInt64("clickCount"), first.GetInt64("x"), first.GetInt64("y")).Should().Be(("left", 1L, x, y));
        (second.GetString("button"), second.GetInt64("clickCount"), second.GetInt64("x"), second.GetInt64("y")).Should().Be(("left", 2L, x, y));
    }

    [DesktopFact]
    public void MouseMove_PutsThePointerOnTheExactPixel()
    {
        using var e2e = StartInFront();
        var swatch = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchRed);
        var (x, y) = (swatch.X + swatch.Width - 1, swatch.Y + 1);

        var result = e2e.Server.Input.MoveMouse(x, y, Target(e2e), releaseHeldModifiers: false, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        (result.ActualX, result.ActualY).Should().Be((x, y));
        E2ENative.GetCursorPos(out var cursor);
        (cursor.X, cursor.Y).Should().Be((x, y));
    }

    /// <summary>
    /// Clicks the four corner pixels and the centre of every swatch, and checks that the fixture got each press as a
    /// primary (left) press at that very pixel.
    /// </summary>
    private void ClickEveryCornerAndCentreOfTheSwatches(E2ESession e2e)
    {
        var misses = new List<string>();
        foreach (var swatch in FixtureContract.Swatches)
        {
            var rect = e2e.Fixture.Ready.Element(swatch.Id);
            foreach (var (x, y) in new[]
                     {
                         (rect.X, rect.Y), (rect.X + rect.Width - 1, rect.Y), (rect.X, rect.Y + rect.Height - 1),
                         (rect.X + rect.Width - 1, rect.Y + rect.Height - 1), (rect.CenterX, rect.CenterY)
                     })
            {
                var after = LastSequence(e2e);
                var result = e2e.Server.Input.Click(x, y, Target(e2e), releaseHeldModifiers: false, CancellationToken.None);
                result.Success.Should().BeTrue($"the click at ({x}, {y}) on {swatch.Id}: {result.ErrorCode} {result.Error}");
                var press = NextPress(e2e, after);
                _output.WriteLine($"({x}, {y}) on {swatch.Id}: {press}");
                if (press.GetInt64("x") != x || press.GetInt64("y") != y || press.GetString("button") != "left")
                {
                    misses.Add($"aimed at ({x}, {y}) on {swatch.Id}, the fixture got {press}");
                }

                // Far enough apart in time that the next press is not counted as a double click
                Thread.Sleep(E2ENative.GetDoubleClickTime() + 50);
            }
        }

        misses.Should().BeEmpty();
    }

    /// <summary>The fixture, brought to the front by the server (verified activation), as the clicks need it there.</summary>
    private E2ESession StartInFront(FixtureLaunchOptions? launch = null)
    {
        var e2e = E2ESession.Start(DesktopTier.Input, _output, launch);
        try
        {
            var main = e2e.Fixture.MainWindowHandle;
            var focus = e2e.Server.Foreground.EnsureForeground(main);
            Wait.Until(() => E2ENative.GetAncestor(E2ENative.GetForegroundWindow(), E2ENative.GA_ROOTOWNER) == main, SettleTimeout)
                .Should().BeTrue($"focus_window reported isForeground={focus.IsForeground} ({focus.ErrorCode}: {focus.Error}); nothing was clicked");
            return e2e;
        }
        catch
        {
            e2e.Dispose();
            throw;
        }
    }

    private static InputTarget Target(E2ESession e2e) => InputTarget.Window(e2e.Fixture.MainWindowHandle);

    private static long LastSequence(E2ESession e2e) => e2e.Fixture.Events.ReadAll().LastOrDefault()?.Sequence ?? 0;

    private static FixtureEvent NextPress(E2ESession e2e, long after) =>
        e2e.Fixture.WaitForEvent(FixtureContract.Events.MouseDown, e => e.Sequence > after);
}
