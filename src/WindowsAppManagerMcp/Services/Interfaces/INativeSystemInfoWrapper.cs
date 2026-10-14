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

    /// <summary>
    /// Where Windows sends mouse wheel input (SystemParametersInfo SPI_GETMOUSEWHEELROUTING): to the window under the
    /// pointer or to the window with the keyboard focus. Null when it cannot be read (Windows before 10 has no such
    /// setting and sends the wheel to the focus window).
    /// </summary>
    MouseWheelRouting? GetMouseWheelRouting();

    /// <summary>
    /// The virtual screen, the bounding rectangle of every monitor in physical pixels (GetSystemMetrics SM_XVIRTUALSCREEN,
    /// SM_YVIRTUALSCREEN, SM_CXVIRTUALSCREEN and SM_CYVIRTUALSCREEN, which a per-monitor DPI aware thread reads unscaled):
    /// what Windows maps a MOUSEEVENTF_VIRTUALDESK move's normalized coordinates across. Its origin is negative when a
    /// monitor is left of or above the primary one. Null when it cannot be read (no size); the native wrapper reads it on
    /// every call, so a monitor change is seen at once.
    /// </summary>
    VirtualScreenBounds? GetVirtualScreen();

    /// <summary>
    /// True when the user swapped the mouse buttons ("Primary mouse button: Right" in Windows' mouse settings;
    /// GetSystemMetrics SM_SWAPBUTTON): Windows then turns the left button's events into right-button messages and the
    /// reverse, injected ones too. Null means unknown; the native wrapper always answers, so only a fake returns null.
    /// </summary>
    bool? AreMouseButtonsSwapped();
}
