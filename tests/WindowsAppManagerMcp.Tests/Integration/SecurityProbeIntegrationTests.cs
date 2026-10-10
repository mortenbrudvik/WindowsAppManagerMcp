using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// Read-only checks of the real native layer behind <see cref="IProcessSecurityProbe"/> and
/// <see cref="IHostGuard"/>: the P/Invoke signatures and struct offsets against the running test
/// process. Every call only reads (tokens with TOKEN_QUERY, the input desktop with
/// DESKTOP_READOBJECTS, session information, the process list and window ownership); nothing is
/// sent to, focused on or changed on the desktop. Assertions that depend on the machine (elevation,
/// a locked session, a console) are conditional.
/// </summary>
public class SecurityProbeIntegrationTests
{
    /// <summary>A multiple of 4 above any PID Windows hands out.</summary>
    private const int NoSuchProcessId = int.MaxValue - 3;

    private readonly NativeSecurityWrapper _native = new();

    private static DateTime OwnStartTime()
    {
        using var current = Process.GetCurrentProcess();
        return current.StartTime.ToUniversalTime();
    }

    #region NativeSecurityWrapper

    [Fact]
    public void QueryProcessToken_OwnProcess_ReadsIntegrityElevationAndCreationTime()
    {
        // Act
        var token = _native.QueryProcessToken(Environment.ProcessId);

        // Assert
        token.Status.Should().Be(TokenQueryStatus.Succeeded);
        token.IntegrityRid.Should().BeGreaterThanOrEqualTo(ProcessIntegrity.LowRid, "a test host never runs untrusted");
        token.IsElevated.Should().NotBeNull();
        token.CreationTime.Should().BeCloseTo(OwnStartTime(), TimeSpan.FromSeconds(1));
        token.CreationTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void GetProcessCreationTime_OwnProcess_MatchesTheTokenQuery()
    {
        _native.GetProcessCreationTime(Environment.ProcessId)
            .Should().Be(_native.QueryProcessToken(Environment.ProcessId).CreationTime);
    }

    [Fact]
    public void QueryProcessToken_NoSuchProcess_IsProcessNotFound()
    {
        // Act
        var token = _native.QueryProcessToken(NoSuchProcessId);

        // Assert
        token.Status.Should().Be(TokenQueryStatus.ProcessNotFound);
        token.Win32Error.Should().Be(87);
        _native.GetProcessCreationTime(NoSuchProcessId).Should().BeNull();
    }

    [Fact]
    public void QuerySessionState_ReadsTheServersOwnSession()
    {
        // Act
        var session = _native.QuerySessionState();

        // Assert - the session ID at the WTSINFOEX_LEVEL1 offset proves the struct layout
        session.Should().NotBeNull();
        using var current = Process.GetCurrentProcess();
        session!.SessionId.Should().Be(current.SessionId);
        session.SessionFlags.Should().BeOneOf(-1, 0, 1);
        session.ConnectState.Should().BeInRange(0, 9);
    }

    [Fact]
    public void GetThreadDesktopName_ReadsTheDesktopOfTheTestThread()
    {
        _native.GetThreadDesktopName().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void QueryInputDesktop_EitherNamesTheDesktopOrReportsTheError()
    {
        // Act
        var desktop = _native.QueryInputDesktop();

        // Assert - without an interactive desktop (some CI runners) opening it fails
        if (desktop.Opened)
        {
            desktop.Name.Should().NotBeNullOrWhiteSpace();
            desktop.Win32Error.Should().Be(0);
        }
        else
        {
            desktop.Win32Error.Should().NotBe(0);
        }
    }

    #endregion

    #region ProcessSecurityProbe on the real wrapper

    [Fact]
    public void Probe_ServerIntegrity_IsKnownAndMatchesElevation()
    {
        // Arrange
        var probe = new ProcessSecurityProbe(_native);

        // Act
        var server = probe.ServerIntegrity;

        // Assert
        server.ProcessId.Should().Be(Environment.ProcessId);
        server.Level.Should().BeOneOf(IntegrityLevel.Low, IntegrityLevel.Medium, IntegrityLevel.MediumPlus, IntegrityLevel.High, IntegrityLevel.System);
        if (server.IsElevated == true)
        {
            server.Level.Should().BeOneOf(IntegrityLevel.High, IntegrityLevel.System);
        }

        probe.GetIntegrity(Environment.ProcessId).Should().Be(server);
    }

    [Fact]
    public void Probe_SystemProcess_IsSystemOrRestricted()
    {
        // Arrange - PID 4 is the System process
        var probe = new ProcessSecurityProbe(_native);

        // Act
        var system = probe.GetIntegrity(4);

        // Assert - a medium server is denied OpenProcess, as it is for every other account's process,
        // so the level is unknown (Restricted) rather than inferred; an elevated server may read it
        system.Level.Should().BeOneOf(IntegrityLevel.System, IntegrityLevel.Restricted);
        system.IsMorePrivilegedThan(probe.ServerIntegrity).Should().Be(
            system.Level == IntegrityLevel.System && probe.ServerIntegrity.IntegrityRid < ProcessIntegrity.SystemRid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void QueryProcessToken_NotAProcessId_IsProcessNotFoundWithoutAStaleError(int processId)
    {
        // Arrange - leave a different last error on this thread first
        _native.QueryProcessToken(4);

        // Act
        var token = _native.QueryProcessToken(processId);

        // Assert
        token.Status.Should().Be(TokenQueryStatus.ProcessNotFound);
        token.Win32Error.Should().Be(87);
    }

    [Fact]
    public void Probe_DesktopAndSessionReads_DoNotThrow()
    {
        // Arrange
        var probe = new ProcessSecurityProbe(_native);

        // Act - the answers depend on the machine (a CI runner may have no interactive desktop)
        var read = () => (probe.IsInputDesktopAvailable(), probe.IsSessionLocked());

        // Assert
        read.Should().NotThrow();
    }

    #endregion

    #region HostGuard on the real wrappers

    [Fact]
    public void GetAncestorProcesses_RealSnapshot_AreTheAncestorIdsWithStartTimes()
    {
        // Act
        var ancestors = new NativeProcessWrapper().GetAncestorProcesses();

        // Assert - the test host always has a running parent (dotnet test / vstest)
        ancestors.Should().NotBeEmpty();
        ancestors.Select(a => a.ProcessId).Should().Equal(NativeProcessWrapper.GetAncestorProcessIds());
        ancestors[0].StartTime.Should().NotBeNull().And.BeOnOrBefore(OwnStartTime());
    }

    [Fact]
    public void GetProcessStartTime_OwnProcess_IsItsStartTime()
    {
        new NativeProcessWrapper().GetProcessStartTime(Environment.ProcessId).Should().Be(OwnStartTime());
        new NativeProcessWrapper().GetProcessStartTime(NoSuchProcessId).Should().BeNull();
    }

    [Fact]
    public void GetAncestor_RealWindows_ResolvesTopLevelWindowsToThemselves()
    {
        // Arrange - read-only: the wrapper refuses every window change
        var windows = new ReadOnlyNativeWindowWrapper();
        var topLevel = new List<nint>();
        windows.EnumWindows(handle =>
        {
            if (windows.IsWindowVisible(handle))
            {
                topLevel.Add(handle);
            }

            return topLevel.Count < 5;
        });

        // Act & Assert
        windows.GetAncestor(nint.Zero, WindowAncestor.RootOwner).Should().Be(nint.Zero);
        foreach (var handle in topLevel)
        {
            windows.GetAncestor(handle, WindowAncestor.Root).Should().Be(handle, "an enumerated window is top-level");
        }
    }

    [Fact]
    public void HostGuard_RealSystem_RecognizesTheTestHostsOwnChain()
    {
        // Act - records the real host: process list, console window and foreground window (reads only)
        var guard = new HostGuard(new ReadOnlyNativeWindowWrapper(), new NativeProcessWrapper());

        // Assert
        guard.Host.ServerProcessId.Should().Be(Environment.ProcessId);
        guard.Host.Ancestors.Should().NotBeEmpty();
        guard.IsHostProcess(Environment.ProcessId).Should().BeTrue();
        guard.IsHostProcess(guard.Host.Ancestors[0].ProcessId).Should().BeTrue();
        guard.IsHostProcess(NoSuchProcessId).Should().BeFalse();
        if (guard.Host.ConsoleHostWindow is { } consoleHost)
        {
            guard.IsHostWindow(consoleHost.Handle).Should().BeTrue();
        }

        if (guard.Host.ShellProcessId is { } shell)
        {
            guard.Host.StartupForegroundWindow?.Process.ProcessId.Should().NotBe(shell);
        }
    }

    #endregion
}
