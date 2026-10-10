using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The frame windows of UWP (Store) apps. The top-level window of every such app is an
/// ApplicationFrameWindow of the one shared ApplicationFrameHost.exe, while the app itself runs in the
/// process of the CoreWindow that the frame hosts as a child.
/// </summary>
/// <remarks>Only reads that do not wait for any application: GetClassName, GetWindowThreadProcessId and
/// EnumChildWindows do not send messages to the windows they read.</remarks>
public static class UwpFrames
{
    /// <summary>
    /// The process of the app that a top-level window shows: for a UWP frame window, the process of the
    /// CoreWindow it hosts; otherwise, and for a frame that hosts no CoreWindow of another process
    /// (Windows moves the CoreWindow of a minimized or suspended app out of the frame), the window's own
    /// process. 0 when the window is gone.
    /// </summary>
    public static uint AppProcessId(INativeWindowWrapper native, nint window)
    {
        var processId = native.GetWindowThreadProcessId(window);
        if (processId == 0 || !WindowClassification.IsApplicationFrameWindow(native.GetClassName(window)))
            return processId;

        return FindHostedAppProcessId(native, window, processId) ?? processId;
    }

    /// <summary>
    /// The process of the CoreWindow child that <paramref name="frame"/> hosts while its app runs, or null
    /// when it has none in a process other than <paramref name="frameProcessId"/> (the frame host).
    /// </summary>
    public static uint? FindHostedAppProcessId(INativeWindowWrapper native, nint frame, uint frameProcessId)
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
                return childProcessId;
        }

        return null;
    }
}
