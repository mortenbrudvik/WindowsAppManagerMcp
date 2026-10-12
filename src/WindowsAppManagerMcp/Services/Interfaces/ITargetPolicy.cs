using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The server-enforced target policy (roadmap M1.12; see <see cref="TargetPolicySettings"/>). The input
/// guard, close_window and close_windows_batch, the window changes (focus_window, move, resize, bounds,
/// state, snap, move to a monitor, their batches and apply_layout), kill_process, capture_window,
/// capture_with_elements and the UI Automation reads (<see cref="UiaTargets.ReadVerdict"/>) ask it before they act or read,
/// and refuse with TargetDeniedByPolicy. It is matched on the image name of the target window's process (for a UWP window
/// also its frame host); no tool argument overrides it.
/// </summary>
public interface ITargetPolicy
{
    /// <summary>The policy in force, for reporting (the start log and get_server_info).</summary>
    TargetPolicySettings Settings { get; }

    /// <summary>
    /// Decides for a target that goes by <paramref name="processNames"/> (a window's process, and for a
    /// UWP window also its frame host). Refused when any name is on the deny list. With an allow list,
    /// allowed only when one of the names is on it; a target without a readable name is then refused.
    /// Names compare case-insensitively, without a folder or ".exe".
    /// </summary>
    TargetPolicyVerdict Evaluate(IReadOnlyCollection<string?> processNames);
}

/// <summary>Convenience overloads for <see cref="ITargetPolicy"/>.</summary>
public static class TargetPolicyExtensions
{
    /// <summary>Decides for a window: its process and, for a UWP window, its frame host process.</summary>
    public static TargetPolicyVerdict Evaluate(this ITargetPolicy policy, WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(window);
        return policy.Evaluate([window.ProcessName, window.HostProcessName]);
    }

    /// <summary>
    /// Whether the policy can refuse anything: it has a deny list or an allow list. Only then does a target whose process
    /// cannot be named need checking. A policy whose settings are unknown counts as one that can.
    /// </summary>
    public static bool DeniesAnything(this ITargetPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Settings is not { DeniedProcesses.Count: 0, AllowedProcesses: null };
    }

    /// <summary>
    /// Decides for a window as <see cref="Evaluate(ITargetPolicy, WindowInfo)"/> does, but fails closed as the UI Automation
    /// reads do (<see cref="UiaTargets.ReadVerdict"/>): while the policy denies anything, a window whose process, or for a
    /// UWP window whose frame host, cannot be named (<see cref="WindowInfo.UnknownProcessName"/>) is refused
    /// (<see cref="TargetPolicyVerdict.Unreadable"/>). The input guard, the captures, get_desktop_state's choice of window
    /// and focus, and the window service's check (<see cref="IWindowService.EvaluateTargetPolicy"/>: the window changes, the
    /// closes, focus_window and the windows a restore would show) decide with it.
    /// </summary>
    public static TargetPolicyVerdict EvaluateFailClosed(this ITargetPolicy policy, WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return policy.EvaluateFailClosed(window.HostProcessName == null
            ? [window.ProcessName]
            : [window.ProcessName, window.HostProcessName]);
    }

    /// <summary>
    /// Decides for a target that goes by <paramref name="processNames"/>, failing closed: while the policy denies anything,
    /// a target with no name, or with a name that is blank or <see cref="WindowInfo.UnknownProcessName"/>, is refused.
    /// </summary>
    public static TargetPolicyVerdict EvaluateFailClosed(this ITargetPolicy policy, IReadOnlyCollection<string?> processNames)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(processNames);
        var verdict = policy.Evaluate(processNames);
        return verdict.Allowed && policy.DeniesAnything() && (processNames.Count == 0 || processNames.Any(IsUnnamed))
            ? TargetPolicyVerdict.Unreadable
            : verdict;
    }

    private static bool IsUnnamed(string? processName) =>
        string.IsNullOrWhiteSpace(processName) || string.Equals(processName, WindowInfo.UnknownProcessName, StringComparison.Ordinal);
}
