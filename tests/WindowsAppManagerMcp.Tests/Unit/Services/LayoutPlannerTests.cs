namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// LayoutPlanner, apply_layout's planning phase as a pure function (M6 S0f, the first slice of M6.8): it is handed the
/// placements, one window snapshot, the monitors and an exe-path lookup, and touches nothing else, so a dry run can
/// show exactly the plan the real apply follows. The matching rules themselves are covered through ApplyPresetAsync in
/// LayoutServiceMatchingTests and LayoutServicePlanningCharacterizationTests.
/// </summary>
public class LayoutPlannerTests
{
    private static readonly IReadOnlyList<MonitorInfo> OneMonitor = [TestDataFactory.CreateMonitorInfo()];

    private static readonly IReadOnlyList<MonitorInfo> PrimaryAndLeft =
    [
        TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 0, 0, 1920, 1040, 1.5),
        TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, -2560, 0, 2560, 1440, -2560, 0, 2560, 1400, 1.0)
    ];

    private static WindowInfo Window(int handle, string title, string process, int pid, int x = 0, int y = 0, int width = 960,
        int height = 1040) =>
        TestDataFactory.CreateWindowInfo(handle, title, process, pid, x, y, width, height);

    private static WindowPlacement Placement(WindowMatcher matcher, int order, double x = 0, double y = 0, double width = 0.5,
        double height = 1, int monitorIndex = 0) =>
        new(matcher, monitorIndex, new RelativePosition(x, y, width, height), null, false, order);

    private static string? NoExePath(int processId) => throw new InvalidOperationException("No exe path should be read");

    [Fact]
    public void Plan_ReturnsOnePlanPerPlacement_InTheOrderGiven_WithItsLabel()
    {
        // Arrange
        var placements = new[]
        {
            Placement(new WindowMatcher("Mail", "chrome"), 3),
            Placement(new WindowMatcher(null, null), 1)
        };

        // Act
        var plans = LayoutPlanner.Plan(placements, [], OneMonitor, LayoutMatchBy.ProcessAndTitle, NoExePath);

        // Assert
        plans.Select(p => p.Placement).Should().Equal(placements);
        plans.Select(p => p.Label).Should().Equal(
            "placement 3 (process 'chrome', title containing 'Mail')",
            "placement 1 (no matcher)");
    }

    [Fact]
    public void Plan_GivesEachPlacementItsTargetAndWorkArea_AndItsWindow()
    {
        // Arrange
        var mail = Window(0x1, "Inbox - Mail", "chrome", 1);
        var code = Window(0x2, "repo - Visual Studio Code", "Code", 2, 960);

        // Act
        var plans = LayoutPlanner.Plan(
            [Placement(new WindowMatcher(null, "Code"), 0, x: 0.5), Placement(new WindowMatcher("Mail", "chrome"), 1, monitorIndex: 1)],
            [mail, code], PrimaryAndLeft, LayoutMatchBy.ProcessAndTitle, NoExePath);

        // Assert
        plans[0].Window.Should().Be(code);
        plans[0].Target.Should().Be(new WindowRect(960, 0, 960, 1040));
        plans[0].WorkArea.Should().Be(PrimaryAndLeft[0].WorkArea);
        plans[1].Window.Should().Be(mail);
        plans[1].Target.Should().Be(new WindowRect(-2560, 0, 1280, 1400));
        plans[1].WorkArea.Should().Be(PrimaryAndLeft[1].WorkArea);
        plans.Should().OnlyContain(p => p.NotApplicable == null && p.MonitorError == null);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Plan_APlacementOnAMonitorThatDoesNotExist_HasAMonitorError_AndNoTargetOrWindow(int monitorIndex)
    {
        // Act
        var plan = LayoutPlanner.Plan(
            [Placement(new WindowMatcher(null, "chrome"), 0, monitorIndex: monitorIndex)],
            [Window(0x1, "Inbox", "chrome", 1)], PrimaryAndLeft, LayoutMatchBy.ProcessAndTitle, NoExePath).Single();

        // Assert
        plan.MonitorError.Should().StartWith("Did not arrange placement 0 (process 'chrome'): ")
            .And.Contain($"Monitor index {monitorIndex} does not exist");
        plan.Target.Should().BeNull();
        plan.WorkArea.Should().BeNull();
        plan.Window.Should().BeNull();
        plan.NotApplicable.Should().BeNull();
    }

    [Fact]
    public void Plan_APlacementTheMatchByCannotUse_IsNotApplicable_EvenOnAMissingMonitor()
    {
        // Act - the criterion check comes before the monitor check
        var plan = LayoutPlanner.Plan(
            [Placement(new WindowMatcher(null, "chrome"), 0, monitorIndex: 5)],
            [Window(0x1, "Inbox", "chrome", 1)], OneMonitor, LayoutMatchBy.TitleOnly, NoExePath).Single();

        // Assert
        plan.NotApplicable.Should().StartWith("Skipped placement 0 (process 'chrome'): matchBy title_only needs titleContains");
        plan.MonitorError.Should().BeNull();
        plan.Target.Should().BeNull();
        plan.Window.Should().BeNull();
    }

    [Fact]
    public void Plan_ReadsEachProcessExePathOnce_AndOnlyWhenAPlacementRecordedOne()
    {
        // Arrange
        var lookups = new List<int>();
        string? ExePath(int processId)
        {
            lookups.Add(processId);
            return processId == 7 ? @"C:\app.exe" : null;
        }

        // Act
        LayoutPlanner.Plan(
            [
                Placement(new WindowMatcher(null, "app") { ExePath = @"C:\app.exe" }, 0),
                Placement(new WindowMatcher(null, "app") { ExePath = @"C:\app.exe" }, 1, x: 0.5),
                Placement(new WindowMatcher(null, "other"), 2)
            ],
            [Window(0x1, "A", "app", 7), Window(0x2, "B", "app", 7), Window(0x3, "C", "app", 8), Window(0x4, "D", "other", 9)],
            OneMonitor, LayoutMatchBy.ProcessAndTitle, ExePath);

        // Assert
        lookups.Should().Equal(7, 8);
    }

    [Fact]
    public void Plan_AWindowIsGivenToAtMostOnePlacement()
    {
        // Act
        var plans = LayoutPlanner.Plan(
            [Placement(new WindowMatcher(null, "app"), 0), Placement(new WindowMatcher(null, "app"), 1)],
            [Window(0x1, "Only", "app", 1)], OneMonitor, LayoutMatchBy.ProcessAndTitle, NoExePath);

        // Assert
        plans[0].Window.Should().NotBeNull();
        plans[1].Window.Should().BeNull();
        plans[1].Target.Should().NotBeNull("a placement that found no window still has its rectangle, for launchMissing");
    }

    [Theory]
    // Each component is truncated toward zero on its own: neighbouring thirds can be a pixel apart
    [InlineData(0, 0, 1920, 1040, 0.5, 0, 0.5, 1, 960, 0, 960, 1040)]
    [InlineData(0, 0, 2560, 1400, 1 / 3.0, 0, 1 / 3.0, 1, 853, 0, 853, 1400)]
    [InlineData(-2560, 0, 2560, 1400, 1 / 3.0, 0, 1 / 3.0, 1, -1706, 0, 853, 1400)]
    [InlineData(-2560, 0, 2560, 1400, 0, 0, 1 / 3.0, 1, -2560, 0, 853, 1400)]
    [InlineData(1920, -1080, 1920, 1040, 0.25, 0.5, 0.5, 0.5, 2400, -560, 960, 520)]
    [InlineData(0, 0, 1921, 1041, 0.5, 0.5, 0.5, 0.5, 960, 520, 960, 520)]
    public void TargetOf_IsTheFractionsOfTheWorkArea_EachComponentTruncated(
        int areaX, int areaY, int areaWidth, int areaHeight, double x, double y, double width, double height,
        int expectedX, int expectedY, int expectedWidth, int expectedHeight)
    {
        LayoutPlanner.TargetOf(new RelativePosition(x, y, width, height), new MonitorRect(areaX, areaY, areaWidth, areaHeight))
            .Should().Be(new WindowRect(expectedX, expectedY, expectedWidth, expectedHeight));
    }
}
