namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The native queries behind <see cref="IProcessSecurityProbe"/>: process tokens (integrity level
/// and elevation), the input desktop and the state of the server's session. Every member only
/// reads and never throws; nothing here changes a process, token, desktop or session.
/// </summary>
public interface INativeSecurityWrapper
{
    /// <summary>
    /// Returns the creation time (UTC) of a running process, or null when there is no such process
    /// or it cannot be opened for PROCESS_QUERY_LIMITED_INFORMATION.
    /// </summary>
    DateTime? GetProcessCreationTime(int processId);

    /// <summary>
    /// Opens the process for PROCESS_QUERY_LIMITED_INFORMATION and its token for TOKEN_QUERY, and reads
    /// the creation time, the mandatory integrity RID (TokenIntegrityLevel) and TokenElevation.
    /// </summary>
    ProcessTokenQuery QueryProcessToken(int processId);

    /// <summary>
    /// Opens the input desktop (the desktop that receives keyboard and mouse input) for
    /// DESKTOP_READOBJECTS, reads its name and closes it again.
    /// </summary>
    InputDesktopQuery QueryInputDesktop();

    /// <summary>
    /// Returns the name of the desktop the calling thread is on (normally "Default"), or null when it
    /// cannot be read.
    /// </summary>
    string? GetThreadDesktopName();

    /// <summary>
    /// Reads WTSSessionInfoEx for the server's own session. Returns null when it cannot be read.
    /// </summary>
    SessionStateQuery? QuerySessionState();

    /// <summary>
    /// Reads the User Account Control policy (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System: EnableLUA and
    /// ConsentPromptBehaviorAdmin, roadmap M6.5). Returns null when the key cannot be read; a value that is missing or
    /// not a number is null.
    /// </summary>
    UacSettings? ReadUacSettings();
}

/// <summary>
/// The User Account Control values that decide whether Windows asks before it elevates a program: EnableLUA (0 turns UAC
/// off) and ConsentPromptBehaviorAdmin (0 elevates an administrator without asking).
/// </summary>
public sealed record UacSettings(int? EnableLua, int? ConsentPromptBehaviorAdmin);

/// <summary>How far <see cref="INativeSecurityWrapper.QueryProcessToken"/> got.</summary>
public enum TokenQueryStatus
{
    /// <summary>The token was read; the integrity RID is set.</summary>
    Succeeded,

    /// <summary>No process with this ID is running (OpenProcess failed with ERROR_INVALID_PARAMETER).</summary>
    ProcessNotFound,

    /// <summary>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) failed with ERROR_ACCESS_DENIED.</summary>
    ProcessAccessDenied,

    /// <summary>The process was opened, but OpenProcessToken(TOKEN_QUERY) failed with ERROR_ACCESS_DENIED.</summary>
    TokenAccessDenied,

    /// <summary>Any other failure; see the Win32 error.</summary>
    Failed
}

/// <summary>
/// The result of <see cref="INativeSecurityWrapper.QueryProcessToken"/>.
/// </summary>
/// <param name="Status">How far the query got.</param>
/// <param name="CreationTime">The process's creation time (UTC), when the process could be opened.</param>
/// <param name="IntegrityRid">
/// The last sub-authority of the token's mandatory label SID, for example 0x2000 for medium and
/// 0x3000 for high integrity; null when it was not read.
/// </param>
/// <param name="IsElevated">TokenElevation; null when it was not read.</param>
/// <param name="Win32Error">The Win32 error of the call that failed, or 0.</param>
public sealed record ProcessTokenQuery(
    TokenQueryStatus Status,
    DateTime? CreationTime = null,
    int? IntegrityRid = null,
    bool? IsElevated = null,
    int Win32Error = 0);

/// <summary>
/// The result of <see cref="INativeSecurityWrapper.QueryInputDesktop"/>.
/// </summary>
/// <param name="Opened">True when OpenInputDesktop returned a handle.</param>
/// <param name="Name">The input desktop's name ("Default", "Winlogon", ...), when it could be read.</param>
/// <param name="Win32Error">The Win32 error of OpenInputDesktop when it failed (5 on the secure desktop), or 0.</param>
public sealed record InputDesktopQuery(bool Opened, string? Name, int Win32Error);

/// <summary>
/// The parts of WTSINFOEX_LEVEL1 the probe uses.
/// </summary>
/// <param name="SessionId">The session ID the information is for.</param>
/// <param name="ConnectState">WTS_CONNECTSTATE_CLASS: 0 is WTSActive, 4 is WTSDisconnected.</param>
/// <param name="SessionFlags">WTS_SESSIONSTATE_LOCK (0), WTS_SESSIONSTATE_UNLOCK (1) or WTS_SESSIONSTATE_UNKNOWN (-1).</param>
public sealed record SessionStateQuery(int SessionId, int ConnectState, int SessionFlags);
