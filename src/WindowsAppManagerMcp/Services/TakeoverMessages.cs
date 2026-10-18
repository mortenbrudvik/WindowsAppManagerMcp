using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>What the results say when takeover detection (roadmap M7.4) saw the user's input during an operation.</summary>
public static class TakeoverMessages
{
    /// <summary>The UserInterrupted error: <paramref name="done"/> says how far the operation got.</summary>
    public static string Interrupted(string done) =>
        $"{done} The user used the keyboard or mouse while it ran, so takeover detection (WAM_TAKEOVER_DETECTION=stop) stopped " +
        "it there and released what it held. Do not try again at once: the user is using the computer. Ask the user before " +
        "you go on.";

    /// <summary>The warning of mode warn: <paramref name="what"/> names the operation, such as "the text was typed".</summary>
    public static string Warning(string what) =>
        $"The user used the keyboard or mouse while {what} (takeover detection, WAM_TAKEOVER_DETECTION=warn): their input may be " +
        "mixed into it, or the focus or the pointer may have moved. Check the result before you go on.";

    /// <summary>
    /// The warning when detection is on but did not work for all or part of the operation (the monitor marked its watch,
    /// <see cref="TakeoverWatch.Unwatched"/>): the user's input was not watched then, so it neither stopped nor flagged the
    /// operation. <paramref name="what"/> names the operation as for <see cref="Warning"/>; <paramref name="why"/> is the
    /// reason the monitor gave.
    /// </summary>
    public static string Unavailable(string what, TakeoverMode mode, string? why) =>
        $"Takeover detection (WAM_TAKEOVER_DETECTION={mode.ToString().ToLowerInvariant()}) did not work while {what} (for all " +
        "of it or part of it), so the user's keyboard and mouse were not watched then: had the user used them, the operation " +
        $"would not have {(mode == TakeoverMode.Stop ? "stopped" : "said so")}. {why ?? "Its hooks were not installed."} " +
        "get_control_status shows its state now. Check the result before you go on.";

    /// <summary>
    /// The warning the result of a watched operation (type_text, drag, run_steps) carries about takeover detection, or null:
    /// with mode warn, that the user acted (<see cref="Warning"/>); with warn or stop, that detection did not work for some
    /// of it (<see cref="Unavailable"/>), unless the user's input was seen all the same, which the result already reports
    /// (this warning, or UserInterrupted), so it is not contradicted. Read from <paramref name="watch"/> itself, never from
    /// the monitor's status, which can still show an earlier failure. Call it before the watch is disposed or right after.
    /// </summary>
    public static string? For(TakeoverWatch watch, string what)
    {
        ArgumentNullException.ThrowIfNull(watch);
        return Combine(
            watch.Warns ? Warning(what) : null,
            watch.Unwatched && !watch.UserActed ? Unavailable(what, watch.Mode, watch.UnwatchedBecause) : null);
    }

    /// <summary><paramref name="first"/> and <paramref name="second"/> as one warning; null when both are.</summary>
    public static string? Combine(string? first, string? second) =>
        first == null ? second : second == null ? first : $"{first} {second}";
}
