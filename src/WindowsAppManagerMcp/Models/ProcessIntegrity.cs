namespace WindowsAppManagerMcp.Models;

/// <summary>
/// The mandatory integrity level of a process, from the mandatory label of its token. Windows'
/// User Interface Privilege Isolation (UIPI) silently drops input and UI Automation from a process
/// to windows of a process with a higher integrity level.
/// </summary>
public enum IntegrityLevel
{
    /// <summary>The level could not be determined (no such process, or the token could not be read).</summary>
    Unknown = 0,

    /// <summary>Untrusted (below 0x1000).</summary>
    Untrusted,

    /// <summary>Low (0x1000), for example sandboxed browser processes.</summary>
    Low,

    /// <summary>Medium (0x2000): ordinary, non-elevated applications.</summary>
    Medium,

    /// <summary>Medium plus (0x2100 up to high).</summary>
    MediumPlus,

    /// <summary>High (0x3000): elevated ("Run as administrator") applications.</summary>
    High,

    /// <summary>System (0x4000 and above, including protected processes): services and system processes.</summary>
    System,

    /// <summary>
    /// The token could not be opened because access was denied, while the server itself runs below
    /// high integrity. The process is most likely elevated, a system process or a protected process,
    /// so treat it as more privileged than the server.
    /// </summary>
    LikelyElevated
}

/// <summary>
/// The integrity level and elevation of a process, as read by <c>IProcessSecurityProbe</c>.
/// </summary>
/// <param name="ProcessId">The process.</param>
/// <param name="Level">The integrity level.</param>
/// <param name="IntegrityRid">The raw integrity RID (0x2000 is medium), or null when it was not read.</param>
/// <param name="IsElevated">Whether the token is elevated (TokenElevation), or null when it was not read.</param>
/// <param name="Reason">Why the level is <see cref="IntegrityLevel.Unknown"/> or <see cref="IntegrityLevel.LikelyElevated"/>; null otherwise.</param>
public sealed record ProcessIntegrity(
    int ProcessId,
    IntegrityLevel Level,
    int? IntegrityRid,
    bool? IsElevated,
    string? Reason)
{
    /// <summary>SECURITY_MANDATORY_LOW_RID.</summary>
    public const int LowRid = 0x1000;

    /// <summary>SECURITY_MANDATORY_MEDIUM_RID.</summary>
    public const int MediumRid = 0x2000;

    /// <summary>SECURITY_MANDATORY_MEDIUM_PLUS_RID.</summary>
    public const int MediumPlusRid = 0x2100;

    /// <summary>SECURITY_MANDATORY_HIGH_RID.</summary>
    public const int HighRid = 0x3000;

    /// <summary>SECURITY_MANDATORY_SYSTEM_RID.</summary>
    public const int SystemRid = 0x4000;

    /// <summary>
    /// Maps an integrity RID to its level. RIDs between the well-known values belong to the level
    /// below them, as Windows compares raw RIDs.
    /// </summary>
    public static IntegrityLevel LevelFromRid(int rid) => rid switch
    {
        < LowRid => IntegrityLevel.Untrusted,
        < MediumRid => IntegrityLevel.Low,
        < MediumPlusRid => IntegrityLevel.Medium,
        < HighRid => IntegrityLevel.MediumPlus,
        < SystemRid => IntegrityLevel.High,
        _ => IntegrityLevel.System
    };

    /// <summary>
    /// True when this process is known or likely to have a higher integrity level than
    /// <paramref name="other"/>, so UIPI blocks input and UI Automation from <paramref name="other"/>
    /// to its windows. Compares raw RIDs, as Windows does. A <see cref="IntegrityLevel.LikelyElevated"/>
    /// process counts as higher than any process below high integrity. Returns false when either
    /// level is unknown.
    /// </summary>
    public bool IsMorePrivilegedThan(ProcessIntegrity other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Level == IntegrityLevel.LikelyElevated)
        {
            return other.IntegrityRid is < HighRid;
        }

        return IntegrityRid is { } rid && other.IntegrityRid is { } otherRid && rid > otherRid;
    }
}
