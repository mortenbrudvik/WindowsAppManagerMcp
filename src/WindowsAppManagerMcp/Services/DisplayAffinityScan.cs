using System.Drawing;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The windows that declare a display affinity (SetWindowDisplayAffinity, issue #17), for the results whose pixels are copied
/// from the screen (excludedFromCapture): a window excluded from capture is on the screen but a copy of the screen leaves it
/// out and shows what is behind it, and one shown only on the monitor is black in the copy. Apps do this to toasts and
/// overlays they keep out of screen shares, so an agent that looks for one in an image would take it for not being there.
/// <para>
/// Read from the drawn top-level windows (<see cref="DrawnWindows"/>) whose affinity is set, whatever the policy denies:
/// unlike <see cref="DeniedWindowMask"/> it never stops early. Each is listed with its visible frame, also where other windows
/// cover it; <see cref="Within"/> cuts the frames to the captured area. A window the user's target policy denies, or whose
/// process it cannot check, is listed by handle, affinity and rectangle only, as maskedWindows lists it. The scan remembers
/// what it may say of each window by window and process, so a wait that reads again and again asks the policy once per window.
/// A window that closes during the read is gone. Not thread-safe: one capture or one wait uses one scan.
/// </para>
/// </summary>
public sealed class DisplayAffinityScan
{
    private readonly INativeWindowWrapper _windows;
    private readonly ICaptureTargetPolicy? _capturePolicy;
    private readonly bool _deniesAnything;
    private readonly Dictionary<(nint Handle, uint ProcessId), (string? ProcessName, string? Title)> _named = [];

    /// <param name="windows">Lists the top-level windows in z-order and reads their affinity, frame, title and process.</param>
    /// <param name="capturePolicy">The policy's check of a window capture, which decides whether a window's process and title
    /// are listed. Null: none are.</param>
    /// <param name="policy">The policy itself: one that denies nothing lists every window's process and title without asking
    /// <paramref name="capturePolicy"/>. Null: every window is checked.</param>
    public DisplayAffinityScan(INativeWindowWrapper windows, ICaptureTargetPolicy? capturePolicy, ITargetPolicy? policy)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _capturePolicy = capturePolicy;
        _deniesAnything = policy?.DeniesAnything() ?? true;
    }

    /// <summary>
    /// The drawn windows that declare a display affinity now, front to back, with their visible frames. One EnumWindows pass;
    /// of a window without an affinity only its visibility and affinity are read. <paramref name="leaveOut"/> is never
    /// listed: the captured window, which its own DisplayAffinity warning reports.
    /// </summary>
    public IReadOnlyList<ExcludedWindow> Read(nint leaveOut = 0)
    {
        var found = new List<ExcludedWindow>();
        foreach (var handle in DrawnWindows.FrontToBack(_windows, handle => handle != leaveOut && _windows.GetWindowDisplayAffinity(handle) != 0))
        {
            // Read again: the app may have cleared it meanwhile
            if (DisplayAffinities.Of(_windows.GetWindowDisplayAffinity(handle)) is not { } affinity || Frame(handle) is not { } frame)
            {
                continue;
            }

            var (processName, title) = Describe(handle);
            found.Add(new ExcludedWindow((long)handle, affinity, processName, title, frame.X, frame.Y, frame.Width, frame.Height));
        }

        return found;
    }

    /// <summary>
    /// The windows of <paramref name="reads"/> over <paramref name="area"/>, each cut to it, each window once: a window read
    /// more than once (before and after the pixels, or frame after frame) keeps its last read, in the order it was first read.
    /// Null when none is over the area, so the result leaves the field out.
    /// </summary>
    public static IReadOnlyList<ExcludedWindow>? Within(Rectangle area, IEnumerable<ExcludedWindow> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        var windows = new Dictionary<long, ExcludedWindow>();
        var order = new List<long>();
        foreach (var window in reads)
        {
            var part = Rectangle.Intersect(new Rectangle(window.X, window.Y, window.Width, window.Height), area);
            if (part.Width <= 0 || part.Height <= 0)
            {
                continue;
            }

            if (!windows.ContainsKey(window.Handle))
            {
                order.Add(window.Handle);
            }

            windows[window.Handle] = window with { X = part.X, Y = part.Y, Width = part.Width, Height = part.Height };
        }

        return order.Count == 0 ? null : order.Select(handle => windows[handle]).ToList();
    }

    /// <summary>The visible frame (without the invisible resize borders), or the window rectangle; null when the window is gone.</summary>
    private Rectangle? Frame(nint handle)
    {
        if (_windows.GetWindowRect(handle) is not { } rect)
        {
            return null;
        }

        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var visible = _windows.GetExtendedFrameBounds(handle) is { } frame
            ? Rectangle.Intersect(Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom), bounds)
            : bounds;
        return visible.Width > 0 && visible.Height > 0 ? visible : null;
    }

    /// <summary>
    /// The window's process and title, cut as the window tools cut it, when the policy allows them: nothing of a window it
    /// denies, of one it cannot check, or when the check fails (fail-closed). Once per window and process.
    /// </summary>
    private (string? ProcessName, string? Title) Describe(nint handle)
    {
        var processId = _windows.GetWindowThreadProcessId(handle);
        var key = (handle, processId);
        if (_named.TryGetValue(key, out var named))
        {
            return named;
        }

        named = Allowed(handle)
            ? (_windows.GetProcessName(processId), AppText.Cut(_windows.GetWindowText(handle) is { Length: > 0 } title ? title : null))
            : (null, null);
        _named[key] = named;
        return named;
    }

    private bool Allowed(nint handle)
    {
        if (!_deniesAnything)
        {
            return true;
        }

        try
        {
            return _capturePolicy != null && _capturePolicy.Check(handle) == null;
        }
        catch (Exception)
        {
            // The window closed while the policy was asked, or the check failed: listed without its process and title
            return false;
        }
    }
}
