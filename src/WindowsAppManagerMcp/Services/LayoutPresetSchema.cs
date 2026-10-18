using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Layout schema version 2 (roadmap M6.8, owner decision D15): the in-memory upgrade of a version 1 preset, the version 1
/// fields every written preset keeps for older servers, what launchMissing starts for a placement, and the strict check of
/// the placements an agent writes (save_layout's placements). Pure.
/// </summary>
public static partial class LayoutPresetSchema
{
    /// <summary>The schema version the server writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The <see cref="LayoutPreset.AuthoredBy"/> of a preset whose placements an agent wrote.</summary>
    public const string AgentAuthor = "agent";

    /// <summary>The most placements save_layout takes from an agent in one preset.</summary>
    public const int MaxPlacements = 50;

    /// <summary>
    /// The most programs and apps an agent-written preset may start (review M6.8 #1): as many as apply_layout's question to the
    /// user shows, so the user confirms only what they saw.
    /// </summary>
    public const int MaxLaunchTargets = 8;

    /// <summary>The longest executable or appId an agent-written launch may have (MAX_PATH).</summary>
    public const int MaxLaunchLength = 260;

    /// <summary>The longest processName, titleContains, className, titlePattern or monitorName save_layout takes.</summary>
    public const int MaxTextLength = 260;

    /// <summary>The longest exePath save_layout takes.</summary>
    public const int MaxExePathLength = 1024;

    /// <summary>The largest ordinal save_layout takes.</summary>
    public const int MaxOrdinal = 999;

    /// <summary>The longest description a preset may have.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>
    /// How far a rectangle may reach past the work area's right or bottom edge: a window rect has invisible resize borders
    /// (about 7 px at 100%), so a window snapped to the right reaches that far past it.
    /// </summary>
    public const double EdgeSlack = 0.01;

    /// <summary>
    /// How many work areas a position read from a file may reach, each way (<see cref="LoadedError"/>): far more than any
    /// window a person put beside or below its monitor, and little enough that the rectangle on any monitor is well inside
    /// the int range apply_layout casts it to.
    /// </summary>
    public const double MaxLoadedFraction = 100;

    /// <summary>
    /// Whether an agent wrote the preset (review M6.8 #2): any authoredBy at all, in any case or spelling, so a preset is trusted
    /// only when it has none. save_layout writes "agent".
    /// </summary>
    public static bool IsAgentAuthored(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return !string.IsNullOrWhiteSpace(preset.AuthoredBy);
    }

    /// <summary>The distinct programs and apps the preset starts, as launchMissing starts them, in placement order.</summary>
    public static IReadOnlyList<string> LaunchTargets(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset.Placements.Select(LaunchTarget).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Why the user cannot be asked to confirm the preset's launches (review M6.8 #1): it starts more programs than the question
    /// shows, or one whose name is longer than it shows whole (a file written by hand or by an older or newer server). Null
    /// when the question can show them all.
    /// </summary>
    public static string? ConfirmationRefusal(LayoutPreset preset)
    {
        var targets = LaunchTargets(preset);
        if (targets.Count > MaxLaunchTargets)
        {
            return $"the layout starts {targets.Count} programs or apps, more than the {MaxLaunchTargets} a question to the user shows, " +
                   "so the user is not asked";
        }

        return targets.Any(t => t.Length > PackagedAppId.AppsFolderPrefix.Length + MaxLaunchLength)
            ? $"one of the programs it starts is longer than {MaxLaunchLength} characters, more than a question to the user shows, " +
              "so the user is not asked"
            : null;
    }

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
    /// Why a preset read from a file cannot be applied (review finding F2), whoever wrote it: a placement's position or
    /// normalPosition that is no rectangle a window can have. Each value must be a finite number and the width and height
    /// above 0, as <see cref="RangeError"/> requires of what an agent writes; unlike there, a rectangle may lie off its work
    /// area (a window a person put beside its monitor, a snapped window's borders), up to <see cref="MaxLoadedFraction"/>
    /// work areas away, as apply_layout has always placed it. The preset's shape (a name, placements with a matcher and a
    /// position) is checked first. Null when every placement passes.
    /// </summary>
    public static string? LoadedError(LayoutPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        for (var i = 0; i < preset.Placements.Count; i++)
        {
            var placement = preset.Placements[i];
            if (LoadedPositionError(placement.Position) is { } positionError)
                return $"placements[{i}].position: {positionError}";

            if (placement.NormalPosition is { } normal && LoadedPositionError(normal) is { } normalError)
                return $"placements[{i}].normalPosition: {normalError}";
        }

        return null;
    }

    /// <summary>Why fractions read from a file are no rectangle (<see cref="LoadedError"/>); null when they are one.</summary>
    private static string? LoadedPositionError(RelativePosition position)
    {
        if (NumbersAndSizeError(position) is { } error)
            return error;

        return Math.Abs(position.X) > MaxLoadedFraction || Math.Abs(position.Y) > MaxLoadedFraction ||
               position.Width > MaxLoadedFraction || position.Height > MaxLoadedFraction
            ? string.Create(CultureInfo.InvariantCulture,
                $"x {position.X}, y {position.Y}, width {position.Width}, height {position.Height} reach more than {MaxLoadedFraction} work areas away, which no window does.")
            : null;
    }

    /// <summary>
    /// The rules every rectangle of a layout follows, read or written: each value a finite number, and the width and height
    /// above 0 (a size of 0 or less is no window). Null when they hold.
    /// </summary>
    private static string? NumbersAndSizeError(RelativePosition position)
    {
        if (!(double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Width) && double.IsFinite(position.Height)))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"x {position.X}, y {position.Y}, width {position.Width}, height {position.Height} must all be finite numbers.");
        }

        return position.Width > 0 && position.Height > 0
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"width {position.Width} and height {position.Height} must be above 0: a window needs a size.");
    }

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

    /// <summary>
    /// Why a layout's launch target is not one a layout may start (review M6.8 #3), whoever wrote it: a layout starts a
    /// program by a bare name (no spaces, no folder), by a full path on a local drive, or by a URI, and an app by its
    /// AppUserModelID; never a network path, a relative path or a command line (launch_application would read "cmd.exe /c x"
    /// as a file in the current folder). Null when it may; the launch's own planning checks it further.
    /// </summary>
    public static string? LaunchTargetError(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.StartsWith(PackagedAppId.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AppIdPattern().IsMatch(target[PackagedAppId.AppsFolderPrefix.Length..])
                ? null
                : $"'{target[PackagedAppId.AppsFolderPrefix.Length..]}' is not an app's appId (list_installed_apps lists them).";
        }

        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal))
            return $"'{target}' is on a network path, which a layout does not start.";

        if (LocalPathPattern().IsMatch(target) || UriPattern().IsMatch(target))
            return null;

        return target.Any(c => char.IsWhiteSpace(c) || c is '\\' or '/')
            ? $"'{target}' is neither a program's name nor a full path on a local drive (a command line, or a relative path, is " +
              "not started): give the program's full path, its name without arguments, or an app's appId."
            : null;
    }

    // An app's AppUserModelID, as shell:AppsFolder takes it (InputValidationService allows the same)
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*(![A-Za-z0-9][A-Za-z0-9._-]*)?$")]
    private static partial Regex AppIdPattern();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex LocalPathPattern();

    // A scheme of two letters or more, so C:\ is a path, not a URI
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]+:[^\\]")]
    private static partial Regex UriPattern();

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

        // Nothing the agent wrote is ignored (review M6.8 #4): get_layout's own fields pass only when they agree (below)
        var echoes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (member, value) in spec.UnknownMembers ?? [])
        {
            if (member is not (OrderMember or MonitorIndexMember or LaunchCommandMember))
                return Fail($"{at}.{member} is not a placement field, so it would be ignored. Leave it out.", out error);
            echoes[member] = value;
        }

        if (spec.Launch?.UnknownMembers is { Count: > 0 } launchExtras)
        {
            return Fail($"{at}.launch.{launchExtras.Keys.First()} is not taken: a launch is an executable or an appId, without " +
                        "arguments (owner decision D15).", out error);
        }

        if (LengthError(spec, at) is { } tooLong)
            return Fail(tooLong, out error);

        var processName = Trimmed(spec.ProcessName);
        var titleContains = Trimmed(spec.TitleContains);
        if (processName == null && titleContains == null)
            return Fail($"{at} needs processName or titleContains, so apply_layout knows its window.", out error);

        // Text that normalizes to nothing would match every window, as the window selectors refuse it (review finding F4)
        if (BlankMatcherError(processName, titleContains) is { } blank)
            return Fail($"{at}.{blank}", out error);

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
            if ((executable ?? appId)!.Length > MaxLaunchLength)
                return Fail($"{at}.launch is longer than {MaxLaunchLength} characters.", out error);
            launch = new LayoutLaunch(executable, appId);
            if (LaunchTargetError(LaunchTarget(new WindowPlacement(new WindowMatcher(), 0, new RelativePosition(0, 0, 1, 1)) { Launch = launch })!)
                is { } targetError)
            {
                return Fail($"{at}.launch: {targetError}", out error);
            }
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

        built = built with { LaunchCommand = LaunchTarget(built) };
        if (EchoError(echoes, built, spec.Monitor, index, at, out var echoedIndex) is { } echoError)
            return Fail(echoError, out error);

        placement = echoedIndex is { } kept ? built with { MonitorIndex = kept } : built;
        error = null;
        return true;
    }

    private const string OrderMember = "order";
    private const string MonitorIndexMember = "monitorIndex";
    private const string LaunchCommandMember = "launchCommand";

    /// <summary>
    /// Checks the get_layout fields save_layout does not set itself, which an unchanged get_layout output carries: order must be
    /// the placement's place in the array, launchCommand what its launch starts, and monitorIndex comes only with monitor (as
    /// get_layout shows them both), where it is kept as the index of last resort. Anything else would be ignored, so it fails.
    /// </summary>
    private static string? EchoError(Dictionary<string, JsonElement> echoes, WindowPlacement built, MonitorSelector? monitor, int index,
        string at, out int? monitorIndex)
    {
        monitorIndex = null;
        if (echoes.TryGetValue(OrderMember, out var order) &&
            !(order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var given) && given == index))
        {
            return $"{at}.order is {order}, but a placement's order is its place in the array ({index}). Reorder the array, and " +
                   "leave order out.";
        }

        if (echoes.TryGetValue(LaunchCommandMember, out var command) && command.ValueKind != JsonValueKind.Null &&
            !(command.ValueKind == JsonValueKind.String && string.Equals(command.GetString(), built.LaunchCommand, StringComparison.Ordinal)))
        {
            return $"{at}.launchCommand is set from launch, as get_layout shows it; it would be ignored. Use launch (an executable " +
                   "or an appId, without arguments), and leave launchCommand out.";
        }

        if (echoes.TryGetValue(MonitorIndexMember, out var echoed) && echoed.ValueKind != JsonValueKind.Null)
        {
            if (echoed.ValueKind != JsonValueKind.Number || !echoed.TryGetInt32(out var value) || value < 0)
                return $"{at}.monitorIndex is {echoed}, not a monitor index.";

            if (monitor == null)
                return $"{at}.monitorIndex is deprecated and would be ignored: use monitor.";

            if (monitor.Index is { } chosen && chosen != value)
                return $"{at} has monitor {chosen} and monitorIndex {value}: leave monitorIndex out.";

            monitorIndex = value;
        }

        return null;
    }

    /// <summary>Why a text of the placement is too long (review M6.8 #4), or its ordinal out of range; null when none is.</summary>
    private static string? LengthError(LayoutPlacementSpec spec, string at)
    {
        foreach (var (name, value, max) in new (string, string?, int)[]
                 {
                     ("processName", spec.ProcessName, MaxTextLength), ("titleContains", spec.TitleContains, MaxTextLength),
                     ("className", spec.ClassName, MaxTextLength), ("titlePattern", spec.TitlePattern, MaxTextLength),
                     ("monitorName", spec.MonitorName, MaxTextLength), ("exePath", spec.ExePath, MaxExePathLength),
                     ("monitor", spec.Monitor?.Name, MaxTextLength)
                 })
        {
            if (value?.Length > max)
                return $"{at}.{name} is longer than {max} characters.";
        }

        return spec.Ordinal is < 0 or > MaxOrdinal ? $"{at}.ordinal must be 0 to {MaxOrdinal}." : null;
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

    /// <summary>
    /// The criterion of a placement's matcher, read from a file, that is given but blank once normalized (review finding F4),
    /// with why, as <see cref="TryBuildPlacement"/> refuses it in what an agent writes: apply_layout does not apply such a
    /// placement. Text of only whitespace counts as left out, as save_layout takes it. Null when neither criterion is blank.
    /// </summary>
    public static string? BlankCriterion(WindowMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        return BlankMatcherError(Trimmed(matcher.ProcessName), Trimmed(matcher.TitleContains));
    }

    /// <summary>
    /// The field of a matcher that is given but blank once normalized, as apply_layout and the window selectors compare it, with
    /// why: a processName that is nothing but '.exe', or a titleContains of only invisible format characters (a zero-width
    /// space, bidirectional marks). Either normalizes to "", which matches every window. Null when neither is blank.
    /// </summary>
    private static string? BlankMatcherError(string? processName, string? titleContains)
    {
        if (processName != null && WindowMatching.WithoutExe(processName).Length == 0)
        {
            return $"processName '{processName}' is blank once '.exe' is taken off, so it would match every window. Give the " +
                   "process's name, or leave it out.";
        }

        return titleContains != null && WindowTitles.Normalize(titleContains).Length == 0
            ? "titleContains holds only invisible characters, so it would match every window. Give text the title shows, or leave it out."
            : null;
    }

    /// <summary>
    /// Why fractions of a work area are out of range: x and y from 0 to 1, width and height above 0 up to 1, and the rectangle
    /// inside the work area (x + width and y + height up to 1, with <see cref="EdgeSlack"/> for the invisible borders).
    /// </summary>
    private static string? RangeError(RelativePosition position)
    {
        static bool In(double value, bool aboveZero) => !double.IsNaN(value) && value <= 1 && (aboveZero ? value > 0 : value >= 0);

        // The rules every layout rectangle follows, then the stricter range of what an agent writes
        if (NumbersAndSizeError(position) is { } shared)
            return shared;

        if (!(In(position.X, false) && In(position.Y, false) && In(position.Width, true) && In(position.Height, true)))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"x {position.X}, y {position.Y}, width {position.Width}, height {position.Height} are fractions of the work area: x and y from 0 to 1, width and height above 0 up to 1.");
        }

        return position.X + position.Width > 1 + EdgeSlack || position.Y + position.Height > 1 + EdgeSlack
            ? string.Create(CultureInfo.InvariantCulture,
                $"x {position.X} + width {position.Width} and y {position.Y} + height {position.Height} must be at most 1: the rectangle reaches past the work area.")
            : null;
    }

    /// <summary>
    /// The rectangle moved back inside the work area when it reaches past its right or bottom edge by more than
    /// <see cref="EdgeSlack"/> (a window the user dragged partly off the screen), keeping its size; save_layout's capture
    /// records it so, and so the preset goes back into save_layout. Within the slack (a snapped window's invisible borders) it
    /// is kept as it is.
    /// </summary>
    public static RelativePosition InsideWorkArea(RelativePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var x = position.X + position.Width > 1 + EdgeSlack ? Math.Max(0, 1 - position.Width) : position.X;
        var y = position.Y + position.Height > 1 + EdgeSlack ? Math.Max(0, 1 - position.Height) : position.Y;
        return position with { X = x, Y = y };
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
