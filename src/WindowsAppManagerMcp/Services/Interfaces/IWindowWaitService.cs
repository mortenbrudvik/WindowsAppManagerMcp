using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Waits for the window a launch opens, whichever process owns it (roadmap M2.6): a new top-level window that was not
/// there before the start, or, for single-instance and tabbed apps, an existing window that the app reused.
/// </summary>
public interface IWindowWaitService
{
    /// <summary>
    /// Records the top-level windows, their titles and the foreground window. Take it right before the start, so every
    /// window the launch opens is new against it.
    /// </summary>
    WindowBaseline TakeBaseline();

    /// <summary>
    /// Polls the top-level windows (every 100 ms) until a window of the launch is found or <see cref="WindowWaitOptions.TimeoutMs"/>
    /// has passed (at most 60 seconds):
    /// <list type="bullet">
    /// <item>A <b>new</b> window (not in <paramref name="baseline"/>) that is a user-facing app window (visible, titled,
    /// not cloaked, not a tool or shell window) and that <paramref name="isOwner"/> accepts, once it has stayed for
    /// <see cref="WindowWaitOptions.StableMs"/> (which skips splash screens). Unowned windows go before owned ones (a
    /// dialog). Then a bounded WaitForInputIdle runs on its process.</item>
    /// <item>While no new window is pending, an <b>existing</b> window that <paramref name="isOwner"/> accepts and that came
    /// to the front since the baseline, or whose title changed to show one of <see cref="WindowWaitOptions.DocumentHints"/>
    /// or to contain <see cref="WindowWaitOptions.TitleContains"/> (which its title in the baseline did not), once that has
    /// held for <see cref="WindowWaitOptions.ReuseSettleMs"/>: <see cref="WindowWaitOutcome.OpenedInTab"/> when its title
    /// changed, else <see cref="WindowWaitOutcome.ReusedExistingWindow"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="baseline">The windows before the start.</param>
    /// <param name="isOwner">Whether a window belongs to the launch; called with each candidate (see <see cref="WindowCandidate"/>).</param>
    /// <param name="options">The timing and the hints.</param>
    /// <param name="cancellationToken">Ends the wait with <see cref="OperationCanceledException"/>.</param>
    Task<WindowWaitResult> WaitForNewWindowAsync(
        WindowBaseline baseline,
        Func<WindowCandidate, bool> isOwner,
        WindowWaitOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until a window condition holds (roadmap M3.7, wait_for_window), polling every 100 ms (every 250 ms after the
    /// first 2 seconds), at most <see cref="WindowConditionRequest.TimeoutMs"/> (clamped to 60 seconds). The selector is
    /// resolved on every poll with the rules of <see cref="IWindowService.ResolveWindow"/> (the listing's rules, so
    /// includeUntitled or className finds an untitled dialog), minus <see cref="WindowConditionRequest.ExcludeHandles"/>
    /// and, with <see cref="WindowConditionRequest.NewOnly"/>, minus the windows shown at the start; the selector's index
    /// then picks one of what is left. The condition must hold for <see cref="WindowConditionRequest.StableMs"/>.
    /// </summary>
    /// <remarks>
    /// Ends early, unsatisfied, when the condition can no longer be met: a selector that cannot be used (InvalidArguments),
    /// a window.handle that is no window for any condition but gone (InvalidHandle), the window of title_changed closing
    /// (InvalidHandle) or not resolving to one window at the start (its resolution's errorCode, with candidates), and
    /// input_idle finding several windows (AmbiguousWindow). Only reads windows; the one wait on an application,
    /// WaitForInputIdle, is bounded to 250 ms per poll. Progress (elapsed of total ms) is reported about once a second.
    /// A window event (<see cref="IWindowEventSource"/>, roadmap M3.F22) ends the sleep between two polls early, so the
    /// next poll comes sooner; the polls alone decide.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<WindowConditionResult> WaitForConditionAsync(
        WindowConditionRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The top-level windows before a launch: each handle with its title, and the foreground window.</summary>
public sealed record WindowBaseline(IReadOnlyDictionary<nint, string> Titles, nint Foreground)
{
    public bool Contains(nint handle) => Titles.ContainsKey(handle);
}

/// <summary>
/// A window the wait considers: its handle, the process of the app it shows (for a UWP frame, the app's CoreWindow
/// process; <paramref name="HostProcessId"/> is then the ApplicationFrameHost) and its title.
/// </summary>
public sealed record WindowCandidate(nint Handle, int ProcessId, int? HostProcessId, string Title);

/// <summary>How a window wait ended.</summary>
public enum WindowWaitOutcome
{
    NewWindow,
    ReusedExistingWindow,
    OpenedInTab,
    TimedOut
}

/// <summary>A window wait's outcome: the window (null on timeout) and how long the wait took.</summary>
public sealed record WindowWaitResult(WindowWaitOutcome Outcome, WindowInfo? Window, long ElapsedMs);

/// <summary>The timing and hints of a window wait.</summary>
public sealed record WindowWaitOptions
{
    /// <summary>How long to wait in all, clamped to 0-60000 ms.</summary>
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>How long a new window must stay before it counts (splash screens close sooner).</summary>
    public int StableMs { get; init; } = 300;

    /// <summary>How long a reuse signal (an existing window came to the front or shows the document) must hold.</summary>
    public int ReuseSettleMs { get; init; } = 1000;

    /// <summary>The longest WaitForInputIdle on the found window's process (bounded by the time left, too).</summary>
    public int InputIdleMs { get; init; } = 1000;

    /// <summary>
    /// The files and folders the launch opens: an existing window now shows one when its title shows it and did not before,
    /// as <see cref="LaunchTitles.Shows"/> tells (a whole name, and never after another path than the launched one).
    /// </summary>
    public IReadOnlyList<DocumentHint> DocumentHints { get; init; } = [];

    /// <summary>
    /// The caller's expectWindow.titleContains, or null: an existing window now shows the launch when its title contains it
    /// (case-insensitive) and did not before. The only title sign for a web address, whose page sets the title.
    /// </summary>
    public string? TitleContains { get; init; }

    /// <summary>
    /// Claims a new window for this wait, so launches waited for together (a batch) never report the same new window:
    /// false means another launch has it. Null claims every window.
    /// </summary>
    public Func<nint, bool>? TryClaim { get; init; }
}
