using System.Diagnostics;
using System.Text.Json;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The tab a browser window shows (roadmap M5.3), picked the same way for every caller: the reads
/// (<see cref="CdpService.FindTargetForWindowAsync"/>, and through it the DOM bridge and browser_element_rect's check of a
/// targetId), and list_tabs' shownTargetId (<see cref="CdpTabService.ListTabsAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// CDP reports no active tab, so the window's title picks it (<see cref="CdpTargetMatcher"/>): among the tabs of the
/// browser window it is bound to, else among all tabs; when the bound window has no tab with the title, its only tab.
/// </para>
/// <para>
/// A page sets its own title, so a title is no proof: a background tab titled "Inbox - Google Chrome" also starts the
/// window title "Inbox - Google Chrome", and is longer than the shown tab "Inbox". When several tabs have a title the window
/// shows, each one's document.visibilityState is read in an isolated world (<see cref="CdpIsolatedWorld"/>), which the
/// page's scripts cannot redefine, and the one shown is taken. Anything short of exactly one shown tab is
/// CdpTargetAmbiguous, listing the candidates: none or several shown, a visibility that could not be read, too many
/// candidates to read, a minimized window (none of its tabs is shown, so one shown is another window's), and candidates in
/// more than one browser window (a shown tab is the one its own window shows, which need not be this window). It never
/// guesses.
/// </para>
/// </remarks>
internal static class CdpShownTab
{
    /// <summary>The most tabs whose visibility is read for one window; with more, the result is CdpTargetAmbiguous.</summary>
    public const int MaxVisibilityReads = 8;

    /// <summary>What a tab's page is asked, in an isolated world.</summary>
    public const string VisibilityExpression = "document.visibilityState";

    /// <summary>
    /// The tab <paramref name="window"/> shows among <paramref name="all"/>, the browser's page tabs.
    /// </summary>
    /// <param name="transport">Connects to each candidate tab to read whether it is shown.</param>
    /// <param name="window">The window: its title and, when known, where it is.</param>
    /// <param name="all">Every page tab of the browser.</param>
    /// <param name="windows">The browser window of each tab (Browser.getWindowForTarget), or null when not read.</param>
    /// <param name="visibilityTimeout">How long reading the candidates' visibility may take, all of them together.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The tab, or CdpTargetNotFound / CdpTargetAmbiguous.</returns>
    public static async Task<CdpResult<CdpTarget>> PickAsync(
        ICdpTransport transport,
        CdpWindow window,
        IReadOnlyList<CdpTarget> all,
        IReadOnlyDictionary<string, CdpBrowserWindow>? windows,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(all);

        CdpTarget? soleInWindow = null;
        if (window.Geometry is { } geometry && windows is { Count: > 0 })
        {
            var ids = CdpWindowBinder.MatchingWindows(geometry, windows.Values);
            var inWindow = all.Where(t => windows.TryGetValue(t.Id, out var w) && ids.Contains(w.WindowId)).ToList();
            if (inWindow.Count > 0)
            {
                var bound = await ChooseAsync(transport, window, inWindow, windows, visibilityTimeout, cancellationToken).ConfigureAwait(false);
                if (bound.Success || bound.ErrorCode == BrowserErrorCode.CdpTargetAmbiguous)
                    return bound;

                if (inWindow.Count == 1)
                    soleInWindow = inWindow[0];
            }
        }

        // As before M5.3: the title among all tabs (the binding may have missed, for example on a monitor at another scale)
        var anywhere = await ChooseAsync(transport, window, all, windows, visibilityTimeout, cancellationToken).ConfigureAwait(false);

        // The window's only tab is the tab it shows when no tab has the window's caption at all: a page that sets
        // document.title is listed with the new title before Windows shows it in the caption (seen on a real Edge)
        return anywhere.ErrorCode == BrowserErrorCode.CdpTargetNotFound && soleInWindow != null
            ? CdpResult<CdpTarget>.Ok(soleInWindow)
            : anywhere;
    }

    /// <summary>The one tab among <paramref name="tabs"/> whose title the window shows, or of several, the one shown.</summary>
    private static async Task<CdpResult<CdpTarget>> ChooseAsync(
        ICdpTransport transport,
        CdpWindow window,
        IReadOnlyList<CdpTarget> tabs,
        IReadOnlyDictionary<string, CdpBrowserWindow>? windows,
        TimeSpan visibilityTimeout,
        CancellationToken cancellationToken)
    {
        var found = CdpTargetMatcher.FindCandidates(window.Title, tabs);
        if (!found.Success)
            return found.FailAs<CdpTarget>();

        var candidates = found.Value;
        if (candidates.Count == 1)
            return CdpResult<CdpTarget>.Ok(candidates[0]);

        if (window.Geometry is { State: CdpWindowState.Minimized })
        {
            return CdpTargetMatcher.Ambiguous(window.Title, candidates,
                "the window is minimized, so it shows none of its tabs, and a tab that is shown is another window's.");
        }

        // A tab that is shown is the one its own browser window shows, so it tells nothing among several windows
        if (windows != null &&
            candidates.Select(t => windows.TryGetValue(t.Id, out var w) ? w.WindowId : (int?)null).Distinct().Count() > 1)
        {
            return CdpTargetMatcher.Ambiguous(window.Title, candidates,
                "they are in more than one browser window, and that a tab is shown tells only that its own window shows it.");
        }

        if (candidates.Count > MaxVisibilityReads)
        {
            return CdpTargetMatcher.Ambiguous(window.Title, candidates,
                $"more than {MaxVisibilityReads} tabs have it, so which of them is shown was not read.");
        }

        var reads = candidates.Select(t => ReadVisibilityAsync(transport, t, visibilityTimeout, cancellationToken)).ToList();
        await Task.WhenAll(reads).ConfigureAwait(false);

        for (var i = 0; i < candidates.Count; i++)
        {
            var read = reads[i].Result;
            if (!read.Success)
            {
                return CdpTargetMatcher.Ambiguous(window.Title, candidates,
                    $"whether tab {candidates[i].Id} is shown could not be read ({read.DescribeError()}), so none of them is taken.");
            }
        }

        var shown = candidates.Where((_, i) => reads[i].Result.Value == "visible").ToList();
        return shown.Count switch
        {
            1 => CdpResult<CdpTarget>.Ok(shown[0]),
            0 => CdpTargetMatcher.Ambiguous(window.Title, candidates,
                "none of them is shown (document.visibilityState, read where the page's scripts cannot change it, is not " +
                "'visible'): the window may be minimized or covered by other windows. Bring it to the front (focus_window) and " +
                "try again."),
            _ => CdpTargetMatcher.Ambiguous(window.Title, candidates,
                $"{shown.Count} of them are shown (document.visibilityState 'visible'), each by a window of its own.")
        };
    }

    /// <summary>
    /// Tab <paramref name="target"/>'s document.visibilityState, read on a connection of its own in an isolated world of its
    /// main frame (<see cref="CdpIsolatedWorld"/>), closed before it returns.
    /// </summary>
    private static async Task<CdpResult<string>> ReadVisibilityAsync(
        ICdpTransport transport, CdpTarget target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return CdpResult<string>.Fail(BrowserErrorCode.CdpTimeout,
                $"No time was left to read whether tab {target.Id} is shown.");
        }

        var stopwatch = Stopwatch.StartNew();
        var label = $"tab {target.Id}";
        var webSocketUrl = CdpEndpointValidator.ValidateWebSocketUrl(target.WebSocketDebuggerUrl, target.Port, label);
        if (!webSocketUrl.Success)
            return webSocketUrl.FailAs<string>();

        var connected = await transport.ConnectAsync(webSocketUrl.Value, target.Port, label, timeout, cancellationToken).ConfigureAwait(false);
        if (!connected.Success)
            return connected.FailAs<string>();

        await using var connection = connected.Value;
        var left = timeout - stopwatch.Elapsed;
        var (method, reply) = await CdpIsolatedWorld.EvaluateAsync(
            connection, VisibilityExpression, left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
        if (!reply.Success)
            return reply.FailAs<string>();

        if (reply.Value.Error is { } fault)
            return CdpReplies.ToFailure<string>(method, fault);

        var result = reply.Value.Result;
        return result.ValueKind == JsonValueKind.Object &&
               !result.TryGetProperty("exceptionDetails", out _) &&
               result.TryGetProperty("result", out var remote) &&
               CdpReplies.GetString(remote, "value") is { Length: > 0 } state
            ? CdpResult<string>.Ok(state)
            : CdpResult<string>.Fail(BrowserErrorCode.CdpProtocolError,
                $"Tab {target.Id} answered Runtime.evaluate without its document.visibilityState.");
    }
}
