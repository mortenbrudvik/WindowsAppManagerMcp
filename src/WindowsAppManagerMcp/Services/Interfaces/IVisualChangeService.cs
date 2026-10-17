using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// wait_for_visual's waits (roadmap M6.4): reads a window, region or monitor again and again, compares each frame's visual
/// signature tile by tile, and returns when it changed or stopped changing, with where (rectangles, never pixels). Only
/// reads; runs outside the input gate.
/// </summary>
public interface IVisualChangeService
{
    /// <summary>
    /// Waits until <paramref name="request"/>'s condition holds or its timeout passes. A wait that is not satisfied returns
    /// Success false with its code (WaitTimedOut, or why the target cannot be watched); it throws only when cancelled.
    /// </summary>
    Task<VisualWaitResult> WaitAsync(
        VisualWaitRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
