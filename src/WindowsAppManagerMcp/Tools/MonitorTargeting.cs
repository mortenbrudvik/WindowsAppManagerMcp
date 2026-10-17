using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// The `monitor` parameter of the tools that take a monitor (roadmap M6.7, owner decision D13): snap_window,
/// move_window_to_monitor, snap_windows_batch's items, take_screenshot, read_screen_text, find_text_on_screen,
/// wait_for_visual and the launch tools' placement. Each keeps monitorIndex, deprecated until 3.0.0 (owner decision D1);
/// giving both is InvalidArguments. A name is resolved to an index here, before anything is done, and the index goes on to
/// the tool's own checks, so an index out of range is still the tool's InvalidMonitorIndex.
/// </summary>
internal static class MonitorTargeting
{
    /// <summary>
    /// The monitor index the call names with <paramref name="monitorIndex"/> or <paramref name="monitor"/>, or null when it
    /// names none. <paramref name="allowAll"/>: whether 'all' (<see cref="MonitorSelection.AllMonitors"/>) is a target.
    /// </summary>
    /// <exception cref="ToolErrorException">
    /// Both are given, or 'all' where it is not a target (InvalidArguments), or the name fits no monitor (MonitorNotFound) or
    /// two (AmbiguousMonitor). <paramref name="nothingDone"/> ends the message.
    /// </exception>
    public static int? Index(IMonitorService? monitors, int? monitorIndex, MonitorSelector? monitor, bool allowAll, string nothingDone)
    {
        if (TryIndex(monitors, monitorIndex, monitor, allowAll, string.Empty, out var index, out var errorCode, out var error))
        {
            return index;
        }

        throw new ToolErrorException(errorCode!, $"{WithoutNothingDone(error!)} {nothingDone}");
    }

    /// <summary>A resolution's error without its closing "Nothing was done.", for a tool that says what it did not do itself.</summary>
    public static string WithoutNothingDone(string error) =>
        error.EndsWith(NothingDone, StringComparison.Ordinal) ? error[..^NothingDone.Length] : error;

    private const string NothingDone = " Nothing was done.";

    /// <summary>
    /// <see cref="Index"/> for a batch item or a placement, whose failure is its own: false with the error code and an error
    /// that starts with <paramref name="prefix"/> (the argument's path, such as "placement.").
    /// </summary>
    public static bool TryIndex(IMonitorService? monitors, int? monitorIndex, MonitorSelector? monitor, bool allowAll, string prefix,
        out int? index, out string? errorCode, out string? error)
    {
        index = monitorIndex;
        errorCode = null;
        error = null;
        if (monitor == null)
        {
            return true;
        }

        if (monitorIndex != null)
        {
            errorCode = ToolErrorReporting.InvalidArgumentsErrorCode;
            error = $"Pass {prefix}monitor or {prefix}monitorIndex, not both ({prefix}monitorIndex is deprecated: use {prefix}monitor). " +
                    "Nothing was done.";
            return false;
        }

        var resolution = monitor.Index is { } given
            ? MonitorResolution.Found(given, null)
            : (monitors ?? throw new InvalidOperationException("No monitor service is registered.")).ResolveMonitor(monitor);
        if (!resolution.Succeeded)
        {
            errorCode = resolution.ErrorCode;
            error = prefix.Length == 0 ? resolution.Error : $"{prefix}monitor: {resolution.Error}";
            return false;
        }

        if (resolution.Index == MonitorSelection.AllMonitors && monitor.Index == null && !allowAll)
        {
            errorCode = ToolErrorReporting.InvalidArgumentsErrorCode;
            error = $"{prefix}monitor '{monitor}' names every monitor, and this takes one. Nothing was done.";
            return false;
        }

        index = resolution.Index;
        return true;
    }
}
