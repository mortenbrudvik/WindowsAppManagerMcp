using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// The debug browser's lifecycle (roadmap M5.4): launch_browser_with_debug's profile, reuseIfRunning and placement
/// checks, and close_debug_browser.
/// </summary>
public partial class BrowserReadingTools
{
    private const string DebugPlacementDescription =
        "Where to put its window, as launch_application's placement; checked before anything is launched";

    /// <summary>The shortest and longest wait for the browser to exit after Browser.close.</summary>
    private const int MinCloseTimeoutMs = 1000;

    private const int MaxCloseTimeoutMs = 30_000;

    /// <summary>
    /// launch_browser_with_debug's checks of profile, reuseIfRunning and placement (roadmap M5.4), before anything is
    /// launched; the placement's checked plan, or null without one.
    /// </summary>
    private LaunchPlacementPlan? CheckDebugLaunch(string? profile, string? userDataDir, bool reuseIfRunning, LaunchPlacement? placement)
    {
        if (profile != null)
        {
            if (userDataDir != null)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                    "Pass profile or userDataDir, not both. Nothing was launched.");
            }

            if (DebugBrowserProfiles.NameRefusal(profile) is { } badName)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, $"{badName} Nothing was launched.");
            }
        }

        if (reuseIfRunning && profile == null && userDataDir == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "reuseIfRunning needs profile or userDataDir: a new temporary profile has no running browser. Nothing was launched.");
        }

        if (placement == null)
        {
            return null;
        }

        if (!LaunchPlacing.TryPlan(placement, _monitorService, out var plan, out var errorCode, out var error))
        {
            throw new ToolErrorException(errorCode!, $"{error} Nothing was launched.");
        }

        return plan;
    }

    [McpServerTool(Name = "close_debug_browser", Title = "Close Debug Browser", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Close a debug browser launch_browser_with_debug started (processId or cdpPort) and delete its temporary profile. " +
                 "Only a browser this server started, or whose command line shows a DevTools port and a profile in the server's own folders, " +
                 "is closed; any other is NotADebugBrowser, so the user's own browser is never closed. The user confirms when the client can " +
                 "ask (ConfirmationDeclined); otherwise confirm=true is required (ConfirmationRequired). Sends Browser.close, waits up to " +
                 "timeoutMs, then ends what is left. A named profile is kept unless deleteProfile=true; a userDataDir is never deleted. " +
                 "An unreadable process list closes nothing (ProcessTreeUnreadable: try again).")]
    public async Task<CloseDebugBrowserResultDto> CloseDebugBrowser(
        [Description("The processId launch_browser_with_debug returned.")]
        int? processId = null,
        [Description("Or the cdpPort it returned.")]
        int? cdpPort = null,
        [Description("Also delete a named profile (its sign-ins). Default false.")]
        bool deleteProfile = false,
        [Description("Required when the client cannot ask the user.")]
        bool confirm = false,
        [Description("Wait for the exit after Browser.close before ending it (1000-30000 ms, default 5000).")]
        int timeoutMs = 5000,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        // Nothing while the user has paused input, not even a question
        InputGateToolFilter.ThrowIfPaused(_session);

        if (processId == null && cdpPort == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "Pass processId or cdpPort, as launch_browser_with_debug returned them. Nothing was closed.");
        }

        if (processId is <= 0)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"processId must be a process ID above 0, but it is {processId}. Nothing was closed.");
        }

        if (cdpPort is < 1 or > DebugBrowserLaunchRequest.MaxPort)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"cdpPort must be a TCP port from 1 to 65535, but it is {cdpPort}. Nothing was closed.");
        }

        if (timeoutMs is < MinCloseTimeoutMs or > MaxCloseTimeoutMs)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"timeoutMs must be from {MinCloseTimeoutMs} to {MaxCloseTimeoutMs}, but it is {timeoutMs}. Nothing was closed.");
        }

        var lifecycle = _debugBrowsers ?? throw new InvalidOperationException("No debug browser lifecycle is registered.");
        var check = lifecycle.CheckClose(processId, cdpPort, deleteProfile);
        if (check.Target is not { } target)
        {
            return ToDto(check.Refusal!);
        }

        if (UserConfirmation.CanAsk(server))
        {
            // Asked before the input gate is taken: a user who takes minutes to answer must not hold up every input call
            var answer = await UserConfirmation.AskAsync(
                server!, CloseDebugBrowserQuestion(target), "Close the debug browser", _answerTimeout, cancellationToken).ConfigureAwait(false);
            if (!answer.Confirmed)
            {
                return ToDto(new DebugBrowserCloseResult(false, target.ProcessId,
                    $"{answer.Reason} Nothing was done: the browser (processId {target.ProcessId}) is still running. Ask the user how to continue.",
                    nameof(ProcessErrorCode.ConfirmationDeclined), target.UserDataDir, target.ProfileKind.ToWireName()));
            }
        }
        else if (!confirm)
        {
            return ToDto(new DebugBrowserCloseResult(false, target.ProcessId,
                $"Closing the browser (processId {target.ProcessId}) needs confirm=true. Nothing was closed.",
                nameof(ProcessErrorCode.ConfirmationRequired), target.UserDataDir, target.ProfileKind.ToWireName()));
        }

        // Each step that can close windows runs inside the input gate; the waits for the browser to exit do not
        var result = await lifecycle.CloseAsync(
            target, TimeSpan.FromMilliseconds(timeoutMs),
            token => InputGateToolFilter.EnterAsync(_session, token, _gateWaitTimeout),
            cancellationToken).ConfigureAwait(false);
        return ToDto(result);
    }

    /// <summary>The question close_debug_browser asks the user: which browser, its windows, and what happens to its profile.</summary>
    private string CloseDebugBrowserQuestion(DebugBrowserCloseTarget target)
    {
        var windows = _windowService?.FindWindows(new WindowQuery { ProcessId = target.ProcessId })
            .Select(w => w.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(5).ToList() ?? [];
        var windowText = windows.Count == 0 ? string.Empty : $" Its windows: {string.Join(", ", windows.Select(t => UserConfirmation.Quote(t)))}.";
        var whose = target.StartedByServer
            ? "it started"
            : "with a profile in this server's folder";
        var profileText = target.ProfileKind switch
        {
            DebugProfileKind.Temporary => "Its temporary profile is deleted.",
            DebugProfileKind.Named when target.DeletesProfile =>
                $"Its named profile {UserConfirmation.Quote(Path.GetFileName(target.UserDataDir))} is deleted, with its sign-ins and cookies.",
            DebugProfileKind.Named => $"Its named profile {UserConfirmation.Quote(Path.GetFileName(target.UserDataDir))} is kept.",
            _ => $"Its profile folder {UserConfirmation.Quote(target.UserDataDir)} is kept."
        };
        return $"The AI agent asks to close the debug browser {whose}: '{target.ProcessName}' (PID " +
               $"{target.ProcessId.ToString(CultureInfo.InvariantCulture)}). Pages open in it are closed.{windowText} {profileText} Allow it?";
    }

    private static CloseDebugBrowserResultDto ToDto(DebugBrowserCloseResult result) => new(
        Success: result.Success,
        ProcessId: result.ProcessId,
        Error: result.Error,
        ErrorCode: result.ErrorCode,
        UserDataDir: result.UserDataDir,
        ProfileKind: result.ProfileKind,
        ClosedGracefully: result.ClosedGracefully,
        Forced: result.Forced,
        Exited: result.Exited,
        ProfileDeleted: result.ProfileDeleted,
        Warning: result.Warning);
}

/// <summary>
/// DTO for close_debug_browser (roadmap M5.4). ClosedGracefully: the browser accepted Browser.close; Forced: it was ended
/// after the wait; Exited: it is gone. ProfileKind is temporary, named or userDataDir; ProfileDeleted is true when its folder
/// was deleted, false when it should have been but could not be, and null when it is kept.
/// </summary>
public record CloseDebugBrowserResultDto(
    bool Success,
    int? ProcessId,
    string? Error = null,
    string? ErrorCode = null,
    string? UserDataDir = null,
    string? ProfileKind = null,
    bool? ClosedGracefully = null,
    bool? Forced = null,
    bool? Exited = null,
    bool? ProfileDeleted = null,
    string? Warning = null
);
