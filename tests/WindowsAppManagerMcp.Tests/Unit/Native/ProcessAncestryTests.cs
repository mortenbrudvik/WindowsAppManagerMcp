namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Unit tests for the parent-chain walk used to refuse killing the agent's host process.
/// FindAncestors is pure; the real snapshot test only reads the process list.
/// </summary>
public class ProcessAncestryTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

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
}
