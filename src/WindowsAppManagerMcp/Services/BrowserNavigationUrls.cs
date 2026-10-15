using System.Net;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The URLs browser_navigate opens (roadmap M5.5): web pages (http and https, with a host) and about:blank, nothing else.
/// </summary>
/// <remarks>
/// The browser loads the URL itself, so this is not launch_application's <see cref="ProtocolAllowlist"/>, which hands a
/// URI to the app registered for its scheme; that list's user additions (WAM_ALLOWED_PROTOCOLS) name external apps and are
/// not used here. Its <see cref="ProtocolAllowlist.BlockedSchemes"/> (javascript:, data:, vbscript: and the rest) are
/// refused as they are everywhere, and so is everything else a tab can show: file: (a local file or folder, which the
/// agent could then read through get_browser_content, a read this server does not otherwise give), the browsers' own pages
/// (chrome:, edge:, brave:, about: other than about:blank: settings, flags, passwords), devtools:, extensions'
/// (chrome-extension:) and view-source:, blob: and filesystem: URLs of other pages.
/// A browser's DevTools HTTP endpoint on this computer is refused too: /json/list, /json/activate and /json/close answer a
/// plain navigation, so another browser's tabs could be read or closed past the target policy. Its paths are refused here;
/// <see cref="BrowserTabService"/> refuses any loopback port a browser listens on.
/// </remarks>
public static class BrowserNavigationUrls
{
    /// <summary>The longest URL taken; Chromium's own limit is 2 MB, but a page address is far shorter.</summary>
    public const int MaxLength = 32_768;

    /// <summary>The one about: page allowed: an empty page.</summary>
    public const string AboutBlank = "about:blank";

    /// <summary>
    /// Why browser_navigate refuses <paramref name="url"/>, or null when it may open it. The message says what is allowed
    /// and that nothing was done.
    /// </summary>
    public static string? Refusal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "url must be the address of a web page, such as https://example.com/. Nothing was done.";

        if (url.Length > MaxLength)
            return $"url is {url.Length} characters long; at most {MaxLength} are taken. Nothing was done.";

        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return "url holds whitespace or a control character (such as a line break). Percent-encode it (%20 for a space). " +
                   "Nothing was done.";
        }

        if (string.Equals(url, AboutBlank, StringComparison.OrdinalIgnoreCase))
            return null;

        var colon = url.IndexOf(':');
        var scheme = colon > 0 ? url[..colon] : null;
        if (scheme == null || !scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') || !char.IsAsciiLetter(scheme[0]))
        {
            return $"url must be a full address with its scheme, such as https://{url.TrimStart('/')}. Nothing was done.";
        }

        var isWeb = scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        if (!isWeb)
        {
            var why = ProtocolAllowlist.IsBlocked(scheme)
                ? "it runs script or carries content of its own"
                : scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                    ? "it opens a local file or folder, which could then be read from the page"
                    : "it is not a web page (the browser's own pages, DevTools, extensions and other schemes are refused)";
            return $"url scheme '{scheme.ToLowerInvariant()}:' is not opened: {why}. browser_navigate opens http and https pages and " +
                   $"{AboutBlank}. Nothing was done.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            !url.AsSpan(scheme.Length).StartsWith("://", StringComparison.Ordinal))
        {
            return $"url is not a valid {scheme.ToLowerInvariant()} address: expected {scheme.ToLowerInvariant()}://host/... " +
                   "Nothing was done.";
        }

        return IsLoopback(uri) && IsDevToolsPath(uri.AbsolutePath) ? DevToolsRefusal : null;
    }

    /// <summary>The refusal of a URL on a browser's DevTools port of this computer.</summary>
    public const string DevToolsRefusal =
        "url is a browser's DevTools endpoint on this computer: its /json pages list, show and close tabs when opened, past " +
        "the target policy and the tab tools' checks. Use list_tabs and activate_tab instead. Nothing was done.";

    /// <summary>
    /// Whether <paramref name="uri"/>'s host is this computer: localhost and *.localhost, which browsers resolve to it
    /// themselves, a loopback address, and 0.0.0.0 or ::. <see cref="Uri"/> has already put 127.1 and 0x7f000001 in their
    /// usual form, so the host is read from it, never from the text.
    /// </summary>
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) &&
               (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4())));
    }

    /// <summary>A path the DevTools HTTP handler answers: /json and what is under it, and /devtools/.</summary>
    private static bool IsDevToolsPath(string path) =>
        IsUnder(path, "/json") || IsUnder(path, "/devtools");

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == '/');
}
