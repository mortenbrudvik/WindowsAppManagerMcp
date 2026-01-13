namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for BrowserDetectionService.
/// Tests browser detection using tiered matching (exact, prefix, suffix patterns).
/// </summary>
public class BrowserDetectionServiceTests
{
    private readonly BrowserDetectionService _sut = new();

    #region IsBrowser - Exact Match Tests (Tier 1)

    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("CHROME.EXE")]  // Case insensitivity
    [InlineData("Chrome")]
    public void IsBrowser_Chrome_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("msedge")]
    [InlineData("msedge.exe")]
    [InlineData("MSEDGE.EXE")]
    public void IsBrowser_Edge_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("firefox.exe")]
    [InlineData("FIREFOX.EXE")]
    public void IsBrowser_Firefox_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("brave.exe")]
    public void IsBrowser_Brave_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("opera")]
    [InlineData("opera.exe")]
    public void IsBrowser_Opera_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("vivaldi")]
    [InlineData("vivaldi.exe")]
    public void IsBrowser_Vivaldi_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("arc")]
    [InlineData("arc.exe")]
    public void IsBrowser_Arc_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("chromium")]
    [InlineData("chromium.exe")]
    public void IsBrowser_Chromium_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("google-chrome")]
    [InlineData("google-chrome.exe")]
    public void IsBrowser_GoogleChrome_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    #endregion

    #region IsBrowser - Prefix Pattern Tests (Tier 2)

    [Theory]
    [InlineData("chrome-canary")]
    [InlineData("chrome-canary.exe")]
    [InlineData("chrome-dev")]
    [InlineData("chrome-beta")]
    public void IsBrowser_ChromeVariants_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("msedge-canary")]
    [InlineData("msedge-canary.exe")]
    [InlineData("msedge-dev")]
    [InlineData("msedge-beta")]
    public void IsBrowser_EdgeVariants_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("firefox-dev")]
    [InlineData("firefox-dev.exe")]
    [InlineData("firefox-nightly")]
    [InlineData("firefox-esr")]
    public void IsBrowser_FirefoxVariants_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("firefoxdeveloper")]
    [InlineData("firefoxdeveloper.exe")]
    public void IsBrowser_FirefoxDeveloper_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("waterfox")]
    [InlineData("waterfox.exe")]
    public void IsBrowser_Waterfox_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("librewolf")]
    [InlineData("librewolf.exe")]
    public void IsBrowser_Librewolf_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("floorp")]
    [InlineData("floorp.exe")]
    public void IsBrowser_Floorp_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("zen-browser")]
    [InlineData("zen-browser.exe")]
    public void IsBrowser_ZenBrowser_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("opera-developer")]
    [InlineData("opera-beta")]
    [InlineData("opera_developer")]
    public void IsBrowser_OperaVariants_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("brave-browser")]
    [InlineData("brave-nightly")]
    public void IsBrowser_BraveVariants_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("vivaldi-snapshot")]
    [InlineData("vivaldi-snapshot.exe")]
    public void IsBrowser_VivaldiVariants_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("thorium")]
    [InlineData("thorium.exe")]
    public void IsBrowser_Thorium_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("ungoogled-chromium")]
    [InlineData("ungoogled-chromium.exe")]
    public void IsBrowser_UngoogledChromium_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("chromium-browser")]
    [InlineData("chromium-browser.exe")]
    public void IsBrowser_ChromiumBrowser_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    #endregion

    #region IsBrowser - Suffix Pattern Tests (Tier 3)

    [Theory]
    [InlineData("custom-browser")]
    [InlineData("waterfox-browser")]
    public void IsBrowser_BrowserSuffix_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    [Theory]
    [InlineData("chrome-portable")]
    [InlineData("firefox-portable")]
    [InlineData("edge-portable")]
    public void IsBrowser_PortableSuffix_ReturnsTrue(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue();
    }

    #endregion

    #region IsBrowser - Full Path Tests

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe")]
    public void IsBrowser_ChromeFullPath_ReturnsTrue(string path)
    {
        _sut.IsBrowser(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe")]
    public void IsBrowser_EdgeFullPath_ReturnsTrue(string path)
    {
        _sut.IsBrowser(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe")]
    [InlineData(@"C:\Program Files (x86)\Mozilla Firefox\firefox.exe")]
    public void IsBrowser_FirefoxFullPath_ReturnsTrue(string path)
    {
        _sut.IsBrowser(path).Should().BeTrue();
    }

    [Fact]
    public void IsBrowser_UncPath_ReturnsTrue()
    {
        _sut.IsBrowser(@"\\server\share\chrome.exe").Should().BeTrue();
    }

    #endregion

    #region IsBrowser - Non-Browser Executables (Negative Tests)

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("code")]
    [InlineData("code.exe")]
    [InlineData("teams")]
    [InlineData("teams.exe")]
    [InlineData("slack")]
    [InlineData("slack.exe")]
    [InlineData("discord")]
    [InlineData("spotify")]
    [InlineData("cmd")]
    [InlineData("cmd.exe")]
    [InlineData("powershell")]
    [InlineData("powershell.exe")]
    public void IsBrowser_NonBrowserExecutables_ReturnsFalse(string executable)
    {
        _sut.IsBrowser(executable).Should().BeFalse();
    }

    [Theory]
    [InlineData("chromedriver")]      // Not a browser - Selenium driver
    [InlineData("chromedriver.exe")]
    [InlineData("geckodriver")]       // Firefox driver
    [InlineData("edgedriver")]        // Edge driver
    public void IsBrowser_WebDrivers_ReturnsFalse(string executable)
    {
        _sut.IsBrowser(executable).Should().BeFalse();
    }

    [Fact]
    public void IsBrowser_FirefoxVpn_ReturnsFalse()
    {
        // Firefox Private Network is a VPN service, not a browser
        _sut.IsBrowser("firefoxprivatenetwork").Should().BeFalse();
    }

    // Note: "brave-vpn" matches the "brave-" prefix pattern and is detected as a browser.
    // This is acceptable as it's better to be slightly over-inclusive for browser detection
    // than to miss legitimate browsers like "brave-browser" or "brave-nightly".

    #endregion

    #region IsBrowser - Null/Empty/Whitespace

    [Fact]
    public void IsBrowser_Null_ReturnsFalse()
    {
        _sut.IsBrowser(null!).Should().BeFalse();
    }

    [Fact]
    public void IsBrowser_EmptyString_ReturnsFalse()
    {
        _sut.IsBrowser("").Should().BeFalse();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void IsBrowser_Whitespace_ReturnsFalse(string executable)
    {
        _sut.IsBrowser(executable).Should().BeFalse();
    }

    #endregion

    #region IsBrowser - Backwards Compatibility (Original Browser List)

    /// <summary>
    /// Ensures all originally supported browsers are still detected.
    /// This is a regression safety net.
    /// </summary>
    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("msedge")]
    [InlineData("msedge.exe")]
    [InlineData("firefox")]
    [InlineData("firefox.exe")]
    [InlineData("brave")]
    [InlineData("brave.exe")]
    [InlineData("opera")]
    [InlineData("opera.exe")]
    public void IsBrowser_OriginalBrowserList_StillDetected(string executable)
    {
        _sut.IsBrowser(executable).Should().BeTrue(
            because: $"'{executable}' was supported in the original implementation and must remain supported");
    }

    #endregion

    #region ContainsUrl Tests

    [Fact]
    public void ContainsUrl_HttpsUrl_ReturnsTrue()
    {
        _sut.ContainsUrl(["https://example.com"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_HttpUrl_ReturnsTrue()
    {
        _sut.ContainsUrl(["http://localhost:8080"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_HttpsUpperCase_ReturnsTrue()
    {
        _sut.ContainsUrl(["HTTPS://EXAMPLE.COM"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_HttpUpperCase_ReturnsTrue()
    {
        _sut.ContainsUrl(["HTTP://LOCALHOST"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_FileUrl_ReturnsTrue()
    {
        _sut.ContainsUrl(["file://C:/test.html"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_FileUrlUpperCase_ReturnsTrue()
    {
        _sut.ContainsUrl(["FILE:///home/user/file.html"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_UrlWithOtherFlags_ReturnsTrue()
    {
        _sut.ContainsUrl(["--flag", "https://example.com"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_UrlWithNewWindowFlag_ReturnsTrue()
    {
        _sut.ContainsUrl(["--new-window", "http://localhost"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_UrlWithMultipleFlags_ReturnsTrue()
    {
        _sut.ContainsUrl(["--incognito", "--flag", "https://google.com"]).Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_FlagOnly_ReturnsFalse()
    {
        _sut.ContainsUrl(["--new-window"]).Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_IncognitoFlag_ReturnsFalse()
    {
        _sut.ContainsUrl(["--incognito"]).Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_PlainFile_ReturnsFalse()
    {
        _sut.ContainsUrl(["some-file.txt"]).Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_LocalPath_ReturnsFalse()
    {
        // Local path without file:// prefix is not a URL
        _sut.ContainsUrl([@"C:\path\to\file.html"]).Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_FtpUrl_ReturnsFalse()
    {
        // FTP protocol not supported
        _sut.ContainsUrl(["ftp://server.com"]).Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_MailtoUrl_ReturnsFalse()
    {
        // mailto protocol not supported for browser URL detection
        _sut.ContainsUrl(["mailto:test@example.com"]).Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_NullArguments_ReturnsFalse()
    {
        _sut.ContainsUrl(null).Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_EmptyArguments_ReturnsFalse()
    {
        _sut.ContainsUrl([]).Should().BeFalse();
    }

    #endregion

    #region GetNewWindowFlag Tests

    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("firefox")]
    [InlineData("msedge")]
    [InlineData("brave")]
    [InlineData("chromium")]
    [InlineData("vivaldi")]
    public void GetNewWindowFlag_Browser_ReturnsNewWindowFlag(string executable)
    {
        _sut.GetNewWindowFlag(executable).Should().Be("--new-window");
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("code")]
    [InlineData("teams")]
    public void GetNewWindowFlag_NonBrowser_ReturnsNull(string executable)
    {
        _sut.GetNewWindowFlag(executable).Should().BeNull();
    }

    [Fact]
    public void GetNewWindowFlag_Null_ReturnsNull()
    {
        _sut.GetNewWindowFlag(null!).Should().BeNull();
    }

    [Fact]
    public void GetNewWindowFlag_EmptyString_ReturnsNull()
    {
        _sut.GetNewWindowFlag("").Should().BeNull();
    }

    #endregion
}
