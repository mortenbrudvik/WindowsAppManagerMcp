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
}
