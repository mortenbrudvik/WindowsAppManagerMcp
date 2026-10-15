namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for BrowserUrls, what the UI Automation reads of a browser accept as a URL (roadmap M5.7).
/// </summary>
public class BrowserUrlsTests
{
    [Theory]
    [InlineData("https://github.com/mortenbrudvik/WindowsAppManagerMcp")]
    [InlineData("http://127.0.0.1:8080/a?b=c#d")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("file:///C:/Users/me/page.html")]
    [InlineData("about:blank")]
    [InlineData("data:text/html,<h1>Hello there</h1>")]
    [InlineData("chrome://settings/")]
    [InlineData("edge://newtab/")]
    [InlineData("chrome-extension://abcdefghijklmnop/popup.html")]
    [InlineData("view-source:https://example.com/")]
    [InlineData("devtools://devtools/bundled/devtools_app.html")]
    public void IsAbsoluteUrl_AcceptsTheAddressesOfABrowserTab(string url)
    {
        BrowserUrls.IsAbsoluteUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("github.com/x")]
    [InlineData("Address and search bar")]
    [InlineData("Adresse- og søkefelt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("C:\\Users\\me\\page.html")]
    [InlineData("C:/Users/me/page.html")]
    [InlineData("https://")]
    [InlineData("https:")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://example.com/\nhttps://other.example/")]
    [InlineData("mailto:me@example.com")]
    public void IsAbsoluteUrl_RefusesLabelsSearchesPathsAndOtherSchemes(string? text)
    {
        BrowserUrls.IsAbsoluteUrl(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("github.com/mortenbrudvik/WindowsAppManagerMcp", true)]
    [InlineData("www.example.com", true)]
    [InlineData("localhost:3000", true)]
    [InlineData("localhost", true)]
    [InlineData("192.168.0.1:8080/a", true)]
    [InlineData("[::1]:9222/json", true)]
    [InlineData("blåbær.no/sti", true)]
    [InlineData("hello world", false)]
    [InlineData("version 2.0", false)]
    [InlineData("1.5", false)]
    [InlineData("Address bar", false)]
    [InlineData("hvordan bake brød", false)]
    [InlineData("example", false)]
    [InlineData("", false)]
    public void IsHostLike_AcceptsAnAddressShownWithoutItsScheme(string text, bool expected)
    {
        BrowserUrls.IsHostLike(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("chrome://newtab/", true)]
    [InlineData("chrome://new-tab-page/", true)]
    [InlineData("chrome-search://local-ntp/local-ntp.html", true)]
    [InlineData("edge://newtab", true)]
    [InlineData("https://ntp.msn.com/edge/ntp?locale=nb-NO", true)]
    [InlineData("https://www.google.com/_/chrome/newtab?ie=UTF-8", true)]
    [InlineData("about:newtab", true)]
    [InlineData("about:home", true)]
    [InlineData("about:blank", false)]
    [InlineData("chrome://newtabs/", false)]
    [InlineData("https://ntp.msn.com/edge/ntpx", false)]
    [InlineData("https://example.com/", false)]
    [InlineData(null, false)]
    public void IsNewTabPage_KnowsTheBrowsersNewTabPages(string? url, bool expected)
    {
        BrowserUrls.IsNewTabPage(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("devtools://devtools/bundled/devtools_app.html", true)]
    [InlineData("chrome://read-later.top-chrome/", true)]
    [InlineData("chrome://customize-chrome-side-panel.top-chrome/", true)]
    [InlineData("chrome://settings/", false)]
    [InlineData("https://top-chrome.example/", false)]
    [InlineData(null, false)]
    public void IsBrowserUi_KnowsDevToolsAndChromesSidePanels(string? url, bool expected)
    {
        BrowserUrls.IsBrowserUi(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("github.com/x", "https://github.com/x", true)]
    [InlineData("github.com/x", "https://www.github.com/x/", true)]
    [InlineData("https://github.com/x", "https://github.com/x", true)]
    [InlineData("GitHub.com/x", "https://github.com/x", true)]
    [InlineData("github.com", "https://github.com/x?y=1", true)]
    [InlineData("github.com/x", "https://github.com/xy", false)]
    [InlineData("github.com/x", "https://evil.example/github.com/x", false)]
    [InlineData("", "https://github.com/x", false)]
    [InlineData("github.com/x", null, false)]
    [InlineData("C:/Users/me/a%20b.html", "file:///C:/Users/me/a%20b.html", true)]
    [InlineData("C:/Users/me/a%20b.html", "file:///C:/Users/me/other.html", false)]
    public void Shows_TellsWhetherTheAddressBarShowsTheUrl(string? display, string? url, bool expected)
    {
        BrowserUrls.Shows(display, url).Should().Be(expected);
    }

    [Theory]
    [InlineData("C:/Users/me/AppData/Local/Temp/wam-m57%20local%20page.html", true)]
    [InlineData("c:/page.html", true)]
    [InlineData("D:/", true)]
    [InlineData("C:\\Users\\me\\page.html", false)]
    [InlineData("C:/Users/me/a page.html", false)]
    [InlineData("C:", false)]
    [InlineData("C:page.html", false)]
    [InlineData("//server/share/page.html", false)]
    [InlineData("CD:/page.html", false)]
    [InlineData(null, false)]
    public void IsShownFilePath_AcceptsAFilePagesPathAsChromeAndEdgeShowIt(string? text, bool expected)
    {
        BrowserUrls.IsShownFilePath(text).Should().Be(expected);
        BrowserUrls.IsUrlLike(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("C:/Users/me/a%20b.html", "file:///C:/Users/me/a%20b.html")]
    [InlineData("github.com/x", "github.com/x")]
    [InlineData("https://example.com/", "https://example.com/")]
    public void AddressOf_GivesAShownFilePathsFileUrl_AndAnyOtherTextAsItIs(string display, string expected)
    {
        BrowserUrls.AddressOf(display).Should().Be(expected);
    }

    [Theory]
    [InlineData("chrome://settings/passwords", true)]
    [InlineData("CHROME://flags", true)]
    [InlineData("edge://settings", true)]
    [InlineData("brave://wallet", true)]
    [InlineData("chrome-extension://abcdefghijklmnop/popup.html", true)]
    [InlineData("extension://abcdefghijklmnop/popup.html", true)]
    [InlineData("chrome-untrusted://print/", true)]
    [InlineData("chrome-search://local-ntp/local-ntp.html", true)]
    [InlineData("devtools://devtools/bundled/inspector.html", true)]
    [InlineData("view-source:https://example.com/", true)]
    [InlineData("about:version", true)]
    [InlineData("about:settings", true)]
    [InlineData("chrome://side-panel.top-chrome/", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("not a url", true)]
    [InlineData("blob:chrome-extension://abcdefghijklmnop/6f1c0e1a-2b3c-4d5e-8f90-a1b2c3d4e5f6", true)]
    [InlineData("filesystem:chrome-extension://abcdefghijklmnop/temporary/a.html", true)]
    [InlineData("blob:null/6f1c0e1a-2b3c-4d5e-8f90-a1b2c3d4e5f6", true)]
    [InlineData("isolated-app://aerugqztij5biqquuk3mfwpsaibuegaqcitgfchwuosuofdjabzqaaic/", true)]
    [InlineData("some-future-scheme://page", true)]
    [InlineData("ftp://example.com/file", true)]
    [InlineData("blob:https://example.com/6f1c0e1a-2b3c-4d5e-8f90-a1b2c3d4e5f6", false)]
    [InlineData("filesystem:https://example.com/temporary/a.html", false)]
    [InlineData("about:blank", false)]
    [InlineData("about:blank#top", false)]
    [InlineData("about:srcdoc", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("http://127.0.0.1:8080/form", false)]
    [InlineData("file:///C:/Users/me/AppData/Local/Temp/page.html", false)]
    [InlineData("data:text/html,<p>x</p>", false)]
    public void IsBrowserOwnPage_AllowsOnlyWebPages(string? url, bool expected)
    {
        BrowserUrls.IsBrowserOwnPage(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.com/inbox", "https://example.com/inbox", true)]
    [InlineData("https://www.example.com/inbox/", "http://example.com/inbox", true)]
    [InlineData("https://example.com/inbox#top", "https://example.com/inbox", true)]
    [InlineData("https://example.com/", "https://example.com/inbox", false)]
    [InlineData("https://example.com/inbox", "https://example.com/", false)]
    [InlineData("https://example.com/inbox", "https://example.com/inbox?x=1", false)]
    [InlineData("", "https://example.com/", false)]
    [InlineData(null, null, false)]
    public void SamePage_IsTheSameAddress_NotTheStartOfOne(string? a, string? b, bool expected)
    {
        BrowserUrls.SamePage(a, b).Should().Be(expected);
        BrowserUrls.SamePage(b, a).Should().Be(expected);
    }
}
