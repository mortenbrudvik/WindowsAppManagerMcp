using System.ComponentModel;

namespace WindowsAppManagerMcp.Models;

/// <summary>A window of a layout preset: which window, and where and how it goes.</summary>
/// <param name="MonitorIndex">
/// The monitor, as get_monitors numbers them. Since schema version 2 (roadmap M6.8) it is the last resort, after
/// <see cref="Monitor"/> and <see cref="MonitorName"/>; apply_layout fails a placement whose monitor does not exist.
/// </param>
/// <param name="Position">The window's rectangle (the window rect, with its invisible borders) as fractions of the monitor's work area.</param>
/// <param name="LaunchCommand">
/// What launchMissing starts for a missing window (schema version 1). A version 2 preset keeps it filled from
/// <see cref="Launch"/>, for older servers.
/// </param>
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
    /// The window state to restore: Maximized maximizes the window on its monitor, Minimized places and then minimizes it,
    /// Normal (and null, for presets saved before it was recorded) gives it the saved rectangle.
    /// </summary>
    public WindowState? State { get; init; }

    /// <summary>
    /// The monitor by identity (schema version 2): a monitor id ("m-" and 8 hex digits, which follows a monitor on its port), a
    /// device name (\\.\DISPLAY2, recorded when the id could not be read), or 'primary', 'left' or 'right' (agent-written).
    /// apply_layout resolves it first; when it names no monitor, it tries <see cref="MonitorName"/>, then
    /// <see cref="MonitorIndex"/>, and reports the fallback. Null: <see cref="MonitorIndex"/> alone.
    /// </summary>
    public string? Monitor { get; init; }

    /// <summary>The monitor's EDID name when it was saved (get_monitors' friendlyName): the fallback when <see cref="Monitor"/> names none.</summary>
    public string? MonitorName { get; init; }

    /// <summary>
    /// The restore rectangle of a maximized or minimized window (schema version 2), as fractions of the work area like
    /// <see cref="Position"/>: where it goes before it is maximized, and where a minimized one returns to. Null for a normal
    /// window, and for presets saved before it was recorded.
    /// </summary>
    public RelativePosition? NormalPosition { get; init; }

    /// <summary>
    /// A snap position (snap_window's names, schema version 2, agent-written): the window is snapped on its monitor instead of
    /// given <see cref="Position"/>, which holds the same rectangle for older servers.
    /// </summary>
    public string? Snap { get; init; }

    /// <summary>What launchMissing starts for a missing window (schema version 2): a program or an installed app, never arguments.</summary>
    public LayoutLaunch? Launch { get; init; }
}

/// <summary>
/// What apply_layout's launchMissing starts for a placement whose window is missing (roadmap M6.8): a program's path or name,
/// or an installed app's AppUserModelID (list_installed_apps' appId), one of the two. It never holds arguments (owner decision
/// D15): save_layout records the program a window runs, not how it was started.
/// </summary>
public sealed record LayoutLaunch(
    [property: Description("A program's path or name")]
    string? Executable = null,
    [property: Description("An installed app's appId (list_installed_apps)")]
    string? AppId = null);
