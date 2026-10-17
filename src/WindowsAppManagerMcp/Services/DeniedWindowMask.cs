using System.Drawing;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The windows the user's target policy denies over a screen area (owner decision D8): a read of a region or monitor shows
/// whatever is on the screen there, so a reader that is not a capture tool leaves these rectangles out instead of reporting
/// what they show. Top-level windows on the screen only (visible, not minimized, not cloaked), each by its whole window
/// rectangle; windows that cover a denied one lose that part too, which errs on the side of reporting less. One mask
/// remembers its verdicts by window and process, so a wait that reads the area again and again asks the policy once per
/// window. Not thread-safe: one wait uses one mask.
/// </summary>
public sealed class DeniedWindowMask
{
    private readonly INativeWindowWrapper _windows;
    private readonly ICaptureTargetPolicy _capturePolicy;
    private readonly bool _deniesAnything;
    private readonly Dictionary<(nint Handle, uint ProcessId), bool> _denied = [];

    /// <param name="windows">Lists the windows and reads their state and rectangles.</param>
    /// <param name="capturePolicy">The policy's check of a window capture, which fails closed as capture_window's does.</param>
    /// <param name="policy">The policy itself: one that denies nothing masks nothing, without listing a window. Null: every
    /// window is checked.</param>
    public DeniedWindowMask(INativeWindowWrapper windows, ICaptureTargetPolicy capturePolicy, ITargetPolicy? policy)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _capturePolicy = capturePolicy ?? throw new ArgumentNullException(nameof(capturePolicy));
        _deniesAnything = policy?.DeniesAnything() ?? true;
    }

    /// <summary>The denied windows over <paramref name="area"/> now, front to back, with their rectangles clipped to it.</summary>
    public IReadOnlyList<MaskedWindow> Over(Rectangle area)
    {
        if (!_deniesAnything)
        {
            return [];
        }

        var masked = new List<MaskedWindow>();
        _windows.EnumWindows(window =>
        {
            if (_windows.IsWindowVisible(window) && !_windows.IsIconic(window) && !_windows.IsCloaked(window) &&
                _windows.GetWindowRect(window) is { } rect)
            {
                var bounds = Rectangle.Intersect(area, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
                if (bounds.Width > 0 && bounds.Height > 0 && IsDenied(window))
                {
                    masked.Add(new MaskedWindow((long)window, bounds.X, bounds.Y, bounds.Width, bounds.Height));
                }
            }

            return true;
        });

        return masked;
    }

    private bool IsDenied(nint window)
    {
        var key = (window, _windows.GetWindowThreadProcessId(window));
        if (!_denied.TryGetValue(key, out var denied))
        {
            denied = _capturePolicy.Check(window) != null;
            _denied[key] = denied;
        }

        return denied;
    }
}
