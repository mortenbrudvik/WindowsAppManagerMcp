using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Finds the window a launch opens by comparing the top-level windows against a baseline taken before the start
/// (roadmap M2.6). See <see cref="IWindowWaitService.WaitForNewWindowAsync"/>. The waits for a window condition
/// (roadmap M3.7, wait_for_window) are in WindowWaitService.Conditions.cs.
/// </summary>
/// <remarks>
/// Only reads that do not wait for an application: EnumWindows, GetWindowText (which reads the stored title of
/// another process's window without sending it a message), the class, style, owner and cloaking reads of the
/// listing predicate, and GetWindowThreadProcessId. The one wait, WaitForInputIdle, is bounded. Each poll reads only
/// the windows that are new against the baseline, plus the foreground window and the titles of the baseline's windows.
/// </remarks>
public sealed partial class WindowWaitService : IWindowWaitService
{
    /// <summary>How often the windows are read.</summary>
    public const int PollIntervalMs = 100;

    /// <summary>The longest wait, whatever a caller asks for.</summary>
    public const int MaxTimeoutMs = 60_000;

    private const uint GW_OWNER = 4;

    private readonly INativeWindowWrapper _native;
    private readonly IWindowService _windows;
    private readonly INativeProcessWrapper _processes;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="native">Reads the top-level windows.</param>
    /// <param name="windows">The listing predicate (IsUserFacingAppWindow) and the full read of the found window.</param>
    /// <param name="processes">WaitForInputIdle on the found window's process.</param>
    /// <param name="time">The clock; default the system clock.</param>
    /// <param name="delay">Waits between two polls; default Task.Delay on <paramref name="time"/>. Tests pass one that moves a manual clock.</param>
    public WindowWaitService(
        INativeWindowWrapper native,
        IWindowService windows,
        INativeProcessWrapper processes,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _native = native;
        _windows = windows;
        _processes = processes;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _time, token));
    }

    public WindowBaseline TakeBaseline()
    {
        var titles = new Dictionary<nint, string>();
        foreach (var handle in EnumerateTopLevelWindows())
        {
            titles[handle] = _native.GetWindowText(handle) ?? string.Empty;
        }

        return new WindowBaseline(titles, _native.GetForegroundWindow());
    }

    public async Task<WindowWaitResult> WaitForNewWindowAsync(
        WindowBaseline baseline,
        Func<WindowCandidate, bool> isOwner,
        WindowWaitOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(isOwner);
        ArgumentNullException.ThrowIfNull(options);

        var timeoutMs = Math.Clamp(options.TimeoutMs, 0, MaxTimeoutMs);
        var stableMs = Math.Max(0, options.StableMs);
        var reuseSettleMs = Math.Max(0, options.ReuseSettleMs);
        var start = _time.GetTimestamp();
        long Elapsed() => (long)_time.GetElapsedTime(start).TotalMilliseconds;

        // New windows of the launch, with when each was first seen; windows another launch claimed are left out
        var firstSeen = new Dictionary<nint, long>();
        var claimedElsewhere = new HashSet<nint>();
        ReuseSignal? reuse = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Elapsed();
            var present = EnumerateTopLevelWindows();
            var presentSet = new HashSet<nint>(present);

            foreach (var handle in present)
            {
                if (baseline.Contains(handle) || claimedElsewhere.Contains(handle))
                    continue;

                if (IsLaunchWindow(handle, isOwner))
                    firstSeen.TryAdd(handle, now);
                else
                    firstSeen.Remove(handle);
            }

            foreach (var gone in firstSeen.Keys.Where(h => !presentSet.Contains(h)).ToList())
                firstSeen.Remove(gone);

            // A new window that stayed: unowned ones (main windows) before owned ones (dialogs), then the earliest. A dialog
            // whose owner is a new window that is still settling waits for it: the main window is the launch's window
            var stable = firstSeen
                .Where(seen => now - seen.Value >= stableMs)
                .Select(seen => (Handle: seen.Key, Seen: seen.Value, Owner: _native.GetWindow(seen.Key, GW_OWNER)))
                .Where(seen => seen.Owner == nint.Zero || !firstSeen.ContainsKey(seen.Owner) || now - firstSeen[seen.Owner] >= stableMs)
                .OrderBy(seen => seen.Owner == nint.Zero ? 0 : 1)
                .ThenBy(seen => seen.Seen)
                .Select(seen => seen.Handle)
                .ToList();
            foreach (var handle in stable)
            {
                if (options.TryClaim?.Invoke(handle) == false)
                {
                    firstSeen.Remove(handle);
                    claimedElsewhere.Add(handle);
                    continue;
                }

                WaitForInputIdle(handle, Math.Min(options.InputIdleMs, (int)Math.Max(0, timeoutMs - Elapsed())));
                if (_windows.GetWindowInfo(handle) is { } window)
                    return new WindowWaitResult(WindowWaitOutcome.NewWindow, window, Elapsed());

                firstSeen.Remove(handle);
            }

            // An existing window that the app reused counts only while no new window is pending: a new window wins
            if (firstSeen.Count == 0)
            {
                var signal = FindReuseSignal(baseline, presentSet, isOwner, options);
                if (signal == null || reuse == null || signal.Handle != reuse.Handle || signal.TitleChanged != reuse.TitleChanged)
                {
                    reuse = signal == null ? null : signal with { Since = now };
                }
                else if (now - reuse.Since >= reuseSettleMs && _windows.GetWindowInfo(reuse.Handle) is { } window)
                {
                    return new WindowWaitResult(
                        reuse.TitleChanged ? WindowWaitOutcome.OpenedInTab : WindowWaitOutcome.ReusedExistingWindow,
                        window, Elapsed());
                }
            }

            var remaining = timeoutMs - Elapsed();
            if (remaining <= 0)
                return new WindowWaitResult(WindowWaitOutcome.TimedOut, null, Elapsed());

            await _delay(TimeSpan.FromMilliseconds(Math.Min(PollIntervalMs, remaining)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The top-level windows in z-order, collected first so that no window is read inside the enumeration callback.</summary>
    private List<nint> EnumerateTopLevelWindows()
    {
        var handles = new List<nint>();
        _native.EnumWindows(handle =>
        {
            handles.Add(handle);
            return true;
        });
        return handles;
    }

    /// <summary>A user-facing app window (the cheap checks) that <paramref name="isOwner"/> accepts (the process checks).</summary>
    private bool IsLaunchWindow(nint handle, Func<WindowCandidate, bool> isOwner) =>
        _windows.IsUserFacingAppWindow(handle) && Candidate(handle) is { } candidate && isOwner(candidate);

    private WindowCandidate? Candidate(nint handle)
    {
        var appProcessId = UwpFrames.AppProcessId(_native, handle);
        if (appProcessId == 0)
            return null;

        var ownerProcessId = _native.GetWindowThreadProcessId(handle);
        return new WindowCandidate(
            handle,
            (int)appProcessId,
            ownerProcessId != 0 && ownerProcessId != appProcessId ? (int)ownerProcessId : null,
            _native.GetWindowText(handle) ?? string.Empty);
    }

    /// <summary>
    /// The existing window the launch most likely reused, or null: the foreground window when it came to the front since
    /// the baseline, else a window whose title changed to show what the launch opens (<see cref="NowShows"/>). Either must
    /// be a user-facing window that <paramref name="isOwner"/> accepts. Placement moves and may focus the window found, so a
    /// title that only contains a hint inside a longer word, or shows the name after another path, does not count.
    /// </summary>
    private ReuseSignal? FindReuseSignal(
        WindowBaseline baseline, HashSet<nint> present, Func<WindowCandidate, bool> isOwner, WindowWaitOptions options)
    {
        var foreground = _native.GetForegroundWindow();
        if (foreground != nint.Zero && foreground != baseline.Foreground && baseline.Contains(foreground) &&
            present.Contains(foreground) && IsLaunchWindow(foreground, isOwner))
        {
            return new ReuseSignal(foreground, TitleChanged(baseline, foreground), 0);
        }

        if (options.DocumentHints.Count == 0 && string.IsNullOrEmpty(options.TitleContains))
            return null;

        foreach (var (handle, oldTitle) in baseline.Titles)
        {
            if (!present.Contains(handle))
                continue;

            var title = _native.GetWindowText(handle) ?? string.Empty;
            if (title == oldTitle || !NowShows(title, oldTitle, options))
                continue;

            if (IsLaunchWindow(handle, isOwner))
                return new ReuseSignal(handle, true, 0);
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="title"/> shows what the launch opens and <paramref name="oldTitle"/> did not: a document
    /// hint as <see cref="LaunchTitles.Shows"/> tells (a whole name, and after a path only after the launched one), or
    /// the caller's titleContains. A window that already showed it before the start only changed on its own (an edit
    /// mark, a counter).
    /// </summary>
    private static bool NowShows(string title, string oldTitle, WindowWaitOptions options)
    {
        static bool Shows(string text, DocumentHint hint) => LaunchTitles.Shows(text, hint) is TitleShows.NameOnly or TitleShows.FullPath;

        return options.DocumentHints.Any(hint => Shows(title, hint) && !Shows(oldTitle, hint)) ||
               (!string.IsNullOrEmpty(options.TitleContains) &&
                title.Contains(options.TitleContains, StringComparison.OrdinalIgnoreCase) &&
                !oldTitle.Contains(options.TitleContains, StringComparison.OrdinalIgnoreCase));
    }

    private bool TitleChanged(WindowBaseline baseline, nint handle) =>
        !string.Equals(_native.GetWindowText(handle) ?? string.Empty, baseline.Titles.GetValueOrDefault(handle), StringComparison.Ordinal);

    private void WaitForInputIdle(nint handle, int milliseconds)
    {
        if (milliseconds <= 0)
            return;

        var processId = UwpFrames.AppProcessId(_native, handle);
        if (processId != 0)
            _processes.WaitForInputIdle((int)processId, milliseconds);
    }

    /// <summary>An existing window that may have been reused, whether its title changed, and since when that holds.</summary>
    private sealed record ReuseSignal(nint Handle, bool TitleChanged, long Since);
}
