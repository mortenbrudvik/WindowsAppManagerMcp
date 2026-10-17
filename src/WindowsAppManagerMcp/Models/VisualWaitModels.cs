using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>What wait_for_visual waits for (roadmap M6.4).</summary>
[ToolEnum(WindowsAppManagerMcp.Infrastructure.ToolErrorReporting.InvalidArgumentsErrorCode, "condition", NullMeans = "changed")]
public enum VisualWaitCondition
{
    /// <summary>The target looks different from the baseline: since's capture, or the first frame of the wait.</summary>
    [JsonStringEnumMemberName("changed")]
    Changed,

    /// <summary>The target stopped changing: no two frames in a row differed for stableMs.</summary>
    [JsonStringEnumMemberName("stable")]
    Stable
}

/// <summary>What wait_for_visual watches (roadmap M6.4): a window, a screen region, a monitor, or what a capture showed.</summary>
public abstract record VisualTarget
{
    private VisualTarget()
    {
    }

    /// <summary>A window as capture_window captures it: drawn by itself, so windows that cover it do not count.</summary>
    public sealed record Window(nint Handle, bool IncludeFrame) : VisualTarget;

    /// <summary>A rectangle of the screen in physical virtual-screen pixels; the part off the screen is cut off.</summary>
    public sealed record Region(int X, int Y, int Width, int Height) : VisualTarget;

    /// <summary>A monitor by its index, or -1 for the virtual screen.</summary>
    public sealed record Monitor(int Index) : VisualTarget;

    /// <summary>What the capture with this id showed, compared with its visual signature: its window, or its screen rectangle.</summary>
    public sealed record Since(string CaptureId) : VisualTarget;
}

/// <summary>A wait_for_visual call, its arguments checked by the tool (roadmap M6.4).</summary>
public sealed record VisualWaitRequest
{
    public required VisualTarget Target { get; init; }

    public VisualWaitCondition Condition { get; init; } = VisualWaitCondition.Changed;

    /// <summary>How long to wait, 0-60000 ms; 0 checks once.</summary>
    public int TimeoutMs { get; init; } = 10_000;

    /// <summary>How long from one frame to the next, 100-2000 ms.</summary>
    public int PollMs { get; init; } = 200;

    /// <summary>stable only: how long no frame may differ from the one before.</summary>
    public int StableMs { get; init; } = 500;

    /// <summary>
    /// A frame counts as changed only when more than this share of its tiles changed, in percent (0-100): 0 counts any
    /// tile; a little more ignores a clock or a small animation.
    /// </summary>
    public double MinChangedPercent { get; init; }
}

/// <summary>
/// A window the user's target policy denies, over a region or monitor target (owner decision D8): its rectangle was left out
/// of the comparison, so nothing it shows counts as a change and nothing of it is reported.
/// </summary>
/// <param name="Handle">The window.</param>
/// <param name="X">Left edge of the rectangle left out, in screen pixels.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public sealed record MaskedWindow(long Handle, int X, int Y, int Width, int Height);

/// <summary>
/// wait_for_visual's result (roadmap M6.4). Success is Satisfied: a wait that was not satisfied goes out with isError
/// (ToolOutcome) and its errorCode, as wait_for_window's does: WaitTimedOut after timeoutMs (TimedOut true, with what the
/// last check saw), or why the wait could not go on. No pixels are ever returned, only rectangles.
/// </summary>
/// <param name="Condition">changed or stable.</param>
/// <param name="ElapsedMs">How long the wait took.</param>
/// <param name="Checks">How many frames were read.</param>
/// <param name="Reason">changed only: pixels (tiles changed), resized (the window or the area changed size), closed,
/// minimized or hidden (the window is gone from the screen: a visible effect too).</param>
/// <param name="ChangedPercent">The share of the tiles compared that changed, in percent: for changed against the baseline,
/// for stable the last change seen.</param>
/// <param name="ChangedRegions">Where the tiles changed, in screen pixels, merged into at most 8 rectangles, largest first.
/// Not satisfied: for changed the change too small to count, for stable what kept changing.</param>
/// <param name="StableForMs">stable: how long nothing changed.</param>
/// <param name="CaptureId">The last frame, registered as a capture without an image: since for the next wait.</param>
/// <param name="CapturedRegion">The screen rectangle the last frame showed.</param>
/// <param name="CaptureMethod">How the last frame was read: PrintWindow, or ScreenBitBlt (a screen target, or a window that
/// does not answer).</param>
/// <param name="MaskedWindows">Region and monitor targets, and a window target copied from the screen: the windows the policy
/// denies, left out of the comparison.</param>
/// <param name="Warnings">Each starts with its code: ImageBlank, WindowNotResponding, DpiScaledWindow, RegionClipped,
/// BaselineReset.</param>
public sealed record VisualWaitResult(
    bool Success,
    bool Satisfied,
    bool TimedOut,
    string Condition,
    long ElapsedMs,
    int Checks,
    string? Reason = null,
    double? ChangedPercent = null,
    IReadOnlyList<BoundsDto>? ChangedRegions = null,
    long? StableForMs = null,
    string? CaptureId = null,
    CapturedRegion? CapturedRegion = null,
    string? CaptureMethod = null,
    IReadOnlyList<MaskedWindow>? MaskedWindows = null,
    IReadOnlyList<string>? Warnings = null,
    string? Error = null,
    string? ErrorCode = null);
