namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The user's pause as one flag that several servers can share (roadmap M1.10). Every MCP client session
/// starts its own server process, and only one of them can register the pause hotkey's chord, so the pause
/// is kept in a flag that every server on the desktop opens (a named event, see
/// <see cref="INativeHotkeyWrapper.OpenDesktopPause"/>): the hotkey sets and resets it, and every server's
/// input session follows it. A session that does not share its pause (before the server starts, and in tests)
/// keeps it in its own state alone.
/// </summary>
public interface IDesktopPauseFlag
{
    /// <summary>True while input is paused.</summary>
    bool IsSet { get; }

    /// <summary>Pauses input for every server that shares the flag.</summary>
    void Set();

    /// <summary>Resumes input for every server that shares the flag.</summary>
    void Reset();

    /// <summary>Signaled while the flag is set, so a server can wait for a pause made by another server.</summary>
    WaitHandle WaitHandle { get; }
}
