namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Unit tests for the parent-chain walk used to refuse killing the agent's host process.
/// FindAncestors is pure; the real snapshot test only reads the process list.
/// </summary>
public class ProcessAncestryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task WaitForExitAsync_OfAProcessThatKeepsRunning_ReturnsFalseAtTheTimeout()
    {
        // Arrange - this test process, which certainly does not exit; nothing is started, closed or killed
        using var self = new NativeProcessWrapper().OpenProcess(Environment.ProcessId)!;

        // Act
        var exited = await self.WaitForExitAsync(50, CancellationToken.None);

        // Assert
        exited.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForExitAsync_WhenTheCallIsCancelled_Throws()
    {
        // Arrange
        using var self = new NativeProcessWrapper().OpenProcess(Environment.ProcessId)!;
        using var call = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        var act = () => self.WaitForExitAsync(60000, call.Token);

        // Assert - a cancellation, not "did not exit within the timeout"
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void FindAncestors_ReturnsChainNearestFirst()
    {
        // Arrange - server(500) <- claude(400) <- pwsh(300) <- WindowsTerminal(200) <- explorer(100)
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 300, [300] = 200, [200] = 100, [100] = 50 };
        var starts = new Dictionary<int, DateTime>
        {
            [100] = T0, [200] = T0.AddMinutes(1), [300] = T0.AddMinutes(2), [400] = T0.AddMinutes(3), [500] = T0.AddMinutes(4)
        };

        // Act
        var ancestors = NativeProcessWrapper.FindAncestors(500, parents, pid => starts.TryGetValue(pid, out var t) ? t : null);

        // Assert - 50 is not running (not in the snapshot), so the walk stops at 100
        ancestors.Should().Equal(400, 300, 200, 100);
    }

    [Fact]
    public void FindAncestors_ParentStartedAfterChild_StopsBecausePidWasReused()
    {
        // Arrange - the real parent of 400 exited and an unrelated process reused PID 300
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 300, [300] = 200, [200] = 0 };
        var starts = new Dictionary<int, DateTime>
        {
            [200] = T0, [300] = T0.AddHours(5), [400] = T0.AddMinutes(1), [500] = T0.AddMinutes(2)
        };

        // Act
        var ancestors = NativeProcessWrapper.FindAncestors(500, parents, pid => starts.TryGetValue(pid, out var t) ? t : null);

        // Assert
        ancestors.Should().Equal(400);
    }

    [Fact]
    public void FindAncestors_UnknownStartTime_UsesChildStartTimeAsBound()
    {
        // Arrange - 400's start time is unreadable (e.g. elevated); 300 started after 500, so it is a reused PID
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 300, [300] = 200 };
        var starts = new Dictionary<int, DateTime> { [500] = T0, [300] = T0.AddMinutes(10) };

        // Act
        var ancestors = NativeProcessWrapper.FindAncestors(500, parents, pid => starts.TryGetValue(pid, out var t) ? t : null);

        // Assert
        ancestors.Should().Equal(400);
    }

    [Fact]
    public void FindAncestors_Cycle_Terminates()
    {
        // Arrange
        var parents = new Dictionary<int, int> { [500] = 400, [400] = 300, [300] = 400 };

        // Act
        var ancestors = NativeProcessWrapper.FindAncestors(500, parents, _ => null);

        // Assert
        ancestors.Should().Equal(400, 300);
    }

    [Fact]
    public void FindAncestors_ProcessNotInSnapshot_ReturnsEmpty()
    {
        // Act
        var ancestors = NativeProcessWrapper.FindAncestors(500, new Dictionary<int, int>(), _ => null);

        // Assert
        ancestors.Should().BeEmpty();
    }

    [Fact]
    public void GetAncestorProcessIds_RealSnapshot_ReturnsParentsButNotSelf()
    {
        // Act - reads the process list only
        var ancestors = NativeProcessWrapper.GetAncestorProcessIds();

        // Assert - the test host always has a running parent (dotnet test / vstest)
        ancestors.Should().NotBeEmpty();
        ancestors.Should().NotContain(Environment.ProcessId);
        new NativeProcessWrapper().IsAncestorOfCurrentProcess(Environment.ProcessId).Should().BeFalse();
        new NativeProcessWrapper().IsAncestorOfCurrentProcess(ancestors[0]).Should().BeTrue();
    }

    [Fact]
    public void FindDescendants_ReturnsTheWholeTreeBreadthFirst()
    {
        // Arrange - cmd(100) -> conhost(200), KeePass(300) -> helper(400); 500 is unrelated
        var parents = new Dictionary<int, int> { [100] = 50, [200] = 100, [300] = 100, [400] = 300, [500] = 50 };
        var starts = new Dictionary<int, DateTime>
        {
            [100] = T0, [200] = T0.AddSeconds(1), [300] = T0.AddMinutes(1), [400] = T0.AddMinutes(2), [500] = T0
        };

        // Act
        var descendants = NativeProcessWrapper.FindDescendants(100, parents, pid => starts.TryGetValue(pid, out var t) ? t : null);

        // Assert
        descendants.Should().Equal(200, 300, 400);
    }

    [Fact]
    public void FindDescendants_ChildStartedBeforeItsParent_IsAReusedPid()
    {
        // Arrange - 300 names 100 as its parent, but started before 100: its real parent exited and 100 is a newer process
        var parents = new Dictionary<int, int> { [100] = 50, [300] = 100, [400] = 300 };
        var starts = new Dictionary<int, DateTime> { [100] = T0.AddHours(1), [300] = T0, [400] = T0.AddMinutes(1) };

        // Act & Assert
        NativeProcessWrapper.FindDescendants(100, parents, pid => starts.TryGetValue(pid, out var t) ? t : null)
            .Should().BeEmpty();
    }

    [Fact]
    public void FindDescendants_Cycle_Terminates()
    {
        var parents = new Dictionary<int, int> { [100] = 200, [200] = 100, [0] = 0 };

        NativeProcessWrapper.FindDescendants(100, parents, _ => null).Should().Equal(200);
    }

    [Fact]
    public void GetDescendantsOf_RealSnapshot_ReadsTheTreeOfARunningProcess()
    {
        // Act - reads the process list only
        var descendants = new NativeProcessWrapper().GetDescendantsOf(Environment.ProcessId);

        // Assert - the test host is in the snapshot, so its children are known (whatever they are)
        descendants.Should().NotBeNull().And.NotContain(d => d.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void GetDescendantsOf_RealSnapshot_AProcessThatIsNotRunningHasUnknownChildren()
    {
        // Act - no process has this ID (Windows process IDs are multiples of 4)
        var descendants = new NativeProcessWrapper().GetDescendantsOf(int.MaxValue - 1);

        // Assert - not an empty list: a forced kill must not read it as "no children"
        descendants.Should().BeNull();
    }

    // Odd IDs, so that the start-time reads (the PID reuse check) never find a real process
    private const int Cmd = 2_000_000_001, Conhost = 2_000_000_003, KeePass = 2_000_000_005, Explorer = 2_000_000_007;

    [Fact]
    public void ReadProcessList_ReadsEveryEntry()
    {
        // Arrange - a snapshot with three processes, read the way Process32FirstW and Process32NextW return them
        var entries = new Queue<ProcessSnapshotEntry>([new(Explorer, 4, "explorer.exe"), new(Cmd, Explorer, "cmd.exe"), new(Conhost, Cmd, "conhost.exe")]);
        var firsts = new List<bool>();

        // Act
        var processes = NativeProcessWrapper.ReadProcessList(first =>
        {
            firsts.Add(first);
            return entries.Count > 0 ? entries.Dequeue() : null;
        });

        // Assert
        processes.Should().NotBeNull();
        processes!.Keys.Should().BeEquivalentTo([Explorer, Cmd, Conhost]);
        processes[Conhost].Should().Be((Cmd, "conhost.exe"));
        firsts.Should().Equal(true, false, false, false);
    }

    [Fact]
    public void ReadProcessList_WhenTheFirstEntryCannotBeRead_IsUnknown()
    {
        // Act - Process32FirstW failed: a snapshot always lists at least the idle process and this one, so the
        // process list was not read, and an empty list would read as "no processes, no children"
        var processes = NativeProcessWrapper.ReadProcessList(_ => null);

        // Assert
        processes.Should().BeNull();
    }

    [Fact]
    public void GetDescendantsOf_WhenTheFirstSnapshotEntryCannotBeRead_IsUnknown()
    {
        // Arrange - the snapshot was taken, but Process32FirstW failed
        var wrapper = new NativeProcessWrapper(() => NativeProcessWrapper.ReadProcessList(_ => null));

        // Act & Assert - null, which kill_process refuses with ProcessTreeUnreadable
        wrapper.GetDescendantsOf(Cmd).Should().BeNull();
    }

    [Fact]
    public void GetDescendantsOf_WhenTheSnapshotDoesNotListTheProcess_IsUnknown()
    {
        // Arrange - kill_process found the process running, but the snapshot taken next does not list it
        var wrapper = new NativeProcessWrapper(() => Snapshot(new(Explorer, 4, "explorer.exe"), new(Conhost, Cmd, "conhost.exe")));

        // Act & Assert - its children are unknown, not absent: the tree kill would still find and end them
        wrapper.GetDescendantsOf(Cmd).Should().BeNull();
    }

    [Fact]
    public void GetDescendantsOf_ReadsTheTreeFromTheSnapshot()
    {
        // Arrange
        var wrapper = new NativeProcessWrapper(() => Snapshot(
            new(Explorer, 4, "explorer.exe"), new(Cmd, Explorer, "cmd.exe"), new(Conhost, Cmd, "conhost.exe"), new(KeePass, Conhost, "KeePass.exe")));

        // Act
        var descendants = wrapper.GetDescendantsOf(Cmd);

        // Assert
        descendants.Should().Equal((Conhost, "conhost.exe"), (KeePass, "KeePass.exe"));
    }

    private static Dictionary<int, (int ParentProcessId, string ImageName)>? Snapshot(params ProcessSnapshotEntry[] entries)
    {
        var pending = new Queue<ProcessSnapshotEntry>(entries);
        return NativeProcessWrapper.ReadProcessList(_ => pending.Count > 0 ? pending.Dequeue() : null);
    }
}
