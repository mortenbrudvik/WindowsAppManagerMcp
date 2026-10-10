namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// The UIPI comparison on <see cref="ProcessIntegrity"/>: which targets are more privileged than the server.
/// </summary>
public class ProcessIntegrityTests
{
    private static ProcessIntegrity Known(int rid) =>
        new(100, ProcessIntegrity.LevelFromRid(rid), rid, rid >= ProcessIntegrity.HighRid, null);

    private static readonly ProcessIntegrity Unknown = new(100, IntegrityLevel.Unknown, null, null, "unreadable");

    private static readonly ProcessIntegrity LikelyElevated = new(100, IntegrityLevel.LikelyElevated, null, null, "access denied");

    private static readonly ProcessIntegrity Restricted = new(100, IntegrityLevel.Restricted, null, null, "access denied");

    [Theory]
    [InlineData(0x3000, 0x2000, true)]  // elevated target, medium server
    [InlineData(0x4000, 0x3000, true)]  // system target, elevated server
    [InlineData(0x2100, 0x2000, true)]  // raw RIDs are compared, as UIPI does
    [InlineData(0x2000, 0x2000, false)] // same level
    [InlineData(0x1000, 0x2000, false)] // low target, medium server
    [InlineData(0x2000, 0x3000, false)] // medium target, elevated server
    public void IsMorePrivilegedThan_KnownLevels_ComparesRids(int targetRid, int serverRid, bool expected)
    {
        Known(targetRid).IsMorePrivilegedThan(Known(serverRid)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0x1000, true)]
    [InlineData(0x2000, true)]
    [InlineData(0x2100, true)]
    [InlineData(0x3000, false)]
    public void IsMorePrivilegedThan_LikelyElevated_CountsAsHigherThanServersBelowHigh(int serverRid, bool expected)
    {
        LikelyElevated.IsMorePrivilegedThan(Known(serverRid)).Should().Be(expected);
    }

    [Fact]
    public void IsMorePrivilegedThan_UnknownOnEitherSide_IsFalse()
    {
        Unknown.IsMorePrivilegedThan(Known(0x2000)).Should().BeFalse();
        Known(0x3000).IsMorePrivilegedThan(Unknown).Should().BeFalse();
        LikelyElevated.IsMorePrivilegedThan(Unknown).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x2000)]
    [InlineData(0x3000)]
    public void IsMorePrivilegedThan_Restricted_IsFalse(int serverRid)
    {
        // Its level is unknown: it may be SYSTEM, or another user's ordinary process that UIPI does not block
        Restricted.IsMorePrivilegedThan(Known(serverRid)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x0000, IntegrityLevel.Untrusted)]
    [InlineData(0x0FFF, IntegrityLevel.Untrusted)]
    [InlineData(0x1000, IntegrityLevel.Low)]
    [InlineData(0x1FFF, IntegrityLevel.Low)]
    [InlineData(0x2000, IntegrityLevel.Medium)]
    [InlineData(0x2100, IntegrityLevel.MediumPlus)]
    [InlineData(0x2FFF, IntegrityLevel.MediumPlus)]
    [InlineData(0x3000, IntegrityLevel.High)]
    [InlineData(0x4000, IntegrityLevel.System)]
    [InlineData(0x5000, IntegrityLevel.System)]
    public void LevelFromRid_UsesTheWellKnownBoundaries(int rid, IntegrityLevel expected)
    {
        ProcessIntegrity.LevelFromRid(rid).Should().Be(expected);
    }
}
