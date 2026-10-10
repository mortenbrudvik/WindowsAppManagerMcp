using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service interface for simulating mouse and keyboard input. Every call passes the input guard
/// (<see cref="IInputTargetGuard"/>) before each SendInput batch, with or without a target window, and its
/// result reports the receiving window and the foreground window afterwards (mouse calls also the cursor
/// position).
/// </summary>
/// <remarks>
/// Session safety (roadmap M1.10), for every call: while the user has paused input it returns PausedByUser
/// and sends nothing, and a pause stops a running call between batches; it waits at most
/// <see cref="Services.InputGate.InputWaitTimeout"/> for the input gate (InputBusy) and stops when
/// <c>cancellationToken</c> is cancelled (the MCP request's token: OperationCanceledException); it returns
/// ModifiersHeld when Shift, Ctrl, Alt or Windows is down, unless <c>releaseHeldModifiers</c> releases them
/// first; and no key or button it presses stays down after it returns.
/// </remarks>
public interface IInputSimulationService
{
    /// <summary>
    /// Performs a left mouse click at the specified screen coordinates. With a target, the point must be on
    /// the target or a popup of it.
    /// </summary>
    ClickResult Click(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Performs a right mouse click at the specified screen coordinates.
    /// </summary>
    ClickResult RightClick(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Performs a double left click at the specified screen coordinates. The window at the point is
    /// checked again before the second click.
    /// </summary>
    ClickResult DoubleClick(int x, int y, int delayMs, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the mouse cursor to the specified screen coordinates without clicking.
    /// </summary>
    MouseMoveResult MoveMouse(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current cursor position.
    /// </summary>
    (int X, int Y)? GetCursorPosition();

    /// <summary>
    /// Checks if the given screen coordinates are within the bounds of any connected monitor.
    /// </summary>
    bool IsValidScreenCoordinate(int x, int y);

    /// <summary>
    /// Types text by simulating keyboard input using Unicode characters. With a target, brings it to the
    /// front first; the foreground window is checked again before every batch of characters, and typing
    /// stops (TargetLostFocus, with CharactersTyped) when another window comes to the front, and
    /// (ModifiersHeld) when a modifier key goes down.
    /// </summary>
    /// <param name="text">The text to type.</param>
    /// <param name="delayBetweenKeysMs">Delay between keystrokes in milliseconds (0-100).</param>
    /// <param name="target">The target window, or <see cref="InputTarget.None"/>.</param>
    /// <param name="releaseHeldModifiers">Release modifier keys that are down before typing, instead of returning ModifiersHeld.</param>
    /// <param name="cancellationToken">The request's token: cancelling it stops typing between batches.</param>
    Task<TypeTextResult> TypeTextAsync(string text, int delayBetweenKeysMs, InputTarget target, bool releaseHeldModifiers,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends key combinations using SendKeys-style syntax.
    /// Supports special keys like {ENTER}, {TAB}, {F1}-{F12}, and modifiers ^ (Ctrl), % (Alt), + (Shift).
    /// Destructive chords need a target (DestructiveChordNeedsTarget); the user's pause hotkey chord is
    /// refused (PauseChordNotAllowed).
    /// </summary>
    /// <param name="keys">The keys to send in SendKeys format.</param>
    /// <param name="target">The target window, or <see cref="InputTarget.None"/>.</param>
    /// <param name="releaseHeldModifiers">Release modifier keys that are down first, instead of returning ModifiersHeld.</param>
    /// <param name="cancellationToken">The request's token.</param>
    Task<SendKeysResult> SendKeysAsync(string keys, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);
}
