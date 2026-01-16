using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for retrieving UI element trees from windows using Windows UI Automation.
/// </summary>
public interface IUIElementService
{
    /// <summary>
    /// Gets the UI element tree for a window.
    /// </summary>
    /// <param name="windowHandle">The window handle to get elements from.</param>
    /// <param name="maxDepth">Maximum depth to traverse (1-10, default 5).</param>
    /// <param name="filter">Optional filter to apply to elements.</param>
    /// <param name="cancellationToken">Cancellation token for timeout.</param>
    /// <returns>The result containing the UI element tree.</returns>
    UIElementResult GetUIElements(
        nint windowHandle,
        int maxDepth = 5,
        UIElementFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if UI Automation is available on this system.
    /// </summary>
    /// <returns>True if UI Automation is available.</returns>
    bool IsAvailable();
}
