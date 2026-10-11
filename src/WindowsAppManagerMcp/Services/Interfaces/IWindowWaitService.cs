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
    /// to the front since the baseline, or whose title changed to contain one of <see cref="WindowWaitOptions.DocumentHints"/>,
    /// once that has held for <see cref="WindowWaitOptions.ReuseSettleMs"/>: <see cref="WindowWaitOutcome.OpenedInTab"/>
    /// when its title changed, else <see cref="WindowWaitOutcome.ReusedExistingWindow"/>.</item>
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
    /// Texts that mean an existing window now shows the launched document or URL when its title contains one (file names,
    /// folder names, host names), case-insensitive.
    /// </summary>
    public IReadOnlyList<string> DocumentHints { get; init; } = [];

    /// <summary>
    /// Claims a new window for this wait, so launches waited for together (a batch) never report the same new window:
    /// false means another launch has it. Null claims every window.
    /// </summary>
    public Func<nint, bool>? TryClaim { get; init; }
}
