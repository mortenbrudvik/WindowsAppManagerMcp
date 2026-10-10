using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The user's pause hotkey (roadmap M1.10): a system-wide hotkey on a dedicated message-loop thread. The
/// server starts it once, when it starts (<c>InputSessionStartup</c>); pressing the chord calls the action it
/// was started with, which pauses or resumes the server's input.
/// </summary>
public interface IPauseHotkey
{
    /// <summary>The chord and whether it is registered, or why it is not.</summary>
    PauseHotkeyStatus Status { get; }

    /// <summary>
    /// Starts the hotkey thread and registers the chord on it. Returns once the registration has succeeded or
    /// failed (<see cref="Status"/>). Does nothing when the hotkey is turned off or already started.
    /// </summary>
    /// <param name="pressed">Runs on the hotkey thread each time the user presses the chord.</param>
    void Start(Action pressed);

    /// <summary>Frees the hotkey and ends its thread.</summary>
    void Stop();
}
