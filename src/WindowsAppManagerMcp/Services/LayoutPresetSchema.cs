using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Layout schema version 2 (roadmap M6.8, owner decision D15): the in-memory upgrade of a version 1 preset, the version 1
/// fields every written preset keeps for older servers, what launchMissing starts for a placement, and the strict check of
/// the placements an agent writes (save_layout's placements). Pure.
/// </summary>
public static class LayoutPresetSchema
{
    /// <summary>The schema version the server writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The <see cref="LayoutPreset.AuthoredBy"/> of a preset whose placements an agent wrote.</summary>
    public const string AgentAuthor = "agent";

    /// <summary>The most placements save_layout takes from an agent in one preset.</summary>
    public const int MaxPlacements = 50;

    /// <summary>
    /// A preset read from a file, in the version 2 shape: a version 1 placement's launchCommand also becomes its
    /// <see cref="WindowPlacement.Launch"/> (a shell:AppsFolder entry as the app's appId), and the version becomes
    /// <see cref="CurrentVersion"/>. The version 1 fields stay as they were, and a later version is kept as it was read, so
    /// <see cref="IsNewer"/> can warn. The file is never rewritten.
    /// </summary>
    public static LayoutPreset Upgrade(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset with
        {
            Version = Math.Max(preset.Version, CurrentVersion),
            Placements = preset.Placements.Select(p => p.Launch != null || string.IsNullOrWhiteSpace(p.LaunchCommand)
                ? p
                : p with { Launch = LaunchOf(p.LaunchCommand.Trim()) }).ToList()
        };
    }

    /// <summary>Whether a server newer than this one wrote the preset: its fields this server does not know are ignored.</summary>
    public static bool IsNewer(LayoutPreset preset) => preset.Version > CurrentVersion;

    /// <summary>
    /// The preset as it is written: version <see cref="CurrentVersion"/>, and each placement's version 1 launchCommand filled
    /// from its launch when it has none, so an older server still launches it.
    /// </summary>
    public static LayoutPreset ForWriting(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset with
        {
            Version = CurrentVersion,
            Placements = preset.Placements
                .Select(p => p.LaunchCommand == null && LaunchTarget(p) is { } target ? p with { LaunchCommand = target } : p)
                .ToList()
        };
    }

    /// <summary>
    /// What launchMissing starts for the placement: an appId as its shell:AppsFolder entry (a packaged app is activated
    /// through it, roadmap M6.5), else the executable, else the version 1 launchCommand; null when the placement launches nothing.
    /// </summary>
    public static string? LaunchTarget(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.Launch is { AppId: { } appId } && !string.IsNullOrWhiteSpace(appId))
        {
            var id = appId.Trim();
            return id.StartsWith(PackagedAppId.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) ? id : PackagedAppId.AppsFolderPrefix + id;
        }

        if (placement.Launch is { Executable: { } executable } && !string.IsNullOrWhiteSpace(executable))
            return executable.Trim();

        return string.IsNullOrWhiteSpace(placement.LaunchCommand) ? null : placement.LaunchCommand.Trim();
    }

    /// <summary>
    /// What identifies the launches of this version of an agent-written preset (owner decision D15): its name, when it was
    /// last saved, and each placement's launch target. The user's confirmation holds for it until the preset is saved again
    /// or its launches change.
    /// </summary>
    public static string LaunchFingerprint(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var text = new StringBuilder()
            .Append(preset.Name.ToUpperInvariant()).Append('\n')
            .Append(preset.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var placement in preset.Placements)
        {
            text.Append(LaunchTarget(placement) ?? string.Empty).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static LayoutLaunch LaunchOf(string command) =>
        command.StartsWith(PackagedAppId.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase) &&
        command.Length > PackagedAppId.AppsFolderPrefix.Length
            ? new LayoutLaunch(AppId: command[PackagedAppId.AppsFolderPrefix.Length..])
            : new LayoutLaunch(Executable: command);

    /// <summary>
    /// Checks a placement an agent wrote and builds the preset's placement from it, or says why not (save_layout fails with
    /// InvalidPlacement and writes nothing). It needs processName or titleContains; snap or position, not both (neither only
    /// for state maximized, which fills the work area); fractions in 0 to 1 (a width or height above 0); normalPosition only
    /// for state maximized or minimized; a snap position and a state by their names; no focus with minimized, no snap with
    /// maximized; a monitor that is an index of 0 or more, 'primary', 'left', 'right', an id or a device name (default
    /// 'primary'); and a launch with either executable or appId. A snap's rectangle on the monitor as it is now becomes the
    /// position, and a named monitor's index now becomes the monitor index (the primary's when it is not connected), so an
    /// older server places the window there.
    /// </summary>
    /// <param name="index">The placement's place in the array: its order, and the start of an error.</param>
    public static bool TryBuildPlacement(LayoutPlacementSpec spec, int index, IReadOnlyList<MonitorInfo> monitors,
        out WindowPlacement? placement, out string? error)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(monitors);
        placement = null;
        var at = $"placements[{index}]";

        var processName = Trimmed(spec.ProcessName);
        var titleContains = Trimmed(spec.TitleContains);
        if (processName == null && titleContains == null)
            return Fail($"{at} needs processName or titleContains, so apply_layout knows its window.", out error);

        SnapPosition? snap = null;
        if (spec.Snap != null)
        {
            if (!ToolEnum<SnapPosition>.TryParse(spec.Snap, out var parsed, out var snapError))
                return Fail($"{at}.snap: {snapError}", out error);
            snap = parsed;
        }

        LaunchWindowState? state = null;
        if (spec.State != null)
        {
            if (!ToolEnum<LaunchWindowState>.TryParse(spec.State, out var parsed, out var stateError))
                return Fail($"{at}.state: {stateError}", out error);
            state = parsed;
        }

        if (snap != null && spec.Position != null)
            return Fail($"{at} takes snap or position, not both.", out error);

        if (state == LaunchWindowState.Maximized && snap != null)
            return Fail($"{at}: state maximized fills the monitor, so it takes no snap (normalPosition is where it restores to).", out error);

        if (snap == null && spec.Position == null && state != LaunchWindowState.Maximized)
            return Fail($"{at} needs snap or position (or state maximized).", out error);

        if (spec.Position is { } position && RangeError(position) is { } positionError)
            return Fail($"{at}.position: {positionError}", out error);

        if (spec.NormalPosition is { } normal)
        {
            if (state is not (LaunchWindowState.Maximized or LaunchWindowState.Minimized))
                return Fail($"{at}.normalPosition is the restore rectangle of state maximized or minimized only.", out error);
            if (RangeError(normal) is { } normalError)
                return Fail($"{at}.normalPosition: {normalError}", out error);
        }

        if (spec.Focus == true && state == LaunchWindowState.Minimized)
            return Fail($"{at}.focus cannot bring a window to the front that state minimized minimizes.", out error);

        if (!TryMonitor(spec.Monitor, monitors, at, out var monitorText, out var monitorIndex, out var monitorInfo, out error))
            return false;

        LayoutLaunch? launch = null;
        if (spec.Launch is { } given)
        {
            var executable = Trimmed(given.Executable);
            var appId = Trimmed(given.AppId);
            if ((executable == null) == (appId == null))
                return Fail($"{at}.launch takes executable or appId, one of the two.", out error);
            launch = new LayoutLaunch(executable, appId);
        }

        var workArea = monitorInfo?.WorkArea;
        var rectangle = snap is { } s && workArea != null
            ? FractionsOf(SnapMath.Calculate(workArea, s), workArea)
            : spec.Position ?? new RelativePosition(0, 0, 1, 1);

        var built = new WindowPlacement(
            new WindowMatcher(titleContains, processName, Trimmed(spec.ClassName))
            {
                ExactProcessName = spec.ExactProcessName == true && processName != null,
                TitlePattern = Trimmed(spec.TitlePattern),
                ExePath = Trimmed(spec.ExePath),
                Ordinal = spec.Ordinal
            },
            monitorIndex,
            rectangle,
            Focus: spec.Focus == true,
            Order: index)
        {
            State = state switch
            {
                LaunchWindowState.Maximized => WindowState.Maximized,
                LaunchWindowState.Minimized => WindowState.Minimized,
                LaunchWindowState.Normal => WindowState.Normal,
                _ => null
            },
            Monitor = monitorText,
            MonitorName = Trimmed(spec.MonitorName),
            NormalPosition = spec.NormalPosition,
            Snap = snap is { } named ? ToolEnum<SnapPosition>.Name(named) : null,
            Launch = launch
        };

        placement = built with { LaunchCommand = LaunchTarget(built) };
        error = null;
        return true;
    }

    /// <summary>
    /// The monitor a placement names: as written (null for an index), its index now (the primary's when the name is not
    /// connected), and the monitor itself when it is connected.
    /// </summary>
    private static bool TryMonitor(MonitorSelector? selector, IReadOnlyList<MonitorInfo> monitors, string at,
        out string? text, out int index, out MonitorInfo? monitor, out string? error)
    {
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        text = null;
        index = primary?.Index ?? 0;
        monitor = primary;
        error = null;

        if (selector?.Index is { } given)
        {
            if (given < 0)
                return Fail($"{at}.monitor {given} is no monitor: indexes start at 0.", out error);

            index = given;
            monitor = given < monitors.Count ? monitors[given] : null;
            return true;
        }

        var name = selector?.Name ?? MonitorSelection.Primary;
        if (Is(name, MonitorSelection.Current) || Is(name, MonitorSelection.All))
        {
            return Fail($"{at}.monitor '{name}' cannot be saved: it names no one monitor when the layout is applied. Use 'primary', " +
                        "'left', 'right', or an id or name from get_monitors.", out error);
        }

        var keyword = Is(name, MonitorSelection.Primary) || Is(name, MonitorSelection.Left) || Is(name, MonitorSelection.Right);
        var id = name.StartsWith(MonitorIdentity.IdPrefix, StringComparison.OrdinalIgnoreCase);
        var deviceName = name.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                         name.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase);
        if (!keyword && !id && !deviceName)
        {
            return Fail($"{at}.monitor '{name}' names no monitor: give an index, 'primary', 'left', 'right', or an id or name from " +
                        "get_monitors.", out error);
        }

        text = keyword || id ? name.ToLowerInvariant() : name;
        var resolution = MonitorSelection.Resolve(monitors, MonitorSelector.Of(name), () => null);
        if (resolution is { Succeeded: true, Monitor: { } found })
        {
            index = found.Index;
            monitor = found;
        }

        return true;
    }

    /// <summary>Why fractions of a work area are out of range: x and y from 0 to 1, width and height above 0 up to 1.</summary>
    private static string? RangeError(RelativePosition position)
    {
        static bool In(double value, bool aboveZero) => !double.IsNaN(value) && value <= 1 && (aboveZero ? value > 0 : value >= 0);

        return In(position.X, false) && In(position.Y, false) && In(position.Width, true) && In(position.Height, true)
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"x {position.X}, y {position.Y}, width {position.Width}, height {position.Height} are fractions of the work area: x and y from 0 to 1, width and height above 0 up to 1.");
    }

    private static RelativePosition FractionsOf(MonitorRect rect, MonitorRect workArea) => new(
        (double)(rect.X - workArea.X) / workArea.Width,
        (double)(rect.Y - workArea.Y) / workArea.Height,
        (double)rect.Width / workArea.Width,
        (double)rect.Height / workArea.Height);

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool Is(string name, string value) => string.Equals(name, value, StringComparison.OrdinalIgnoreCase);

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
