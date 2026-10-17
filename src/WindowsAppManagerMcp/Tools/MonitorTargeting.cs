using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// What a call's monitor argument named (roadmap M6.7): <see cref="Index"/> as given or resolved, null when it named none;
/// <see cref="Named"/> the monitor a name resolved to (null for an index), so a tool that acts later, inside the input gate,
/// finds that same monitor again by its id or device name (<see cref="MonitorTargeting.Current"/>) rather than trusting an
/// index docking may have renumbered meanwhile.
/// </summary>
internal readonly record struct MonitorChoice(int? Index, MonitorInfo? Named = null, string? Name = null);

/// <summary>
/// The `monitor` parameter of the tools that take a monitor (roadmap M6.7, owner decision D13): snap_window,
/// move_window_to_monitor, set_window_bounds (in percent), snap_windows_batch's items, take_screenshot, read_screen_text,
/// find_text_on_screen, wait_for_visual and the launch tools' placement. 3.0.0 removed the monitorIndex they also took
/// (owner decision D1). A name is resolved here, before anything is done; an index goes on to the tool's own checks, so an index out of range is still the tool's InvalidMonitorIndex, but -1 (every monitor) is
/// refused here for a tool that takes one monitor.
/// </summary>
internal static class MonitorTargeting
{
    private const string NothingDone = " Nothing was done.";

    /// <summary>
    /// The monitor index the call names with <paramref name="monitor"/>, or null when it names none, for a tool that acts on it at once (the captures). <paramref name="allowAll"/>: whether 'all' and -1
    /// (<see cref="MonitorSelection.AllMonitors"/>) are a target.
    /// </summary>
    /// <exception cref="ToolErrorException">As <see cref="Choose"/>.</exception>
    public static int? Index(IMonitorService? monitors, MonitorSelector? monitor, bool allowAll, string nothingDone) =>
        Choose(monitors, monitor, allowAll, nothingDone).Index;

    /// <summary>What the call names, for a tool that acts later: pass it to <see cref="Current"/> inside the input gate.</summary>
    /// <exception cref="ToolErrorException">
    /// 'all' where it is not a target (InvalidArguments), -1 where it is not a target (InvalidMonitorIndex),
    /// or the name fits no monitor (MonitorNotFound) or two (AmbiguousMonitor). <paramref name="nothingDone"/> ends the message.
    /// </exception>
    public static MonitorChoice Choose(IMonitorService? monitors, MonitorSelector? monitor, bool allowAll, string nothingDone) =>
        TryChoose(monitors, monitor, allowAll, string.Empty, out var choice, out var errorCode, out var error)
            ? choice
            : throw new ToolErrorException(errorCode!, $"{WithoutNothingDone(error!)} {nothingDone}");

    /// <summary>
    /// <see cref="Choose"/> for a placement or a batch item, whose failure is its own: false with the error code and an error
    /// that starts with <paramref name="prefix"/> (the argument's path, such as "placement.").
    /// </summary>
    public static bool TryChoose(IMonitorService? monitors, MonitorSelector? monitor, bool allowAll, string prefix,
        out MonitorChoice choice, out string? errorCode, out string? error)
    {
        choice = default;
        errorCode = null;
        error = null;
        if (monitor == null)
        {
            return true;
        }

        if (monitor.Index is { } given)
        {
            if (given < 0 && !allowAll)
            {
                return Fail(nameof(WindowErrorCode.InvalidMonitorIndex),
                    $"{prefix}monitor {given} is not a monitor: indexes start at 0 (see get_monitors), and -1, every monitor, is " +
                    "only for the captures. Nothing was done.", out errorCode, out error);
            }

            choice = new MonitorChoice(given);
            return true;
        }

        var resolution = (monitors ?? throw new InvalidOperationException("No monitor service is registered.")).ResolveMonitor(monitor);
        if (!resolution.Succeeded)
        {
            return Fail(resolution.ErrorCode!, prefix.Length == 0 ? resolution.Error! : $"{prefix}monitor: {resolution.Error}",
                out errorCode, out error);
        }

        if (resolution.Index == MonitorSelection.AllMonitors && !allowAll)
        {
            return Fail(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"{prefix}monitor '{monitor}' names every monitor, and this takes one. Nothing was done.", out errorCode, out error);
        }

        choice = new MonitorChoice(resolution.Index, resolution.Monitor, monitor.Name);
        return true;
    }

    /// <summary>
    /// The index of the monitor <paramref name="choice"/> named, read again now (inside the input gate): an index as it was
    /// given; a named monitor found again by its id, or by its device name when it has none.
    /// </summary>
    /// <exception cref="ToolErrorException">MonitorNotFound: the named monitor is gone. <paramref name="nothingDone"/> ends the message.</exception>
    public static int? Current(IMonitorService? monitors, MonitorChoice choice, string nothingDone) =>
        TryCurrent(monitors, choice, string.Empty, out var index, out var error)
            ? index
            : throw new ToolErrorException(nameof(WindowErrorCode.MonitorNotFound), $"{WithoutNothingDone(error!)} {nothingDone}");

    /// <summary><see cref="Current"/> for a batch item: false with a MonitorNotFound error that starts with <paramref name="prefix"/>.</summary>
    public static bool TryCurrent(IMonitorService? monitors, MonitorChoice choice, string prefix, out int? index, out string? error)
    {
        index = choice.Index;
        error = null;
        if (choice.Named is not { } named)
        {
            return true;
        }

        var now = (monitors ?? throw new InvalidOperationException("No monitor service is registered.")).GetAllMonitors()
            .FirstOrDefault(m => named.Id != null
                ? string.Equals(m.Id, named.Id, StringComparison.OrdinalIgnoreCase)
                : string.Equals(m.DeviceName, named.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (now == null)
        {
            error = $"{prefix}monitor '{choice.Name}' named monitor {named.Id ?? named.DeviceName}, which is gone: the displays " +
                    "changed during the call. Call get_monitors. Nothing was done.";
            return false;
        }

        index = now.Index;
        return true;
    }

    /// <summary>A resolution's error without its closing "Nothing was done.", for a tool that says what it did not do itself.</summary>
    public static string WithoutNothingDone(string error) =>
        error.EndsWith(NothingDone, StringComparison.Ordinal) ? error[..^NothingDone.Length] : error;

    private static bool Fail(string code, string message, out string? errorCode, out string? error)
    {
        errorCode = code;
        error = message;
        return false;
    }
}
