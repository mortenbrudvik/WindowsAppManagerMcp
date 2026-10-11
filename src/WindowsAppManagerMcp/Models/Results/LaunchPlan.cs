using System.Diagnostics;

namespace WindowsAppManagerMcp.Models.Results;

/// <summary>
/// The outcome of validating and resolving a launch request, before anything is started.
/// </summary>
/// <param name="StartInfo">The start info to pass to Process.Start, or null when the request was rejected.</param>
/// <param name="TargetKind">How the resolved target will be run (null when rejected before resolution).</param>
/// <param name="Warning">Validation warnings to report with the launch result.</param>
/// <param name="ErrorMessage">Why the request was rejected.</param>
/// <param name="ErrorCode">A LaunchErrorCode name when the request was rejected.</param>
/// <param name="Target">The resolved target (null when rejected before resolution): what the window wait looks for.</param>
/// <param name="ForceAccessibilityApplied">With forceAccessibility (roadmap M3.3): whether --force-renderer-accessibility is
/// on the command line (the target is a Chromium, Electron or CEF program); null when it was not asked for.</param>
public record LaunchPlan(
    ProcessStartInfo? StartInfo,
    LaunchTargetKind? TargetKind = null,
    string? Warning = null,
    string? ErrorMessage = null,
    string? ErrorCode = null,
    ResolvedLaunchTarget? Target = null,
    bool? ForceAccessibilityApplied = null)
{
    public bool IsValid => StartInfo != null;

    public static LaunchPlan Rejected(string? errorMessage, string? errorCode, string? warning = null) =>
        new(null, Warning: warning, ErrorMessage: errorMessage, ErrorCode: errorCode);
}
