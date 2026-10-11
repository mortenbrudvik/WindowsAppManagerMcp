namespace WindowsAppManagerMcp.Models;

/// <summary>A window of a layout preset: which window, and where and how it goes.</summary>
/// <param name="MonitorIndex">The monitor, as get_monitors numbers them; apply_layout fails a placement whose monitor does not exist.</param>
/// <param name="Position">The window's rectangle as fractions of the monitor's work area.</param>
public record WindowPlacement(
    WindowMatcher Matcher,
    int MonitorIndex,
    RelativePosition Position,
    string? LaunchCommand = null,
    bool Focus = false,
    int Order = 0
)
{
    /// <summary>
    /// The window state to restore: Maximized maximizes the window on <see cref="MonitorIndex"/>, Minimized places and then
    /// minimizes it, Normal (and null, for presets saved before it was recorded) gives it the saved rectangle.
    /// </summary>
    public WindowState? State { get; init; }
}
