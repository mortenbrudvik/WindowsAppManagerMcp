namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// The two ways a LaunchPlacementPlan is made (review finding F3): TryCreate for a launch tool's placement, ForLayout for a
/// window apply_layout launched, each with its own rules; no other code can make one, nor change a plan's snap, state or
/// focus afterwards. ForLayout keeps today's plans for valid presets, and resolves focus with state minimized one way:
/// the window stays minimized and is not brought to the front.
/// </summary>
public class LaunchPlacementPlanForLayoutTests
{
    private const string Key = "m-22222222";
    private static readonly WindowRect Rect = new(192, 104, 960, 520);
    private static readonly RelativePosition Fractions = new(0.1, 0.1, 0.5, 0.5);

    [Fact]
    public void APlan_HasNoPublicConstructor_AndItsSnapStateAndFocusCannotBeSetAfterwards()
    {
        var type = typeof(LaunchPlacementPlan);

        type.GetConstructors().Should().BeEmpty("only TryCreate and ForLayout make a plan");
        foreach (var name in new[] { nameof(LaunchPlacementPlan.Snap), nameof(LaunchPlacementPlan.State), nameof(LaunchPlacementPlan.Focus) })
        {
            type.GetProperty(name)!.SetMethod.Should().BeNull($"a with expression must not change {name}");
        }
    }

    [Fact]
    public void ASnap_IsSnappedOnItsMonitor_FoundAgainByItsKey()
    {
        var plan = LaunchPlacementPlan.ForLayout(Key, 1, snap: SnapPosition.LeftHalf, focus: true);

        plan.Should().BeEquivalentTo(new
        {
            Snap = (SnapPosition?)SnapPosition.LeftHalf, MonitorIndex = (int?)1, Bounds = (WindowRect?)null, State = (LaunchWindowState?)null,
            Focus = true, Frame = BoundsFrame.Visible, MonitorKey = Key, RelativeBounds = (RelativePosition?)null
        });
    }

    [Fact]
    public void BoundsWithStateMaximized_AreTheRestoreRectangle_AsTheWindowRect()
    {
        var plan = LaunchPlacementPlan.ForLayout(Key, 1, bounds: Rect, relativeBounds: Fractions, state: LaunchWindowState.Maximized);

        plan.Bounds.Should().Be(Rect);
        plan.State.Should().Be(LaunchWindowState.Maximized);
        plan.Frame.Should().Be(BoundsFrame.Outer);
        plan.RelativeBounds.Should().Be(Fractions);
        plan.MonitorKey.Should().Be(Key);
        plan.MonitorIndex.Should().BeNull("bounds are virtual-screen coordinates; the key finds the monitor again");
    }

    [Fact]
    public void NeitherSnapNorBounds_IsMaximizedOnItsMonitor()
    {
        var plan = LaunchPlacementPlan.ForLayout(Key, 2, state: LaunchWindowState.Maximized);

        plan.MonitorIndex.Should().Be(2);
        plan.State.Should().Be(LaunchWindowState.Maximized);
        plan.ChangesGeometry.Should().BeTrue();
    }

    [Fact]
    public void FocusWithStateMinimized_StaysMinimized_AndIsNotBroughtToTheFront()
    {
        var plan = LaunchPlacementPlan.ForLayout(Key, 0, bounds: Rect, relativeBounds: Fractions, state: LaunchWindowState.Minimized, focus: true);

        plan.State.Should().Be(LaunchWindowState.Minimized);
        plan.Focus.Should().BeFalse("minimized wins");
    }

    [Fact]
    public void TheSamePlacement_MakesEqualPlans()
    {
        LaunchPlacementPlan.ForLayout(Key, 1, snap: SnapPosition.LeftHalf).Should()
            .Be(LaunchPlacementPlan.ForLayout(Key, 1, snap: SnapPosition.LeftHalf));
    }

    public static TheoryData<string, Func<LaunchPlacementPlan>> BrokenRules() => new()
    {
        { "snap and bounds", () => LaunchPlacementPlan.ForLayout(Key, 0, SnapPosition.LeftHalf, Rect, Fractions) },
        { "bounds without fractions", () => LaunchPlacementPlan.ForLayout(Key, 0, bounds: Rect) },
        { "fractions without bounds", () => LaunchPlacementPlan.ForLayout(Key, 0, relativeBounds: Fractions, state: LaunchWindowState.Maximized) },
        { "a snap maximized", () => LaunchPlacementPlan.ForLayout(Key, 0, SnapPosition.LeftHalf, state: LaunchWindowState.Maximized) },
        { "nothing to do", () => LaunchPlacementPlan.ForLayout(Key, 0) },
        { "neither snap nor bounds, minimized", () => LaunchPlacementPlan.ForLayout(Key, 0, state: LaunchWindowState.Minimized) },
        { "bounds of 0 px", () => LaunchPlacementPlan.ForLayout(Key, 0, bounds: new WindowRect(0, 0, 0, 520), relativeBounds: Fractions) },
        { "no monitor key", () => LaunchPlacementPlan.ForLayout(" ", 0, SnapPosition.LeftHalf) },
        { "a negative monitor index", () => LaunchPlacementPlan.ForLayout(Key, -1, SnapPosition.LeftHalf) },
    };

    [Theory]
    [MemberData(nameof(BrokenRules))]
    public void APlanThatBreaksTheLayoutsRules_IsABug_AndThrows(string why, Func<LaunchPlacementPlan> create)
    {
        create.Should().Throw<ArgumentException>(why);
    }

    [Fact]
    public void TryCreate_StillRefusesWhatALaunchToolsPlacementMayNotAsk()
    {
        // Bounds with maximized, and focus with minimized, stay refused for launch_application
        LaunchPlacementPlan.TryCreate(new LaunchPlacement(Bounds: new BoundsDto(0, 0, 800, 600), State: "maximized"), out _, out var maximized, out _)
            .Should().BeFalse();
        LaunchPlacementPlan.TryCreate(new LaunchPlacement(Snap: "left_half", State: "minimized", Focus: true), out _, out var minimized, out _)
            .Should().BeFalse();

        maximized.Should().Be("InvalidArguments");
        minimized.Should().Be("InvalidArguments");
    }
}
