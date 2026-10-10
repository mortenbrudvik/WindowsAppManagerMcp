using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The user's control over the server's input, and the one way input reaches SendInput (roadmap M1.10).
/// Every batch the server injects (the input tools and EnsureForeground's activation input) goes through
/// <see cref="Send"/>, which refuses it while the user has paused input and keeps track of the keys and
/// buttons it leaves down, so they can be released again: right away on a short SendInput count or an
/// exception, at the end of every input call, on the pause, and when the server stops.
/// </summary>
/// <remarks>
/// <see cref="Send"/>, <see cref="SetCursorPos"/>, the releases and <see cref="Pause"/> share one lock, so
/// once <see cref="Pause"/> has returned no further batch is sent until input is resumed. Releases are
/// never refused: they only send key-ups and button-ups for what the server itself pressed.
/// </remarks>
public interface IInputSession
{
    /// <summary>True while the user has paused input.</summary>
    bool IsPaused { get; }

    /// <summary>Whether input is paused, how and since when, and the last resume.</summary>
    InputPauseState PauseState { get; }

    /// <summary>
    /// Cancelled when the user pauses input. A call links it with its request's token, so a pause stops it
    /// between batches; after a resume a new token is handed out.
    /// </summary>
    CancellationToken PauseToken { get; }

    /// <summary>The pause hotkey's chord, such as "Ctrl+Alt+Shift+Pause"; null when it is turned off.</summary>
    string? PauseChord { get; }

    /// <summary>
    /// Pauses input: later batches are refused, running calls are cancelled through <see cref="PauseToken"/>,
    /// and the keys and buttons the server holds are released. False when it was already paused.
    /// </summary>
    bool Pause(string source);

    /// <summary>Resumes input. False when it was not paused.</summary>
    bool Resume(string source);

    /// <summary>The pause hotkey's action: pauses running input, resumes paused input.</summary>
    void TogglePauseByHotkey();

    /// <summary>
    /// The pause hotkey's chord if <paramref name="events"/> press it (the same key with at least its
    /// modifiers held), otherwise null. send_keys refuses such keys: the agent must not pause or resume itself.
    /// </summary>
    string? FindPauseChord(IReadOnlyList<InputEvent> events);

    /// <summary>
    /// Sends one batch with SendInput, unless input is paused. Records which keys and buttons the inserted
    /// events leave down. When SendInput inserts fewer events than asked, releases what the server holds;
    /// when it throws, releases the keys and buttons of this batch that Windows reports down, and rethrows.
    /// </summary>
    InputSendOutcome Send(IReadOnlyList<InputEvent> events);

    /// <summary>Moves the cursor (SetCursorPos), unless input is paused: then null.</summary>
    bool? SetCursorPos(int x, int y);

    /// <summary>The keys, characters and buttons the server sent down and has not released yet.</summary>
    IReadOnlyList<HeldInput> ServerHeld { get; }

    /// <summary>
    /// The modifier keys (left and right Shift, Ctrl, Alt and Windows) that Windows reports down now. The
    /// user may be holding them, or they are stuck; Windows does not tell which.
    /// </summary>
    IReadOnlyList<HeldInput> ReadModifiersDown();

    /// <summary>
    /// Sends the up event of every key, character and button the server holds (with a masking key first when
    /// Alt or Windows is among them, so no menu opens). Runs while paused too. Sends nothing when nothing is
    /// held.
    /// </summary>
    InputReleaseOutcome ReleaseHeldInput(string reason);

    /// <summary>
    /// Sends the up event of <paramref name="keys"/>, modifier keys that are down although the server did not
    /// press them (the user's, or stuck): only when the agent asked for it with releaseHeldModifiers. Refused
    /// while input is paused.
    /// </summary>
    InputReleaseOutcome ReleaseModifiers(IReadOnlyList<HeldInput> keys, string reason);

    /// <summary>The last release that sent something; null if none.</summary>
    InputReleaseRecord? LastRelease { get; }
}
