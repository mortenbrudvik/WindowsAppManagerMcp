using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service interface for simulating mouse input.
/// </summary>
public interface IInputSimulationService
{
    /// <summary>
    /// Performs a left mouse click at the specified screen coordinates.
    /// </summary>
    ClickResult Click(int x, int y);

    /// <summary>
    /// Performs a right mouse click at the specified screen coordinates.
    /// </summary>
    ClickResult RightClick(int x, int y);

    /// <summary>
    /// Performs a double left click at the specified screen coordinates.
    /// </summary>
    ClickResult DoubleClick(int x, int y, int delayMs = 50);

    /// <summary>
    /// Moves the mouse cursor to the specified screen coordinates without clicking.
    /// </summary>
    MouseMoveResult MoveMouse(int x, int y);

    /// <summary>
    /// Gets the current cursor position.
    /// </summary>
    (int X, int Y)? GetCursorPosition();

    /// <summary>
    /// Checks if the given screen coordinates are within the bounds of any connected monitor.
    /// </summary>
    bool IsValidScreenCoordinate(int x, int y);

    /// <summary>
    /// Types text by simulating keyboard input using Unicode characters.
    /// </summary>
    /// <param name="text">The text to type.</param>
    /// <param name="delayBetweenKeysMs">Optional delay between keystrokes in milliseconds (0-100).</param>
    TypeTextResult TypeText(string text, int delayBetweenKeysMs = 0);

    /// <summary>
    /// Sends key combinations using SendKeys-style syntax.
    /// Supports special keys like {ENTER}, {TAB}, {F1}-{F12}, and modifiers ^ (Ctrl), % (Alt), + (Shift).
    /// </summary>
    /// <param name="keys">The keys to send in SendKeys format.</param>
    SendKeysResult SendKeys(string keys);
}
