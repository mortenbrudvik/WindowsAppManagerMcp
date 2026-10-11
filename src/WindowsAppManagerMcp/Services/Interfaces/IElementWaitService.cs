using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// wait_for_element (roadmap M3.8): waits on the server until an element condition holds, instead of sleeps and repeated
/// tree reads after a launch, a click or a load.
/// </summary>
public interface IElementWaitService
{
    /// <summary>
    /// Waits, at most <see cref="ElementWaitRequest.TimeoutMs"/> (clamped to 60 seconds), until the condition holds.
    /// With criteria the window is waited for first (<see cref="IWindowWaitService.WaitForConditionAsync"/>, condition
    /// exists), then each check runs find_ui_elements' search (<see cref="IElementSearchService"/>) and tests its matches; a
    /// ref is resolved through the element registry (<see cref="IElementRegistry"/>) on each check. Checks back off from
    /// 100 to 500 ms. Only reads.
    /// </summary>
    /// <remarks>
    /// The wait ends early, unsatisfied, when the condition can no longer be met: the window closed, a ref (or withinRef)
    /// that is stale or ambiguous (a stop signal, never a hint to follow a look-alike), a control type or name pattern that
    /// cannot be used, or value_equals on a password field, whose value is never read. A check that could not tell (the
    /// window did not respond, UI Automation did not answer in time, a search that did not cover the window) does not end
    /// it. Progress (elapsed of total ms) is reported about once a second.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<WaitForElementResult> WaitAsync(
        ElementWaitRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
