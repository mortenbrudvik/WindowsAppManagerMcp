using System.ComponentModel;

namespace WindowsAppManagerMcp.Models;

// Input models for batch operations. Each item names its window by handle or by a window selector (roadmap M2.4), not both.

/// <summary>A set_windows_bounds_batch item: the visible frame to place the window at.</summary>
public record WindowBoundsPlacement(
    int X,
    int Y,
    int Width,
    int Height,
    long? Handle = null,
    bool BringToFront = true,
    WindowSelector? Window = null
);

/// <summary>
/// A snap_windows_batch item. Position is checked per item (an unknown one fails that item with InvalidSnapPosition);
/// the input schema lists the positions.
/// </summary>
public record WindowSnapPlacement(
    [ToolValues(typeof(SnapPosition))] string Position,
    long? Handle = null,
    int? MonitorIndex = null,
    bool BringToFront = true,
    WindowSelector? Window = null
);

/// <summary>
/// A set_windows_state_batch item. State is checked per item (an unknown one fails that item with InvalidState); the
/// input schema lists the states.
/// </summary>
public record WindowStateChange(
    [ToolValues(typeof(WindowStateRequest))] string State,
    long? Handle = null,
    WindowSelector? Window = null
);

/// <summary>A launch_applications_batch item: what launch_application takes for one application.</summary>
public record ApplicationLaunchRequest(
    string Executable,
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
    [property: Description("Start the .exe as administrator, as launch_application's runAsAdmin (default false)")]
    bool RunAsAdmin = false
);

/// <summary>A close_windows_batch item: the window to close.</summary>
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

public record BoundsDto(int X, int Y, int Width, int Height);
