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
/// started (packaged apps, URIs and documents handed to an app that is already running). With
/// <paramref name="ReuseExisting"/>, the open window reused must match it too.
/// </param>
/// <param name="ReuseExisting">
/// Use a window of the app that is already open instead of starting the program (roadmap M3.13); see
/// IProcessService.LaunchApplicationAsync for which window counts.
/// </param>
/// <param name="ForceAccessibility">
/// Start a Chromium browser, an Electron or a CEF app with --force-renderer-accessibility, so its pages' accessibility
/// trees are built from the start (roadmap M3.3). Added only for such a program (Services.ChromiumPrograms); for any other
/// target nothing is added and the result's warning says why.
/// </param>
public sealed record LaunchRequest(
    string Executable,
    string[]? Arguments = null,
    string? WorkingDirectory = null,
    LaunchWindowHint? ExpectWindow = null,
    bool ReuseExisting = false,
    bool ForceAccessibility = false)
{
    /// <summary>
    /// Count only windows of the process that was started and the processes it started, never a window of another
    /// instance of the app (by name or path) or of <see cref="ExpectWindow"/>. A launch that hands off to a running
    /// instance then ends as <see cref="LaunchStatus.ExitedWithoutWindow"/>, not with that instance's window.
    /// launch_browser_with_debug sets it (roadmap M5.1): the user's own browser window is never the debug browser's. No
    /// tool parameter sets it.
    /// </summary>
    public bool OnlyStartedProcess { get; init; }

    /// <summary>
    /// Start the program elevated, as administrator (ProcessStartInfo.Verb "runas", roadmap M6.5): Windows asks the user
    /// through User Account Control. Only a program's .exe; any other target is refused (InvalidArguments).
    /// </summary>
    public bool RunAsAdmin { get; init; }

    /// <summary>
    /// With <see cref="RunAsAdmin"/>: the launch the user approved through the client (elicitation), which an elevation
    /// Windows would not ask about needs (roadmap M6.5, owner decision D11; review M6.5 #1, #2). Only that launch runs: when
    /// planning the request again gives another program or command line, nothing is started. Only launch_application sets
    /// it, after the user's yes to the question that showed it.
    /// </summary>
    public ElevationApproval? ApprovedElevation { get; init; }

    /// <summary>
    /// Called with the started process's ID as soon as the start returns one, before the window wait. launch_browser_with_debug
    /// sets it (roadmap M5.4), so that a call cancelled during the wait can still close the browser it started: its debug
    /// port has no authentication, and nobody else learns its process ID. No tool parameter sets it.
    /// </summary>
    public Action<int>? OnStarted { get; init; }

    /// <summary>
    /// Called when the call is cancelled while the start itself still runs (CreateProcess or ShellExecute has not
    /// returned), with that start: the receiver owns the process handle it brings. launch_browser_with_debug sets it
    /// (roadmap M5.4), so that the browser a late start brings is ended too, and its profile deleted only after that.
    /// Without it, the handle is released when it comes. No tool parameter sets it.
    /// </summary>
    public Action<Task<Services.Interfaces.IProcessHandle?>>? OnStartAbandoned { get; init; }
}

/// <summary>
/// The window a launch is expected to open, for the cases the server cannot work out on its own: an app started through
/// a URI or a desktop app's shell:AppsFolder entry, or a document opened by whatever app is registered for it (a packaged
/// app's windows are told by its package, roadmap M6.5). A window
/// that appears during the wait and matches every field that is set counts as the launch's window; reuseExisting reuses
/// only an open window that matches it (a web address needs <see cref="TitleContains"/>, since a page sets its own title).
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
    /// The start did not return in time: Windows is most likely showing a consent or security prompt (UAC, SmartScreen,
    /// a security warning). It starts only if the user approves; a later decline is not reported.
    /// </summary>
    AwaitingUserConsent,

    /// <summary>
    /// reuseExisting found a window of the app already open (showing what the launch opens), so nothing was started
    /// (roadmap M3.13).
    /// </summary>
    AlreadyOpen,

    /// <summary>
    /// launch_browser_with_debug's reuseIfRunning found the debug browser already running with the profile, its DevTools
    /// port its own, so nothing was started (roadmap M5.4).
    /// </summary>
    AlreadyRunning
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
        LaunchStatus.AlreadyOpen => "alreadyOpen",
        LaunchStatus.AlreadyRunning => "alreadyRunning",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}

/// <summary>An elevated launch as the user saw and approved it: the resolved program and its command line.</summary>
/// <param name="FileName">The program, as ShellExecute gets it (a full path once resolved).</param>
/// <param name="Arguments">The command line after it, as built from the arguments (empty for none).</param>
public sealed record ElevationApproval(string FileName, string Arguments);

/// <summary>
/// What a runAsAdmin request would start, planned without starting anything (IProcessService.PreviewElevation, roadmap
/// M6.5): whether the user must approve it because Windows would not ask (and why), and the launch to show them. A request
/// the plan refuses has <paramref name="ErrorCode"/> and <paramref name="Error"/> instead, and needs no question.
/// </summary>
public sealed record ElevationPreview(
    bool NeedsConfirmation,
    string? Reason,
    ElevationApproval? Launch,
    string? ErrorCode = null,
    string? Error = null);
