using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Answers the questions that decide whether input and UI Automation can reach a target at all:
/// is the target more privileged than the server (UIPI drops input to it without an error), can the
/// input desktop be reached, and is the session locked. Windows reports none of these failures
/// through SendInput, so callers check before they act.
/// </summary>
/// <remarks>
/// Typical use before sending input to a window's process, most specific failure first:
/// <code>
/// if (probe.IsSessionLocked()) -> SessionLocked
/// if (!probe.IsInputDesktopAvailable()) -> DesktopUnavailable
/// if (probe.GetIntegrity(pid).IsMorePrivilegedThan(probe.ServerIntegrity)) -> TargetElevated
/// </code>
/// </remarks>
public interface IProcessSecurityProbe
{
    /// <summary>The integrity level and elevation of the server's own process, read once.</summary>
    ProcessIntegrity ServerIntegrity { get; }

    /// <summary>
    /// Returns the integrity level and elevation of a process. Results are cached per process ID and
    /// creation time, so a reused process ID is read again. When the token cannot be opened because
    /// access is denied and the server runs below high integrity, the level is
    /// <see cref="IntegrityLevel.LikelyElevated"/>. Never throws.
    /// </summary>
    ProcessIntegrity GetIntegrity(int processId);

    /// <summary>
    /// True when keyboard and mouse input from the server can reach the desktop: the input desktop can
    /// be opened, it is the desktop the server runs on, and the session is not disconnected. False on
    /// the secure desktop (UAC prompt, lock screen, Ctrl+Alt+Del) and in a disconnected session.
    /// Read live on every call.
    /// </summary>
    bool IsInputDesktopAvailable();

    /// <summary>
    /// True when the server's session is locked. False when it is unlocked or the state cannot be
    /// read. Read live on every call.
    /// </summary>
    bool IsSessionLocked();
}
