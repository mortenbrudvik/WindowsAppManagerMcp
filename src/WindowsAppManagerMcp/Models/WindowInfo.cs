namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A window as IWindowService reads it. For a UWP app, ProcessName and ProcessId name the app's own
/// process (the process of its CoreWindow), and HostProcessName/HostProcessId the
/// ApplicationFrameHost process that owns the frame window.
/// </summary>
public record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    WindowRect Bounds,
    WindowState State,
    bool IsVisible,
    int MonitorIndex
)
{
    /// <summary>
    /// What <see cref="ProcessName"/> (and <see cref="HostProcessName"/>) say when the process's name cannot be read (it
    /// has exited, or this server may not query it). Such a window cannot be checked against the target policy.
    /// </summary>
    public const string UnknownProcessName = "Unknown";

    /// <summary>
    /// The visible frame (DWMWA_EXTENDED_FRAME_BOUNDS): <see cref="Bounds"/> without the invisible resize borders that
    /// GetWindowRect includes (about 7 px left, right and bottom at 100%), so a maximized window does not overhang its
    /// monitor. Null when DWM cannot report it; the geometry tools then report Bounds.
    /// </summary>
    public WindowRect? VisibleBounds { get; init; }

    /// <summary>The window class name, for example "Notepad", "#32770" (dialog) or "Shell_TrayWnd" (taskbar).</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>
    /// Whether DWM cloaks the window: it is on another virtual desktop, is a suspended UWP frame, or
    /// its app hid it. A cloaked window is not on screen even though IsVisible is true.
    /// </summary>
    public bool IsCloaked { get; init; }

    /// <summary>The owner window (GW_OWNER), for example the main window of a dialog; null when unowned.</summary>
    public nint? OwnerHandle { get; init; }

    /// <summary>For a UWP frame window: the frame host process (ApplicationFrameHost); otherwise null.</summary>
    public string? HostProcessName { get; init; }

    /// <summary>For a UWP frame window: the ID of the frame host process; otherwise null.</summary>
    public int? HostProcessId { get; init; }

    /// <summary>
    /// Whether the window belongs to the agent's own host (IHostGuard.IsHostWindow): the terminal, editor
    /// or chat app the server was started from, or a window it owns. Input tools refuse such a window
    /// unless it is named explicitly with allowHostTarget, and close_window refuses it without force.
    /// </summary>
    public bool IsAgentHost { get; init; }
}
