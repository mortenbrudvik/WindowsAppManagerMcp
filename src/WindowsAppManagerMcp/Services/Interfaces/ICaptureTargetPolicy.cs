using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The target policy's check for reading a window's pixels (roadmap M1.12): capture_window, capture_with_elements and
/// <see cref="IScreenshotService.CapturePixels"/> ask it before any pixel of a window is read, so that every way of
/// reading a window's pixels refuses a window the user's policy denies with TargetDeniedByPolicy (roadmap M6: OCR and
/// visual change detection read pixels without the capture tools). It fails closed as the UI Automation reads do.
/// </summary>
public interface ICaptureTargetPolicy
{
    /// <summary>
    /// TargetDeniedByPolicy when the policy refuses capturing <paramref name="windowHandle"/>, or null. A handle that is
    /// no window is left to the capture, which reports InvalidHandle.
    /// </summary>
    CaptureRefusal? Check(nint windowHandle);

    /// <summary>
    /// The verdict on capturing a window already read: by its process, failing closed. A window whose process cannot be
    /// named, or that could not be read at all (<paramref name="window"/> null), is refused while the policy denies anything.
    /// </summary>
    TargetPolicyVerdict Verdict(WindowInfo? window);
}
