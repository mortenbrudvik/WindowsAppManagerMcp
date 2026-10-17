namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// The strict check of a launch placement (roadmap M3.13), made before anything is launched: known values only, and no
/// combination that contradicts itself.
/// </summary>
public class LaunchPlacementPlanTests
{
    private static (LaunchPlacementPlan? Plan, string? ErrorCode, string? Error) Check(LaunchPlacement placement)
    {
        LaunchPlacementPlan.TryCreate(placement, out var plan, out var errorCode, out var error);
        return (plan, errorCode, error);
    }

    [Fact]
    public void ASnapWithAMonitorAndFocus_IsParsed()
    {
        var (plan, errorCode, _) = Check(new LaunchPlacement(Snap: "Left-Half", Monitor: MonitorSelector.OfIndex(1), Focus: true));

        errorCode.Should().BeNull();
        plan.Should().Be(new LaunchPlacementPlan(SnapPosition.LeftHalf, 1, null, null, true));
        plan!.ChangesGeometry.Should().BeTrue();
    }

    [Fact]
    public void Bounds_AreTheRectangle()
    {
        var (plan, _, _) = Check(new LaunchPlacement(Bounds: new BoundsDto(-1920, 0, 960, 1040), State: "normal"));

        plan!.Bounds.Should().Be(new WindowRect(-1920, 0, 960, 1040));
        plan.State.Should().Be(LaunchWindowState.Normal);
    }

    [Theory]
    [InlineData("maximize", LaunchWindowState.Maximized)]
    [InlineData("minimize", LaunchWindowState.Minimized)]
    [InlineData("restore", LaunchWindowState.Normal)]
    public void TheSetWindowStateNames_AreAcceptedToo(string state, LaunchWindowState expected)
    {
        Check(new LaunchPlacement(State: state)).Plan!.State.Should().Be(expected);
    }

    [Fact]
    public void FocusAlone_ChangesNoGeometry()
    {
        var (plan, _, _) = Check(new LaunchPlacement(Focus: true));

        plan!.ChangesGeometry.Should().BeFalse();
        plan.Focus.Should().BeTrue();
    }

    [Theory]
    [InlineData("middle", null, "InvalidSnapPosition")]
    [InlineData(null, "hidden", "InvalidState")]
    public void AnUnknownValue_IsRefusedWithItsErrorCode_AndTheAllowedValues(string? snap, string? state, string expected)
    {
        var (plan, errorCode, error) = Check(new LaunchPlacement(Snap: snap, State: state));

        plan.Should().BeNull();
        errorCode.Should().Be(expected);
        error.Should().StartWith("placement.").And.Contain("Use one of:");
    }

    public static TheoryData<LaunchPlacement, string, string> Contradictions() => new()
    {
        { new LaunchPlacement(Snap: "left_half", Bounds: new BoundsDto(0, 0, 10, 10)), "InvalidArguments", "snap or bounds" },
        { new LaunchPlacement(Bounds: new BoundsDto(0, 0, 10, 10), Monitor: MonitorSelector.OfIndex(1)), "InvalidArguments", "leave out monitor" },
        { new LaunchPlacement(Bounds: new BoundsDto(0, 0, 10, 10), Monitor: MonitorSelector.Of("left")), "InvalidArguments", "leave out monitor" },
        { new LaunchPlacement(Bounds: new BoundsDto(0, 0, 0, 10)), "InvalidArguments", "at least 1 pixel" },
        { new LaunchPlacement(Snap: "left_half", State: "maximized"), "InvalidArguments", "maximized" },
        { new LaunchPlacement(Bounds: new BoundsDto(0, 0, 10, 10), State: "maximized"), "InvalidArguments", "maximized" },
        { new LaunchPlacement(State: "minimized", Focus: true), "InvalidArguments", "minimized" },
        { new LaunchPlacement(Monitor: MonitorSelector.OfIndex(-1)), "InvalidMonitorIndex", "start at 0" },
        { new LaunchPlacement(), "InvalidArguments", "asks for nothing" },
        { new LaunchPlacement(Focus: false), "InvalidArguments", "asks for nothing" },
    };

    [Theory]
    [MemberData(nameof(Contradictions))]
    public void AContradictionOrAnEmptyPlacement_IsRefused(LaunchPlacement placement, string expectedCode, string expectedText)
    {
        var (plan, errorCode, error) = Check(placement);

        plan.Should().BeNull();
        errorCode.Should().Be(expectedCode);
        error.Should().Contain(expectedText);
    }

    [Fact]
    public void MonitorIndexWithMaximized_IsAllowed()
    {
        Check(new LaunchPlacement(Monitor: MonitorSelector.OfIndex(1), State: "maximized")).Plan.Should().NotBeNull();
    }
}
