using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// A minimized window restored for a capture (restoreIfMinimized, roadmap M2.9). It was restored with
/// SW_SHOWNOACTIVATE, so the window in front and the keyboard focus did not change; <see cref="Dispose"/> minimizes
/// it again with SW_SHOWMINNOACTIVE, because SW_MINIMIZE would activate the next window in the Z order.
/// </summary>
public sealed class MinimizedWindowRestore : IDisposable
{
    private readonly INativeWindowWrapper _windows;
    private readonly IWindowCallRunner _windowCalls;
    private readonly TimeSpan _stateTimeout;
    private readonly List<string> _warnings = [];
    private bool _restoreSent;
    private bool _disposed;

    /// <summary>
    /// A restore of <paramref name="window"/> that has not been sent yet: <see cref="IScreenshotService.RestoreForCapture"/>
    /// sends it. Disposing one that was never sent does nothing.
    /// </summary>
    public MinimizedWindowRestore(
        nint window,
        INativeWindowWrapper windows,
        IWindowCallRunner windowCalls,
        TimeSpan stateTimeout)
    {
        Window = window;
        _windows = windows;
        _windowCalls = windowCalls;
        _stateTimeout = stateTimeout;
    }

    /// <summary>The window that was restored.</summary>
    public nint Window { get; }

    /// <summary>Why the window could not be restored for the capture, or null when it was restored.</summary>
    public CaptureRefusal? Failure { get; private set; }

    /// <summary>
    /// What the capture result should say about the restore: that the window was restored and minimized again, that
    /// it may still have been painting, or that it could not be minimized again (read after <see cref="Dispose"/>).
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Sends SW_SHOWNOACTIVATE and waits until the window is no longer minimized. A hidden or cloaked window is not
    /// restored: SW_SHOWNOACTIVATE would show a window its app hid (an app minimized to the notification area), and
    /// SW_SHOWMINNOACTIVE afterwards would leave it visible with a taskbar button; a cloaked one is on another desktop.
    /// </summary>
    internal void Restore()
    {
        if (!_windows.IsWindowVisible(Window))
        {
            Failure = new CaptureRefusal(ScreenshotErrorCode.WindowHidden, ScreenshotService.HiddenMessage);
            return;
        }

        if (_windows.IsCloaked(Window))
        {
            Failure = new CaptureRefusal(ScreenshotErrorCode.Cloaked, ScreenshotService.CloakedMessage);
            return;
        }

        try
        {
            _restoreSent = true;
            _windowCalls.Run(Window, () => _windows.ShowWindow(Window, NativeEnums.SW_SHOWNOACTIVATE));
        }
        catch (WindowNotRespondingException)
        {
            Failure = new CaptureRefusal(ScreenshotErrorCode.WindowMinimized,
                "The window is minimized, and restoring it for the capture failed: its application is not responding. " +
                "Nothing was captured. A restore that was sent may still happen once the application answers; check the " +
                "window with find_windows before you act on it.");
            return;
        }

        if (!WaitFor(() => !_windows.IsIconic(Window)))
        {
            Failure = new CaptureRefusal(ScreenshotErrorCode.WindowMinimized,
                $"The window is minimized, and it did not restore within {_stateTimeout.TotalMilliseconds:0} ms (the application " +
                "may refuse it, or run with higher privileges than this server). Nothing was captured.");
            return;
        }

        _warnings.Add(CaptureWarnings.Format(CaptureWarnings.WindowRestored,
            "the window was minimized; it was restored without activation for this capture and minimized again. A window " +
            "that was maximized before it was minimized is shown at its normal size, and the user's next restore may bring " +
            "it back un-maximized."));
    }

    /// <summary>Notes that the window was still painting when the capture was taken.</summary>
    internal void StillPainting(TimeSpan waited) =>
        _warnings.Add(CaptureWarnings.Format(CaptureWarnings.WindowStillPainting,
            $"the restored window did not show two equal frames within {waited.TotalMilliseconds:0} ms, so the image may be " +
            "partly painted. Capture again if it looks incomplete."));

    /// <summary>Minimizes the window again without activating another one, when the restore was sent.</summary>
    public void Dispose()
    {
        if (_disposed || !_restoreSent)
        {
            _disposed = true;
            return;
        }

        _disposed = true;
        try
        {
            _windowCalls.Run(Window, () => _windows.ShowWindow(Window, NativeEnums.SW_SHOWMINNOACTIVE));
            if (!_windows.IsWindow(Window) || WaitFor(() => _windows.IsIconic(Window)))
            {
                return;
            }
        }
        catch (WindowNotRespondingException)
        {
            // Reported below
        }

        _warnings.Add(CaptureWarnings.Format(CaptureWarnings.ReminimizeFailed,
            "the window could not be minimized again after the capture, so it may still be restored. Minimize it with " +
            "set_window_state (state 'minimize') if the user wants it out of the way."));
    }

    private bool WaitFor(Func<bool> condition)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed >= _stateTimeout)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }
}
