namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for how MonitorService adds each monitor's identity from the display configuration (roadmap M6.7, S0d) and
/// reads it again at every snapshot refresh but not at every lookup. The native reads are faked, so no display is read.
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

    #region Reading At Every Snapshot

    private const string LaptopPath = @"\\?\DISPLAY#BOE0A1C#4&1b2c3d4e&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string EpsonPath = @"\\?\DISPLAY#EPS0001#5&99aa88bb&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string BenqPath = @"\\?\DISPLAY#BNQ7F21#5&99aa88bb&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>A laptop alone: its panel is DISPLAY1 at 1920x1080.</summary>
    private static readonly IReadOnlyList<(nint Handle, MonitorInfo Info)> Laptop = [Monitor(100, 0, @"\\.\DISPLAY1", 0, isPrimary: true)];

    private static readonly DisplayTarget LaptopPanel = new(@"\\.\DISPLAY1", null, LaptopPath, DisplayOutputTechnology.Internal);
    private static readonly DisplayTarget EpsonClone = new(@"\\.\DISPLAY1", "EPSON PJ", EpsonPath, DisplayOutputTechnology.Hdmi);
    private static readonly DisplayTarget BenqClone = new(@"\\.\DISPLAY1", "BENQ PJ", BenqPath, DisplayOutputTechnology.Hdmi);

    [Fact]
    public void TheDisplayConfiguration_IsReadOncePerSnapshot_NotOncePerLookup()
    {
        // Arrange
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = _ => DeskTargets, WindowMonitor = 101 };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);

        // Act - several snapshots, and a window listing's per-window lookups in between
        for (var i = 0; i < 5; i++)
        {
            sut.GetAllMonitors();
            for (var window = 0; window < 50; window++)
            {
                sut.GetMonitorForWindow(window);
            }

            clock.Advance(MonitorService.SnapshotLifetime);
        }

        // Assert - one read per snapshot refresh, none per lookup
        wrapper.EnumerateCount.Should().Be(5);
        wrapper.TargetsCount.Should().Be(5);
        sut.GetAllMonitors().Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), MonitorIdentity.IdOf(LgPath));
    }

    [Theory]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("swapped")]
    public void WhenACloneTargetChangesOnASourceThatStays_TheIdAndNameFollow(string change)
    {
        // Arrange - a projector duplicating the laptop panel (Win+P Duplicate) at the panel's own resolution is a second
        // target on DISPLAY1's source: the monitor handle, device name and bounds EnumDisplayMonitors reports all stay
        var (before, after) = change switch
        {
            "added" => (new[] { LaptopPanel }, new[] { LaptopPanel, EpsonClone }),
            "removed" => (new[] { LaptopPanel, EpsonClone }, new[] { LaptopPanel }),
            _ => (new[] { LaptopPanel, EpsonClone }, new[] { LaptopPanel, BenqClone })
        };
        var wrapper = new FakeMonitorWrapper { Topology = _ => Laptop, Targets = call => call == 1 ? before : after };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);
        sut.GetAllMonitors();

        // Act
        clock.Advance(MonitorService.SnapshotLifetime);
        var result = sut.GetAllMonitors().Single();

        // Assert
        result.Id.Should().Be(MonitorIdentity.IdOf(after.Select(t => t.DevicePath)));
        result.FriendlyName.Should().Be(change switch { "added" => "EPSON PJ", "removed" => null, _ => "BENQ PJ" });
        result.IsInternal.Should().BeTrue();
    }

    [Fact]
    public void ARead_ThatMissesAClonedPath_IsReadAgainAtTheNextSnapshot()
    {
        // Arrange - the laptop panel is cloned to a projector, but the first read has only the panel's path
        var wrapper = new FakeMonitorWrapper
        {
            Topology = _ => Laptop,
            Targets = call => call == 1 ? [LaptopPanel] : [LaptopPanel, EpsonClone]
        };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);
        sut.GetAllMonitors();

        // Act
        clock.Advance(MonitorService.SnapshotLifetime);
        var result = sut.GetAllMonitors().Single();

        // Assert - the id is hashed from both cloned paths, not kept from the partial read
        result.Id.Should().Be(MonitorIdentity.IdOf([LaptopPath, EpsonPath]));
        result.FriendlyName.Should().Be("EPSON PJ");
    }

    [Fact]
    public void AFailedRead_IsReadAgainAtTheNextSnapshot()
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
    public void ARead_ThatMissesAMonitor_IsReadAgainAtTheNextSnapshot()
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
    public void ARead_WhoseTargetNameFailed_IsReadAgainAtTheNextSnapshot()
    {
        // Arrange - GET_TARGET_NAME failed for DISPLAY2 (a mode set or a monitor waking up), while its source name was read,
        // so the read names every monitor but has no device path for the LG. The handles, names and bounds stay the same
        var unreadLg = new DisplayTarget(@"\\.\DISPLAY2", null, null, DisplayOutputTechnology.Hdmi) { TargetRead = false };
        var partial = new List<DisplayTarget> { DeskTargets[0], unreadLg };
        var wrapper = new FakeMonitorWrapper { Topology = _ => Desk, Targets = call => call == 1 ? partial : DeskTargets };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);

        // Act
        var first = sut.GetAllMonitors();
        clock.Advance(MonitorService.SnapshotLifetime);
        var second = sut.GetAllMonitors();

        // Assert - what was read is used, but read again at the next snapshot, which then has the LG's id
        first.Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), null);
        second.Select(m => m.Id).Should().Equal(MonitorIdentity.IdOf(DellPath), MonitorIdentity.IdOf(LgPath));
        second[1].FriendlyName.Should().Be("LG ULTRAFINE");
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
