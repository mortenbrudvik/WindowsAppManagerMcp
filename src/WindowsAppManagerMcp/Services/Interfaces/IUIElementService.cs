using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Service for retrieving UI element trees from windows using Windows UI Automation.
/// </summary>
public interface IUIElementService
{
    /// <summary>
    /// Reads the UI element tree of a window. The walk runs on a UI Automation worker thread; the caller waits
    /// asynchronously.
    /// </summary>
    /// <param name="windowHandle">The window handle to get elements from.</param>
    /// <param name="maxDepth">Maximum depth to traverse (1-10, default 5).</param>
    /// <param name="filter">Optional filter to apply to elements.</param>
    /// <param name="progress">
    /// Receives the number of elements read every <see cref="UIElementService.ProgressInterval"/> elements, so a
    /// long walk shows that it is alive. Optional.
    /// </param>
    /// <param name="cancellationToken">
    /// Ends the walk: the elements read so far are returned with truncated=true and truncationReason Timeout
    /// (OperationTimedOut when none matched yet). The tools cancel it at their timeoutMs or when the client cancels the
    /// call, and then report the cancellation instead of the result (see <see cref="Tools.UIElementTools"/>).
    /// </param>
    /// <returns>The result containing the UI element tree.</returns>
    Task<UIElementResult> GetUIElementsAsync(
        nint windowHandle,
        int maxDepth = 5,
        UIElementFilter? filter = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if UI Automation is available on this system.
    /// </summary>
    /// <returns>True if UI Automation is available.</returns>
    bool IsAvailable();
}
