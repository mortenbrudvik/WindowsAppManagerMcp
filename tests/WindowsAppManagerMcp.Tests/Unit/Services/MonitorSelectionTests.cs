namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// How a `monitor` argument names a monitor (roadmap M6.7, owner decision D13): <see cref="MonitorSelection.Resolve"/> over
/// fake topologies, and <see cref="MonitorService.ResolveMonitor"/> with the foreground window's monitor for 'current'.
/// Nothing reads a real display.
/// </summary>
public class MonitorSelectionTests
{
    private const string LeftPath = @"\\?\DISPLAY#DEL40F2#5&2a8e5b0f&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static MonitorInfo Monitor(int index, int x, int y = 0, int width = 1920, int height = 1080, bool primary = false,
        string? id = null) =>
        TestDataFactory.CreateMonitorInfo(index, $@"\\.\DISPLAY{index + 1}", primary, x, y, width, height, x, y, width, height - 40)
            with { Id = id };

    /// <summary>A monitor left of the primary, the primary, and one right of it, enumerated primary first.</summary>
    private static readonly IReadOnlyList<MonitorInfo> ThreeInARow =
    [
        Monitor(0, 0, primary: true),
        Monitor(1, -1920, id: MonitorIdentity.IdOf(LeftPath)),
        Monitor(2, 1920)
    ];

    private static MonitorResolution Resolve(IReadOnlyList<MonitorInfo> monitors, string json, MonitorInfo? current = null) =>
        MonitorSelection.Resolve(monitors, System.Text.Json.JsonSerializer.Deserialize<MonitorSelector>(json,
            WindowsAppManagerMcp.Infrastructure.ToolJson.SerializerOptions)!, () => current);

    #region Indexes

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("\"1\"", 1)]
    public void AnIndex_IsThatMonitor(string json, int index)
    {
        // Act
        var resolution = Resolve(ThreeInARow, json);

        // Assert
        resolution.Succeeded.Should().BeTrue();
        resolution.Index.Should().Be(index);
        resolution.Monitor.Should().Be(ThreeInARow[index]);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("-2")]
    public void AnIndexThatIsNoMonitor_IsPassedOn_ForTheCallersOwnRangeCheck(string json)
    {
        // Act
        var resolution = Resolve(ThreeInARow, json);

        // Assert - InvalidMonitorIndex, with the valid range, stays each tool's own error
        resolution.Succeeded.Should().BeTrue();
        resolution.Index.Should().Be(int.Parse(json));
        resolution.Monitor.Should().BeNull();
    }

    [Theory]
    [InlineData("\"all\"")]
    [InlineData("\"ALL\"")]
    [InlineData("-1")]
    public void All_IsMinusOne_TheWholeVirtualScreen(string json)
    {
        // Act
        var resolution = Resolve(ThreeInARow, json);

        // Assert
        resolution.Succeeded.Should().BeTrue();
        resolution.Index.Should().Be(MonitorSelection.AllMonitors);
        resolution.Monitor.Should().BeNull();
    }

    #endregion

    #region Names

    [Theory]
    [InlineData("\"primary\"", 0)]
    [InlineData("\"Primary\"", 0)]
    [InlineData("\"left\"", 1)]
    [InlineData("\"leftmost\"", 1)]
    [InlineData("\"LEFT\"", 1)]
    [InlineData("\"right\"", 2)]
    [InlineData("\"rightmost\"", 2)]
    public void AKeyword_NamesItsMonitor_InAnyCase(string json, int index)
    {
        // Act
        var resolution = Resolve(ThreeInARow, json);

        // Assert
        resolution.Succeeded.Should().BeTrue();
        resolution.Index.Should().Be(index);
        resolution.Monitor!.Index.Should().Be(index);
    }

    [Theory]
    [InlineData("\"left\"")]
    [InlineData("\"right\"")]
    public void LeftAndRight_WithOneMonitor_AreMonitorNotFound(string json)
    {
        // Act
        var resolution = Resolve([Monitor(0, 0, primary: true)], json);

        // Assert
        resolution.Succeeded.Should().BeFalse();
        resolution.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        resolution.Error.Should().Contain("one monitor");
    }

    [Fact]
    public void Left_WhenTwoMonitorsAreStacked_IsAmbiguousMonitor_WithTheCandidates()
    {
        // Arrange - a laptop under a wider external monitor: their centres line up, neither is left of the other
        IReadOnlyList<MonitorInfo> stacked = [Monitor(0, 0, primary: true), Monitor(1, -320, -1440, 2560, 1440)];

        // Act
        var resolution = Resolve(stacked, "\"left\"");

        // Assert
        resolution.Succeeded.Should().BeFalse();
        resolution.ErrorCode.Should().Be(nameof(WindowErrorCode.AmbiguousMonitor));
        resolution.Error.Should().Contain(@"0 (\\.\DISPLAY1").And.Contain(@"1 (\\.\DISPLAY2");
    }

    [Fact]
    public void Right_WhenOnlyAnotherMonitorIsStacked_IsStillTheRightmost()
    {
        // Arrange - a laptop under the left monitor, and a monitor right of both
        IReadOnlyList<MonitorInfo> monitors = [Monitor(0, 0, primary: true), Monitor(1, 0, 1080), Monitor(2, 1920)];

        // Act
        var right = Resolve(monitors, "\"right\"");
        var left = Resolve(monitors, "\"left\"");

        // Assert
        right.Index.Should().Be(2);
        left.ErrorCode.Should().Be(nameof(WindowErrorCode.AmbiguousMonitor));
    }

    [Fact]
    public void Right_ForMonitorsThatOverlapByLessThanHalf_IsTheOneFurtherRight()
    {
        // Arrange - the second monitor is lower and 560 px into the first's columns: still to its right
        IReadOnlyList<MonitorInfo> monitors = [Monitor(0, 0, width: 2560, height: 1440, primary: true), Monitor(1, 2000, 1440, 2560, 1440)];

        // Act
        var resolution = Resolve(monitors, "\"right\"");

        // Assert
        resolution.Index.Should().Be(1);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("Current")]
    public void Current_IsTheMonitorOfTheForegroundWindow(string word)
    {
        // Act
        var resolution = Resolve(ThreeInARow, $"\"{word}\"", current: ThreeInARow[2]);

        // Assert
        resolution.Succeeded.Should().BeTrue();
        resolution.Index.Should().Be(2);
    }

    [Fact]
    public void Current_WithNoForegroundWindow_IsMonitorNotFound_NeverThePrimary()
    {
        // Act
        var resolution = Resolve(ThreeInARow, "\"current\"", current: null);

        // Assert
        resolution.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        resolution.Error.Should().Contain("foreground");
    }

    [Theory]
    [InlineData(LeftPath, false)]
    [InlineData(LeftPath, true)]
    public void AnId_IsTheMonitorWithThatId_InAnyCase(string path, bool upper)
    {
        // Arrange
        var id = MonitorIdentity.IdOf(path)!;

        // Act
        var resolution = Resolve(ThreeInARow, $"\"{(upper ? id.ToUpperInvariant() : id)}\"");

        // Assert
        resolution.Index.Should().Be(1);
    }

    [Fact]
    public void AnIdNoMonitorHas_IsMonitorNotFound_WithTheIdsThereAre()
    {
        // Act
        var resolution = Resolve(ThreeInARow, "\"m-00000000\"");

        // Assert
        resolution.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        resolution.Error.Should().Contain(MonitorIdentity.IdOf(LeftPath)).And.Contain("get_monitors");
    }

    [Theory]
    [InlineData(@"\\\\.\\DISPLAY3")]
    [InlineData("display3")]
    public void ADeviceName_IsThatMonitor(string escaped)
    {
        // Act
        var resolution = Resolve(ThreeInARow, $"\"{escaped}\"");

        // Assert
        resolution.Index.Should().Be(2);
    }

    [Fact]
    public void AnUnknownName_IsMonitorNotFound_WithWhatMonitorTakes()
    {
        // Act
        var resolution = Resolve(ThreeInARow, "\"the dell\"");

        // Assert
        resolution.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        resolution.Error.Should().Contain("'primary'").And.Contain("'current'").And.Contain("get_monitors");
    }

    [Fact]
    public void AName_WithNoMonitors_IsMonitorNotFound()
    {
        // Act
        var resolution = Resolve([], "\"primary\"");

        // Assert
        resolution.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
    }

    #endregion

    #region Relation to the primary

    [Fact]
    public void Relation_SaysWhereEachMonitorIsFromThePrimary()
    {
        // Arrange
        var primary = Monitor(0, 0, primary: true);

        // Act & Assert
        MonitorSelection.RelationToPrimary(primary, primary).Should().BeNull();
        MonitorSelection.RelationToPrimary(Monitor(1, -1920), primary).Should().Be("left");
        MonitorSelection.RelationToPrimary(Monitor(1, 1920, 300), primary).Should().Be("right");
        MonitorSelection.RelationToPrimary(Monitor(1, -320, -1440, 2560, 1440), primary).Should().Be("above");
        MonitorSelection.RelationToPrimary(Monitor(1, 0, 1080), primary).Should().Be("below");
    }

    [Fact]
    public void Relation_OfAMonitorMostlyAboveThePrimary_ButShiftedLeft_IsAbove()
    {
        // Arrange - its centre is left of the primary's left edge, but it shares 1260 of the primary's 1920 columns
        var primary = Monitor(0, 0, primary: true);

        // Act & Assert
        MonitorSelection.RelationToPrimary(Monitor(1, -1300, -1440, 2560, 1440), primary).Should().Be("above");
        MonitorSelection.RelationToPrimary(Monitor(1, -1300, 1080, 2560, 1440), primary).Should().Be("below");
        MonitorSelection.RelationToPrimary(Monitor(1, -1700, -1440, 2560, 1440), primary).Should().Be("left", "860 shared columns are less than half");
    }

    #endregion

    #region MonitorService

    [Fact]
    public void ResolveMonitor_Current_UsesTheForegroundWindowsMonitor()
    {
        // Arrange - the foreground window is on the second monitor
        var wrapper = new FakeMonitors([(100, ThreeInARow[0]), (101, ThreeInARow[1])]) { WindowMonitor = 101 };
        var sut = new MonitorService(wrapper, foregroundWindow: () => 0x4242);

        // Act
        var resolution = sut.ResolveMonitor(MonitorSelector.Of("current"));

        // Assert
        resolution.Index.Should().Be(1);
        wrapper.AskedForWindow.Should().Be((nint)0x4242);
    }

    [Fact]
    public void ResolveMonitor_Current_WithoutAForegroundWindow_IsMonitorNotFound()
    {
        // Arrange
        var sut = new MonitorService(new FakeMonitors([(100, ThreeInARow[0])]), foregroundWindow: () => 0);

        // Act
        var resolution = sut.ResolveMonitor(MonitorSelector.Of("current"));

        // Assert
        resolution.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
    }

    [Fact]
    public void ResolveMonitor_Current_AfterTheDisplaysChanged_ResolvesOnOneFreshSnapshot()
    {
        // Arrange - the snapshot lists the old topology; then docking adds a monitor that is enumerated first, and the
        // foreground window is on it
        var wrapper = new FakeMonitors([(100, ThreeInARow[0])]) { WindowMonitor = 200 };
        var sut = new MonitorService(wrapper, foregroundWindow: () => 0x4242);
        sut.GetAllMonitors().Should().HaveCount(1);
        var docked = Monitor(0, -1920);
        wrapper.Monitors = [(200, docked), (100, ThreeInARow[0] with { Index = 1 })];

        // Act
        var resolution = sut.ResolveMonitor(MonitorSelector.Of("current"));

        // Assert - the index and the monitor come from the same snapshot, the one the next call lists
        resolution.Index.Should().Be(0);
        resolution.Monitor.Should().Be(docked);
        sut.GetAllMonitors()[resolution.Index!.Value].Should().Be(resolution.Monitor);
    }

    [Fact]
    public void ResolveMonitor_ResolvesNamesOnTheMonitorsItLists()
    {
        // Arrange
        var sut = new MonitorService(new FakeMonitors([(100, ThreeInARow[0]), (101, ThreeInARow[1]), (102, ThreeInARow[2])]));

        // Act & Assert
        sut.ResolveMonitor(MonitorSelector.Of("left")).Index.Should().Be(1);
        sut.ResolveMonitor(MonitorSelector.OfIndex(2)).Monitor!.DeviceName.Should().Be(@"\\.\DISPLAY3");
    }

    private sealed class FakeMonitors(IReadOnlyList<(nint Handle, MonitorInfo Info)> monitors) : INativeMonitorWrapper
    {
        public nint WindowMonitor { get; init; } = 100;

        public nint? AskedForWindow { get; private set; }

        /// <summary>The monitors the next enumeration lists.</summary>
        public IReadOnlyList<(nint Handle, MonitorInfo Info)> Monitors { get; set; } = monitors;

        public IReadOnlyList<(nint Handle, MonitorInfo Info)> EnumerateMonitors() => Monitors.ToList();

        public nint MonitorFromWindow(nint windowHandle)
        {
            AskedForWindow = windowHandle;
            return WindowMonitor;
        }

        public nint MonitorFromPoint(int x, int y) => 100;

        public IReadOnlyList<DisplayTarget>? GetDisplayTargets() => null;
    }

    #endregion
}
