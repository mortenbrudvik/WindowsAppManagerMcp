namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for the launch_browser_with_debug argument list (BRW-9 / mcp-m3).
/// Pure: builds argument lists and command-line strings only, nothing is launched.
/// </summary>
public class DebugBrowserArgumentsTests
{
    [Fact]
    public void BuildDebugBrowserArguments_UserDataDirWithSpaces_HasNoEmbeddedQuotes()
    {
        // Act
        var arguments = ProcessService.BuildDebugBrowserArguments(9222, @"C:\Users\Jane Doe\dbg", url: null);

        // Assert - quoting is the command-line builder's job; the value itself must stay raw
        arguments.Should().Equal("--remote-debugging-port=9222", @"--user-data-dir=C:\Users\Jane Doe\dbg");
    }

    [Fact]
    public void BuildDebugBrowserArguments_WithUrl_AppendsUrlLast()
    {
        // Act
        var arguments = ProcessService.BuildDebugBrowserArguments(9333, @"C:\Temp\dbg", "https://example.com/?a=1&b=2");

        // Assert
        arguments.Should().Equal(
            "--remote-debugging-port=9333",
            @"--user-data-dir=C:\Temp\dbg",
            "https://example.com/?a=1&b=2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildDebugBrowserArguments_BlankUrl_IsOmitted(string url)
    {
        // Act
        var arguments = ProcessService.BuildDebugBrowserArguments(9222, @"C:\Temp\dbg", url);

        // Assert
        arguments.Should().HaveCount(2);
    }

    [Fact]
    public void BuildDebugBrowserArguments_UserDataDirWithSpaces_IsQuotedOnceOnTheCommandLine()
    {
        // Arrange
        var arguments = ProcessService.BuildDebugBrowserArguments(9222, @"C:\Users\Jane Doe\AppData\Local\Temp\browser-debug-1\", url: null);

        // Act
        var commandLine = CommandLineBuilder.JoinArguments(arguments);

        // Assert - one quoted token; the trailing backslash is doubled so it cannot escape the quote
        commandLine.Should().Be(
            "--remote-debugging-port=9222 \"--user-data-dir=C:\\Users\\Jane Doe\\AppData\\Local\\Temp\\browser-debug-1\\\\\"");
    }
}
