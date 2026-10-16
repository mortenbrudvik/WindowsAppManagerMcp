namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The process-listing members of NativeProcessWrapper (roadmap M6.6): FindParent is pure; ListRunningProcesses and
/// ReadProcessDetails are read on this test process only, and only read.
/// </summary>
public class ProcessListingTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static Func<int, DateTime?> StartTimes(Dictionary<int, DateTime> starts) =>
        pid => starts.TryGetValue(pid, out var t) ? t : null;

    [Fact]
    public void FindParent_IsTheParent_WhenItRunsAndStartedFirst()
    {
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 300, [300] = 0 };
        var starts = new Dictionary<int, DateTime> { [300] = T0, [400] = T0.AddMinutes(1), [500] = T0.AddMinutes(2) };

        NativeProcessWrapper.FindParent(500, parents, StartTimes(starts)).Should().Be(400);
    }

    [Fact]
    public void FindParent_OfAParentThatStartedAfterTheChild_IsNull_BecauseItsPidWasReused()
    {
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 0 };
        var starts = new Dictionary<int, DateTime> { [400] = T0.AddHours(1), [500] = T0 };

        NativeProcessWrapper.FindParent(500, parents, StartTimes(starts)).Should().BeNull();
    }

    [Fact]
    public void FindParent_OfAParentThatIsNotRunning_IsNull()
    {
        var parents = new Dictionary<int, int> { [500] = 400 };

        NativeProcessWrapper.FindParent(500, parents, _ => null).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void FindParent_OfTheIdleProcessOrItself_IsNull(int parent)
    {
        var parents = new Dictionary<int, int> { [500] = parent, [0] = 0 };

        NativeProcessWrapper.FindParent(500, parents, _ => null).Should().BeNull();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void FindParent_WhenAStartTimeIsUnknown_IsNull_ThoughFindAncestorsTrustsTheParent(bool childKnown, bool parentKnown)
    {
        // Arrange - unknown start times cannot rule out a reused PID; the parent here did start after the child
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 0 };
        var starts = new Dictionary<int, DateTime>();
        if (childKnown)
        {
            starts[500] = T0;
        }

        if (parentKnown)
        {
            starts[400] = T0.AddHours(1);
        }

        // Act & Assert - a parent is named only when both start times show it started first; the kill checks'
        // walk keeps trusting it
        NativeProcessWrapper.FindParent(500, parents, StartTimes(starts)).Should().BeNull();
        NativeProcessWrapper.FindAncestors(500, parents, StartTimes(starts)).Should().Equal(400);
    }

    [Fact]
    public void FindParent_ThatStartedAtTheSameTimeAsTheChild_IsTheParent()
    {
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 0 };
        var starts = new Dictionary<int, DateTime> { [400] = T0, [500] = T0 };

        NativeProcessWrapper.FindParent(500, parents, StartTimes(starts)).Should().Be(400);
    }

    [Fact]
    public void FindParent_ReadsOnlyTheStartTimesOfTheProcessAndItsParent()
    {
        // Arrange - a long chain; the grandparents' start times are not needed for the parent
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 300, [300] = 200, [200] = 0 };
        var read = new List<int>();

        // Act
        var parent = NativeProcessWrapper.FindParent(500, parents, pid =>
        {
            read.Add(pid);
            return T0;
        });

        // Assert
        parent.Should().Be(400);
        read.Should().BeEquivalentTo([500, 400]);
    }

    [Fact]
    public void ListRunningProcesses_ListsThisTestProcess_WithItsNameAndWorkingSet()
    {
        // Act - reads the system's process list only
        var processes = new NativeProcessWrapper().ListRunningProcesses();

        // Assert
        processes.Should().NotBeNull();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var me = processes!.Should().ContainSingle(p => p.ProcessId == Environment.ProcessId).Subject;
        me.Name.Should().Be(self.ProcessName);
        me.WorkingSetBytes.Should().BeGreaterThan(0);
        me.SessionId.Should().Be(self.SessionId);
    }

    [Fact]
    public void ReadProcessDetails_OfThisTestProcess_ReadsItsPathAndStartTime()
    {
        // Act
        var details = new NativeProcessWrapper().ReadProcessDetails(Environment.ProcessId);

        // Assert - the same start time as .NET reads, which has no sub-millisecond precision on every runtime
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        details.Should().NotBeNull();
        details!.ImagePath.Should().BeEquivalentTo(Environment.ProcessPath);
        details.StartTime.Should().NotBeNull();
        details.StartTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
        details.StartTime.Value.Should().BeCloseTo(self.StartTime.ToUniversalTime(), TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue - 2)] // process IDs are multiples of 4, so this one is never a process's
    public void ReadProcessDetails_OfNoRunningProcess_IsNull(int processId)
    {
        new NativeProcessWrapper().ReadProcessDetails(processId).Should().BeNull();
    }
}
