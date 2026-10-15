using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The target policy's check for window captures (see <see cref="ICaptureTargetPolicy"/>), moved out of ScreenshotTools so
/// that the capture tools and the pixel reads of <see cref="IScreenshotService.CapturePixels"/> share one check and one message.
/// </summary>
public sealed class CaptureTargetPolicy : ICaptureTargetPolicy
{
    private readonly IWindowService _windows;
    private readonly ITargetPolicy _policy;

    /// <param name="windows">Reads the window's process.</param>
    /// <param name="policy">The user's target policy.</param>
    public CaptureTargetPolicy(IWindowService windows, ITargetPolicy policy)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <inheritdoc />
    public CaptureRefusal? Check(nint windowHandle)
    {
        var window = _windows.GetWindowInfo(windowHandle);
        if (window == null && !_windows.IsValidWindow(windowHandle))
        {
            return null;
        }

        return Verdict(window) is { Allowed: false } verdict
            ? new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, Message(verdict))
            : null;
    }

    /// <inheritdoc />
    public TargetPolicyVerdict Verdict(WindowInfo? window) =>
        window != null ? _policy.EvaluateFailClosed(window)
        : _policy.DeniesAnything() ? TargetPolicyVerdict.Unreadable
        : TargetPolicyVerdict.Allow;

    /// <summary>The TargetDeniedByPolicy message of a refused window capture.</summary>
    public static string Message(TargetPolicyVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return $"The window is refused by the server's target policy: {verdict.Reason}. Nothing was captured. The policy is set by " +
               "the user in the server's environment; no tool argument changes it.";
    }
}
