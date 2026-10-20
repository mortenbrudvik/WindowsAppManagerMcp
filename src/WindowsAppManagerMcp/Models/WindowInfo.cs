namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A window as IWindowService reads it. For a UWP app, ProcessName and ProcessId name the app's own
/// process (the process of its CoreWindow), and HostProcessName/HostProcessId the
/// ApplicationFrameHost process that owns the frame window. MonitorIndex is the monitor the window is on (most of it); for a
/// minimized window, the monitor it restores to. Null when the monitor cannot be read (issue #12): never monitor 0 as a guess.
/// </summary>
public record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    WindowRect Bounds,
    WindowState State,
    bool IsVisible,
    int? MonitorIndex
)
{
    /// <summary>
    /// The id of the window's monitor, as get_monitors gives it ("m-0d6fc36f"), read with <see cref="MonitorIndex"/> in the
    /// same lookup (issue #12). Null when the monitor cannot be read or has no id.
    /// </summary>
    public string? MonitorId { get; init; }

    /// <summary>
    /// The snap_window position the visible frame fills on its monitor's work area, or "monitor" for the whole monitor
    /// (<see cref="Services.WindowZones.Match"/>, issue #12). Null for a minimized window, a frame that fills no zone, and a
    /// monitor that cannot be read.
    /// </summary>
    public string? Zone { get; init; }

    /// <summary>Whether the visible frame covers more than one monitor (<see cref="Services.WindowZones.Spans"/>, issue #12).</summary>
    public bool SpansMonitors { get; init; }

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

    /// <summary>
    /// Whether <see cref="IsAgentHost"/> is set only because the server cannot read its ancestor processes
    /// (IHostGuard.IsHostWindowOnlyWhileAncestorsUnknown): the window's program started before the server, so it may be the
    /// terminal, but nothing shows that it is. Refusals of such a window say so (HostGuard.AncestorsUnknownNoteFor) rather
    /// than call it the agent's own window.
    /// </summary>
    public bool IsAgentHostOnlyWhileAncestorsUnknown { get; init; }

    /// <summary>
    /// Whether the window is topmost (WS_EX_TOPMOST): it stays above every window that is not, also while another window is
    /// active (roadmap M6.7; set_window_zorder changes it).
    /// </summary>
    public bool IsTopmost { get; init; }
}
