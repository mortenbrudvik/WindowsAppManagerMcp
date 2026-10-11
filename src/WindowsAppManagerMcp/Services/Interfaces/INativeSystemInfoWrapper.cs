using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Read-only queries about the server process and the desktop settings that decide how its coordinates and window
/// activation behave: the effective DPI awareness, the foreground lock timeout and the session the server runs in.
/// Nothing here changes a setting, and no member throws.
/// </summary>
public interface INativeSystemInfoWrapper
{
    /// <summary>
    /// The calling thread's DPI awareness (GetThreadDpiAwarenessContext). The server never overrides it per thread,
    /// so it is the process's: what the app manifest set. <see cref="DpiAwareness.Unknown"/> when it cannot be read.
    /// </summary>
    DpiAwareness GetDpiAwareness();

    /// <summary>
    /// How long, in milliseconds, Windows keeps a process from taking the foreground after the user's last input
    /// (SystemParametersInfo SPI_GETFOREGROUNDLOCKTIMEOUT; 200000 by default), or null when it cannot be read.
    /// </summary>
    int? GetForegroundLockTimeout();

    /// <summary>The Remote Desktop Services session the server runs in, or null when it cannot be read.</summary>
    int? GetSessionId();
}
