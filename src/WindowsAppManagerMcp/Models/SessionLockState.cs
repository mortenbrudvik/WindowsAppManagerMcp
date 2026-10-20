using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Whether the server's session is locked, as get_desktop_state and get_foreground_window report it (<c>sessionLock</c>,
/// issue #18), read from the WTS session flags. Always written, never left out: a state that cannot be read is
/// <see cref="Unknown"/>, not unlocked, so an agent does not take an unread state for an unlocked desktop.
/// </summary>
/// <remarks>
/// It says nothing about which window is in front: while the session is locked there may be no window in front, or the lock
/// screen app (LockApp) may be.
/// </remarks>
public enum SessionLockState
{
    /// <summary>WTS_SESSIONSTATE_UNKNOWN, or the session's state could not be read. The default, so a value never read is not a guess.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    /// <summary>WTS_SESSIONSTATE_LOCK: the session is locked; input tools return SessionLocked.</summary>
    [JsonStringEnumMemberName("locked")]
    Locked,

    /// <summary>WTS_SESSIONSTATE_UNLOCK: the session is not locked.</summary>
    [JsonStringEnumMemberName("unlocked")]
    Unlocked
}
