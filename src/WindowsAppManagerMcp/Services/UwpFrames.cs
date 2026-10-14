using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The frame windows of UWP (Store) apps. The top-level window of every such app is an
/// ApplicationFrameWindow of the one shared ApplicationFrameHost.exe, while the app itself runs in the
/// process of the CoreWindow that the frame hosts as a child. A prompt may come up in a window of
/// ApplicationFrameHost of another class (CredDialogHost through ModalExperienceManager, for example), so any
/// window of that process counts as a frame (roadmap M3.F7).
/// </summary>
/// <remarks>Only reads that do not wait for any application: GetClassName, GetWindowThreadProcessId and
/// EnumChildWindows do not send messages to the windows they read, and the process name is read from the process.</remarks>
public static class UwpFrames
{
    /// <summary>The process that owns the frame windows of every UWP app, as GetProcessName names it.</summary>
    public const string FrameHostProcessName = "ApplicationFrameHost";

    /// <summary>
    /// The process of the app that a top-level window shows: for a UWP frame window (<see cref="IsFrame"/>), the process
    /// of the CoreWindow it hosts; otherwise, and for a frame that hosts no CoreWindow of another process
    /// (Windows moves the CoreWindow of a minimized or suspended app out of the frame), the window's own
    /// process. 0 when the window is gone.
    /// </summary>
    public static uint AppProcessId(INativeWindowWrapper native, nint window)
    {
        var processId = native.GetWindowThreadProcessId(window);
        if (processId == 0 || !IsFrame(native.GetClassName(window), processId, native.GetProcessName))
            return processId;

        return FindHostedAppProcessId(native, window, processId) ?? processId;
    }

    /// <summary>
    /// Whether a top-level window may host an app's CoreWindow: an ApplicationFrameWindow, as before, or any other window
    /// of ApplicationFrameHost (roadmap M3.F7). Keyed on the frame host's process rather than on a list of classes, since
    /// which classes the frames of prompts have is not documented. The class is checked first, so the process name is
    /// looked up only for a window of another class, once per window; the children are listed only for a frame.
    /// </summary>
    /// <param name="className">The window's class name.</param>
    /// <param name="processId">The process that owns the window (GetWindowThreadProcessId).</param>
    /// <param name="processName">Looks up a process name.</param>
    public static bool IsFrame(string? className, uint processId, Func<uint, string?> processName) =>
        WindowClassification.IsApplicationFrameWindow(className) ||
        (processId != 0 && IsFrameHostProcess(processName(processId)));

    /// <summary>Whether the process name, with or without a path or ".exe", is ApplicationFrameHost's.</summary>
    public static bool IsFrameHostProcess(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) &&
        string.Equals(TargetPolicySettings.NormalizeProcessName(processName), FrameHostProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The process of the CoreWindow child that <paramref name="frame"/> hosts while its app runs, or null
    /// when it has none in a process other than <paramref name="frameProcessId"/> (the frame host).
    /// </summary>
    public static uint? FindHostedAppProcessId(INativeWindowWrapper native, nint frame, uint frameProcessId) =>
        FindHostedCoreWindow(native, frame, frameProcessId)?.ProcessId;

    /// <summary>
    /// The CoreWindow child that <paramref name="frame"/> hosts while its app runs, with its process, or null when it has
    /// none in a process other than <paramref name="frameProcessId"/> (the frame host). Its thread is the app's UI thread,
    /// which serves the app's UI Automation provider.
    /// </summary>
    public static (nint Window, uint ProcessId)? FindHostedCoreWindow(INativeWindowWrapper native, nint frame, uint frameProcessId)
    {
        // Collect first, so no window is read inside the native enumeration callback
        var children = new List<nint>();
        native.EnumChildWindows(frame, child =>
        {
            children.Add(child);
            return true;
        });

        foreach (var child in children)
        {
            if (!WindowClassification.IsCoreWindow(native.GetClassName(child)))
                continue;

            var childProcessId = native.GetWindowThreadProcessId(child);
            if (childProcessId != 0 && childProcessId != frameProcessId)
                return (child, childProcessId);
        }

        return null;
    }
}
