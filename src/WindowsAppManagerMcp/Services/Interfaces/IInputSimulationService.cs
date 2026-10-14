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
    /// Clicks the primary mouse button (the left one, or the right one when the user swapped the buttons) at the specified
    /// screen coordinates. With a target, the point must be on the target or a popup of it. The move to the point and the
    /// click go in one SendInput batch (roadmap M4.2); a pointer that is not at the point afterwards is CursorClamped, with
    /// the click sent.
    /// </summary>
    ClickResult Click(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Clicks the secondary mouse button (the right one, or the left one when the user swapped the buttons) at the specified
    /// screen coordinates, as <see cref="Click"/> does.
    /// </summary>
    ClickResult RightClick(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Double-clicks the primary mouse button at the specified screen coordinates, as <see cref="Click"/> does: both clicks
    /// go in the one batch with the move, so Windows counts them as one double click whatever its double-click time.
    /// </summary>
    ClickResult DoubleClick(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the mouse cursor to the specified screen coordinates without clicking, with an injected absolute move (roadmap
    /// M4.2); a pointer that is not at the point afterwards is CursorClamped.
    /// </summary>
    MouseMoveResult MoveMouse(int x, int y, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current cursor position.
    /// </summary>
    (int X, int Y)? GetCursorPosition();

    /// <summary>
    /// Turns the mouse wheel at a point (roadmap M2.15): moves the pointer there and sends one wheel event per notch, in
    /// one SendInput call. Without a point, at the centre of the target window's client area. The guard checks the point
    /// as for a click, and the wheel must reach the window at the point: when Windows sends the wheel to the focus window
    /// instead, the window at the point must be the foreground window (otherwise WheelFollowsFocus).
    /// </summary>
    /// <param name="x">The point's x, or null (with <paramref name="y"/>) for the target's client centre.</param>
    /// <param name="y">The point's y, or null.</param>
    /// <param name="deltaY">Vertical notches: positive scrolls down, negative up; at most <see cref="InputSimulationService.MaxScrollNotches"/> either way.</param>
    /// <param name="deltaX">Horizontal notches: positive scrolls right, negative left.</param>
    /// <param name="target">The target window; required when no point is given.</param>
    /// <param name="releaseHeldModifiers">Release modifier keys that are down first, instead of returning ModifiersHeld.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <exception cref="ArgumentException">Only one of x and y, no point and no target, or no notches or too many.</exception>
    ScrollResult Scroll(int? x, int? y, int deltaY, int deltaX, InputTarget target, bool releaseHeldModifiers, CancellationToken cancellationToken);

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
    /// <param name="controlChars">
    /// What the text's line breaks (a Windows line break and a lone carriage return count as a line feed) and tabs do;
    /// null refuses text that has any (InvalidArguments).
    /// </param>
    /// <param name="cancellationToken">The request's token: cancelling it stops typing between batches.</param>
    Task<TypeTextResult> TypeTextAsync(string text, int delayBetweenKeysMs, InputTarget target, bool releaseHeldModifiers,
        TextControlChars? controlChars, CancellationToken cancellationToken);

    /// <summary>
    /// Sends keys and key combinations in send_keys' grammar (<see cref="KeySpecParser"/>), every character mapped with the
    /// keyboard layout of the window they go to. Destructive chords need a target (DestructiveChordNeedsTarget); the user's
    /// pause hotkey chord is refused (PauseChordNotAllowed).
    /// </summary>
    /// <param name="keys">The keys to send.</param>
    /// <param name="target">The target window, or <see cref="InputTarget.None"/>.</param>
    /// <param name="releaseHeldModifiers">Release modifier keys that are down first, instead of returning ModifiersHeld.</param>
    /// <param name="delayMs">
    /// Milliseconds to wait between keys (0 to <see cref="InputSimulationService.MaxSendKeysDelayMs"/>); 0 sends them all in
    /// one SendInput call.
    /// </param>
    /// <param name="cancellationToken">The request's token: cancelling it stops the keys between batches.</param>
    Task<SendKeysResult> SendKeysAsync(string keys, InputTarget target, bool releaseHeldModifiers, int delayMs, CancellationToken cancellationToken);
}
