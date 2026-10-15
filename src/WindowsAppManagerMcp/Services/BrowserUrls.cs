using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What the UI Automation reads of a browser accept as a URL (roadmap M5.7): text read from a page's Document or from the
/// address bar is returned as a URL only when it is one, never because of where it was found. A label, a search the user
/// is typing or a name is not.
/// </summary>
public static partial class BrowserUrls
{
    /// <summary>The schemes a browser tab shows: the web's, local files, the browsers' own pages and extensions.</summary>
    private static readonly HashSet<string> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "ftp", "file", "about", "data", "blob", "view-source",
        "chrome", "chrome-extension", "chrome-search", "chrome-untrusted", "devtools",
        "edge", "brave", "opera", "vivaldi", "extension", "moz-extension"
    };

    /// <summary>The schemes whose URL has a host, which <see cref="Uri"/> must find.</summary>
    private static readonly HashSet<string> HostSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ftp" };

    /// <summary>
    /// The pages a browser shows in a new tab. Chrome's and Edge's own (an empty address bar), Edge's MSN page and Chrome's
    /// third-party one, which are web pages.
    /// </summary>
    private static readonly string[] NewTabPages =
    [
        "chrome://newtab", "chrome://new-tab-page", "chrome-search://local-ntp", "chrome://vivaldi-webui/startpage",
        "edge://newtab", "brave://newtab", "opera://startpage", "vivaldi://startpage", "about:newtab", "about:home",
        "https://ntp.msn.com/edge/ntp", "https://www.google.com/_/chrome/newtab"
    ];

    /// <summary>
    /// True when <paramref name="text"/> is an absolute URL a browser tab can show: it starts with one of the browsers'
    /// schemes and a colon, a web URL has a host, and it holds no line break or control character (spaces only in a data:
    /// URL).
    /// </summary>
    public static bool IsAbsoluteUrl(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl) || SchemeOf(text) is not { } scheme ||
            !Schemes.Contains(scheme) || text.Length == scheme.Length + 1)
        {
            return false;
        }

        if (text.Contains(' ') && !scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !HostSchemes.Contains(scheme) ||
               (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host));
    }

    /// <summary>
    /// True when <paramref name="text"/> is an address the way an address bar shows it without its scheme: a host name with
    /// a top-level domain, localhost, an IPv4 or bracketed IPv6 address, then an optional port and path, with no spaces
    /// (<c>github.com/x</c>, <c>localhost:3000</c>, <c>192.168.0.1:8080/a</c>). A search such as "hello world" is not.
    /// </summary>
    public static bool IsHostLike(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= 8192 && HostLike().IsMatch(text);

    /// <summary>
    /// True when <paramref name="text"/> is a local file's path as Chrome and Edge show a file: page's address in the address
    /// bar, without file:///: a drive letter, a colon and a forward slash, then the path as the URL has it (percent-encoded,
    /// so with no spaces) and with no backslash (<c>C:/Users/me/page%20one.html</c>). A path written the Windows way
    /// (<c>C:\Users\me</c>), as typed rather than shown, is not.
    /// </summary>
    public static bool IsShownFilePath(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= 8192 && ShownFilePath().IsMatch(text);

    /// <summary>
    /// True when <paramref name="text"/> is an absolute URL, an address shown without its scheme, or a file page's path.
    /// </summary>
    public static bool IsUrlLike(string? text) => IsAbsoluteUrl(text) || IsHostLike(text) || IsShownFilePath(text);

    /// <summary>
    /// The address the address bar's URL-like <paramref name="display"/> stands for: a file page's path as its file:/// URL,
    /// anything else as it is (an address shown without its scheme stays so: which scheme it has cannot be told).
    /// </summary>
    public static string AddressOf(string display) => IsShownFilePath(display) ? "file:///" + display : display;

    /// <summary>True when <paramref name="url"/> is one of the browsers' new tab pages.</summary>
    public static bool IsNewTabPage(string? url) =>
        url != null && NewTabPages.Any(page =>
            url.StartsWith(page, StringComparison.OrdinalIgnoreCase) &&
            (url.Length == page.Length || url[page.Length] is '/' or '?' or '#'));

    /// <summary>
    /// True when a tab at <paramref name="url"/> shows anything but a web page (roadmap M5.8), so the DOM bridge never reads
    /// or acts on it: an allow list, not a deny list. Web pages are http: and https: pages, file: and data: pages,
    /// about:blank and about:srcdoc, and blob: and filesystem: URLs whose origin is an http: or https: one. Everything else is
    /// refused: the browsers' own pages (chrome:, edge:, devtools:, view-source:, about: pages), extensions' pages and their
    /// blob: and filesystem: URLs (blob:chrome-extension://...), isolated-app: pages, schemes a browser adds later, and a URL
    /// that is no URL at all. Such pages may run with the browser's or an extension's privileges.
    /// </summary>
    public static bool IsBrowserOwnPage(string? url) => !IsWebPage(url);

    /// <summary>True when <paramref name="url"/> is a web page in the sense of <see cref="IsBrowserOwnPage"/>.</summary>
    private static bool IsWebPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl) || SchemeOf(url.Trim()) is not { } scheme)
        {
            return false;
        }

        var text = url.Trim();
        var rest = text[(scheme.Length + 1)..];
        switch (scheme.ToLowerInvariant())
        {
            case "http":
            case "https":
                return IsWebAddress(text);
            case "file":
            case "data":
                return true;
            case "about":
                var end = rest.IndexOfAny(['?', '#']);
                var page = end >= 0 ? rest[..end] : rest;
                return page.Equals("blank", StringComparison.OrdinalIgnoreCase) || page.Equals("srcdoc", StringComparison.OrdinalIgnoreCase);
            case "blob":
            case "filesystem":
                // The URL's origin is the page that made it: only a web page's counts
                return SchemeOf(rest) is { } inner &&
                       (inner.Equals("http", StringComparison.OrdinalIgnoreCase) || inner.Equals("https", StringComparison.OrdinalIgnoreCase)) &&
                       IsWebAddress(rest);
            default:
                return false;
        }
    }

    private static bool IsWebAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && !IsBrowserUi(url);

    /// <summary>
    /// True when <paramref name="url"/> is a page of the browser's own UI rather than a tab's: docked DevTools, and
    /// Chrome's side panels and bubbles (WebUI on a host that ends in ".top-chrome").
    /// </summary>
    public static bool IsBrowserUi(string? url) =>
        url != null &&
        (url.StartsWith("devtools:", StringComparison.OrdinalIgnoreCase) ||
         (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
          uri.Host.EndsWith(".top-chrome", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// True when the address bar's text <paramref name="display"/> shows <paramref name="url"/>: the same address once the
    /// scheme, a leading "www." and a trailing slash are left out of both (Chrome hides them), or the start of it up to a
    /// '/', '?' or '#'. A file page's path shows its file:/// URL (<see cref="AddressOf"/>). Case is ignored.
    /// </summary>
    public static bool Shows(string? display, string? url)
    {
        if (string.IsNullOrEmpty(display) || string.IsNullOrEmpty(url))
        {
            return false;
        }

        var shown = Bare(AddressOf(display));
        var address = Bare(url);
        return shown.Length > 0 &&
               (address.Equals(shown, StringComparison.OrdinalIgnoreCase) ||
                (address.StartsWith(shown, StringComparison.OrdinalIgnoreCase) && address[shown.Length] is '/' or '?' or '#'));
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are the same page's address (roadmap M5.8): equal once the
    /// scheme, a leading "www.", a trailing slash and the fragment are left out of both. Case is ignored. Unlike
    /// <see cref="Shows"/>, the start of an address is not the page: a site's root is not every page under it.
    /// </summary>
    public static bool SamePage(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        static string Page(string url)
        {
            var hash = url.IndexOf('#');
            return Bare(hash >= 0 ? url[..hash] : url);
        }

        var first = Page(a);
        return first.Length > 0 && first.Equals(Page(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The address without its scheme, a leading "www." and a trailing slash.</summary>
    private static string Bare(string text)
    {
        var bare = text.Trim();
        var separator = bare.IndexOf("://", StringComparison.Ordinal);
        if (separator > 0 && SchemeOf(bare) is { } scheme && scheme.Length == separator)
        {
            bare = bare[(separator + 3)..];
        }

        if (bare.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[4..];
        }

        return bare.TrimEnd('/');
    }

    /// <summary>The scheme the text starts with (letters, digits, '+', '-' and '.', then a colon), or null.</summary>
    private static string? SchemeOf(string text)
    {
        var colon = text.IndexOf(':');
        return colon > 0 && Scheme().IsMatch(text[..colon]) ? text[..colon] : null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*$")]
    private static partial Regex Scheme();

    [GeneratedRegex(@"^[A-Za-z]:/[^\\\s\p{C}]*$")]
    private static partial Regex ShownFilePath();

    [GeneratedRegex(@"^(?:localhost|\[[0-9A-Fa-f:.]+\]|(?:\d{1,3}\.){3}\d{1,3}|(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+\p{L}{2,63})(?::\d{1,5})?(?:[/?#]\S*)?$")]
    private static partial Regex HostLike();
}
