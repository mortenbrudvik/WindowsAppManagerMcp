using System.ComponentModel;
using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// One application to launch: what launch_application and each launch_applications_batch item ask for.
/// </summary>
/// <param name="Executable">A path, a bare program name or a protocol URI.</param>
/// <param name="Arguments">The command-line arguments, one element per argument.</param>
/// <param name="WorkingDirectory">The working directory; null means the program's own folder.</param>
/// <param name="ExpectWindow">
/// A hint for the window wait: which window the launch opens when the server cannot tell from the process that was
/// started (packaged apps, URIs and documents handed to an app that is already running).
/// </param>
public sealed record LaunchRequest(
    string Executable,
    string[]? Arguments = null,
    string? WorkingDirectory = null,
    LaunchWindowHint? ExpectWindow = null);

/// <summary>
/// The window a launch is expected to open, for the cases the server cannot work out on its own: a packaged app
/// started through a URI or shell:AppsFolder, or a document opened by whatever app is registered for it. A window
/// that appears during the wait and matches every field that is set counts as the launch's window.
/// </summary>
/// <param name="ProcessName">Part of the name of the process that owns the window, case-insensitive (for example 'CalculatorApp' or 'SystemSettings').</param>
/// <param name="TitleContains">Part of the window title, case-insensitive.</param>
public sealed record LaunchWindowHint(
    [property: Description("Part of the name of the process that will own the window, case-insensitive (e.g. 'CalculatorApp', 'SystemSettings', 'WINWORD')")]
    string? ProcessName = null,
    [property: Description("Part of the title of the window, case-insensitive")]
    string? TitleContains = null)
{
    /// <summary>True when the hint names neither a process nor a title, so it matches nothing.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(ProcessName) && string.IsNullOrWhiteSpace(TitleContains);
}

/// <summary>
/// What a launch led to. <see cref="Started"/> and <see cref="PossibleHandoff"/> are the outcomes without a window
/// wait; the others come from waiting for the window (waitForWindow), except <see cref="AwaitingUserConsent"/>.
/// </summary>
public enum LaunchStatus
{
    /// <summary>A process was started; the call did not wait for a window.</summary>
    Started,

    /// <summary>A new window of the launch appeared and stayed (owned by the started process, a child of it, or the app it handed off to).</summary>
    WindowObserved,

    /// <summary>No new window appeared, but an existing window of the app came to the front: the app reused it (single-instance apps).</summary>
    ReusedExistingWindow,

    /// <summary>No new window appeared, but an existing window of the app now shows the launched document or URL (a new tab).</summary>
    OpenedInTab,

    /// <summary>
    /// Windows started no new process: the request went to an app that was already running, or a packaged app was
    /// activated. No window was found for it (or the call did not wait).
    /// </summary>
    PossibleHandoff,

    /// <summary>The started process exited, and no window of it or of an app it may have handed off to appeared.</summary>
    ExitedWithoutWindow,

    /// <summary>The started process is still running, but no window of it appeared within the wait.</summary>
    TimedOut,

    /// <summary>
    /// The start did not return in time: Windows is most likely asking the user to approve the program (UAC) on the
    /// secure desktop. It starts only if the user approves.
    /// </summary>
    AwaitingUserConsent
}

public static class LaunchStatusNames
{
    /// <summary>The camelCase name of a status, as the launch tools report it (launchStatus).</summary>
    public static string ToWireName(this LaunchStatus status) => status switch
    {
        LaunchStatus.Started => "started",
        LaunchStatus.WindowObserved => "windowObserved",
        LaunchStatus.ReusedExistingWindow => "reusedExistingWindow",
        LaunchStatus.OpenedInTab => "openedInTab",
        LaunchStatus.PossibleHandoff => "possibleHandoff",
        LaunchStatus.ExitedWithoutWindow => "exitedWithoutWindow",
        LaunchStatus.TimedOut => "timedOut",
        LaunchStatus.AwaitingUserConsent => "awaitingUserConsent",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
