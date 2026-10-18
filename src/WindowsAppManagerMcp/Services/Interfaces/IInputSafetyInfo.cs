using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The safety settings in force, for reporting: what the host guard recorded, the target policy, and the
/// destructive chords that need a target. get_server_info reports them (ServerInfoService).
/// </summary>
public interface IInputSafetyInfo
{
    /// <summary>
    /// Reads the current settings. Cheap; nothing is probed, except that the host guard reads the server's ancestors again
    /// while they are unknown (at most once a second, see HostGuard).
    /// </summary>
    InputSafetySnapshot Read();
}

/// <summary>The safety settings in force.</summary>
/// <param name="Host">What the host guard recorded at server start: the agent's host processes and windows.</param>
/// <param name="Policy">The target policy (WAM_DENY_PROCESSES, WAM_ALLOW_PROCESSES).</param>
/// <param name="DestructiveChords">The send_keys chords that need windowHandle, in send_keys syntax.</param>
/// <param name="DestructiveChordSource">"default", or WAM_DESTRUCTIVE_CHORDS.</param>
public sealed record InputSafetySnapshot(
    HostSnapshot Host,
    TargetPolicySettings Policy,
    IReadOnlyList<string> DestructiveChords,
    string DestructiveChordSource);
