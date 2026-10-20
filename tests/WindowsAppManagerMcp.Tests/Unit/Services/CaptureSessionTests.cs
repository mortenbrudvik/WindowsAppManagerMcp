using FluentAssertions;
using Moq;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;
using Xunit;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// CaptureSession: the one decision the captures and wait_for_visual warn by while the session is locked (issues #17 and
/// #18).
/// </summary>
public class CaptureSessionTests
{
    [Theory]
    [InlineData(SessionLockState.Locked, true, CaptureWarnings.SessionLocked)]
    [InlineData(SessionLockState.Locked, false, CaptureWarnings.SessionLocked)]
    [InlineData(SessionLockState.Unlocked, false, CaptureWarnings.DesktopUnavailable)]
    [InlineData(SessionLockState.Unknown, false, CaptureWarnings.DesktopUnavailable)]
    [InlineData(SessionLockState.Unlocked, true, null)]
    [InlineData(SessionLockState.Unknown, true, null)]
    public void WarningCode_IsTheLockFirst_ThenTheInputDesktop(SessionLockState sessionLock, bool desktopAvailable, string? expected)
    {
        var security = new Mock<IProcessSecurityProbe>();
        security.Setup(s => s.GetSessionLock()).Returns(sessionLock);
        security.Setup(s => s.IsInputDesktopAvailable()).Returns(desktopAvailable);

        CaptureSession.WarningCode(security.Object).Should().Be(expected);
    }

    [Fact]
    public void WarningCode_WithoutAProbe_IsNull()
    {
        CaptureSession.WarningCode(null).Should().BeNull();
    }
}
