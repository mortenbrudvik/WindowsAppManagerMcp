using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.Tools;

/// <summary>wait_for_window (roadmap M3.7): one server-side wait instead of sleeps and repeated find_windows calls.</summary>
public partial class WindowFinderTools
{
    /// <summary>The longest stableMs: a condition that must hold longer than this is not a settle time.</summary>
    public const int MaxStableMs = 10_000;

    /// <summary>The most handles excludeHandles takes.</summary>
    public const int MaxExcludeHandles = 256;

    [McpServerTool(Name = "wait_for_window", Title = "Wait for Window", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Wait on the server until a window condition holds, instead of sleeping or calling find_windows again and again: " +
                 "after a launch, a click that opens a dialog, a close or a save. window is a window selector (an untitled " +
                 "dialog needs className or includeUntitled). condition: exists (default: a matching window is open, minimized " +
                 "included), gone (none is open any more; by handle: it closed), foreground (a matching window is in front), " +
                 "visible (on screen: not minimized, hidden or cloaked), title_changed (the one window named has a title other " +
                 "than previousTitle) or input_idle (best effort: the window is not hung and its process reports input idle, which " +
                 "Windows answers only once per process, right after it starts, and never for console programs; Store apps and " +
                 "programs started through a launcher are checked on the window's own process, not the launcher's). The condition " +
                 "must hold for stableMs, which skips splash screens and dialogs that close and reopen. To catch the window your " +
                 "last action opened, pass the matching handles you saw before it as excludeHandles. Returns satisfied, elapsedMs and " +
                 "window; when several windows satisfy it, window is left out and matchCount and candidates list them. Not satisfied " +
                 "is a tool error: WaitTimedOut after timeoutMs (timedOut true, window as last seen: the one still open, not in front " +
                 "or with the old title); InvalidHandle when the named window closed and cannot match any more; AmbiguousWindow, with " +
                 "candidates, when title_changed or input_idle match several windows; WindowNotFound when title_changed names none. " +
                 "Only reads windows, and other tools run meanwhile.")]
    public async Task<WaitForWindowResult> WaitForWindow(
        [Description("The window(s) to wait for: a window selector (its fields filter as find_windows' do; processName is the whole name; className " +
                     "or includeUntitled for untitled dialogs; index picks one of the windows that count, front to back; handle plus " +
                     "filters waits for that window to match them, e.g. {\"handle\": 1706796, \"titleContains\": \"report.txt\"})")]
        WindowSelector window,
        [Description("What to wait for: exists (default), gone, foreground, visible, title_changed or input_idle")]
        WindowWaitCondition condition = WindowWaitCondition.Exists,
        [Description("Only count windows that open, or are shown, after this call starts (default: false). A window your last action " +
                     "already opened is missed; prefer excludeHandles. Not with gone, title_changed or window.handle")]
        bool newOnly = false,
        [Description("Handles of windows that never count: the matching windows you saw before you acted (at most 256; not window.handle " +
                     "itself). Not with title_changed")]
        long[]? excludeHandles = null,
        [Description("title_changed only: the title you read before acting (default: the title when the wait starts, which misses a " +
                     "change that already happened)")]
        string? previousTitle = null,
        [Description("How long to wait in milliseconds (default: 10000, min: 0, max: 60000); 0 checks once")]
        int timeoutMs = 10_000,
        [Description("How long the condition must hold before it counts, in milliseconds (default: 300, min: 0, max: 10000; cut to timeoutMs)")]
        int stableMs = 300,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var request = CheckWaitArguments(window, condition, newOnly, excludeHandles, previousTitle, timeoutMs, stableMs);
        var waits = _windowWait ?? throw new InvalidOperationException("wait_for_window needs the window wait service; the server always registers it.");

        var outcome = await waits.WaitForConditionAsync(request, ToolProgress.ForServices(progress), cancellationToken).ConfigureAwait(false);
        if (outcome.ErrorCode == ToolErrorReporting.InvalidArgumentsErrorCode)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, outcome.Error ?? "window cannot be used. Nothing was waited for.");
        }

        return WaitForWindowResult.From(outcome);
    }

    /// <summary>The request, or InvalidArguments for an argument that cannot be used; nothing has waited yet.</summary>
    private static WindowConditionRequest CheckWaitArguments(
        WindowSelector? window, WindowWaitCondition condition, bool newOnly, long[]? excludeHandles, string? previousTitle, int timeoutMs,
        int stableMs)
    {
        static ToolErrorException Invalid(string message) =>
            new(ToolErrorReporting.InvalidArgumentsErrorCode, message + " Nothing was waited for.");

        if (window == null)
            throw Invalid("Pass window, a selector such as {\"processName\": \"notepad\"} or {\"titleEquals\": \"Save As\"}.");

        if (WindowTargeting.IsBareForeground(window))
        {
            throw Invalid("window {\"foreground\": true} alone is whatever window is in front, not a window to wait for. Name the " +
                          "window (processName, a title, className or handle) and use condition 'foreground' to wait for it to come to the front.");
        }

        if (timeoutMs is < 0 or > WindowWaitService.MaxTimeoutMs)
            throw Invalid($"timeoutMs must be 0-{WindowWaitService.MaxTimeoutMs}, but it is {timeoutMs}.");

        if (stableMs is < 0 or > MaxStableMs)
            throw Invalid($"stableMs must be 0-{MaxStableMs}, but it is {stableMs}.");

        var excluded = excludeHandles ?? [];
        if (excluded.Length > MaxExcludeHandles)
            throw Invalid($"excludeHandles takes at most {MaxExcludeHandles} handles, but it has {excluded.Length}.");

        if (excluded.Any(h => h <= 0))
            throw Invalid($"excludeHandles must hold window handles (positive integers from find_windows), but it has {excluded.First(h => h <= 0)}.");

        if (newOnly && condition is WindowWaitCondition.Gone or WindowWaitCondition.TitleChanged)
        {
            throw Invalid($"newOnly does not apply to condition '{WindowWaitService.ConditionName(condition)}': gone waits for the " +
                          "windows that are open to close, and title_changed follows the one window the selector names now.");
        }

        if (excluded.Length > 0 && condition == WindowWaitCondition.TitleChanged)
            throw Invalid("excludeHandles does not apply to condition 'title_changed', which follows the one window the selector names now.");

        // A handle names one window that exists already: either would leave nothing that can count, so the wait could only time out
        if (window.Handle.HasValue && newOnly)
        {
            throw Invalid("newOnly does not apply to window.handle: a handle names a window that is already open, so it would " +
                          "never count. Leave newOnly out (condition 'visible' waits for a hidden window to show).");
        }

        if (window.Handle is { } named && excluded.Contains(named))
            throw Invalid($"excludeHandles holds window.handle {named}, so the one window the selector names would never count.");

        if (previousTitle != null && condition != WindowWaitCondition.TitleChanged)
            throw Invalid($"previousTitle applies only to condition 'title_changed', not '{WindowWaitService.ConditionName(condition)}'.");

        return new WindowConditionRequest
        {
            Window = window,
            Condition = condition,
            NewOnly = newOnly,
            ExcludeHandles = excluded.Select(h => (nint)h).ToList(),
            PreviousTitle = previousTitle,
            TimeoutMs = timeoutMs,
            StableMs = stableMs
        };
    }
}

/// <summary>
/// wait_for_window's result (roadmap M3.7). Success is Satisfied: a wait that was not satisfied goes out with isError
/// (ToolOutcome) and its errorCode (WaitTimedOut, InvalidHandle, AmbiguousWindow, WindowNotFound, HandleMismatch). Window is
/// the one window that satisfies the condition, or, when not satisfied, the window as last seen; MatchCount and Candidates
/// appear when several windows satisfy the condition (or are in the way). PreviousTitle comes with title_changed, IdleCheck
/// ('inputIdle' or 'respondingOnly') with a satisfied input_idle, and Warning with a gone wait whose selector matched nothing
/// when it started.
/// </summary>
public sealed record WaitForWindowResult(
    bool Success,
    bool Satisfied,
    bool TimedOut,
    long ElapsedMs,
    WindowInfoDto? Window = null,
    int? MatchCount = null,
    IReadOnlyList<WindowCandidateDto>? Candidates = null,
    string? PreviousTitle = null,
    string? IdleCheck = null,
    string? Warning = null,
    string? Error = null,
    string? ErrorCode = null)
{
    internal static WaitForWindowResult From(WindowConditionResult outcome) => new(
        Success: outcome.Satisfied,
        Satisfied: outcome.Satisfied,
        TimedOut: outcome.TimedOut,
        ElapsedMs: outcome.ElapsedMs,
        Window: outcome.Window is { } window ? WindowFinderTools.ToDto(window) : null,
        MatchCount: outcome.MatchCount > 1 ? outcome.MatchCount : null,
        Candidates: outcome.Candidates.Count > 0 ? outcome.Candidates.Select(WindowCandidateDto.From).ToList() : null,
        PreviousTitle: outcome.PreviousTitle,
        IdleCheck: outcome.IdleCheck switch
        {
            Models.IdleCheck.InputIdle => "inputIdle",
            Models.IdleCheck.RespondingOnly => "respondingOnly",
            _ => null
        },
        Warning: outcome.Warning,
        Error: outcome.Satisfied ? null : outcome.Error,
        ErrorCode: outcome.Satisfied ? null : outcome.ErrorCode);
}
