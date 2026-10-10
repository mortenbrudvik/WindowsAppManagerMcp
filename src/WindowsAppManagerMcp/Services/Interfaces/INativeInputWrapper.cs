using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Abstraction over native Windows API calls for mouse and keyboard input.
/// This interface enables unit testing without sending real input to the desktop.
/// </summary>
public interface INativeInputWrapper
{
    /// <summary>
    /// Moves the cursor to the specified screen coordinates.
    /// </summary>
    bool SetCursorPos(int x, int y);

    /// <summary>
    /// Gets the current cursor position, or null if it cannot be retrieved.
    /// </summary>
    (int X, int Y)? GetCursorPos();

    /// <summary>
    /// Sends the events to the input stream in a single SendInput call.
    /// Returns the number of events that were successfully inserted.
    /// </summary>
    uint SendInput(IReadOnlyList<InputEvent> events);

    /// <summary>
    /// True when the key or mouse button is down now (GetAsyncKeyState). Windows keeps one key state for the
    /// session: a key the user holds, a key another program injected and a key this server injected all look
    /// the same. False when the state cannot be read, for example while a UAC prompt has the input.
    /// </summary>
    bool IsKeyDown(ushort virtualKey);
}
