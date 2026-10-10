using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for CommandLineBuilder (PAL-7 / PAL-8 / BRW-9).
/// The joined command line must be split back into exactly the original arguments by the
/// standard Windows parser (CommandLineToArgvW, which follows the MSVCRT rules).
/// These tests only build and parse strings; nothing is launched.
/// </summary>
public class CommandLineBuilderTests
{
    #region Exact Output

    [Theory]
    [InlineData("file.txt", "file.txt")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("", "\"\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    [InlineData(@"C:\NoSpace\", @"C:\NoSpace\")]
    [InlineData(@"C:\My Folder\", "\"C:\\My Folder\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("--user-data-dir=C:\\Users\\Jane Doe\\dbg", "\"--user-data-dir=C:\\Users\\Jane Doe\\dbg\"")]
    [InlineData("https://example.com/?a=1&b=2", "https://example.com/?a=1&b=2")]
    public void JoinArguments_SingleArgument_ProducesExpectedText(string argument, string expected)
    {
        // Act
        var result = CommandLineBuilder.JoinArguments([argument]);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void JoinArguments_MultipleArguments_SeparatesWithSingleSpaces()
    {
        // Act
        var result = CommandLineBuilder.JoinArguments(["--flag", "two words", "", "last"]);

        // Assert
        result.Should().Be("--flag \"two words\" \"\" last");
    }

    [Fact]
    public void JoinArguments_NoArguments_ReturnsEmptyString()
    {
        // Act
        var result = CommandLineBuilder.JoinArguments([]);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void JoinArguments_ForBatchFile_QuotesCmdDelimitersAndParentheses()
    {
        // cmd.exe splits batch parameters on ',', ';' and '=' and treats parentheses specially in
        // blocks, so arguments containing them are quoted when the target is a batch file.

        // Act
        var result = CommandLineBuilder.JoinArguments(["a;b", "x=1", "f(1)", "c,d", "plain"], quoteCmdDelimiters: true);

        // Assert
        result.Should().Be("\"a;b\" \"x=1\" \"f(1)\" \"c,d\" plain");
    }

    [Fact]
    public void JoinArguments_ForNativeExecutable_DoesNotQuoteCmdDelimiters()
    {
        // Act
        var result = CommandLineBuilder.JoinArguments(["a;b", "x=1"]);

        // Assert
        result.Should().Be("a;b x=1");
    }

    #endregion

    #region Round Trip Through CommandLineToArgvW

    public static IEnumerable<object[]> TrickyArgumentSets()
    {
        yield return [new[] { "simple" }];
        yield return [new[] { @"C:\My Folder\", "next" }];
        yield return [new[] { @"C:\Trailing\\\", "next" }];
        yield return [new[] { "", "after-empty" }];
        yield return [new[] { "embedded \"quotes\" here" }];
        yield return [new[] { "\"already quoted\"" }];
        yield return [new[] { "back\\\\\"slash-quote" }];
        yield return [new[] { "tab\tseparated", "new\nline" }];
        yield return [new[] { @"C:\R&D\proj", "O'Brien", "{GUID}", "(x86)" }];
        yield return [new[] { "--user-data-dir=C:\\Users\\Jane Doe\\AppData\\Local\\Temp\\browser-debug-1\\" }];
        yield return [new[] { "--flag=\"value with spaces\"" }];
        yield return [new[] { "\\\\server\\share\\dir with space\\" }];
        yield return [new[] { "trailing space " , " leading space" }];
    }

    [Theory]
    [MemberData(nameof(TrickyArgumentSets))]
    public void JoinArguments_RoundTripsThroughWindowsParser(string[] arguments)
    {
        // Arrange
        var commandLine = "program.exe " + CommandLineBuilder.JoinArguments(arguments);

        // Act
        var parsed = ParseWithCommandLineToArgvW(commandLine);

        // Assert - argv[0] is the program, the rest must equal the input exactly
        parsed.Skip(1).Should().Equal(arguments);
    }

    [Theory]
    [MemberData(nameof(TrickyArgumentSets))]
    public void JoinArguments_ForBatchFile_RoundTripsThroughWindowsParser(string[] arguments)
    {
        // Arrange
        var commandLine = "program.exe " + CommandLineBuilder.JoinArguments(arguments, quoteCmdDelimiters: true);

        // Act
        var parsed = ParseWithCommandLineToArgvW(commandLine);

        // Assert
        parsed.Skip(1).Should().Equal(arguments);
    }

    /// <summary>
    /// Documents the PAL-8 defect: the previous joiner wrapped arguments containing a space in quotes
    /// without escaping, so a trailing backslash escaped the closing quote and merged arguments.
    /// </summary>
    [Fact]
    public void PreviousNaiveJoin_TrailingBackslash_MergedArguments()
    {
        // Arrange - the joining expression ProcessService used before this fix
        string[] arguments = [@"C:\My Folder\", "next"];
        var naive = string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        // Act
        var naiveParsed = ParseWithCommandLineToArgvW("program.exe " + naive);
        var fixedParsed = ParseWithCommandLineToArgvW("program.exe " + CommandLineBuilder.JoinArguments(arguments));

        // Assert
        naiveParsed.Skip(1).Should().NotEqual(arguments);
        fixedParsed.Skip(1).Should().Equal(arguments);
    }

    #endregion

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string[] ParseWithCommandLineToArgvW(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException("CommandLineToArgvW failed");
        }

        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }
}
