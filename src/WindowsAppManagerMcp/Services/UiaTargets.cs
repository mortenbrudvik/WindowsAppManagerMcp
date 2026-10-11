using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What every UI Automation read of a window checks first (roadmap M3.1, shared with M3.4): whether the window responds,
/// and which application's process a stuck read is counted against (<see cref="IUiaWorker"/>). These are Win32 reads that
/// Windows answers without the window's thread, except the WM_NULL probe, which gives up after
/// <see cref="RespondTimeoutMs"/>.
/// </summary>
public static class UiaTargets
{
    /// <summary>How long the hung-window check waits for the window to answer a message.</summary>
    public const uint RespondTimeoutMs = 500;

    /// <summary>
    /// Why the window must not be read, or null when it responds (or cannot be checked): Windows reports its thread hung
    /// (IsHungAppWindow), or it does not answer a no-op message within <see cref="RespondTimeoutMs"/> (SendMessageTimeout
    /// WM_NULL with SMTO_ABORTIFHUNG). A handle that is no window is left to the UI Automation lookup, which reports
    /// WindowNotFound. A check that throws does not stop the read: the read's own deadline still bounds it.
    /// </summary>
    /// <param name="windows">The window wrapper, or null to check nothing.</param>
    /// <param name="windowHandle">The window to check.</param>
    /// <param name="what">What would not be read, for the message ("its UI elements were not read").</param>
    public static string? NotRespondingReason(INativeWindowWrapper? windows, nint windowHandle, string what = "its UI elements were not read")
    {
        if (windows == null)
        {
            return null;
        }

        try
        {
            if (!windows.IsWindow(windowHandle))
            {
                return null;
            }

            if (windows.IsHungAppWindow(windowHandle))
            {
                return $"The window is not responding (Windows reports it hung), so {what}: every UI " +
                       "Automation call to it would block until it recovers. Wait, then check it again with find_windows or " +
                       "get_window_info (isResponding).";
            }

            // Null: the message could not be sent (the window is gone, or a more privileged one); the read decides
            if (windows.RespondsWithin(windowHandle, RespondTimeoutMs) == false)
            {
                // It may have closed meanwhile; then the lookup reports that
                if (!windows.IsWindow(windowHandle))
                {
                    return null;
                }

                return $"The window did not answer a message within {RespondTimeoutMs} ms, so {what}: " +
                       "the application is busy or hung. Wait, then try again.";
            }

            return null;
        }
        catch (Exception)
        {
            // The check is a courtesy: the read's own deadline still bounds it
            return null;
        }
    }

    /// <summary>
    /// The process of the app that the window shows, or 0 when it is not known. For a UWP frame window that is the app's
    /// own process, not the ApplicationFrameHost that owns the frames of every UWP app (see <see cref="UwpFrames"/>).
    /// </summary>
    public static int TargetProcessId(INativeWindowWrapper? windows, nint windowHandle)
    {
        try
        {
            return windows == null ? 0 : (int)UwpFrames.AppProcessId(windows, windowHandle);
        }
        catch (Exception)
        {
            // Only the stuck-read count depends on it
            return 0;
        }
    }
}
