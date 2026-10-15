namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Reading a Chromium browser's switches from its command line (roadmap M5.3), as Chromium reads them on Windows.
/// </summary>
public class ChromiumCommandLineTests
{
    [Fact]
    public void Split_FollowsTheWindowsArgvRules()
    {
        var argv = ChromiumCommandLine.Split(
            "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --user-data-dir=\"C:\\Temp\\My Profile\" a\\\\\"b c\"  x\\y \"\"");

        argv.Should().Equal(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"--user-data-dir=C:\Temp\My Profile",
            @"a\b c",
            @"x\y",
            "");
    }

    [Fact]
    public void Split_TakesTheProgramNameUpToTheFirstSpace_WhenItIsNotQuoted()
    {
        ChromiumCommandLine.Split(@"C:\Chrome\chrome.exe --remote-debugging-port=9222")
            .Should().Equal(@"C:\Chrome\chrome.exe", "--remote-debugging-port=9222");
    }

    [Fact]
    public void Split_AnEscapedQuote_IsLiteral()
    {
        ChromiumCommandLine.Split("chrome.exe \"a\\\"b\" c").Should().Equal("chrome.exe", "a\"b", "c");
    }

    [Theory]
    [InlineData("chrome.exe --remote-debugging-port=9222", "9222")]
    [InlineData("chrome.exe -remote-debugging-port=9222", "9222")]
    [InlineData("chrome.exe /remote-debugging-port=9222", "9222")]
    [InlineData("chrome.exe --Remote-Debugging-Port=9222", "9222")]
    [InlineData("chrome.exe --remote-debugging-port=1111 --remote-debugging-port=2222", "2222")]
    [InlineData("chrome.exe --remote-debugging-port", "")]
    public void Parse_ReadsASwitch_AsChromiumDoesOnWindows(string commandLine, string expected)
    {
        // Prefixes --, - and /; the name lower-cased; the last value wins
        ChromiumCommandLine.Parse(commandLine).Should().ContainKey("remote-debugging-port")
            .WhoseValue.Should().Be(expected);
    }

    [Theory]
    [InlineData("chrome.exe https://example.com/?--remote-debugging-port=9222")]
    [InlineData("chrome.exe -- --remote-debugging-port=9222")]
    [InlineData("chrome.exe --single-argument --remote-debugging-port=9222")]
    [InlineData("\"C:\\a --remote-debugging-port=1\\chrome.exe\"")]
    [InlineData("chrome.exe - --")]
    [InlineData("")]
    public void Parse_DoesNotReadASwitch_ThatChromiumDoesNot(string commandLine)
    {
        // Not a switch: an argument that does not start with a prefix, anything after "--" or --single-argument (how the
        // shell passes a URL), and the program's own path
        ChromiumCommandLine.Parse(commandLine).Should().NotContainKey("remote-debugging-port");
    }

    [Fact]
    public void Parse_KeepsTheValuesCase()
    {
        ChromiumCommandLine.Parse("chrome.exe \"--user-data-dir=C:\\Temp\\WamM5Check-AbC\"")
            .Should().ContainKey("user-data-dir").WhoseValue.Should().Be(@"C:\Temp\WamM5Check-AbC");
    }
}
