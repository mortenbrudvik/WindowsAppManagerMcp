namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What the server does when the user uses the keyboard or mouse while a long input operation runs (roadmap M7.4): the user
/// sets it with WAM_TAKEOVER_DETECTION; the agent cannot change it.
/// </summary>
public enum TakeoverMode
{
    /// <summary>Nothing is watched (the default).</summary>
    Off,

    /// <summary>The operation goes on, and its result has a warning that the user used the keyboard or mouse meanwhile.</summary>
    Warn,

    /// <summary>The operation stops (UserInterrupted), reports how far it got, and releases what it held.</summary>
    Stop
}

/// <summary>Whether takeover detection works.</summary>
public enum TakeoverState
{
    /// <summary>Turned off (WAM_TAKEOVER_DETECTION not set, or off).</summary>
    Off,

    /// <summary>
    /// On, and nothing runs: the hooks are installed only while type_text, drag or run_steps runs, so between them the server
    /// watches nothing.
    /// </summary>
    Ready,

    /// <summary>On, and an operation is being watched now.</summary>
    Watching,

    /// <summary>On, but Windows did not install the hooks: nothing is detected, and operations run as with off.</summary>
    Unavailable
}

/// <summary>Takeover detection as get_control_status reports it (roadmap M7.4).</summary>
/// <param name="Mode">off, warn or stop.</param>
/// <param name="State">Whether it works now.</param>
/// <param name="Source">Where the mode comes from: "default" or "WAM_TAKEOVER_DETECTION".</param>
/// <param name="Error">Why the configured value was not used, or why the hooks could not be installed.</param>
public sealed record TakeoverStatus(TakeoverMode Mode, TakeoverState State, string Source, string? Error = null);
