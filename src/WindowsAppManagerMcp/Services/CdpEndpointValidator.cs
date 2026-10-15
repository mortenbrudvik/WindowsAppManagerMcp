using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Checks what the server is about to connect to or look up on a DevTools endpoint (roadmap M5.2). The endpoint's own
/// answers are not trusted: /json/list is written by whatever listens on the port.
/// </summary>
public static class CdpEndpointValidator
{
    private static readonly Regex TargetIdPattern = new(
        @"^[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The WebSocket URL to connect to for <paramref name="what"/>, if it is ws:// on the loopback address and on the
    /// DevTools port it was listed by. A listed URL on another host or port could be a different program's endpoint.
    /// </summary>
    /// <param name="url">The webSocketDebuggerUrl the endpoint listed.</param>
    /// <param name="expectedPort">The DevTools port the URL was listed by.</param>
    /// <param name="what">What the URL belongs to in messages, for example "tab 8C1F...".</param>
    /// <returns>The URL; CdpUnavailable when there is none, CdpProtocolError when it is refused.</returns>
    public static CdpResult<Uri> ValidateWebSocketUrl(string? url, int expectedPort, string what)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return CdpResult<Uri>.Fail(
                BrowserErrorCode.CdpUnavailable,
                $"There is no WebSocket debugger URL for {what}. Another DevTools client may be attached to it.");
        }

        if (uri.Scheme != "ws" || !uri.IsLoopback)
        {
            return CdpResult<Uri>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"Refusing to connect to non-loopback WebSocket URL '{uri}' for {what}.");
        }

        if (uri.Port != expectedPort)
        {
            return CdpResult<Uri>.Fail(
                BrowserErrorCode.CdpProtocolError,
                $"Refusing to connect to WebSocket URL '{uri}' for {what}: it is on port {uri.Port}, not on the debug port " +
                $"{expectedPort} that listed it.");
        }

        return CdpResult<Uri>.Ok(uri);
    }

    /// <summary>
    /// True when <paramref name="targetId"/> looks like a CDP target id: 1 to 128 letters, digits, '.', '_' or '-'.
    /// Chromium's ids are 32 hex digits; anything with a slash, '?' or '%' is refused before it is looked up.
    /// </summary>
    public static bool IsValidTargetId(string? targetId) =>
        targetId != null && TargetIdPattern.IsMatch(targetId);
}
