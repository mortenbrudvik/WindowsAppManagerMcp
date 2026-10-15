namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for how MonitorService adds each monitor's identity from the display configuration (roadmap M6.7, S0d) and
/// caches it per topology rather than per snapshot. The native reads are faked, so no display is read.
/// </summary>
public class MonitorServiceIdentityTests
{
    private const string DellPath = @"\\?\DISPLAY#DEL40F2#5&2a8e5b0f&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string LgPath = @"\\?\DISPLAY#GSM5B7F#5&2a8e5b0f&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static (nint Handle, MonitorInfo Info) Monitor(nint handle, int index, string deviceName, int x, bool isPrimary = false) =>
        (handle, TestDataFactory.CreateMonitorInfo(index: index, deviceName: deviceName, isPrimary: isPrimary, boundsX: x, workAreaX: x));

    /// <summary>The Dell on the left as DISPLAY1 and the LG on the right as DISPLAY2.</summary>
    private static readonly IReadOnlyList<(nint Handle, MonitorInfo Info)> Desk =
    [
        Monitor(100, 0, @"\\.\DISPLAY1", 0, isPrimary: true),
        Monitor(101, 1, @"\\.\DISPLAY2", 1920)
    ];

    private static readonly IReadOnlyList<DisplayTarget> DeskTargets =
    [
        new(@"\\.\DISPLAY1", "DELL U2720Q", DellPath, DisplayOutputTechnology.DisplayPortExternal),
        new(@"\\.\DISPLAY2", "LG ULTRAFINE", LgPath, DisplayOutputTechnology.Hdmi)
    ];

    #region Identity On The Monitors

    [Fact]
    public void GetAllMonitors_AddsEachMonitorsIdentity()
    {
        // Arrange
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = _ => DeskTargets };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());

        // Act
        var monitors = sut.GetAllMonitors();

        // Assert
        monitors.Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), MonitorIdentity.IdOf(LgPath));
        monitors.Select(m => m.FriendlyName).Should().Equal("DELL U2720Q", "LG ULTRAFINE");
        monitors.Select(m => m.IsInternal).Should().Equal(false, false);
    }

    [Fact]
    public void EveryLookup_ReturnsTheIdentifiedMonitor()
    {
        // Arrange
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = _ => DeskTargets, WindowMonitor = 101, PointMonitor = 101 };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());
        var lgId = MonitorIdentity.IdOf(LgPath);

        // Act & Assert
        sut.GetPrimaryMonitor().Id.Should().Be(MonitorIdentity.IdOf(DellPath));
        sut.GetMonitorForWindow(42)!.Id.Should().Be(lgId);
        sut.GetMonitorAt(2000, 500)!.Id.Should().Be(lgId);
    }

    [Fact]
    public void WhenDockingRenumbersTheMonitors_TheIdsFollowThePanels()
    {
        // Arrange - after docking, the LG is enumerated first as DISPLAY1 and the Dell second as DISPLAY2
        var docked = new List<(nint Handle, MonitorInfo Info)>
        {
            Monitor(200, 0, @"\\.\DISPLAY1", 1920),
            Monitor(201, 1, @"\\.\DISPLAY2", 0, isPrimary: true)
        };
        var dockedTargets = new List<DisplayTarget>
        {
            new(@"\\.\DISPLAY1", "LG ULTRAFINE", LgPath, DisplayOutputTechnology.Hdmi),
            new(@"\\.\DISPLAY2", "DELL U2720Q", DellPath, DisplayOutputTechnology.DisplayPortExternal)
        };
        var wrapper = new FakeMonitorWrapper
        {
            Topology = call => call == 1 ? Desk : docked,
            Targets = call => call == 1 ? DeskTargets : dockedTargets
        };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);
        var before = sut.GetAllMonitors();

        // Act
        clock.Advance(MonitorService.SnapshotLifetime);
        var after = sut.GetAllMonitors();

        // Assert - the indexes and device names swapped; the ids stay with the Dell and the LG
        var dellBefore = before.Single(m => m.FriendlyName == "DELL U2720Q");
        var dellAfter = after.Single(m => m.FriendlyName == "DELL U2720Q");
        dellBefore.Index.Should().Be(0);
        dellAfter.Index.Should().Be(1);
        dellAfter.Id.Should().Be(dellBefore.Id);
        after.Single(m => m.FriendlyName == "LG ULTRAFINE").Id.Should().Be(before.Single(m => m.FriendlyName == "LG ULTRAFINE").Id);
    }

    [Fact]
    public void WhenTheDisplayConfigurationCannotBeRead_TheMonitorsHaveNoIdentity()
    {
        // Arrange - a remote session or a driver that fails QueryDisplayConfig
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = _ => null };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());

        // Act
        var monitors = sut.GetAllMonitors();

        // Assert - the monitors are still listed, exactly as EnumDisplayMonitors reported them
        monitors.Should().Equal(Desk.Select(m => m.Info));
        monitors.Should().AllSatisfy(m =>
        {
            m.Id.Should().BeNull();
            m.FriendlyName.Should().BeNull();
            m.IsInternal.Should().BeNull();
        });
    }

    [Fact]
    public void TheDpiTheWrapperReports_IsKept()
    {
        var topology = new List<(nint Handle, MonitorInfo Info)>
        {
            (100, TestDataFactory.CreateMonitorInfo(scaleFactor: 1.5) with { Dpi = 144 })
        };
        var sut = new MonitorService(new FakeMonitorWrapper { Topology = _ => topology, Targets = _ => DeskTargets }, new ManualTimeProvider());

        sut.GetAllMonitors().Single().Dpi.Should().Be(144);
    }

    #endregion

    #region Caching By Topology

    [Fact]
    public void TheDisplayConfiguration_IsReadOncePerTopology_NotOncePerSnapshot()
    {
        // Arrange
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = _ => DeskTargets, WindowMonitor = 101 };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);

        // Act - several snapshots with the same topology, and a window listing's per-window lookups in between
        for (var i = 0; i < 5; i++)
        {
            sut.GetAllMonitors();
            for (var window = 0; window < 50; window++)
            {
                sut.GetMonitorForWindow(window);
            }

            clock.Advance(MonitorService.SnapshotLifetime);
        }

        // Assert
        wrapper.EnumerateCount.Should().Be(5);
        wrapper.TargetsCount.Should().Be(1);
        sut.GetAllMonitors().Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), MonitorIdentity.IdOf(LgPath));
    }

    [Theory]
    [InlineData("bounds")]
    [InlineData("deviceName")]
    [InlineData("handle")]
    [InlineData("count")]
    public void WhenTheTopologyChanges_TheDisplayConfigurationIsReadAgain(string change)
    {
        // Arrange
        IReadOnlyList<(nint Handle, MonitorInfo Info)> changed = change switch
        {
            "bounds" => [Desk[0], Monitor(101, 1, @"\\.\DISPLAY2", -1920)],
            "deviceName" => [Desk[0], Monitor(101, 1, @"\\.\DISPLAY3", 1920)],
            "handle" => [Desk[0], Monitor(102, 1, @"\\.\DISPLAY2", 1920)],
            _ => [Desk[0]]
        };
        var wrapper = new FakeMonitorWrapper { Topology = call => call == 1 ? Desk : changed, Targets = _ => DeskTargets };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);
        sut.GetAllMonitors();

        // Act
        clock.Advance(MonitorService.SnapshotLifetime);
        sut.GetAllMonitors();

        // Assert
        wrapper.TargetsCount.Should().Be(2);
    }

    [Fact]
    public void AWorkAreaChange_IsNotATopologyChange()
    {
        // Moving or resizing the taskbar changes the work area, not which monitor is which
        var taskbarMoved = new List<(nint Handle, MonitorInfo Info)>
        {
            (100, Desk[0].Info with { WorkArea = new MonitorRect(0, 40, 1920, 1040) }),
            Desk[1]
        };
        var wrapper = new FakeMonitorWrapper { Topology = call => call == 1 ? Desk : taskbarMoved, Targets = _ => DeskTargets };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);
        sut.GetAllMonitors();

        clock.Advance(MonitorService.SnapshotLifetime);
        var result = sut.GetAllMonitors();

        wrapper.TargetsCount.Should().Be(1);
        result[0].WorkArea.Y.Should().Be(40);
        result[0].Id.Should().Be(MonitorIdentity.IdOf(DellPath));
    }

    [Fact]
    public void AFailedRead_IsNotCached_SoTheNextSnapshotTriesAgain()
    {
        // Arrange - the first read fails, the second succeeds
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = call => call == 1 ? null : DeskTargets };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);
        sut.GetAllMonitors().Should().AllSatisfy(m => m.Id.Should().BeNull());

        // Act
        clock.Advance(MonitorService.SnapshotLifetime);
        var result = sut.GetAllMonitors();

        // Assert
        wrapper.TargetsCount.Should().Be(2);
        result.Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), MonitorIdentity.IdOf(LgPath));
    }

    [Fact]
    public void ARead_ThatMissesAMonitor_IsNotCached()
    {
        // Arrange - the topology changed between EnumDisplayMonitors and QueryDisplayConfig, so DISPLAY2 has no target
        var partial = new List<DisplayTarget> { DeskTargets[0] };
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = call => call == 1 ? partial : DeskTargets };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);

        // Act
        var first = sut.GetAllMonitors();
        clock.Advance(MonitorService.SnapshotLifetime);
        var second = sut.GetAllMonitors();

        // Assert - what was read is used, but read again at the next snapshot
        first.Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), null);
        second.Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), MonitorIdentity.IdOf(LgPath));
        wrapper.TargetsCount.Should().Be(2);
    }

    [Fact]
    public void AnEnumerationWithNoMonitors_DoesNotReadTheDisplayConfiguration()
    {
        var wrapper = new FakeMonitorWrapper { Topology = _ => [], Targets = _ => DeskTargets };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());

        sut.GetAllMonitors().Should().BeEmpty();

        wrapper.TargetsCount.Should().Be(0);
    }

    #endregion

    #region Fakes

    private sealed class FakeMonitorWrapper : INativeMonitorWrapper
    {
        private int _enumerateCount;
        private int _targetsCount;

        /// <summary>Returns the topology for the given 1-based enumeration call number.</summary>
        public Func<int, IReadOnlyList<(nint Handle, MonitorInfo Info)>> Topology { get; init; } = _ => [];

        /// <summary>Returns the display targets for the given 1-based read call number; null is a failed read.</summary>
        public Func<int, IReadOnlyList<DisplayTarget>?> Targets { get; init; } = _ => null;

        public nint WindowMonitor { get; init; } = 100;

        public nint PointMonitor { get; init; } = 100;

        public int EnumerateCount => Volatile.Read(ref _enumerateCount);

        public int TargetsCount => Volatile.Read(ref _targetsCount);

        public IReadOnlyList<(nint Handle, MonitorInfo Info)> EnumerateMonitors() =>
            Topology(Interlocked.Increment(ref _enumerateCount)).ToList();

        public IReadOnlyList<DisplayTarget>? GetDisplayTargets() =>
            Targets(Interlocked.Increment(ref _targetsCount))?.ToList();

        public nint MonitorFromWindow(nint windowHandle) => WindowMonitor;

        public nint MonitorFromPoint(int x, int y) => PointMonitor;
    }

    /// <summary>A clock that only moves when the test advances it.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    #endregion
}
