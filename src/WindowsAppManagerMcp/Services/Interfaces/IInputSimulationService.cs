using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service interface for simulating mouse and keyboard input. Every call passes the input guard
/// (<see cref="IInputTargetGuard"/>) before each SendInput batch, with or without a target window, and its
/// result reports the receiving window and the foreground window afterwards (mouse calls also the cursor
/// position).
/// </summary>
public interface IInputSimulationService
{
    /// <summary>
    /// Performs a left mouse click at the specified screen coordinates. With a target, the point must be on
    /// the target or a popup of it.
    /// </summary>
    ClickResult Click(int x, int y, InputTarget target);

    /// <summary>
    /// Performs a right mouse click at the specified screen coordinates.
    /// </summary>
    ClickResult RightClick(int x, int y, InputTarget target);

    /// <summary>
    /// Performs a double left click at the specified screen coordinates. The window at the point is
    /// checked again before the second click.
    /// </summary>
    ClickResult DoubleClick(int x, int y, int delayMs, InputTarget target);

    /// <summary>
    /// Moves the mouse cursor to the specified screen coordinates without clicking.
    /// </summary>
    MouseMoveResult MoveMouse(int x, int y, InputTarget target);

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
    /// stops (TargetLostFocus, with CharactersTyped) when another window comes to the front.
    /// </summary>
    /// <param name="text">The text to type.</param>
    /// <param name="delayBetweenKeysMs">Delay between keystrokes in milliseconds (0-100).</param>
    /// <param name="target">The target window, or <see cref="InputTarget.None"/>.</param>
    TypeTextResult TypeText(string text, int delayBetweenKeysMs, InputTarget target);

    /// <summary>
    /// Sends key combinations using SendKeys-style syntax.
    /// Supports special keys like {ENTER}, {TAB}, {F1}-{F12}, and modifiers ^ (Ctrl), % (Alt), + (Shift).
    /// Destructive chords need a target (DestructiveChordNeedsTarget).
    /// </summary>
    /// <param name="keys">The keys to send in SendKeys format.</param>
    /// <param name="target">The target window, or <see cref="InputTarget.None"/>.</param>
    SendKeysResult SendKeys(string keys, InputTarget target);
}
