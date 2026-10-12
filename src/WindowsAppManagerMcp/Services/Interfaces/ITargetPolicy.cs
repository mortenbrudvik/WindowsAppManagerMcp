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
}
