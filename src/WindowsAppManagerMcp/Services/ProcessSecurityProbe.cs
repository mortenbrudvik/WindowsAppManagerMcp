using System.Collections.Concurrent;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Integrity, elevation and desktop-state probes on top of <see cref="INativeSecurityWrapper"/>.
/// Integrity results are cached per process ID and creation time; desktop and session state are
/// read live on every call, because they change while the server runs.
/// </summary>
/// <remarks>
/// A process can lower its own integrity level after it starts, but never raise it, so a cached
/// level can only overstate how privileged a process is. That errs towards refusing input.
/// </remarks>
public sealed class ProcessSecurityProbe : IProcessSecurityProbe
{
    /// <summary>
    /// How many processes the cache holds before it starts over. It is keyed by process ID, so it only
    /// grows with the number of distinct IDs the server asks about.
    /// </summary>
    public const int MaxCachedProcesses = 1024;

    /// <summary>WTS_SESSIONSTATE_LOCK in WTSINFOEX_LEVEL1.SessionFlags.</summary>
    private const int SessionStateLock = 0;

    /// <summary>WTS_SESSIONSTATE_UNLOCK in WTSINFOEX_LEVEL1.SessionFlags.</summary>
    private const int SessionStateUnlock = 1;

    /// <summary>WTS_CONNECTSTATE_CLASS.WTSDisconnected.</summary>
    private const int ConnectStateDisconnected = 4;

    private readonly INativeSecurityWrapper _native;
    private readonly int _serverProcessId;
    private readonly Lazy<ProcessIntegrity> _serverIntegrity;
    private readonly ConcurrentDictionary<int, CachedIntegrity> _cache = new();

    public ProcessSecurityProbe(INativeSecurityWrapper native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _serverProcessId = Environment.ProcessId;
        _serverIntegrity = new Lazy<ProcessIntegrity>(
            () => Interpret(_serverProcessId, _native.QueryProcessToken(_serverProcessId), server: null),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ProcessIntegrity ServerIntegrity => _serverIntegrity.Value;

    public ElevationPrompt GetElevationPrompt() => _native.ReadUacSettings() switch
    {
        null => ElevationPrompt.Unknown,
        { EnableLua: 0 } or { ConsentPromptBehaviorAdmin: 0 } => ElevationPrompt.Silent,
        // Review M6.5 #1: the built-in Administrator outside Admin Approval Mode (the default) is never asked
        { IsBuiltInAdministrator: true, FilterAdministratorToken: not 1 } => ElevationPrompt.Silent,
        _ => ElevationPrompt.Prompts
    };

    public ProcessIntegrity GetIntegrity(int processId)
    {
        if (processId <= 0)
        {
            return new ProcessIntegrity(processId, IntegrityLevel.Unknown, null, null, "not a valid process ID");
        }

        // The creation time is read only when there is an entry to check it against
        if (_cache.TryGetValue(processId, out var cached) &&
            _native.GetProcessCreationTime(processId) is { } creationTime &&
            cached.CreationTime == creationTime)
        {
            return cached.Integrity;
        }

        var token = _native.QueryProcessToken(processId);
        var integrity = Interpret(processId, token, ServerIntegrity);

        // Cache under the creation time the token query saw, so a PID reused in between is not mixed
        // up with the process that had it before. Unknown results may be transient; read them again.
        if (token.CreationTime is { } queriedCreationTime && integrity.Level != IntegrityLevel.Unknown)
        {
            if (_cache.Count >= MaxCachedProcesses)
            {
                _cache.Clear();
            }

            _cache[processId] = new CachedIntegrity(queriedCreationTime, integrity);
        }

        return integrity;
    }

    public bool IsInputDesktopAvailable()
    {
        // The secure desktop (UAC prompt, Ctrl+Alt+Del) cannot be opened by the server.
        var input = _native.QueryInputDesktop();
        if (!input.Opened)
        {
            return false;
        }

        // SendInput only reaches the input desktop from a thread on that desktop.
        var serverDesktop = _native.GetThreadDesktopName();
        if (input.Name != null && serverDesktop != null &&
            !string.Equals(input.Name, serverDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // In a disconnected session OpenInputDesktop returns the desktop that becomes active on
        // reconnect, but no input reaches it until then.
        return _native.QuerySessionState()?.ConnectState != ConnectStateDisconnected;
    }

    // Fails open: an unknown or unreadable state is not a lock, so the input guard goes on to its other checks
    public bool IsSessionLocked() => _native.QuerySessionState()?.SessionFlags == SessionStateLock;

    public SessionLockState GetSessionLock() => _native.QuerySessionState()?.SessionFlags switch
    {
        SessionStateLock => SessionLockState.Locked,
        SessionStateUnlock => SessionLockState.Unlocked,
        _ => SessionLockState.Unknown
    };

    /// <summary>
    /// Turns a token query into an integrity result. <paramref name="server"/> is the server's own
    /// integrity, or null while that is what is being read.
    /// </summary>
    private static ProcessIntegrity Interpret(int processId, ProcessTokenQuery token, ProcessIntegrity? server)
    {
        switch (token.Status)
        {
            case TokenQueryStatus.Succeeded when token.IntegrityRid is { } rid:
                return new ProcessIntegrity(processId, ProcessIntegrity.LevelFromRid(rid), rid, token.IsElevated, null);

            case TokenQueryStatus.ProcessNotFound:
                return new ProcessIntegrity(processId, IntegrityLevel.Unknown, null, null, "no process with this ID is running");

            case TokenQueryStatus.TokenAccessDenied when server?.IntegrityRid is < ProcessIntegrity.HighRid:
                // The process lets the server in, but its token keeps a lower-integrity caller out
                return new ProcessIntegrity(processId, IntegrityLevel.LikelyElevated, null, token.IsElevated,
                    "the process can be opened but its token cannot (access denied), so it most likely runs elevated");

            case TokenQueryStatus.TokenAccessDenied:
                return new ProcessIntegrity(processId, IntegrityLevel.Restricted, null, token.IsElevated,
                    "the process token cannot be opened (access denied)");

            case TokenQueryStatus.ProcessAccessDenied:
                // The process DACL: another account (SYSTEM, a service, another user) or a hardened
                // process. That says nothing reliable about its integrity level.
                return new ProcessIntegrity(processId, IntegrityLevel.Restricted, null, token.IsElevated,
                    "the process cannot be opened (access denied): it runs as SYSTEM or under another account, " +
                    "or restricts access to its process, so its integrity level is unknown");

            default:
                return new ProcessIntegrity(processId, IntegrityLevel.Unknown, null, token.IsElevated,
                    $"the process token cannot be read (Win32 error {token.Win32Error})");
        }
    }

    private sealed record CachedIntegrity(DateTime CreationTime, ProcessIntegrity Integrity);
}
