using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What every UI Automation read of a window checks first (roadmap M3.1, shared with M3.4): whether the user's target
/// policy allows reading it (<see cref="ReadVerdict"/>), whether the window responds, and which application's process a
/// stuck read is counted against (<see cref="IUiaWorker"/>). These are Win32 reads that Windows answers without the window's
/// thread, except the WM_NULL probe, which gives up after <see cref="RespondTimeoutMs"/>.
/// </summary>
public static class UiaTargets
{
    /// <summary>How long the hung-window check waits for the window to answer a message.</summary>
    public const uint RespondTimeoutMs = 500;

    /// <summary>
    /// The target policy's verdict on a UI Automation read of the window: the policy covers the reads of a window as it
    /// covers captures (capture_window, capture_with_elements), so no element of an app the user denies is read, named or
    /// not. Matched on the window's process and, for a UWP frame window (an ApplicationFrameWindow, or another window of
    /// ApplicationFrameHost), on the app whose CoreWindow it hosts (see <see cref="UwpFrames"/>), as the window listings
    /// name them, and then on the window itself, which is refused when it is a security prompt that a shared process shows
    /// (<see cref="ITargetPolicy.EvaluateWindow"/>). Fails closed: a window whose process name cannot be read, or that
    /// cannot be checked, is refused while the policy denies anything. Allowed without a policy or a window wrapper, and for
    /// a handle that is no window (the read then reports that).
    /// </summary>
    /// <param name="windows">The window wrapper, or null to check nothing.</param>
    /// <param name="policy">The target policy, or null to check nothing.</param>
    /// <param name="windowHandle">The window to read.</param>
    /// <param name="processName">Looks up a process name (a caller that checks many windows passes a cached lookup); null
    /// for <see cref="INativeWindowWrapper.GetProcessName"/>.</param>
    public static TargetPolicyVerdict ReadVerdict(
        INativeWindowWrapper? windows, ITargetPolicy? policy, nint windowHandle, Func<uint, string?>? processName = null)
    {
        if (windows == null || policy == null || windowHandle == nint.Zero || !policy.DeniesAnything())
        {
            return TargetPolicyVerdict.Allow;
        }

        try
        {
            if (!windows.IsWindow(windowHandle))
            {
                return TargetPolicyVerdict.Allow;
            }

            processName ??= windows.GetProcessName;
            var processId = windows.GetWindowThreadProcessId(windowHandle);
            var names = new List<string?>();
            if (processId != 0)
            {
                // The window's own name is read anyway, so a frame of any class costs no other lookup (roadmap M3.F7)
                var windowName = processName(processId);
                if (UwpFrames.IsFrame(windows.GetClassName(windowHandle), processId, _ => windowName) &&
                    UwpFrames.FindHostedAppProcessId(windows, windowHandle, processId) is { } app)
                {
                    names.Add(processName(app));
                }

                names.Add(windowName);
            }

            var verdict = policy.Evaluate(names);
            if (!verdict.Allowed)
            {
                return verdict;
            }

            // A security prompt that a shared process shows, by its structure (roadmap M3.F5)
            if (policy.EvaluateWindow(windowHandle) is { Allowed: false } prompt)
            {
                return prompt;
            }

            if (processId != 0 && names.All(name => !string.IsNullOrWhiteSpace(name)))
            {
                return verdict;
            }

            // A process that cannot be named cannot be checked; a window that closed meanwhile is left to the read
            return windows.IsWindow(windowHandle) ? TargetPolicyVerdict.Unreadable : TargetPolicyVerdict.Allow;
        }
        catch (Exception)
        {
            return TargetPolicyVerdict.Unreadable;
        }
    }

    /// <summary>
    /// The TargetDeniedByPolicy message of a UI Automation read that <see cref="ReadVerdict"/> refuses, or null when the
    /// policy allows it.
    /// </summary>
    public static string? PolicyRefusal(INativeWindowWrapper? windows, ITargetPolicy? policy, nint windowHandle) =>
        ReadVerdict(windows, policy, windowHandle) is { Allowed: false } verdict ? PolicyRefusalMessage(windowHandle, verdict) : null;

    /// <summary>The TargetDeniedByPolicy message of a UI Automation read of <paramref name="windowHandle"/>.</summary>
    public static string PolicyRefusalMessage(nint windowHandle, TargetPolicyVerdict verdict) =>
        $"Window {(long)windowHandle} is refused by the server's target policy: {verdict.Reason}. Nothing was read: the policy " +
        "covers the UI Automation reads of a window as it covers input and captures. It is set by the user in the server's " +
        "environment and no tool argument changes it; ask the user to do this step.";

    /// <summary>
    /// Why the window must not be read, or null when it responds (or cannot be checked): Windows reports its thread hung
    /// (IsHungAppWindow), or it does not answer a no-op message within <see cref="RespondTimeoutMs"/> (SendMessageTimeout
    /// WM_NULL with SMTO_ABORTIFHUNG). For a UWP frame window (an ApplicationFrameWindow, or another window of
    /// ApplicationFrameHost: <see cref="UwpFrames.IsFrame"/>), the CoreWindow it hosts is checked the same way: the frame's
    /// thread is the shared ApplicationFrameHost's, while the app's elements are served by the CoreWindow's thread in the
    /// app's own process. A handle that is no window is left to the UI Automation lookup, which reports WindowNotFound. A
    /// check that throws does not stop the read: the read's own deadline still bounds it.
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

            if (Unresponsive(windows, windowHandle, windowHandle, what) is { } frame)
            {
                return frame;
            }

            // A UWP app answers on its CoreWindow's thread, not the frame host's
            var processId = windows.GetWindowThreadProcessId(windowHandle);
            return processId != 0 && UwpFrames.IsFrame(windows.GetClassName(windowHandle), processId, windows.GetProcessName) &&
                   UwpFrames.FindHostedCoreWindow(windows, windowHandle, processId) is { } core
                ? Unresponsive(windows, core.Window, windowHandle, what)
                : null;
        }
        catch (Exception)
        {
            // The check is a courtesy: the read's own deadline still bounds it
            return null;
        }
    }

    /// <summary>
    /// The not-responding message when <paramref name="probed"/> is reported hung or does not answer WM_NULL in time, or
    /// null; a window that closed meanwhile (<paramref name="window"/>, the one to read) is left to the lookup.
    /// </summary>
    private static string? Unresponsive(INativeWindowWrapper windows, nint probed, nint window, string what)
    {
        if (windows.IsHungAppWindow(probed))
        {
            return $"The window is not responding (Windows reports it hung), so {what}: every UI " +
                   "Automation call to it would block until it recovers. Wait, then check it again with find_windows or " +
                   "get_window_info (isResponding).";
        }

        // Null: the message could not be sent (the window is gone, or a more privileged one); the read decides
        if (windows.RespondsWithin(probed, RespondTimeoutMs) == false)
        {
            // It may have closed meanwhile; then the lookup reports that
            if (!windows.IsWindow(probed) || !windows.IsWindow(window))
            {
                return null;
            }

            return $"The window did not answer a message within {RespondTimeoutMs} ms, so {what}: " +
                   "the application is busy or hung. Wait, then try again.";
        }

        return null;
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
