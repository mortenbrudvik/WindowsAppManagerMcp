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
    /// The process could be opened but its token could not (access denied), while the server itself
    /// runs below high integrity: the token keeps a lower-integrity caller out, which points to an
    /// elevated process. Treated as more privileged than the server.
    /// </summary>
    /// <remarks>
    /// Not every elevated process shows up this way. On Windows 11 a medium-integrity process can read
    /// the token of a same-user high-integrity process started by the Task Scheduler, so that process
    /// is reported as <see cref="High"/> directly.
    /// </remarks>
    LikelyElevated,

    /// <summary>
    /// Access was denied and nothing points to elevation: the process itself cannot be opened, or the
    /// server cannot compare (it is elevated, or its own level is unknown). OpenProcess for
    /// PROCESS_QUERY_LIMITED_INFORMATION fails when the process runs under another account (SYSTEM, a
    /// service account, DWM, or another user in the same session through "Run as different user") or
    /// has a hardened DACL (some anti-cheat, DRM and security software). Protected processes allow this
    /// access, so a denial is about the DACL, not about protection. The level is unknown: UIPI blocks
    /// input to such a process only when it runs above the server (a SYSTEM process with a window on the
    /// user's desktop), not to another user's ordinary process.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessIntegrity.IsMorePrivilegedThan"/> is false for it, so a guard does not refuse
    /// input on this alone; input that has no effect can name it as a possible cause.
    /// </remarks>
    Restricted
}

/// <summary>
/// The integrity level and elevation of a process, as read by <c>IProcessSecurityProbe</c>.
/// </summary>
/// <param name="ProcessId">The process.</param>
/// <param name="Level">The integrity level.</param>
/// <param name="IntegrityRid">The raw integrity RID (0x2000 is medium), or null when it was not read.</param>
/// <param name="IsElevated">Whether the token is elevated (TokenElevation), or null when it was not read.</param>
/// <param name="Reason">Why the level is <see cref="IntegrityLevel.Unknown"/>, <see cref="IntegrityLevel.LikelyElevated"/> or
/// <see cref="IntegrityLevel.Restricted"/>; null otherwise.</param>
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
    /// level is unknown, including <see cref="IntegrityLevel.Restricted"/>.
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
