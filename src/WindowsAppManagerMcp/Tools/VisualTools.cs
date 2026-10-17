using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// wait_for_visual (roadmap M6.4): one server-side wait for a change on the screen, or for the screen to settle, when UI
/// Automation has nothing to wait on. In the opt-in visual toolset (owner decisions D2 and D10). Only reads: no pixels and
/// no files leave the server, only rectangles.
/// </summary>
[McpServerToolType]
public class VisualTools
{
    /// <summary>The tool's name: captures keep visual signatures only while it is served (owner decision D9).</summary>
    public const string WaitForVisualTool = "wait_for_visual";

    /// <summary>The longest stableMs, as wait_for_window's.</summary>
    public const int MaxStableMs = WindowFinderTools.MaxStableMs;

    /// <summary>How long a stable wait wants nothing to change, by default.</summary>
    public const int DefaultStableMs = 500;

    private readonly IVisualChangeService _visual;
    private readonly IWindowService _windows;
    private readonly IMonitorService? _monitors;

    /// <param name="visual">The waits.</param>
    /// <param name="windows">Resolves a window selector.</param>
    /// <param name="visual">The waits.</param>
    /// <param name="windows">Resolves the window selector.</param>
    /// <param name="monitors">Resolves a `monitor` name (roadmap M6.7). The server always passes it; without it a name fails.</param>
    public VisualTools(IVisualChangeService visual, IWindowService windows, IMonitorService? monitors = null)
    {
        _visual = visual;
        _windows = windows;
        _monitors = monitors;
    }

    [McpServerTool(Name = WaitForVisualTool, Title = "Wait for Visual Change", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Wait on the server until a window, screen region or monitor changes on screen, or stops changing, when UI Automation " +
                 "has nothing to wait on (canvas apps, games, remote desktops, rendering). Frames are compared by a hash per 16-pixel " +
                 "tile; the result has rectangles, never an image. One target: handle or window (read as capture_window reads it, " +
                 "covering windows do not count), x/y/width/height or monitor (copied from the screen; windows the user's policy " +
                 "denies are left out and listed in maskedWindows), or since alone. condition changed (default) compares with since's " +
                 "capture, else with the wait's first frame, which misses a change made before it: before you act, capture the target " +
                 "with output 'metadata' and pass its captureId as since. stable waits until no frame differs from the one before for " +
                 "stableMs. A text caret the app reports is left out. Returns reason (pixels, resized, closed, minimized, hidden), " +
                 "changedPercent, changedRegions (screen pixels, at most 8) and captureId (the last frame, since for the next wait). " +
                 "Not satisfied: WaitTimedOut (timedOut, changedRegions as last seen). Captures keep what since compares with only " +
                 "while this tool is served (else CaptureNotFound). Only reads; other tools run meanwhile.")]
    public async Task<VisualWaitResult> WaitForVisual(
        [Description("changed (default) or stable")]
        VisualWaitCondition condition = VisualWaitCondition.Changed,
        [Description("Window handle (as integer) from find_windows. Pass handle or window, not both")]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("With a window: whether the frame (title bar, borders) is watched too. Default: true")]
        bool? includeFrame = null,
        [Description("Region: X of its left edge in screen pixels; with y, width and height")]
        int? x = null,
        [Description("Region: Y of its top edge")]
        int? y = null,
        [Description("Region: width in pixels")]
        int? width = null,
        [Description("Region: height in pixels")]
        int? height = null,
        [Description("A captureId from a capture taken before you acted (output 'metadata' costs no image): the baseline, and the target " +
                     "(its window, region or monitor). Alone, not with another target")]
        string? since = null,
        [Description("How long to wait in milliseconds (default: 10000, min: 0, max: 60000); 0 compares once, with since")]
        int timeoutMs = 10_000,
        [Description("Milliseconds from one frame to the next (default: 200, min: 100, max: 2000)")]
        int pollMs = VisualChangeService.DefaultPollMs,
        [Description("stable only: how long nothing may change, in milliseconds (default: 500, min: 0, max: 10000; at most timeoutMs)")]
        int? stableMs = null,
        [Description("A frame counts as changed only when more than this share of its tiles changed, in percent (default: 0, any " +
                     "tile; min: 0, below 100): raise it a little to ignore a clock or a spinner")]
        double minChangedPercent = 0,
        [Description(MonitorSelectorDescriptions.MonitorOrAll + " At most 32 megapixels")]
        MonitorSelector? monitor = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The values first: a selector is resolved only for a call that can wait
        var stable = CheckValues(since != null, condition, timeoutMs, pollMs, stableMs, minChangedPercent);
        var request = new VisualWaitRequest
        {
            Target = Target(handle, window, includeFrame, x, y, width, height, monitor, since),
            Condition = condition,
            TimeoutMs = timeoutMs,
            PollMs = pollMs,
            StableMs = stable,
            MinChangedPercent = minChangedPercent
        };
        return await _visual.WaitAsync(request, ToolProgress.ForServices(progress), cancellationToken).ConfigureAwait(false);
    }

    private static ToolErrorException Invalid(string message) =>
        new(ToolErrorReporting.InvalidArgumentsErrorCode, message + " Nothing was waited for.");

    /// <summary>The one target the arguments name, or InvalidArguments; a selector is resolved to its one window.</summary>
    private VisualTarget Target(long? handle, WindowSelector? window, bool? includeFrame, int? x, int? y, int? width, int? height,
        MonitorSelector? monitor, string? since)
    {
        var region = x.HasValue || y.HasValue || width.HasValue || height.HasValue;
        var monitorNamed = monitor != null;
        var named = (handle.HasValue || window != null ? 1 : 0) + (region ? 1 : 0) + (monitorNamed ? 1 : 0) + (since != null ? 1 : 0);
        if (named != 1)
        {
            throw Invalid(named == 0
                ? "Pass one target: handle or window, x/y/width/height, monitor, or since (a captureId)."
                : "Pass one target only: handle or window, x/y/width/height, monitor, or since alone (it names its own target).");
        }

        if (includeFrame.HasValue && !(handle.HasValue || window != null))
        {
            throw Invalid("includeFrame applies only to a window target (handle or window); since keeps its capture's.");
        }

        if (since != null)
        {
            return string.IsNullOrWhiteSpace(since)
                ? throw Invalid("since must be a captureId, such as 'c7-3fa91c', from a capture result.")
                : new VisualTarget.Since(since.Trim());
        }

        if (monitorNamed)
        {
            return new VisualTarget.Monitor(
                MonitorTargeting.Index(_monitors, monitor, allowAll: true, "Nothing was waited for.")!.Value);
        }

        if (region)
        {
            if (x is not { } left || y is not { } top || width is not { } w || height is not { } h)
            {
                throw Invalid("A region needs all of x, y, width and height.");
            }

            return new VisualTarget.Region(left, top, w, h);
        }

        return new VisualTarget.Window(WindowTargeting.Require(_windows, handle, window), includeFrame ?? true);
    }

    /// <summary>stableMs, or InvalidArguments for a value out of range; nothing has waited yet.</summary>
    private static int CheckValues(bool since, VisualWaitCondition condition, int timeoutMs, int pollMs,
        int? stableMs, double minChangedPercent)
    {
        if (timeoutMs is < 0 or > VisualChangeService.MaxTimeoutMs)
            throw Invalid($"timeoutMs must be 0-{VisualChangeService.MaxTimeoutMs}, but it is {timeoutMs}.");

        if (pollMs is < VisualChangeService.MinPollMs or > VisualChangeService.MaxPollMs)
            throw Invalid($"pollMs must be {VisualChangeService.MinPollMs}-{VisualChangeService.MaxPollMs}, but it is {pollMs}.");

        if (double.IsNaN(minChangedPercent) || minChangedPercent is < 0 or >= 100)
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"minChangedPercent must be at least 0 and below 100, but it is {minChangedPercent}."));

        if (condition == VisualWaitCondition.Changed && stableMs.HasValue)
            throw Invalid("stableMs applies only to condition 'stable'.");

        if (timeoutMs == 0 && !since)
        {
            throw Invalid("With timeoutMs 0 the wait reads one frame and has nothing to compare it with. Pass since (a captureId from " +
                          "before you acted) to compare once, or wait longer.");
        }

        var stable = stableMs ?? DefaultStableMs;
        if (stable is < 0 or > MaxStableMs)
            throw Invalid($"stableMs must be 0-{MaxStableMs}, but it is {stable}.");

        if (condition == VisualWaitCondition.Stable && stable > timeoutMs)
            throw Invalid($"stableMs ({stable}) is longer than timeoutMs ({timeoutMs}), so the wait could only time out.");

        return stable;
    }
}
