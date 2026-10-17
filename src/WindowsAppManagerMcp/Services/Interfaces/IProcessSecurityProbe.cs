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
    /// creation time, so a reused process ID is read again. When the process can be opened but its
    /// token cannot (access denied) and the server runs below high integrity, the level is
    /// <see cref="IntegrityLevel.LikelyElevated"/>; when the process itself cannot be opened, it is
    /// <see cref="IntegrityLevel.Restricted"/>, which does not count as more privileged (it may be
    /// SYSTEM, but also another user's ordinary process). Never throws.
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

    /// <summary>
    /// Whether Windows asks the user before it elevates a program started as administrator (roadmap M6.5):
    /// <see cref="ElevationPrompt.Silent"/> when User Account Control is off (EnableLUA 0) or elevates administrators
    /// without asking (ConsentPromptBehaviorAdmin 0), <see cref="ElevationPrompt.Unknown"/> when the policy cannot be
    /// read. A missing value counts as Windows' default, which asks. Read live on every call.
    /// </summary>
    ElevationPrompt GetElevationPrompt();
}

/// <summary>Whether Windows shows a UAC prompt before it elevates a program (see <see cref="IProcessSecurityProbe.GetElevationPrompt"/>).</summary>
public enum ElevationPrompt
{
    /// <summary>Windows asks: the user approves or declines the elevation.</summary>
    Prompts,

    /// <summary>Windows elevates without asking anyone.</summary>
    Silent,

    /// <summary>The policy could not be read.</summary>
    Unknown
}
