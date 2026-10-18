namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Layout placements that would act on the wrong windows or on no rectangle (review findings F2 and F4): a processName that
/// is blank once '.exe' is taken off, or a titleContains of only invisible characters, normalizes to "", which matches every
/// window, so save_layout refuses it (as the window selectors do) and apply_layout skips a file's placement that has
/// nothing else (NoMatchCriterion); a position read from a file must be a rectangle a window can have (LoadedError); and a
/// rectangle less than a pixel on its monitor is not arranged.
/// </summary>
public class LayoutMatcherAndPositionSafetyTests
{
    private static readonly IReadOnlyList<MonitorInfo> OneMonitor = [TestDataFactory.CreateMonitorInfo()];

    private static readonly WindowInfo Notepad = TestDataFactory.CreateWindowInfo(0x1, "notes.txt - Notepad", "notepad", 1);
    private static readonly WindowInfo Code = TestDataFactory.CreateWindowInfo(0x2, "repo - Visual Studio Code", "Code", 2);

    private static WindowPlacement Placement(WindowMatcher matcher, double width = 0.5, double height = 1) =>
        new(matcher, 0, new RelativePosition(0, 0, width, height));

    private static string? NoExePath(int processId) => null;

    #region save_layout's placements (agent-written)

    public static TheoryData<string, LayoutPlacementSpec, string> BlankMatchers() => new()
    {
        { "a processName of only .exe", new LayoutPlacementSpec(".exe", Snap: "left_half"), "processName" },
        { "a processName of .EXE with spaces", new LayoutPlacementSpec("  .EXE ", Snap: "left_half"), "processName" },
        { "an exact processName of only .exe", new LayoutPlacementSpec(".exe", Snap: "left_half", ExactProcessName: true), "processName" },
        { "a titleContains of a zero-width space", new LayoutPlacementSpec(TitleContains: "​", Snap: "left_half"), "titleContains" },
        { "a titleContains of format characters", new LayoutPlacementSpec(TitleContains: "‎‏⁠", Snap: "left_half"), "titleContains" },
        { "a blank processName beside a title", new LayoutPlacementSpec(".exe", "Notepad", Snap: "left_half"), "processName" },
        { "a blank title beside a process", new LayoutPlacementSpec("notepad", "​", Snap: "left_half"), "titleContains" },
    };

    [Theory]
    [MemberData(nameof(BlankMatchers))]
    public void ABlankMatcher_IsRefused_AsItWouldMatchEveryWindow(string why, LayoutPlacementSpec spec, string field)
    {
        LayoutPresetSchema.TryBuildPlacement(spec, 2, OneMonitor, out var placement, out var error).Should().BeFalse(why);

        placement.Should().BeNull();
        error.Should().StartWith($"placements[2].{field}").And.Contain("every window", why);
    }

    [Fact]
    public void AProcessNameWithExe_IsStillTaken()
    {
        LayoutPresetSchema.TryBuildPlacement(new LayoutPlacementSpec("notepad.exe", Snap: "left_half"), 0, OneMonitor, out var placement, out var error)
            .Should().BeTrue(error);
        placement!.Matcher.ProcessName.Should().Be("notepad.exe");
    }

    #endregion

    #region apply_layout of a file's blank matchers

    [Theory]
    [InlineData(".exe", null, LayoutMatchBy.ProcessOnly)]
    [InlineData(".exe", null, LayoutMatchBy.ProcessAndTitle)]
    [InlineData(null, "​", LayoutMatchBy.TitleOnly)]
    [InlineData(null, "​‌", LayoutMatchBy.ProcessAndTitle)]
    [InlineData(".exe", "​", LayoutMatchBy.ProcessAndTitle)]
    public void APlacementWithOnlyBlankCriteria_IsNotApplicable_AndGetsNoWindow(string? processName, string? titleContains, LayoutMatchBy matchBy)
    {
        // Act
        var plan = LayoutPlanner.Plan([Placement(new WindowMatcher(titleContains, processName))], [Notepad, Code], OneMonitor, matchBy, NoExePath)
            .Single();

        // Assert - before, "" matched every window and the placement took the front one
        plan.NotApplicable.Should().NotBeNull();
        plan.Window.Should().BeNull();
    }

    [Fact]
    public void ABlankProcessNameBesideATitle_MatchesByTheTitleAlone()
    {
        var plan = LayoutPlanner.Plan([Placement(new WindowMatcher("Visual Studio Code", ".exe"))], [Notepad, Code], OneMonitor,
            LayoutMatchBy.ProcessAndTitle, NoExePath).Single();

        plan.NotApplicable.Should().BeNull();
        plan.Window.Should().Be(Code);
    }

    #endregion

    #region Positions read from a file

    [Theory]
    [InlineData(double.NaN, 0, 0.5, 1)]
    [InlineData(0, double.PositiveInfinity, 0.5, 1)]
    [InlineData(0, 0, double.NegativeInfinity, 1)]
    [InlineData(0, 0, 0.5, double.NaN)]
    [InlineData(0, 0, -0.5, 1)]
    [InlineData(0, 0, 0.5, 0)]
    [InlineData(101, 0, 0.5, 1)]
    [InlineData(0, -101, 0.5, 1)]
    [InlineData(0, 0, 101, 1)]
    [InlineData(0, 0, 1, 1e20)]
    public void AFilesPositionThatIsNoRectangle_IsAnError(double x, double y, double width, double height)
    {
        var preset = new LayoutPreset("p", null, [Placement(new WindowMatcher(ProcessName: "Code")) with { Position = new RelativePosition(x, y, width, height) }]);

        LayoutPresetSchema.LoadedError(preset).Should().StartWith("placements[0].position: ");
    }

    [Fact]
    public void AFilesRestoreRectangleThatIsNoRectangle_IsAnError()
    {
        var preset = new LayoutPreset("p", null,
        [
            Placement(new WindowMatcher(ProcessName: "Code")),
            Placement(new WindowMatcher(ProcessName: "Code")) with { State = WindowState.Maximized, NormalPosition = new RelativePosition(0, 0, 0, 0) }
        ]);

        LayoutPresetSchema.LoadedError(preset).Should().StartWith("placements[1].normalPosition: ");
    }

    [Theory]
    [InlineData(-0.5, 0, 0.5, 1)]
    [InlineData(0, 1.2, 1, 1)]
    [InlineData(-0.004, -0.007, 1.008, 1.014)]
    [InlineData(-100, 100, 100, 100)]
    public void AFilesPositionOffTheWorkArea_IsNoError(double x, double y, double width, double height)
    {
        var preset = new LayoutPreset("p", null, [Placement(new WindowMatcher(ProcessName: "Code")) with { Position = new RelativePosition(x, y, width, height) }]);

        LayoutPresetSchema.LoadedError(preset).Should().BeNull();
    }

    #endregion

    #region A rectangle less than a pixel

    [Fact]
    public void APositionLessThanAPixelOnItsMonitor_IsNotArranged_AndGetsNoWindow()
    {
        // Arrange - 0.0001 of a 1920 px work area is 0.19 px: valid as a fraction, no window as a rectangle
        var placement = Placement(new WindowMatcher(ProcessName: "Code"), width: 0.0001);

        // Act
        var plan = LayoutPlanner.Plan([placement], [Code], OneMonitor, LayoutMatchBy.ProcessAndTitle, NoExePath).Single();

        // Assert
        plan.PositionError.Should().Contain("less than one pixel").And.Contain("Nothing was changed");
        plan.Window.Should().BeNull();
    }

    [Fact]
    public void ARestoreRectangleLessThanAPixel_IsNotArranged()
    {
        var placement = Placement(new WindowMatcher(ProcessName: "Code")) with
        {
            State = WindowState.Maximized,
            NormalPosition = new RelativePosition(0, 0, 0.5, 0.0001)
        };

        var plan = LayoutPlanner.Plan([placement], [Code], OneMonitor, LayoutMatchBy.ProcessAndTitle, NoExePath).Single();

        plan.PositionError.Should().Contain("normalPosition");
    }

    [Fact]
    public void AMaximizedPlacementsUnusedPosition_IsNotChecked()
    {
        // Maximized without a restore rectangle: the window is maximized on its monitor, and its position is never used
        var placement = Placement(new WindowMatcher(ProcessName: "Code"), width: 0.0001) with { State = WindowState.Maximized };

        var plan = LayoutPlanner.Plan([placement], [Code], OneMonitor, LayoutMatchBy.ProcessAndTitle, NoExePath).Single();

        plan.PositionError.Should().BeNull();
        plan.Window.Should().Be(Code);
    }

    [Fact]
    public void AnOrdinaryPosition_HasNoPositionError()
    {
        var plan = LayoutPlanner.Plan([Placement(new WindowMatcher(ProcessName: "Code"))], [Code], OneMonitor, LayoutMatchBy.ProcessAndTitle, NoExePath)
            .Single();

        plan.PositionError.Should().BeNull();
        plan.Window.Should().Be(Code);
    }

    #endregion
}
