namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="ProcessSecurityProbe"/>: integrity mapping, the per-PID-and-creation-time
/// cache, access-denied handling and the desktop and session probes. The native layer is a strict
/// mock, so nothing is read from the real system.
/// </summary>
public class ProcessSecurityProbeTests
{
    private const int TargetPid = 4321;

    private static readonly int ServerPid = Environment.ProcessId;
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<INativeSecurityWrapper> _native = new(MockBehavior.Strict);

    private ProcessSecurityProbe CreateSut(int serverRid = ProcessIntegrity.MediumRid)
    {
        _native.Setup(n => n.QueryProcessToken(ServerPid))
            .Returns(new ProcessTokenQuery(TokenQueryStatus.Succeeded, T0, serverRid, IsElevated: serverRid >= ProcessIntegrity.HighRid));
        return new ProcessSecurityProbe(_native.Object);
    }

    private void SetupTarget(DateTime? creationTime, ProcessTokenQuery token, int processId = TargetPid)
    {
        _native.Setup(n => n.GetProcessCreationTime(processId)).Returns(creationTime);
        _native.Setup(n => n.QueryProcessToken(processId)).Returns(token);
    }

    private static ProcessTokenQuery Token(int rid, DateTime? creationTime = null, bool? elevated = false) =>
        new(TokenQueryStatus.Succeeded, creationTime ?? T0, rid, elevated);

    #region Integrity levels

    [Theory]
    [InlineData(0x0000, IntegrityLevel.Untrusted)]
    [InlineData(0x1000, IntegrityLevel.Low)]
    [InlineData(0x2000, IntegrityLevel.Medium)]
    [InlineData(0x2100, IntegrityLevel.MediumPlus)]
    [InlineData(0x3000, IntegrityLevel.High)]
    [InlineData(0x4000, IntegrityLevel.System)]
    [InlineData(0x5000, IntegrityLevel.System)] // protected process
    public void GetIntegrity_TokenRead_MapsTheRidToItsLevel(int rid, IntegrityLevel expected)
    {
        // Arrange
        var sut = CreateSut();
        SetupTarget(T0, Token(rid));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Should().Be(new ProcessIntegrity(TargetPid, expected, rid, false, null));
    }

    [Fact]
    public void GetIntegrity_ElevatedToken_ReportsElevation()
    {
        // Arrange
        var sut = CreateSut();
        SetupTarget(T0, Token(ProcessIntegrity.HighRid, elevated: true));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Level.Should().Be(IntegrityLevel.High);
        integrity.IsElevated.Should().BeTrue();
        integrity.IsMorePrivilegedThan(sut.ServerIntegrity).Should().BeTrue();
    }

    [Fact]
    public void GetIntegrity_TokenDeniedFromAMediumServer_IsLikelyElevated()
    {
        // Arrange - the process opens, its token does not
        var sut = CreateSut(ProcessIntegrity.MediumRid);
        SetupTarget(T0, new ProcessTokenQuery(TokenQueryStatus.TokenAccessDenied, T0, Win32Error: 5));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Level.Should().Be(IntegrityLevel.LikelyElevated);
        integrity.IntegrityRid.Should().BeNull();
        integrity.Reason.Should().Contain("access denied").And.Contain("elevated");
        integrity.IsMorePrivilegedThan(sut.ServerIntegrity).Should().BeTrue("UIPI would block input to it");
    }

    [Theory]
    [InlineData(ProcessIntegrity.MediumRid)]
    [InlineData(ProcessIntegrity.HighRid)]
    public void GetIntegrity_ProcessDenied_IsRestrictedNotLikelyElevated(int serverRid)
    {
        // Arrange - OpenProcess itself is denied: SYSTEM or a service account, but just as well another
        // user's medium process ("Run as different user") or a hardened DACL, which UIPI does not block
        var sut = CreateSut(serverRid);
        SetupTarget(null, new ProcessTokenQuery(TokenQueryStatus.ProcessAccessDenied, Win32Error: 5));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Level.Should().Be(IntegrityLevel.Restricted);
        integrity.IntegrityRid.Should().BeNull();
        integrity.Reason.Should().Contain("access denied").And.Contain("another account");
        integrity.IsMorePrivilegedThan(sut.ServerIntegrity).Should().BeFalse("a guard must not refuse input on this alone");
    }

    [Fact]
    public void GetIntegrity_AccessDeniedFromALowServer_IsLikelyElevated()
    {
        // Arrange
        var sut = CreateSut(ProcessIntegrity.LowRid);
        SetupTarget(T0, new ProcessTokenQuery(TokenQueryStatus.TokenAccessDenied, T0, Win32Error: 5));

        // Act & Assert
        sut.GetIntegrity(TargetPid).Level.Should().Be(IntegrityLevel.LikelyElevated);
    }

    [Fact]
    public void GetIntegrity_TokenDeniedFromAnElevatedServer_IsRestricted()
    {
        // Arrange - an elevated server is denied only by system or protected processes; it cannot tell which
        var sut = CreateSut(ProcessIntegrity.HighRid);
        SetupTarget(T0, new ProcessTokenQuery(TokenQueryStatus.TokenAccessDenied, T0, Win32Error: 5));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Level.Should().Be(IntegrityLevel.Restricted);
        integrity.Reason.Should().Contain("access denied");
        integrity.IsMorePrivilegedThan(sut.ServerIntegrity).Should().BeFalse();
    }

    [Fact]
    public void GetIntegrity_ProcessNotFound_IsUnknown()
    {
        // Arrange
        var sut = CreateSut();
        SetupTarget(null, new ProcessTokenQuery(TokenQueryStatus.ProcessNotFound, Win32Error: 87));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Should().Be(new ProcessIntegrity(TargetPid, IntegrityLevel.Unknown, null, null, "no process with this ID is running"));
    }

    [Fact]
    public void GetIntegrity_OtherFailure_IsUnknownWithTheWin32Error()
    {
        // Arrange
        var sut = CreateSut();
        SetupTarget(T0, new ProcessTokenQuery(TokenQueryStatus.Failed, T0, Win32Error: 1234));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Level.Should().Be(IntegrityLevel.Unknown);
        integrity.Reason.Should().Contain("1234");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetIntegrity_InvalidProcessId_IsUnknownWithoutNativeCalls(int processId)
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);

        // Act
        var integrity = sut.GetIntegrity(processId);

        // Assert
        integrity.Level.Should().Be(IntegrityLevel.Unknown);
        _native.VerifyNoOtherCalls();
    }

    #endregion

    #region Server integrity

    [Fact]
    public void ServerIntegrity_IsReadOnceFromTheServersOwnToken()
    {
        // Arrange
        var sut = CreateSut(ProcessIntegrity.HighRid);

        // Act
        var first = sut.ServerIntegrity;
        var second = sut.ServerIntegrity;

        // Assert
        first.Should().Be(new ProcessIntegrity(ServerPid, IntegrityLevel.High, ProcessIntegrity.HighRid, true, null));
        second.Should().BeSameAs(first);
        _native.Verify(n => n.QueryProcessToken(ServerPid), Times.Once);
    }

    [Fact]
    public void ServerIntegrity_OwnTokenUnreadable_IsUnknown_AndAccessDeniedTargetsAreNotLikelyElevated()
    {
        // Arrange - without the server's own level, access denied says nothing about elevation
        _native.Setup(n => n.QueryProcessToken(ServerPid)).Returns(new ProcessTokenQuery(TokenQueryStatus.Failed, Win32Error: 1));
        var sut = new ProcessSecurityProbe(_native.Object);
        SetupTarget(T0, new ProcessTokenQuery(TokenQueryStatus.TokenAccessDenied, T0, Win32Error: 5));

        // Act & Assert
        sut.ServerIntegrity.Level.Should().Be(IntegrityLevel.Unknown);
        var target = sut.GetIntegrity(TargetPid);
        target.Level.Should().Be(IntegrityLevel.Restricted);
        target.IsMorePrivilegedThan(sut.ServerIntegrity).Should().BeFalse();
    }

    #endregion

    #region Cache per PID and creation time

    [Fact]
    public void GetIntegrity_SamePidAndCreationTime_ReadsTheTokenOnce()
    {
        // Arrange
        var sut = CreateSut();
        SetupTarget(T0, Token(ProcessIntegrity.HighRid));

        // Act
        var first = sut.GetIntegrity(TargetPid);
        var second = sut.GetIntegrity(TargetPid);

        // Assert - the creation time is read only to check the cached entry, not before the first read
        second.Should().BeSameAs(first);
        _native.Verify(n => n.QueryProcessToken(TargetPid), Times.Once);
        _native.Verify(n => n.GetProcessCreationTime(TargetPid), Times.Once);
    }

    [Fact]
    public void GetIntegrity_NothingCached_DoesNotOpenTheProcessForItsCreationTime()
    {
        // Arrange - M1.5 calls this before every input batch; the first read opens the process once
        var sut = CreateSut();
        SetupTarget(T0, Token(ProcessIntegrity.MediumRid));

        // Act
        sut.GetIntegrity(TargetPid);

        // Assert
        _native.Verify(n => n.GetProcessCreationTime(TargetPid), Times.Never);
    }

    [Fact]
    public void GetIntegrity_LikelyElevated_IsCachedToo()
    {
        // Arrange
        var sut = CreateSut();
        SetupTarget(T0, new ProcessTokenQuery(TokenQueryStatus.TokenAccessDenied, T0, Win32Error: 5));

        // Act
        sut.GetIntegrity(TargetPid);
        var second = sut.GetIntegrity(TargetPid);

        // Assert
        second.Level.Should().Be(IntegrityLevel.LikelyElevated);
        _native.Verify(n => n.QueryProcessToken(TargetPid), Times.Once);
    }

    [Fact]
    public void GetIntegrity_PidReusedByANewerProcess_ReadsTheTokenAgain()
    {
        // Arrange - an elevated process exits and a medium one gets the same PID
        var sut = CreateSut();
        SetupTarget(T0, Token(ProcessIntegrity.HighRid, T0));
        sut.GetIntegrity(TargetPid).Level.Should().Be(IntegrityLevel.High);

        var reusedAt = T0.AddMinutes(5);
        SetupTarget(reusedAt, Token(ProcessIntegrity.MediumRid, reusedAt));

        // Act
        var integrity = sut.GetIntegrity(TargetPid);

        // Assert
        integrity.Level.Should().Be(IntegrityLevel.Medium);
        _native.Verify(n => n.QueryProcessToken(TargetPid), Times.Exactly(2));
    }

    [Fact]
    public void GetIntegrity_CachesUnderTheCreationTimeTheTokenQuerySaw()
    {
        // Arrange - the PID was reused between the creation-time read and the token read
        var sut = CreateSut();
        var reusedAt = T0.AddSeconds(1);
        SetupTarget(T0, Token(ProcessIntegrity.MediumRid, reusedAt));
        sut.GetIntegrity(TargetPid);

        // Act - the old creation time must not hit the entry for the newer process
        sut.GetIntegrity(TargetPid);

        // Assert
        _native.Verify(n => n.QueryProcessToken(TargetPid), Times.Exactly(2));
    }

    [Fact]
    public void GetIntegrity_CreationTimeUnknown_IsNotCached()
    {
        // Arrange
        var sut = CreateSut();
        SetupTarget(null, new ProcessTokenQuery(TokenQueryStatus.ProcessAccessDenied, Win32Error: 5));

        // Act
        sut.GetIntegrity(TargetPid);
        sut.GetIntegrity(TargetPid);

        // Assert
        _native.Verify(n => n.QueryProcessToken(TargetPid), Times.Exactly(2));
    }

    [Fact]
    public void GetIntegrity_UnknownResult_IsNotCached()
    {
        // Arrange - a transient failure must not stick to the process
        var sut = CreateSut();
        SetupTarget(T0, new ProcessTokenQuery(TokenQueryStatus.Failed, T0, Win32Error: 1234));

        // Act
        sut.GetIntegrity(TargetPid);
        sut.GetIntegrity(TargetPid);

        // Assert
        _native.Verify(n => n.QueryProcessToken(TargetPid), Times.Exactly(2));
    }

    [Fact]
    public void GetIntegrity_Cache_IsBounded()
    {
        // Arrange
        var sut = CreateSut();
        _native.Setup(n => n.GetProcessCreationTime(It.IsAny<int>())).Returns(T0);
        _native.Setup(n => n.QueryProcessToken(It.Is<int>(pid => pid != ServerPid))).Returns(Token(ProcessIntegrity.MediumRid));
        var firstPid = ServerPid + 4;

        // Act - one more process than the cache holds
        for (var i = 0; i <= ProcessSecurityProbe.MaxCachedProcesses; i++)
        {
            sut.GetIntegrity(firstPid + (i * 4));
        }

        sut.GetIntegrity(firstPid);

        // Assert - the first entry was dropped and is read again
        _native.Verify(n => n.QueryProcessToken(firstPid), Times.Exactly(2));
    }

    #endregion

    #region Input desktop

    [Fact]
    public void IsInputDesktopAvailable_InputDesktopCannotBeOpened_IsFalse()
    {
        // Arrange - the secure desktop (UAC prompt, Ctrl+Alt+Del) denies access
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QueryInputDesktop()).Returns(new InputDesktopQuery(false, null, 5));

        // Act & Assert
        sut.IsInputDesktopAvailable().Should().BeFalse();
    }

    [Theory]
    [InlineData("Default", "Default", true)]
    [InlineData("default", "Default", true)]
    [InlineData("Winlogon", "Default", false)]
    [InlineData("Default", null, true)]
    [InlineData(null, "Default", true)]
    public void IsInputDesktopAvailable_ComparesTheInputDesktopWithTheServersDesktop(
        string? inputDesktop, string? serverDesktop, bool expected)
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QueryInputDesktop()).Returns(new InputDesktopQuery(true, inputDesktop, 0));
        _native.Setup(n => n.GetThreadDesktopName()).Returns(serverDesktop);
        _native.Setup(n => n.QuerySessionState()).Returns(new SessionStateQuery(1, ConnectState: 0, SessionFlags: 1));

        // Act & Assert
        sut.IsInputDesktopAvailable().Should().Be(expected);
    }

    [Theory]
    [InlineData(0, true)]  // WTSActive
    [InlineData(4, false)] // WTSDisconnected: OpenInputDesktop still succeeds, but no input arrives
    public void IsInputDesktopAvailable_DisconnectedSession_IsFalse(int connectState, bool expected)
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QueryInputDesktop()).Returns(new InputDesktopQuery(true, "Default", 0));
        _native.Setup(n => n.GetThreadDesktopName()).Returns("Default");
        _native.Setup(n => n.QuerySessionState()).Returns(new SessionStateQuery(1, connectState, SessionFlags: 1));

        // Act & Assert
        sut.IsInputDesktopAvailable().Should().Be(expected);
    }

    [Fact]
    public void IsInputDesktopAvailable_SessionStateUnreadable_TrustsTheDesktopCheck()
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QueryInputDesktop()).Returns(new InputDesktopQuery(true, "Default", 0));
        _native.Setup(n => n.GetThreadDesktopName()).Returns("Default");
        _native.Setup(n => n.QuerySessionState()).Returns((SessionStateQuery?)null);

        // Act & Assert
        sut.IsInputDesktopAvailable().Should().BeTrue();
    }

    [Fact]
    public void IsInputDesktopAvailable_IsReadOnEveryCall()
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.SetupSequence(n => n.QueryInputDesktop())
            .Returns(new InputDesktopQuery(true, "Default", 0))
            .Returns(new InputDesktopQuery(false, null, 5));
        _native.Setup(n => n.GetThreadDesktopName()).Returns("Default");
        _native.Setup(n => n.QuerySessionState()).Returns(new SessionStateQuery(1, 0, 1));

        // Act & Assert
        sut.IsInputDesktopAvailable().Should().BeTrue();
        sut.IsInputDesktopAvailable().Should().BeFalse();
    }

    #endregion

    #region Session lock

    [Theory]
    [InlineData(0, true)]   // WTS_SESSIONSTATE_LOCK
    [InlineData(1, false)]  // WTS_SESSIONSTATE_UNLOCK
    [InlineData(-1, false)] // WTS_SESSIONSTATE_UNKNOWN
    public void IsSessionLocked_ReadsTheSessionFlags(int sessionFlags, bool expected)
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QuerySessionState()).Returns(new SessionStateQuery(1, 0, sessionFlags));

        // Act & Assert
        sut.IsSessionLocked().Should().Be(expected);
    }

    [Fact]
    public void IsSessionLocked_StateUnreadable_IsFalse()
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QuerySessionState()).Returns((SessionStateQuery?)null);

        // Act & Assert
        sut.IsSessionLocked().Should().BeFalse();
    }

    [Theory]
    [InlineData(0, SessionLockState.Locked)]    // WTS_SESSIONSTATE_LOCK
    [InlineData(1, SessionLockState.Unlocked)]  // WTS_SESSIONSTATE_UNLOCK
    [InlineData(-1, SessionLockState.Unknown)]  // WTS_SESSIONSTATE_UNKNOWN, which IsSessionLocked reads as unlocked
    [InlineData(7, SessionLockState.Unknown)]   // a value Windows does not document
    public void GetSessionLock_ReadsTheSessionFlags_WithoutGuessingAnUnknownState(int sessionFlags, SessionLockState expected)
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QuerySessionState()).Returns(new SessionStateQuery(1, 0, sessionFlags));

        // Act & Assert
        sut.GetSessionLock().Should().Be(expected);
    }

    [Fact]
    public void GetSessionLock_StateUnreadable_IsUnknown()
    {
        // Arrange
        var sut = new ProcessSecurityProbe(_native.Object);
        _native.Setup(n => n.QuerySessionState()).Returns((SessionStateQuery?)null);

        // Act & Assert
        sut.GetSessionLock().Should().Be(SessionLockState.Unknown);
    }

    #endregion

    #region Elevation prompt (roadmap M6.5)

    [Theory]
    [InlineData(1, 5, ElevationPrompt.Prompts)]
    [InlineData(1, 2, ElevationPrompt.Prompts)]
    [InlineData(1, 1, ElevationPrompt.Prompts)]
    [InlineData(1, 0, ElevationPrompt.Silent)]
    [InlineData(0, 5, ElevationPrompt.Silent)]
    [InlineData(0, 0, ElevationPrompt.Silent)]
    [InlineData(null, 5, ElevationPrompt.Prompts)]
    [InlineData(1, null, ElevationPrompt.Prompts)]
    public void ElevationPrompt_ReadsWhetherUacAsksAnAdministrator(int? enableLua, int? consentPromptBehaviorAdmin, ElevationPrompt expected)
    {
        // Arrange - a missing value is Windows' default: UAC on, and consent asked for
        _native.Setup(n => n.ReadUacSettings()).Returns(new UacSettings(enableLua, consentPromptBehaviorAdmin, null, false));

        // Act & Assert
        new ProcessSecurityProbe(_native.Object).GetElevationPrompt().Should().Be(expected);
    }

    [Theory]
    [InlineData(null, ElevationPrompt.Silent)] // FilterAdministratorToken is off by default
    [InlineData(0, ElevationPrompt.Silent)]
    [InlineData(1, ElevationPrompt.Prompts)]
    public void ElevationPrompt_TheBuiltInAdministrator_IsAskedOnlyWithAdminApprovalMode(int? filterAdministratorToken, ElevationPrompt expected)
    {
        // Review M6.5 #1: without FilterAdministratorToken the built-in Administrator runs everything with its full token
        _native.Setup(n => n.ReadUacSettings()).Returns(new UacSettings(1, 5, filterAdministratorToken, true));

        new ProcessSecurityProbe(_native.Object).GetElevationPrompt().Should().Be(expected);
    }

    [Fact]
    public void ElevationPrompt_SettingsThatCannotBeRead_AreUnknown()
    {
        _native.Setup(n => n.ReadUacSettings()).Returns((UacSettings?)null);

        new ProcessSecurityProbe(_native.Object).GetElevationPrompt().Should().Be(ElevationPrompt.Unknown);
    }

    [Fact]
    public void ElevationPrompt_IsReadLiveOnEveryCall()
    {
        var settings = new UacSettings(1, 5, null, false);
        _native.Setup(n => n.ReadUacSettings()).Returns(() => settings);
        var sut = new ProcessSecurityProbe(_native.Object);
        sut.GetElevationPrompt().Should().Be(ElevationPrompt.Prompts);

        settings = new UacSettings(0, 5, null, false);

        sut.GetElevationPrompt().Should().Be(ElevationPrompt.Silent);
    }

    #endregion
}
