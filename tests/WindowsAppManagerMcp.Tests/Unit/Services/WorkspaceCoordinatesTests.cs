namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// <see cref="WorkspaceCoordinates"/> (roadmap M6.8): GetWindowPlacement and SetWindowPlacement give a top-level window's
/// restore rectangle in workspace coordinates, which start at the top-left of the work area of the monitor the rectangle is
/// on, so a taskbar on the left or the top shifts them against screen coordinates.
/// </summary>
public class WorkspaceCoordinatesTests
{
    private static MonitorInfo Monitor(int index, int x, int y, int width, int height, int workX, int workY, int workWidth, int workHeight) =>
        new(index, $@"\\.\DISPLAY{index + 1}", index == 0, new MonitorRect(x, y, width, height),
            new MonitorRect(workX, workY, workWidth, workHeight), 1.0);

    /// <summary>Taskbar at the bottom: the work area starts where the monitor does, so nothing shifts.</summary>
    private static readonly MonitorInfo BottomTaskbar = Monitor(0, 0, 0, 1920, 1080, 0, 0, 1920, 1040);

    /// <summary>Taskbar on the left, 48 px wide.</summary>
    private static readonly MonitorInfo LeftTaskbar = Monitor(0, 0, 0, 1920, 1080, 48, 0, 1872, 1080);

    /// <summary>Taskbar at the top, 40 px tall.</summary>
    private static readonly MonitorInfo TopTaskbar = Monitor(0, 0, 0, 1920, 1080, 0, 40, 1920, 1040);

    public static TheoryData<string, MonitorInfo[], WindowRect, WindowRect> Cases() => new()
    {
        { "taskbar at the bottom", [BottomTaskbar], new WindowRect(100, 100, 800, 600), new WindowRect(100, 100, 800, 600) },
        { "taskbar on the left", [LeftTaskbar], new WindowRect(100, 100, 800, 600), new WindowRect(148, 100, 800, 600) },
        { "taskbar at the top", [TopTaskbar], new WindowRect(100, 100, 800, 600), new WindowRect(100, 140, 800, 600) },
        {
            // The second monitor's own taskbar is on its left: its offset applies, not the primary's
            "second monitor with its taskbar on the left",
            [BottomTaskbar, Monitor(1, 1920, 0, 2560, 1440, 1968, 0, 2512, 1440)],
            new WindowRect(2000, 100, 800, 600), new WindowRect(2048, 100, 800, 600)
        },
        {
            "monitor left of the primary, taskbar at its top",
            [LeftTaskbar, Monitor(1, -1920, 0, 1920, 1080, -1920, 40, 1920, 1040)],
            new WindowRect(-1800, 100, 800, 600), new WindowRect(-1800, 140, 800, 600)
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ToScreen_AddsTheOffsetOfTheWorkAreaOfTheMonitorTheRectangleIsOn(
        string layout, MonitorInfo[] monitors, WindowRect workspace, WindowRect screen)
    {
        WorkspaceCoordinates.ToScreen(workspace, monitors).Should().Be(screen, layout);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ToWorkspace_IsTheInverse(string layout, MonitorInfo[] monitors, WindowRect workspace, WindowRect screen)
    {
        WorkspaceCoordinates.ToWorkspace(screen, monitors).Should().Be(workspace, layout);
    }

    [Fact]
    public void ARectangleOnNoMonitor_UsesTheNearestOne()
    {
        // A restore rectangle left behind on a monitor that was unplugged
        var monitors = new[] { LeftTaskbar };

        WorkspaceCoordinates.ToScreen(new WindowRect(5000, 100, 800, 600), monitors).Should().Be(new WindowRect(5048, 100, 800, 600));
    }

    [Fact]
    public void WithoutMonitors_NothingShifts()
    {
        WorkspaceCoordinates.ToScreen(new WindowRect(1, 2, 3, 4), []).Should().Be(new WindowRect(1, 2, 3, 4));
    }

    [Fact]
    public void ARectangleAcrossTwoMonitors_UsesTheOneWithMoreOfIt()
    {
        var monitors = new[] { BottomTaskbar, Monitor(1, 1920, 0, 1920, 1080, 1920, 40, 1920, 1040) };

        // 200 px on the primary, 600 px on the second
        WorkspaceCoordinates.ToScreen(new WindowRect(1720, 100, 800, 600), monitors).Should().Be(new WindowRect(1720, 140, 800, 600));
    }
}
