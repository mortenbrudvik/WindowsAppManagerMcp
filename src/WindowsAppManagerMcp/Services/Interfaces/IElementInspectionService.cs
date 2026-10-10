using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Reads single UI elements: the one with keyboard focus, and the one at a screen point. These are
/// the cheap checks before and after input ("is the caret in the Email field?", "what is under this
/// point of the screenshot?"). Every read runs under a timeout, because a hung application or
/// provider can block UI Automation indefinitely.
/// </summary>
public interface IElementInspectionService
{
    /// <summary>The element that has keyboard focus, with its top-level window and process.</summary>
    /// <param name="timeout">How long to wait for UI Automation.</param>
    /// <param name="cancellationToken">Cancels the call (for example when the client cancels the request).</param>
    FocusedElementResult GetFocusedElement(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>The element at a point, a few of its ancestors, and the top-level window at the point.</summary>
    /// <param name="x">X in physical virtual-screen pixels, the same space as click coordinates.</param>
    /// <param name="y">Y in physical virtual-screen pixels.</param>
    /// <param name="ancestors">How many ancestors to return, nearest first (clamped to 0-10).</param>
    /// <param name="timeout">How long to wait for UI Automation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ElementAtPointResult GetElementAtPoint(
        int x,
        int y,
        int ancestors,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
