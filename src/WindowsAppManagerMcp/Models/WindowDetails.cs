namespace WindowsAppManagerMcp.Models;

/// <summary>
/// One window read in full for get_window_info's detail 'full' (roadmap M2.4): what a listing reports
/// (<see cref="Window"/>) plus the state that explains why an action may fail or where it will land. Every value is a
/// read that never waits for the window's thread.
/// </summary>
/// <param name="Window">The window as the listings report it.</param>
/// <param name="IsForeground">Whether it is the foreground window (GetForegroundWindow).</param>
/// <param name="ZOrder">
/// Its position among all top-level windows, hidden ones included, front to back (0 is the top one); null for a child window.
/// </param>
/// <param name="IsTopmost">Whether it stays above normal windows (WS_EX_TOPMOST).</param>
/// <param name="IsEnabled">False when it is disabled (WS_DISABLED), usually by a modal dialog that takes its input.</param>
/// <param name="IsResponding">False when Windows reports it hung (IsHungAppWindow: no message processed for 5 seconds).</param>
/// <param name="Dpi">Its DPI (GetDpiForWindow; 96 is 100%); null when it cannot be read.</param>
public sealed record WindowDetails(
    WindowInfo Window,
    bool IsForeground,
    int? ZOrder,
    bool IsTopmost,
    bool IsEnabled,
    bool IsResponding,
    int? Dpi)
{
    /// <summary>Where a minimized window comes back to (issue #12); null for any other window, and when it cannot be read.</summary>
    public WindowRestore? Restore { get; init; }
}

/// <summary>
/// Where a minimized window comes back to when it is restored (issue #12), from GetWindowPlacement. For a window Windows
/// snapped (Aero snap) before it was minimized, <see cref="Bounds"/> is the rectangle from before the snap: Windows keeps
/// the snapped one elsewhere, and nothing tells such a window apart while it is minimized.
/// </summary>
/// <param name="State">Normal, or Maximized when it comes back maximized (WPF_RESTORETOMAXIMIZED).</param>
/// <param name="Bounds">
/// Its restore rectangle in screen coordinates: the window rect with the invisible resize borders, as a row's bounds is. A
/// window that comes back maximized fills its monitor's work area, and returns to this rectangle when it is restored again.
/// </param>
/// <param name="Monitor">The monitor with most of <see cref="Bounds"/> (else the nearest); null when no monitor could be read.</param>
public sealed record WindowRestore(WindowState State, WindowRect Bounds, MonitorInfo? Monitor);
