using System.Collections.Concurrent;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for how MonitorService snapshots, caches and looks up monitors.
/// The native monitor APIs are faked, so these tests do not depend on the real displays.
/// </summary>
public class MonitorServiceSnapshotTests
{
    private const nint FirstHandle = 1000;

    private static IReadOnlyList<(nint Handle, MonitorInfo Info)> CreateTopology(int count, nint firstHandle = FirstHandle) =>
        Enumerable.Range(0, count)
            .Select(i => (firstHandle + i, TestDataFactory.CreateMonitorInfo(
                index: i,
                deviceName: $@"\\.\DISPLAY{firstHandle + i}",
                isPrimary: i == 0,
                boundsX: i * 1920,
                workAreaX: i * 1920)))
            .ToList();

    private static List<MonitorInfo> InfosOf(IReadOnlyList<(nint Handle, MonitorInfo Info)> topology) =>
        topology.Select(m => m.Info).ToList();

    #region Concurrency

    [Fact]
    public void ConcurrentCalls_WhileTopologyChanges_AlwaysSeeACompleteSnapshot()
    {
        // Arrange - every call re-enumerates, alternating between a small and a large topology
        var small = CreateTopology(2);
        var large = CreateTopology(32);
        var wrapper = new FakeMonitorWrapper { Topology = call => call % 2 == 0 ? small : large };
        var sut = new MonitorService(wrapper, new AlwaysExpiredTimeProvider());
        var smallInfos = InfosOf(small);
        var largeInfos = InfosOf(large);
        var errors = new ConcurrentQueue<string>();

        // Act
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            for (var i = 0; i < 500; i++)
            {
                try
                {
                    var monitors = sut.GetAllMonitors();
                    if (!monitors.SequenceEqual(smallInfos) && !monitors.SequenceEqual(largeInfos))
                    {
                        errors.Enqueue($"Incomplete snapshot with {monitors.Count} monitors");
                    }

                    if (sut.GetMonitorIndex(42) != 0)
                    {
                        errors.Enqueue("Window resolved to the wrong monitor");
                    }

                    sut.GetPrimaryMonitor().IsPrimary.Should().BeTrue();
                    sut.CalculateSnapBounds(1, SnapPosition.LeftHalf).X.Should().Be(1920);
                }
                catch (Exception ex)
                {
                    errors.Enqueue($"{ex.GetType().Name}: {ex.Message}");
                }
            }
        });

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void GetAllMonitors_CallerMutatesResult_DoesNotAffectLaterCalls()
    {
        // Arrange
        var topology = CreateTopology(2);
        var wrapper = new FakeMonitorWrapper { Topology = _ => topology };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());
        var first = sut.GetAllMonitors();

        // Act - try to overwrite an entry through the mutable list interface
        if (first is IList<MonitorInfo> list)
        {
            try
            {
                list[0] = TestDataFactory.CreateMonitorInfo(deviceName: "tampered");
            }
            catch (NotSupportedException)
            {
                // Expected for a read-only result
            }
        }

        // Assert - the cached snapshot is unchanged
        sut.GetAllMonitors().Should().Equal(InfosOf(topology));
    }

    #endregion

    #region Snapshot Caching

    [Fact]
    public void GetAllMonitors_WithinSnapshotLifetime_EnumeratesOnce()
    {
        // Arrange
        var wrapper = new FakeMonitorWrapper { Topology = _ => CreateTopology(2) };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);

        // Act
        sut.GetAllMonitors();
        clock.Advance(MonitorService.SnapshotLifetime / 2);
        sut.GetAllMonitors();
        sut.GetPrimaryMonitor();
        sut.CalculateSnapBounds(0, SnapPosition.LeftHalf);

        // Assert
        wrapper.EnumerateCount.Should().Be(1);
    }

    [Fact]
    public void GetAllMonitors_AfterSnapshotLifetime_EnumeratesAgain()
    {
        // Arrange - the second enumeration reports a newly connected monitor
        var before = CreateTopology(1);
        var after = CreateTopology(2);
        var wrapper = new FakeMonitorWrapper { Topology = call => call == 1 ? before : after };
        var clock = new ManualTimeProvider();
        var sut = new MonitorService(wrapper, clock);
        sut.GetAllMonitors().Should().HaveCount(1);

        // Act
        clock.Advance(MonitorService.SnapshotLifetime);
        var result = sut.GetAllMonitors();

        // Assert
        result.Should().Equal(InfosOf(after));
        wrapper.EnumerateCount.Should().Be(2);
    }

    [Fact]
    public void GetAllMonitors_WhenEnumerationFindsNoMonitors_DoesNotCacheTheEmptyResult()
    {
        // Arrange
        var topology = CreateTopology(2);
        var wrapper = new FakeMonitorWrapper { Topology = call => call == 1 ? [] : topology };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());
        sut.GetAllMonitors().Should().BeEmpty();

        // Act
        var result = sut.GetAllMonitors();

        // Assert
        result.Should().Equal(InfosOf(topology));
    }

    [Fact]
    public void GetMonitorForWindow_WhenHandleIsMissingFromCachedSnapshot_EnumeratesAgain()
    {
        // Arrange - a monitor is connected after the first snapshot and the window is on it
        var before = CreateTopology(2);
        var after = CreateTopology(3);
        var wrapper = new FakeMonitorWrapper
        {
            Topology = call => call == 1 ? before : after,
            WindowMonitor = FirstHandle + 2
        };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());
        sut.GetAllMonitors();

        // Act
        var result = sut.GetMonitorForWindow(42);

        // Assert
        result.Should().Be(after[2].Info);
        sut.GetMonitorIndex(42).Should().Be(2);
        wrapper.EnumerateCount.Should().Be(2);
    }

    [Fact]
    public void GetMonitorAt_WhenHandleIsMissingFromCachedSnapshot_EnumeratesAgain()
    {
        // Arrange
        var before = CreateTopology(1);
        var after = CreateTopology(2);
        var wrapper = new FakeMonitorWrapper
        {
            Topology = call => call == 1 ? before : after,
            PointMonitor = FirstHandle + 1
        };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());
        sut.GetAllMonitors();

        // Act
        var result = sut.GetMonitorAt(2000, 500);

        // Assert
        result.Should().Be(after[1].Info);
    }

    [Fact]
    public void GetMonitorForWindow_WithUnknownMonitorHandle_ReturnsNullAndEnumeratesAtMostOncePerLookup()
    {
        // Arrange
        var wrapper = new FakeMonitorWrapper { Topology = _ => CreateTopology(2), WindowMonitor = 5 };
        var sut = new MonitorService(wrapper, new ManualTimeProvider());

        // Act & Assert - a fresh enumeration is not repeated
        sut.GetMonitorForWindow(42).Should().BeNull();
        wrapper.EnumerateCount.Should().Be(1);

        // Act & Assert - a cached snapshot is refreshed once, then the index falls back to 0
        sut.GetMonitorIndex(42).Should().Be(0);
        wrapper.EnumerateCount.Should().Be(2);
    }

    #endregion

    #region Lookups Against The Snapshot

    [Fact]
    public void GetPrimaryMonitor_ReturnsMonitorFlaggedAsPrimary()
    {
        // Arrange - the primary monitor is not the first one enumerated
        var topology = new List<(nint Handle, MonitorInfo Info)>
        {
            (FirstHandle, TestDataFactory.CreateMonitorInfo(index: 0, isPrimary: false)),
            (FirstHandle + 1, TestDataFactory.CreateMonitorInfo(index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: true))
        };
        var sut = new MonitorService(new FakeMonitorWrapper { Topology = _ => topology }, new ManualTimeProvider());

        // Act
        var result = sut.GetPrimaryMonitor();

        // Assert
        result.Should().Be(topology[1].Info);
    }

    [Fact]
    public void GetPrimaryMonitor_WithNoMonitors_Throws()
    {
        // Arrange
        var sut = new MonitorService(new FakeMonitorWrapper(), new ManualTimeProvider());

        // Act
        var act = () => sut.GetPrimaryMonitor();

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("No monitors found");
    }

    [Fact]
    public void CalculateSnapBounds_UsesTheRequestedMonitor()
    {
        // Arrange
        var sut = new MonitorService(new FakeMonitorWrapper { Topology = _ => CreateTopology(2) }, new ManualTimeProvider());

        // Act
        var result = sut.CalculateSnapBounds(1, SnapPosition.LeftHalf);

        // Assert
        result.X.Should().Be(1920);
        result.Width.Should().Be(960);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-1)]
    public void CalculateSnapBounds_OfAMonitorThatDoesNotExist_Throws_InsteadOfFallingBackToThePrimary(int monitorIndex)
    {
        // Arrange - roadmap M2.2: the window would land on another monitor than the one asked for
        var sut = new MonitorService(new FakeMonitorWrapper { Topology = _ => CreateTopology(2) }, new ManualTimeProvider());

        // Act
        var act = () => sut.CalculateSnapBounds(monitorIndex, SnapPosition.LeftHalf);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("monitorIndex");
    }

    #endregion

    #region Fakes

    private sealed class FakeMonitorWrapper : INativeMonitorWrapper
    {
        private int _enumerateCount;

        /// <summary>Returns the topology for the given 1-based enumeration call number.</summary>
        public Func<int, IReadOnlyList<(nint Handle, MonitorInfo Info)>> Topology { get; init; } = _ => [];

        public nint WindowMonitor { get; init; } = FirstHandle;

        public nint PointMonitor { get; init; } = FirstHandle;

        public int EnumerateCount => Volatile.Read(ref _enumerateCount);

        public IReadOnlyList<(nint Handle, MonitorInfo Info)> EnumerateMonitors()
        {
            var call = Interlocked.Increment(ref _enumerateCount);

            // A new list per call, like the native wrapper
            return Topology(call).ToList();
        }

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

    /// <summary>A clock that jumps an hour on every read, so no snapshot is ever reused.</summary>
    private sealed class AlwaysExpiredTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerHour);
    }

    #endregion
}
