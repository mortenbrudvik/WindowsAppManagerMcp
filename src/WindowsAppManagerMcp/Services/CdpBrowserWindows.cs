using System.Diagnostics;
using System.Text.Json;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The browser window of each tab (roadmap M5.3), shared by the window binding of the reads and list_tabs (roadmap M5.5):
/// Browser.getWindowForTarget, one command per tab over one connection to the browser's DevTools URL from /json/version,
/// closed before it returns.
/// </summary>
internal static class CdpBrowserWindows
{
    /// <summary>
    /// The browser window of each tab; a tab the browser says is in no browser window (an error reply: not a tab of the tab
    /// strip) is left out. Null when the browser's connection cannot be used or any reply did not come (timed out,
    /// connection closed): a partial binding could narrow the tabs to the wrong ones.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, CdpBrowserWindow>?> QueryAsync(
        ICdpService cdp,
        ICdpTransport transport,
        int port,
        IReadOnlyList<CdpTarget> targets,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (timeout <= TimeSpan.Zero)
            return null;

        var version = await cdp.GetBrowserVersionAsync(port, timeout, cancellationToken).ConfigureAwait(false);
        var left = timeout - stopwatch.Elapsed;
        if (!version.Success || left <= TimeSpan.Zero)
            return null;

        var connected = await transport
            .ConnectAsync(version.Value!.WebSocketDebuggerUrl, port, "the browser", left, cancellationToken)
            .ConfigureAwait(false);
        if (!connected.Success)
            return null;

        await using var connection = connected.Value!;
        var remaining = timeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return null;

        var sends = targets
            .Select(t => (t.Id, Reply: connection.SendAsync("Browser.getWindowForTarget", new { targetId = t.Id }, remaining, cancellationToken)))
            .ToList();
        await Task.WhenAll(sends.Select(s => s.Reply)).ConfigureAwait(false);

        var windows = new Dictionary<string, CdpBrowserWindow>(StringComparer.Ordinal);
        foreach (var (id, reply) in sends)
        {
            var answered = reply.Result;
            if (!answered.Success)
                return null;

            if (answered.Value!.Error == null && Parse(answered.Value.Result) is { } browserWindow)
                windows[id] = browserWindow;
        }

        return windows;
    }

    /// <summary>Browser.getWindowForTarget's {windowId, bounds: {left, top, width, height, windowState}}, or null.</summary>
    public static CdpBrowserWindow? Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !TryGetInt(result, "windowId", out var windowId) ||
            !result.TryGetProperty("bounds", out var bounds) || bounds.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var state = CdpReplies.GetString(bounds, "windowState");
        TryGetInt(bounds, "left", out var left);
        TryGetInt(bounds, "top", out var top);
        TryGetInt(bounds, "width", out var width);
        TryGetInt(bounds, "height", out var height);
        return new CdpBrowserWindow(windowId, left, top, width, height, state.Length > 0 ? state : "normal");
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }
}
