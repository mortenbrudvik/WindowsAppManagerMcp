using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The planning phase of apply_layout, without side effects: which open window each placement of a preset is given, and
/// the rectangle it goes to. It reads only what it is handed (one window snapshot, the monitors, and a lookup of a
/// process's exe path), so a dry run and the real apply can share one plan (roadmap M6.8). The rules are the ones M2.8
/// introduced in LayoutService; moving them here changed no behaviour.
/// </summary>
public static class LayoutPlanner
{
    // How much each identity signal counts when ranking the windows that pass a placement's matchBy filter. The title is
    // the strongest identity, the class tells a main window from a dialog, the exe path two installations apart; the
    // distance from the placement's rectangle and the z-order ordinal only break ties.
    private const double TitleWeight = 40;
    private const double ClassWeight = 20;
    private const double ExePathWeight = 10;
    private const double DistanceWeight = 8;
    private const double OrdinalWeight = 4;

    private static readonly string[] TitleSeparators = [" - ", " – ", " — ", " | ", " · "];

    /// <summary>
    /// Gives each placement at most one window of <paramref name="snapshot"/>, and each window to at most one placement.
    /// Every (placement, window) pair that passes the matchBy filter is scored on the recorded identity (title pattern,
    /// class name, exe path, ordinal) and the distance from the placement's rectangle; the pairs are taken best first,
    /// so a placement that recognizes its window by title is not robbed of it by an earlier placement that would take
    /// any window of the process. Ties go to the earlier placement, then the window higher in the z-order.
    /// </summary>
    /// <param name="placements">The preset's placements, in the order they are applied.</param>
    /// <param name="snapshot">The open windows, in z-order (top first).</param>
    /// <param name="monitors">The monitors, as get_monitors numbers them.</param>
    /// <param name="matchBy">Which of a placement's criteria filter the windows.</param>
    /// <param name="exePathOf">
    /// The exe path of a process id, or null when it cannot be read. It is called only for placements that recorded an
    /// exe path, and the planner asks for each process at most once per plan.
    /// </param>
    /// <returns>One plan per placement, in the order of <paramref name="placements"/>.</returns>
    public static IReadOnlyList<PlannedPlacement> Plan(
        IReadOnlyList<WindowPlacement> placements,
        IReadOnlyList<WindowInfo> snapshot,
        IReadOnlyList<MonitorInfo> monitors,
        LayoutMatchBy matchBy,
        Func<int, string?> exePathOf)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(exePathOf);

        var plans = placements.Select(p => new PlannedPlacement(p, DescribePlacement(p))).ToList();
        foreach (var plan in plans)
        {
            var placement = plan.Placement;

            // A criterion given but blank once normalized ('.exe', invisible characters) is a corrupt file, never a match on the
            // other criterion alone: that would widen an exact '.exe' (which matched nothing), and its launch would take a new
            // window of any process (review finding F4). Refused whatever matchBy uses, as the window selectors refuse it
            if (LayoutPresetSchema.BlankCriterion(placement.Matcher) is { } blank)
            {
                plan.NotApplicable = $"Skipped {plan.Label}: its {blank} Correct the placement in the layout's file, or save the layout again.";
                continue;
            }

            if (MissingCriterion(placement.Matcher, matchBy) is { } missing)
            {
                plan.NotApplicable = $"Skipped {plan.Label}: {missing}";
                continue;
            }

            // A saved monitor that is gone is reported, not swapped for the primary one: the window would land on the
            // wrong screen with success. A monitor saved by identity that is not connected falls back to its name, then its
            // index, and says so (roadmap M6.8)
            var (monitor, fallback, monitorErrorCode, monitorError) = ResolveMonitor(placement, monitors);
            if (monitor == null)
            {
                plan.MonitorErrorCode = monitorErrorCode;
                plan.MonitorError = $"Did not arrange {plan.Label}: {monitorError}";
                continue;
            }

            var workArea = monitor.WorkArea;
            plan.Monitor = monitor;
            plan.MonitorFallback = fallback;
            plan.WorkArea = workArea;

            // A snap position is resolved on the monitor as it is now (roadmap M6.8); a name this server does not know (a
            // hand-edited file) leaves the position, which every preset has
            if (placement.Snap != null && ToolEnum<SnapPosition>.TryParse(placement.Snap, out var snap, out _))
            {
                plan.Snap = snap;
                var frame = SnapMath.Calculate(workArea, snap);
                plan.Target = new WindowRect(frame.X, frame.Y, frame.Width, frame.Height);
            }
            else
            {
                plan.Target = TargetOf(placement.Position, workArea);
            }

            plan.NormalTarget = placement.NormalPosition is { } normal ? TargetOf(normal, workArea) : null;

            // A rectangle less than a pixel wide or high on this monitor is no window (SetWindowBounds would take it), and is
            // given no window: a file's tiny fractions, or an agent's, valid as fractions (review finding F2). Only the
            // rectangles the window is given count: a snap's position and a maximized placement's are not used
            var usesPosition = plan.Snap == null && placement.State != WindowState.Maximized;
            if (((usesPosition ? SizeError(plan.Target, "position") : null) ?? SizeError(plan.NormalTarget, "normalPosition")) is { } tooSmall)
            {
                plan.PositionError = $"Did not arrange {plan.Label}: its {tooSmall} on monitor {monitor.Index}'s work area " +
                                     $"({workArea.Width} x {workArea.Height}), less than one pixel, which is no window. Nothing was changed. " +
                                     "Save the layout again with a larger width and height.";
                plan.Target = null;
                plan.NormalTarget = null;
            }
        }

        // The position of each window among the snapshot's windows of its process, as save_layout counted it
        var ordinals = new int[snapshot.Count];
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < snapshot.Count; i++)
        {
            ordinals[i] = seen.GetValueOrDefault(snapshot[i].ProcessName);
            seen[snapshot[i].ProcessName] = ordinals[i] + 1;
        }

        var exePaths = new Dictionary<int, string?>();
        string? ExePathOf(int processId)
        {
            if (!exePaths.TryGetValue(processId, out var path))
                exePaths[processId] = path = exePathOf(processId);
            return path;
        }

        var pairs = new List<(double Score, int Plan, int Window)>();
        for (var p = 0; p < plans.Count; p++)
        {
            if (plans[p].Target is not { } target)
                continue;

            var matcher = plans[p].Placement.Matcher;
            for (var w = 0; w < snapshot.Count; w++)
            {
                if (Matches(snapshot[w], matcher, matchBy))
                    pairs.Add((Score(matcher, snapshot[w], ordinals[w], target, plans[p].WorkArea!, ExePathOf), p, w));
            }
        }

        var taken = new bool[snapshot.Count];
        foreach (var (_, p, w) in pairs.OrderByDescending(pair => pair.Score).ThenBy(pair => pair.Plan).ThenBy(pair => pair.Window))
        {
            if (plans[p].Window != null || taken[w])
                continue;

            plans[p].Window = snapshot[w];
            taken[w] = true;
        }

        return plans;
    }

    /// <summary>
    /// The monitor a placement goes to (roadmap M6.8): the one its <see cref="WindowPlacement.Monitor"/> names (an id, a device
    /// name, 'primary', 'left' or 'right'); when that names none, the one monitor with its <see cref="WindowPlacement.MonitorName"/>
    /// (fallback "name"), then the one at its <see cref="WindowPlacement.MonitorIndex"/> (fallback "index"). A placement that
    /// saved no identity (schema version 1) has its index alone, without a fallback. When none fits: MonitorNotFound for a
    /// placement that saved an identity, InvalidMonitorIndex for one that did not.
    /// </summary>
    public static (MonitorInfo? Monitor, string? Fallback, string? ErrorCode, string? Error) ResolveMonitor(
        WindowPlacement placement, IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(monitors);

        var saved = !string.IsNullOrWhiteSpace(placement.Monitor);
        if (saved && MonitorSelection.Resolve(monitors, MonitorSelector.Of(placement.Monitor!), () => null) is
                { Succeeded: true, Monitor: { } named })
        {
            return (named, null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(placement.MonitorName))
        {
            var withName = monitors
                .Where(m => string.Equals(m.FriendlyName, placement.MonitorName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (withName.Count == 1)
                return (withName[0], LayoutMonitorFallback.Name, null, null);
        }

        if (placement.MonitorIndex >= 0 && placement.MonitorIndex < monitors.Count)
        {
            var identified = saved || !string.IsNullOrWhiteSpace(placement.MonitorName);
            return (monitors[placement.MonitorIndex], identified ? LayoutMonitorFallback.Index : null, null, null);
        }

        if (!saved)
            return (null, null, nameof(WindowErrorCode.InvalidMonitorIndex), WindowService.MonitorIndexMessage(placement.MonitorIndex, monitors.Count));

        var name = string.IsNullOrWhiteSpace(placement.MonitorName) ? string.Empty : $" ({placement.MonitorName.Trim()})";
        return (null, null, nameof(WindowErrorCode.MonitorNotFound),
            $"its monitor '{placement.Monitor}'{name} is not connected, and its monitor index {placement.MonitorIndex} does not exist " +
            $"({monitors.Count} monitor{(monitors.Count == 1 ? string.Empty : "s")}). Nothing was changed. Call get_monitors to see them.");
    }

    /// <summary>
    /// The rectangle of <paramref name="position"/> (fractions of a work area) on <paramref name="workArea"/>, in
    /// virtual-screen pixels. Each component is truncated toward zero on its own, as apply_layout has always done, so
    /// neighbouring placements can be a pixel apart; <see cref="RelativeRectMath.FromFractions(MonitorRect, RelativePosition)"/>
    /// is the exact tiling M6.8 can switch to, a change of at most one pixel per edge.
    /// </summary>
    public static WindowRect TargetOf(RelativePosition position, MonitorRect workArea)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(workArea);

        return new WindowRect(
            (int)(workArea.X + position.X * workArea.Width),
            (int)(workArea.Y + position.Y * workArea.Height),
            (int)(position.Width * workArea.Width),
            (int)(position.Height * workArea.Height));
    }

    /// <summary>"960 x 0 pixels" when a rectangle is less than a pixel wide or high, with what it is; null otherwise.</summary>
    private static string? SizeError(WindowRect? rect, string name) =>
        rect is { } r && (r.Width < 1 || r.Height < 1) ? $"{name} is {r.Width} x {r.Height} pixels" : null;

    /// <summary>
    /// Why <paramref name="matcher"/> has nothing <paramref name="matchBy"/> can match on, or null when it has. An empty
    /// criterion used to match every window, so title_only on a captured layout moved arbitrary windows; so does one of only
    /// whitespace, which counts as left out, as save_layout takes it. A criterion that is blank once normalized but not
    /// whitespace ('.exe', invisible characters) is refused before this (<see cref="LayoutPresetSchema.BlankCriterion"/>).
    /// </summary>
    private static string? MissingCriterion(WindowMatcher matcher, LayoutMatchBy matchBy)
    {
        var hasProcess = HasProcessName(matcher);
        var hasTitle = HasTitle(matcher);
        return matchBy switch
        {
            LayoutMatchBy.TitleOnly when !hasTitle =>
                "matchBy title_only needs titleContains, and this placement has none (save_layout records the process, " +
                "not title text); use process_and_title or process_only",
            LayoutMatchBy.ProcessOnly when !hasProcess =>
                "matchBy process_only needs a processName, and this placement has none; use process_and_title or title_only",
            LayoutMatchBy.ProcessAndTitle when !hasProcess && !hasTitle =>
                "the placement has neither a processName nor titleContains, so it would match any window",
            _ => null
        };
    }

    /// <summary>
    /// Whether <paramref name="processName"/> names a process once normalized as the windows are compared: not null, whitespace
    /// or only '.exe' (review finding F4). A layout's launch expects a window of that process only then: "" is part of every
    /// process name.
    /// </summary>
    public static bool NamesAProcess(string? processName) =>
        !string.IsNullOrEmpty(processName) && WindowMatching.WithoutExe(processName).Length > 0;

    /// <summary>Whether the matcher's process name names a process once normalized (<see cref="NamesAProcess"/>).</summary>
    private static bool HasProcessName(WindowMatcher matcher) => NamesAProcess(matcher.ProcessName);

    /// <summary>Whether the matcher's title text holds something visible once normalized (<see cref="WindowTitles.Normalize"/>).</summary>
    private static bool HasTitle(WindowMatcher matcher) => WindowTitles.Normalize(matcher.TitleContains).Length > 0;

    private static bool Matches(WindowInfo window, WindowMatcher matcher, LayoutMatchBy matchBy) => matchBy switch
    {
        LayoutMatchBy.ProcessOnly => MatchesProcessName(window, matcher),
        LayoutMatchBy.TitleOnly => MatchesTitle(window, matcher),
        LayoutMatchBy.ProcessAndTitle =>
            (!HasProcessName(matcher) || MatchesProcessName(window, matcher)) &&
            (!HasTitle(matcher) || MatchesTitle(window, matcher)),
        _ => throw new ArgumentOutOfRangeException(nameof(matchBy), matchBy, "Not a matchBy value.")
    };

    /// <summary>
    /// A captured process name (<see cref="WindowMatcher.ExactProcessName"/>) must be the window's whole process name,
    /// so "Code" does not match "Code - Insiders"; a hand-written one may be part of it. Either may name a UWP window's
    /// frame host. The rule is the window selectors' (<see cref="WindowMatching"/>, roadmap M2.4).
    /// </summary>
    private static bool MatchesProcessName(WindowInfo window, WindowMatcher matcher) =>
        WindowMatching.ProcessNameMatches(window.ProcessName, window.HostProcessName, matcher.ProcessName!, contains: !matcher.ExactProcessName);

    /// <summary>
    /// The title criterion, compared as the window selectors compare titles (<see cref="WindowTitles"/>, roadmap M2.4):
    /// ignoring case, invisible format characters and Edge's " and N more pages", so a preset written for
    /// "Microsoft Edge" finds the Edge window whose title has a zero-width space in it.
    /// </summary>
    private static bool MatchesTitle(WindowInfo window, WindowMatcher matcher) =>
        WindowTitles.Contains(window.Title, matcher.TitleContains);

    private static double Score(
        WindowMatcher matcher, WindowInfo window, int ordinal, WindowRect target, MonitorRect workArea, Func<int, string?> exePathOf)
    {
        var score = DistanceWeight * Closeness(window.Bounds, target, workArea);

        if (!string.IsNullOrEmpty(matcher.TitlePattern))
            score += TitleWeight * TitleSimilarity(matcher.TitlePattern, window.Title);

        if (!string.IsNullOrEmpty(matcher.ClassName) && !string.IsNullOrEmpty(window.ClassName))
            score += string.Equals(matcher.ClassName, window.ClassName, StringComparison.OrdinalIgnoreCase) ? ClassWeight : -ClassWeight;

        if (!string.IsNullOrEmpty(matcher.ExePath) && exePathOf(window.ProcessId) is { } exePath)
            score += string.Equals(matcher.ExePath, exePath, StringComparison.OrdinalIgnoreCase) ? ExePathWeight : -ExePathWeight;

        if (matcher.Ordinal == ordinal)
            score += OrdinalWeight;

        return score;
    }

    /// <summary>
    /// 1 when the titles are equal (ignoring case); otherwise up to 0.8 for the share of the pattern's parts (split at
    /// " - ", " | " and dashes, so "Inbox - Mail - Google Chrome" has three) that are also parts of the title.
    /// </summary>
    private static double TitleSimilarity(string pattern, string title)
    {
        if (string.Equals(pattern, title, StringComparison.OrdinalIgnoreCase))
            return 1;

        var patternParts = SplitTitle(pattern);
        if (patternParts.Length == 0)
            return 0;

        var titleParts = new HashSet<string>(SplitTitle(title), StringComparer.OrdinalIgnoreCase);
        return 0.8 * patternParts.Count(titleParts.Contains) / patternParts.Length;
    }

    private static string[] SplitTitle(string title) =>
        title.Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>1 when the window is exactly at the target rectangle, falling to 0 as its edges move a work area away.</summary>
    private static double Closeness(WindowRect bounds, WindowRect target, MonitorRect workArea)
    {
        double scale = 2L * (workArea.Width + workArea.Height);
        if (scale <= 0)
            return 0;

        double offset = Math.Abs((long)bounds.X - target.X) + Math.Abs((long)bounds.Y - target.Y) +
                        Math.Abs((long)bounds.Width - target.Width) + Math.Abs((long)bounds.Height - target.Height);
        return 1 - Math.Min(1, offset / scale);
    }

    private static string DescribePlacement(WindowPlacement placement)
    {
        var criteria = new List<string>();
        if (!string.IsNullOrEmpty(placement.Matcher.ProcessName))
            criteria.Add($"process '{placement.Matcher.ProcessName}'");
        if (!string.IsNullOrEmpty(placement.Matcher.TitleContains))
            criteria.Add($"title containing '{placement.Matcher.TitleContains}'");

        return $"placement {placement.Order} ({(criteria.Count > 0 ? string.Join(", ", criteria) : "no matcher")})";
    }
}

/// <summary>A placement on its way to being applied: the window it was given and where it goes.</summary>
public sealed class PlannedPlacement(WindowPlacement placement, string label)
{
    /// <summary>The preset's placement.</summary>
    public WindowPlacement Placement { get; } = placement;

    /// <summary>The placement in words for messages: its order and criteria.</summary>
    public string Label { get; } = label;

    /// <summary>Why the placement cannot be matched under the chosen matchBy; null when it can.</summary>
    public string? NotApplicable { get; internal set; }

    /// <summary>Why the placement's monitor cannot be used; null when it can.</summary>
    public string? MonitorError { get; internal set; }

    /// <summary>The error code of <see cref="MonitorError"/>: InvalidMonitorIndex, or MonitorNotFound for a monitor saved by identity.</summary>
    public string? MonitorErrorCode { get; internal set; }

    /// <summary>
    /// Why the placement's rectangle cannot be arranged on its monitor (InvalidPlacement): its position or restore rectangle
    /// is less than one pixel wide or high there. It is then given no window, no <see cref="Target"/> and no
    /// <see cref="NormalTarget"/>. Null when it can.
    /// </summary>
    public string? PositionError { get; internal set; }

    /// <summary>The placement's monitor; null when it is not applicable or its monitor does not exist.</summary>
    public MonitorInfo? Monitor { get; internal set; }

    /// <summary>How the monitor was found when the saved one is not connected (<see cref="LayoutMonitorFallback"/>); null otherwise.</summary>
    public string? MonitorFallback { get; internal set; }

    /// <summary>
    /// The placement's rectangle on its monitor, in virtual-screen pixels: the window rect of its position, or the visible frame of
    /// its snap position. Null when it is not applicable, its monitor does not exist, or it has a <see cref="PositionError"/>.
    /// </summary>
    public WindowRect? Target { get; internal set; }

    /// <summary>The snap position the window is snapped to on its monitor (roadmap M6.8); null for a placement with a position.</summary>
    public SnapPosition? Snap { get; internal set; }

    /// <summary>The restore rectangle of a maximized or minimized placement on its monitor (roadmap M6.8); null when none was saved.</summary>
    public WindowRect? NormalTarget { get; internal set; }

    /// <summary>The work area of the placement's monitor; null when it is not applicable or its monitor does not exist.</summary>
    public MonitorRect? WorkArea { get; internal set; }

    /// <summary>The window the placement was given from the snapshot; null when none matched.</summary>
    public WindowInfo? Window { get; internal set; }
}
