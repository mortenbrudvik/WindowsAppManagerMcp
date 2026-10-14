using WindowsAppManagerMcp.E2ETests.Harness;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// mouse_click and drag against the fixture (roadmap M4.3): the full (input) tier. A modifier click reaches the window with
/// its modifiers down, and none stays down after it; the middle and X buttons, and a triple click, arrive as such; a drag
/// presses on one swatch and releases on another, with its modifiers held, and leaves nothing held; a drop point off the
/// target is refused before anything is pressed. Everything happens on the fixture's colour swatches, which do nothing when
/// clicked. The fixture logs every press and release with its point, button and modifiers
/// (<see cref="FixtureContract.Events.MouseDown"/>, <see cref="FixtureContract.Events.MouseUp"/>).
/// </summary>
public class FixtureMouseClickDragTests
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;

    public FixtureMouseClickDragTests(ITestOutputHelper output) => _output = output;

    [DesktopFact]
    public void MouseClick_ACtrlShiftClick_ArrivesWithBothModifiers_AndNeitherStaysDown()
    {
        using var e2e = StartInFront();
        var red = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchRed);
        var after = LastSequence(e2e);

        var result = e2e.Server.Input.MouseClick(red.CenterX, red.CenterY, MouseButton.Primary, 1, [MouseModifier.Ctrl, MouseModifier.Shift],
            Target(e2e), releaseHeldModifiers: false, CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        var press = Next(e2e, FixtureContract.Events.MouseDown, after);
        var release = Next(e2e, FixtureContract.Events.MouseUp, press.Sequence);
        _output.WriteLine($"{press}, {release}");
        (press.GetString("button"), press.GetString("modifiers"), press.GetInt64("x"), press.GetInt64("y"))
            .Should().Be(("left", "ctrl+shift", red.CenterX, red.CenterY));
        release.GetString("modifiers").Should().Be("ctrl+shift");
        PlainClickSeesNoModifier(e2e);
    }

    [DesktopTheory]
    [InlineData(MouseButton.Middle, "middle")]
    [InlineData(MouseButton.X1, "x1")]
    [InlineData(MouseButton.X2, "x2")]
    public void MouseClick_TheMiddleAndXButtons_ArriveAsThemselves(MouseButton button, string expected)
    {
        using var e2e = StartInFront();
        var green = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchGreen);
        var after = LastSequence(e2e);

        var result = e2e.Server.Input.MouseClick(green.CenterX, green.CenterY, button, 1, [], Target(e2e), releaseHeldModifiers: false,
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        var press = Next(e2e, FixtureContract.Events.MouseDown, after);
        var release = Next(e2e, FixtureContract.Events.MouseUp, press.Sequence);
        (press.GetString("button"), press.GetInt64("x"), press.GetInt64("y")).Should().Be((expected, green.CenterX, green.CenterY));
        release.GetString("button").Should().Be(expected);
    }

    [DesktopFact]
    public void MouseClick_ATripleClick_IsOneTripleClick_AtTheExactPixel()
    {
        using var e2e = StartInFront();
        var blue = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchBlue);
        var (x, y) = (blue.X + 2, blue.Y + 2);
        var after = LastSequence(e2e);

        var result = e2e.Server.Input.MouseClick(x, y, MouseButton.Primary, 3, [], Target(e2e), releaseHeldModifiers: false,
            CancellationToken.None);

        result.Success.Should().BeTrue(result.Error);
        result.ClickCount.Should().Be(3);
        var presses = new List<FixtureEvent>();
        for (var i = 0; i < 3; i++)
        {
            presses.Add(Next(e2e, FixtureContract.Events.MouseDown, presses.LastOrDefault()?.Sequence ?? after));
        }

        _output.WriteLine(string.Join(", ", presses));
        presses.Select(p => (p.GetInt64("clickCount"), p.GetInt64("x"), p.GetInt64("y"))).Should().Equal((1L, x, y), (2L, x, y), (3L, x, y));
    }

    [DesktopFact]
    public async Task Drag_FromOneSwatchToAnother_PressesOnTheFirst_AndReleasesOnTheSecond_WithCtrlHeld()
    {
        using var e2e = StartInFront();
        var red = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchRed);
        var blue = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchBlue);
        var after = LastSequence(e2e);

        var result = await e2e.Server.Input.DragAsync(
            new DragRequest(red.CenterX, red.CenterY, blue.CenterX, blue.CenterY, MouseButton.Primary, [MouseModifier.Ctrl]),
            Target(e2e), Target(e2e), releaseHeldModifiers: false, CancellationToken.None);

        result.Success.Should().BeTrue($"{result.ErrorCode}: {result.Error}");
        result.Pressed.Should().BeTrue();
        (result.ActualX, result.ActualY).Should().Be((blue.CenterX, blue.CenterY));
        var press = Next(e2e, FixtureContract.Events.MouseDown, after);
        var release = Next(e2e, FixtureContract.Events.MouseUp, press.Sequence);
        _output.WriteLine($"{press}, {release}");
        (press.GetString("id"), press.GetString("button"), press.GetString("modifiers"), press.GetInt64("x"), press.GetInt64("y"))
            .Should().Be((FixtureContract.Ids.SwatchRed, "left", "ctrl", red.CenterX, red.CenterY));
        (release.GetString("id"), release.GetString("button"), release.GetString("modifiers"), release.GetInt64("x"), release.GetInt64("y"))
            .Should().Be((FixtureContract.Ids.SwatchBlue, "left", "ctrl", blue.CenterX, blue.CenterY));
        e2e.Fixture.Events.ReadAll().Where(e => e.Sequence > press.Sequence && e.Sequence < release.Sequence)
            .Should().NotContain(e => e.Name == FixtureContract.Events.MouseDown || e.Name == FixtureContract.Events.MouseUp,
                "the button stays down from the press to the drop");
        PlainClickSeesNoModifier(e2e);
    }

    [DesktopFact]
    public async Task Drag_ToAPointOffTheTarget_IsRefusedBeforeThePress()
    {
        using var e2e = StartInFront();
        var red = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchRed);
        var window = e2e.Fixture.Ready.WindowRect;
        var after = LastSequence(e2e);

        // Just right of the fixture window: another window or the desktop, never the target
        var result = await e2e.Server.Input.DragAsync(
            new DragRequest(red.CenterX, red.CenterY, window.X + window.Width + 20, red.CenterY, MouseButton.Primary, []),
            Target(e2e), Target(e2e), releaseHeldModifiers: false, CancellationToken.None);

        _output.WriteLine($"{result.ErrorCode}: {result.Error}");
        result.Success.Should().BeFalse();
        result.Pressed.Should().BeFalse();
        result.ErrorCode.Should().BeOneOf(InputErrorCode.TargetOccluded, InputErrorCode.CoordinatesOutOfBounds);
        Thread.Sleep(200);
        e2e.Fixture.Events.ReadAll().Should().NotContain(e => e.Sequence > after && e.Name == FixtureContract.Events.MouseDown);
    }

    /// <summary>A plain click after the call: the window sees no modifier down, so none the call pressed stayed down.</summary>
    private static void PlainClickSeesNoModifier(E2ESession e2e)
    {
        Thread.Sleep(E2ENative.GetDoubleClickTime() + 50);
        var green = e2e.Fixture.Ready.Element(FixtureContract.Ids.SwatchGreen);
        var after = LastSequence(e2e);
        e2e.Server.Input.Click(green.CenterX, green.CenterY, Target(e2e), releaseHeldModifiers: false, CancellationToken.None)
            .Success.Should().BeTrue("no modifier the call pressed is still down (that would be ModifiersHeld)");
        var press = Next(e2e, FixtureContract.Events.MouseDown, after);
        (press.GetString("modifiers"), press.GetInt64("clickCount")).Should().Be(("", 1L));
    }

    /// <summary>The fixture, brought to the front by the server (verified activation), as the modifier keys need it there.</summary>
    private E2ESession StartInFront()
    {
        var e2e = E2ESession.Start(DesktopTier.Input, _output);
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

    private static FixtureEvent Next(E2ESession e2e, string name, long after) => e2e.Fixture.WaitForEvent(name, e => e.Sequence > after);
}
