using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The session's state as pixels are read (issues #17 and #18), decided in one place for the captures and wait_for_visual,
/// so that their warnings never disagree. Each says it in its own words; neither refuses for it, as input is refused.
/// </summary>
public static class CaptureSession
{
    /// <summary>
    /// The warning code for the session now: <see cref="CaptureWarnings.SessionLocked"/> when it is locked, else
    /// <see cref="CaptureWarnings.DesktopUnavailable"/> when the input desktop cannot be reached; null otherwise, and without
    /// the probe. A lock state that cannot be read does not warn.
    /// </summary>
    public static string? WarningCode(IProcessSecurityProbe? security)
    {
        if (security == null)
        {
            return null;
        }

        if (security.GetSessionLock() == SessionLockState.Locked)
        {
            return CaptureWarnings.SessionLocked;
        }

        return security.IsInputDesktopAvailable() ? null : CaptureWarnings.DesktopUnavailable;
    }
}
