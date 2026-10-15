using System.ComponentModel;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Characterization tests for apply_layout's planning phase (which window each placement gets, and the rectangle it
/// is given), written before that phase moved out of LayoutService into LayoutPlanner (M6 S0f, the first slice of
/// M6.8). They pin today's behaviour through the public ApplyPresetAsync, including its quirks: each component of the
/// target is truncated toward zero on its own, so neighbouring tiles can be a pixel apart, and on a monitor left of
/// the primary the truncation rounds up. M6.8 may change the rectangle math on purpose; these tests then change with it.
/// </summary>
public class LayoutServicePlanningCharacterizationTests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), $"LayoutServicePlanningTests_{Guid.NewGuid():N}");
    private readonly Mock<IWindowService> _windows = TestDataFactory.CreateMockWindowService([]);
    private readonly Mock<IProcessService> _processes = TestDataFactory.CreateMockProcessService();
    private readonly Mock<IForegroundService> _foreground = TestDataFactory.CreateMockForegroundService();
    private Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();

    // Monitor 0 is the primary (1920x1040 work area at 0,0); monitor 1 is left of it at a different scale
    private static readonly IReadOnlyList<MonitorInfo> PrimaryAndLeft =
    [
        TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 1920, 1080, 0, 0, 1920, 1040, 1.5),
        TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, -2560, 0, 2560, 1440, -2560, 0, 2560, 1400, 1.0)
    ];

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
            Directory.Delete(_layoutsPath, recursive: true);
    }

    private LayoutService CreateSut() =>
        new(_windows.Object, _monitors.Object, _processes.Object, _foreground.Object, _layoutsPath,
            TestDataFactory.CreateInstantLaunchPlacement(_windows.Object, _foreground.Object));

    private static WindowInfo Window(int handle, string title, string process, int pid, int x, int y, int width, int height,
        int monitorIndex = 0) =>
        TestDataFactory.CreateWindowInfo(handle, title, process, pid, x, y, width, height, monitorIndex: monitorIndex);

    private void OpenWindows(params WindowInfo[] windows)
    {
        _windows.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns(windows);
        foreach (var window in windows)
            _windows.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
    }

    private static WindowPlacement Placement(WindowMatcher matcher, int order, double x, double y, double width, double height,
        int monitorIndex = 0) =>
        new(matcher, monitorIndex, new RelativePosition(x, y, width, height), null, false, order);

    private async Task<LayoutService> SavedAsync(params WindowPlacement[] placements)
    {
        var sut = CreateSut();
        (await sut.SavePresetAsync(new LayoutPreset("planned", null, [.. placements]))).Success.Should().BeTrue();
        return sut;
    }

    private void VerifyPlaced(WindowInfo window, int x, int y, int width, int height) =>
        _windows.Verify(w => w.SetWindowBounds(window.Handle, x, y, width, height, It.IsAny<bool>()), Times.Once);

    [Fact]
    public async Task Target_OnAMonitorLeftOfThePrimary_TruncatesEachComponentTowardZeroOnItsOwn()
    {
        // Arrange - the left and centre thirds of a 2560 px work area at x=-2560
        _monitors = TestDataFactory.CreateMockMonitorService(PrimaryAndLeft);
        var left = Window(0x11, "Left", "left", 11, -2560, 0, 800, 600, monitorIndex: 1);
        var centre = Window(0x12, "Centre", "centre", 12, -1700, 0, 800, 600, monitorIndex: 1);
        OpenWindows(left, centre);
        var sut = await SavedAsync(
            Placement(new WindowMatcher(null, "left"), 0, 0, 0, 1 / 3.0, 1, monitorIndex: 1),
            Placement(new WindowMatcher(null, "centre"), 1, 1 / 3.0, 0, 1 / 3.0, 1, monitorIndex: 1));

        // Act
        var result = await sut.ApplyPresetAsync("planned");

        // Assert - x: (int)(-2560 + 853.33) = -1706 (toward zero), width: (int)853.33 = 853, so the left third ends at
        // -1707 and the centre third starts at -1706: a one-pixel gap, today's behaviour
        result.Success.Should().BeTrue();
        VerifyPlaced(left, -2560, 0, 853, 1400);
        VerifyPlaced(centre, -1706, 0, 853, 1400);
    }

    [Fact]
    public async Task Target_IsInTheWorkAreasPhysicalPixels_WhateverTheMonitorsScale()
    {
        // Arrange - monitor 1 of the dual setup is 2560x1400 at 1920,0 at 125 %
        _monitors = TestDataFactory.CreateMockMonitorService(TestDataFactory.CreateDualMonitorSetup());
        var window = Window(0x21, "Editor", "editor", 21, 0, 0, 800, 600);
        OpenWindows(window);
        var sut = await SavedAsync(Placement(new WindowMatcher(null, "editor"), 0, 0.5, 0.25, 0.5, 0.5, monitorIndex: 1));

        // Act
        await sut.ApplyPresetAsync("planned");

        // Assert - no scaling by the monitor's scale factor
        VerifyPlaced(window, 1920 + 1280, 350, 1280, 700);
    }

    [Fact]
    public async Task Target_OddSizes_AreTruncatedNotRounded()
    {
        // Arrange - 1/3 of 1040 is 346.67 and 2/3 of 1920 is 1280 exactly
        var window = Window(0x31, "Editor", "editor", 31, 0, 0, 800, 600);
        OpenWindows(window);
        var sut = await SavedAsync(Placement(new WindowMatcher(null, "editor"), 0, 1 / 3.0, 2 / 3.0, 2 / 3.0, 1 / 3.0));

        // Act
        await sut.ApplyPresetAsync("planned");

        // Assert - x (int)640.0 = 640, y (int)693.33 = 693, width (int)1280.0 = 1280, height (int)346.67 = 346
        VerifyPlaced(window, 640, 693, 1280, 346);
    }

    [Fact]
    public async Task ExePath_IsReadOncePerProcess_AndOnlyForPlacementsThatRecordedOne()
    {
        // Arrange - two windows of one process, two placements recording an exe path, one that does not
        var first = Window(0x41, "One", "app", 41, 0, 0, 960, 1040);
        var second = Window(0x42, "Two", "app", 41, 960, 0, 960, 1040);
        var other = Window(0x43, "Other", "other", 43, 0, 0, 960, 1040);
        OpenWindows(first, second, other);
        _processes.Setup(p => p.GetProcessPath(41)).Returns(@"C:\app.exe");
        var sut = await SavedAsync(
            Placement(new WindowMatcher(null, "app") { ExePath = @"C:\app.exe" }, 0, 0, 0, 0.5, 1),
            Placement(new WindowMatcher(null, "app") { ExePath = @"C:\app.exe" }, 1, 0.5, 0, 0.5, 1),
            Placement(new WindowMatcher(null, "other"), 2, 0, 0, 0.5, 1));

        // Act
        var result = await sut.ApplyPresetAsync("planned");

        // Assert
        result.Success.Should().BeTrue();
        _processes.Verify(p => p.GetProcessPath(41), Times.Once);
        _processes.Verify(p => p.GetProcessPath(43), Times.Never);
    }

    [Fact]
    public async Task ExePath_ThatCannotBeRead_NeitherHelpsNorHurts_AndDoesNotFailTheApply()
    {
        // Arrange - the window nearer the placement's rectangle wins on distance alone
        var near = Window(0x51, "Near", "app", 51, 0, 0, 960, 1040);
        var far = Window(0x52, "Far", "app", 52, 960, 0, 960, 1040);
        OpenWindows(far, near);
        _processes.Setup(p => p.GetProcessPath(51)).Throws(new Win32Exception(5, "Access is denied"));
        _processes.Setup(p => p.GetProcessPath(52)).Throws(new InvalidOperationException("Process has exited"));
        var sut = await SavedAsync(Placement(new WindowMatcher(null, "app") { ExePath = @"C:\app.exe" }, 0, 0, 0, 0.5, 1));

        // Act
        var result = await sut.ApplyPresetAsync("planned");

        // Assert
        result.Placements!.Single().Window!.Handle.Should().Be(near.Handle.ToInt64());
    }

    [Fact]
    public async Task EqualScores_GoToTheLowerOrderPlacement_AndTheWindowHigherInTheZOrder()
    {
        // Arrange - two identical windows and two identical placements, saved out of order
        var top = Window(0x61, "Same", "app", 61, 0, 0, 960, 1040);
        var below = Window(0x62, "Same", "app", 61, 0, 0, 960, 1040);
        OpenWindows(top, below);
        var sut = await SavedAsync(
            Placement(new WindowMatcher(null, "app"), 5, 0, 0, 0.5, 1),
            Placement(new WindowMatcher(null, "app"), 2, 0, 0, 0.5, 1));

        // Act
        var result = await sut.ApplyPresetAsync("planned");

        // Assert - placements are reported in order; order 2 takes the top window
        result.Placements!.Select(p => (p.Order, p.Window!.Handle)).Should().Equal(
            (2, top.Handle.ToInt64()),
            (5, below.Handle.ToInt64()));
    }

    [Fact]
    public async Task RecordedOrdinal_BreaksATie_BetweenWindowsOfOneProcess_CountedInZOrder()
    {
        // Arrange - another process's window between them does not count toward the ordinal
        var first = Window(0x71, "Same", "app", 71, 0, 0, 960, 1040);
        var between = Window(0x72, "Other", "other", 72, 0, 0, 960, 1040);
        var second = Window(0x73, "Same", "app", 71, 0, 0, 960, 1040);
        OpenWindows(first, between, second);
        var sut = await SavedAsync(Placement(new WindowMatcher(null, "app") { Ordinal = 1 }, 0, 0, 0, 0.5, 1));

        // Act
        var result = await sut.ApplyPresetAsync("planned");

        // Assert
        result.Placements!.Single().Window!.Handle.Should().Be(second.Handle.ToInt64());
    }

    [Fact]
    public async Task APlacementThatIsNotApplicable_DoesNotTakeAWindow_FromALaterPlacement()
    {
        // Arrange - placement 0 has no title text, so title_only cannot match it; placement 1 can
        var window = Window(0x81, "Report - Editor", "editor", 81, 0, 0, 960, 1040);
        OpenWindows(window);
        var sut = await SavedAsync(
            Placement(new WindowMatcher(null, "editor"), 0, 0, 0, 0.5, 1),
            Placement(new WindowMatcher("Report", null), 1, 0.5, 0, 0.5, 1));

        // Act
        var result = await sut.ApplyPresetAsync("planned", LayoutMatchBy.TitleOnly);

        // Assert
        result.Placements!.Select(p => p.Status).Should().Equal(LayoutPlacementStatus.NotApplicable, LayoutPlacementStatus.Applied);
        result.Placements![0].Error.Should().StartWith("Skipped placement 0 (process 'editor'): matchBy title_only needs titleContains");
        VerifyPlaced(window, 960, 0, 960, 1040);
    }

    [Fact]
    public async Task Distance_RanksWindowsTheMatcherCannotTellApart()
    {
        // Arrange - a placement on the right half takes the window already nearest to it, even if it is lower in the z-order
        var leftWindow = Window(0x91, "A", "app", 91, 0, 0, 960, 1040);
        var rightWindow = Window(0x92, "B", "app", 92, 950, 10, 960, 1030);
        OpenWindows(leftWindow, rightWindow);
        var sut = await SavedAsync(Placement(new WindowMatcher(null, "app"), 0, 0.5, 0, 0.5, 1));

        // Act
        var result = await sut.ApplyPresetAsync("planned");

        // Assert
        result.Placements!.Single().Window!.Handle.Should().Be(rightWindow.Handle.ToInt64());
        VerifyPlaced(rightWindow, 960, 0, 960, 1040);
    }
}
