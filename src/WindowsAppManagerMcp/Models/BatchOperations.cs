using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

// Input models for batch operations. Each item names its window by handle or by a window selector (roadmap M2.4), not both.
// An unknown member of an item is refused (InvalidArguments) rather than ignored, as WindowSelector's and LaunchPlacement's
// are: a misspelled field would otherwise act without it (a launch without its arguments, a placement in pixels that meant
// percent).

/// <summary>
/// A set_windows_bounds_batch item: the visible frame to place the window at, in pixels (it takes no unit, relativeTo or
/// monitor, which set_window_bounds does). The four numbers are nullable so that an item without one fails alone
/// (InvalidArguments) instead of binding 0 and moving its window to the origin; the input schema lists them as required
/// integers, without null (<see cref="ToolRequiredAttribute"/>), since a null fails the item too.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record WindowBoundsPlacement(
    [property: ToolRequired] int? X,
    [property: ToolRequired] int? Y,
    [property: ToolRequired] int? Width,
    [property: ToolRequired] int? Height,
    long? Handle = null,
    bool BringToFront = true,
    WindowSelector? Window = null
);

/// <summary>
/// A snap_windows_batch item. Position is checked per item (an unknown one fails that item with InvalidSnapPosition);
/// the input schema lists the positions. An unknown member, such as the monitorIndex 3.0.0 removed, is refused
/// (InvalidArguments), so it is never ignored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record WindowSnapPlacement(
    [ToolValues(typeof(SnapPosition))] string Position,
    long? Handle = null,
    bool BringToFront = true,
    WindowSelector? Window = null,
    [property: Description(MonitorSelectorDescriptions.Monitor)]
    MonitorSelector? Monitor = null
);

/// <summary>
/// A set_windows_state_batch item. State is checked per item (an unknown one fails that item with InvalidState); the
/// input schema lists the states.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record WindowStateChange(
    [ToolValues(typeof(WindowStateRequest))] string State,
    long? Handle = null,
    WindowSelector? Window = null
);

/// <summary>A launch_applications_batch item: what launch_application takes for one application.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record ApplicationLaunchRequest(
    string Executable,
    [property: Description("Checked as launch_application's arguments are (for example UnsafeChromiumSwitch)")]
    string[]? Arguments = null,
    string? WorkingDirectory = null,
    LaunchWindowHint? ExpectWindow = null,
    [property: Description("Where to put the item's window once it is found, as launch_application's placement")]
    LaunchPlacement? Placement = null,
    [property: Description("Use an open window of the app instead of starting it, as launch_application's reuseExisting (default false)")]
    bool ReuseExisting = false,
    [property: Description("Start a Chromium, Electron or CEF .exe with --force-renderer-accessibility, as launch_application's " +
                           "forceAccessibility (default false)")]
    bool ForceAccessibility = false,
    [property: Description("Start the .exe as administrator, as launch_application's runAsAdmin (default false), but only where " +
                           "Windows asks the user (UAC): a batch never asks the user itself (ElevationNotConfirmed)")]
    bool RunAsAdmin = false
);

/// <summary>A close_windows_batch item: the window to close.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record WindowCloseRequest(long? Handle = null, WindowSelector? Window = null);

// Output models for batch operations
public record BatchWindowResult(
    int TotalRequested,
    int Succeeded,
    int Failed,
    IReadOnlyList<WindowResultItem> Results
);

/// <summary>
/// One window of a batch. For the geometry batches (roadmap M2.3) the window as it is afterwards, failed or not (left out
/// for an invalid handle): Bounds is its visible frame, OuterBounds its window rect with the invisible resize borders (the
/// bounds find_windows reports), State normal, minimized or maximized, MonitorIndex its monitor. A minimized window has no
/// bounds. Status and Prompt are close_windows_batch's (roadmap M2.5): what became of the window after its close message,
/// and the prompt that keeps it open; the other batches leave them out. Candidates lists the windows an item's selector
/// matched when it named no single window (roadmap M2.4: AmbiguousWindow); Handle is then 0.
/// </summary>
public record WindowResultItem(
    long Handle,
    bool Success,
    BoundsDto? Bounds = null,
    BoundsDto? OuterBounds = null,
    string? State = null,
    int? MonitorIndex = null,
    string? Error = null,
    string? ErrorCode = null,
    string? Status = null,
    ClosePromptDto? Prompt = null,
    IReadOnlyList<WindowCandidateDto>? Candidates = null
);

public record BatchLaunchResult(
    int TotalRequested,
    int Succeeded,
    int Failed,
    IReadOnlyList<LaunchResultItem> Results
);

/// <summary>
/// A launch_applications_batch result: as launch_application's, for one application. LaunchStatus, Window and
/// LauncherExited are left out when the launch failed (and LauncherExited also without a window wait), Placement
/// without a placement or when the launch failed, and ForceAccessibilityApplied when the item did not ask for
/// forceAccessibility (roadmap M3.F25). Elevation is "uacPrompt" for an item started with runAsAdmin (roadmap M6.5),
/// left out otherwise.
/// </summary>
public record LaunchResultItem(
    string Executable,
    bool Success,
    int? ProcessId = null,
    long? WindowHandle = null,
    string? Error = null,
    string? Warning = null,
    string? ErrorCode = null,
    string? LaunchStatus = null,
    WindowsAppManagerMcp.Tools.WindowInfoDto? Window = null,
    bool? LauncherExited = null,
    WindowsAppManagerMcp.Tools.LaunchPlacementResultDto? Placement = null,
    bool? ForceAccessibilityApplied = null,
    string? Elevation = null
);

/// <summary>
/// A rectangle in pixels, as the results write it. It is no argument: a launch placement's bounds are
/// <see cref="PlacementBounds"/>, whose members are checked for being there instead of binding 0 when they are missing.
/// </summary>
public record BoundsDto(int X, int Y, int Width, int Height);
