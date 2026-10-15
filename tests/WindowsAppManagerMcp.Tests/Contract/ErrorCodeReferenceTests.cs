using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions.Execution;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the "Error code reference" table in docs/TOOLS.md lists exactly the errorCode values
/// the server returns. Agents look codes up there: a missing code leaves them guessing, and a code
/// that nothing returns sends them after a failure that cannot happen.
/// The returned codes are read from the server itself: the members of its *ErrorCode enums that
/// src/ uses (outside comments), the codes <see cref="ToolErrorReporting"/> reports, and any string
/// literal used as an error code. These tests only read repository files and reflect over the
/// server assembly.
/// </summary>
public class ErrorCodeReferenceTests
{
    private const string ReferenceHeading = "### Error code reference";

    private static readonly Assembly ServerAssembly = typeof(ToolErrorReporting).Assembly;

    /// <summary>
    /// Codes that src/ uses but no tool can return, with the only method that returns them. The
    /// reference leaves them out; <see cref="CodesNoToolReturns_AreStillOnlyReturnedByUncalledMethods"/>
    /// fails once a tool can reach them.
    /// </summary>
    private static readonly Dictionary<string, string> CodesNoToolReturns = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void Reference_ListsExactlyTheErrorCodesTheServerReturns()
    {
        // Arrange
        var expected = ReturnedCodes().Except(CodesNoToolReturns.Keys).ToList();

        // Act
        var documented = DocumentedCodes();

        // Assert - both directions in one report
        using var scope = new AssertionScope();
        documented.Should().OnlyHaveUniqueItems("each code has one row");
        documented.Except(expected).Should().BeEmpty(
            "the docs/TOOLS.md error code reference must not list codes the server never returns");
        expected.Except(documented).Should().BeEmpty(
            "every code the server returns needs a row in the docs/TOOLS.md error code reference");
    }

    [Fact]
    public void Reference_IsInAlphabeticalOrder()
    {
        DocumentedCodes().Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnedCodes_IncludeEveryWayTheServerProducesACode()
    {
        // Guards the scan in ReturnedCodes, so the reference test cannot pass by finding nothing.
        var codes = ReturnedCodes();

        codes.Should().Contain(
            [
                ToolErrorReporting.ErrorCode, ToolErrorReporting.ToolErrorCode, ToolErrorReporting.InvalidArgumentsErrorCode,
                "InvalidSnapPosition", // nameof(InvalidSnapPosition) with 'using static WindowErrorCode'
                "UnsafeBatchArguments", // nameof(LaunchErrorCode.UnsafeBatchArguments)
                "CdpTimeout", // BrowserErrorCode.CdpTimeout passed on as an enum value
                "PresetNotFound", // ToolErrorException with a LayoutErrorCode name
                "None", // the success value of the browser, input and UI element results
                "InvalidState", // [ToolEnum(nameof(WindowErrorCode.InvalidState), ...)] on an argument enum
                "MoveFailed", // WindowErrorCode.MoveFailed passed to the window service's placement
            ]);
        codes.Should().NotContain(
            ["UrlExtractionFailed", "InvalidCoordinates", "ContentAreaNotFound"],
            "these enum members are declared but nothing in src/ returns them");
    }

    [Fact]
    public void CodesNoToolReturns_AreStillOnlyReturnedByUncalledMethods()
    {
        var codes = ReturnedCodes();
        var sources = ServerSources();

        foreach (var (code, method) in CodesNoToolReturns)
        {
            codes.Should().Contain(code, "a code src/ no longer uses should leave CodesNoToolReturns");

            // Every occurrence of the name followed by '(' that is not a declaration ("Task<T> Name(")
            var callers = sources
                .Where(source => Regex.Matches(source.Text, $@"\b{method}\s*\(").Count >
                                 Regex.Matches(source.Text, $@"\bTask<\w+>\s+{method}\s*\(").Count)
                .Select(source => source.Path);
            callers.Should().BeEmpty(
                "{0} is left out of the reference because nothing calls {1}; now that something does, document the code",
                code, method);
        }
    }

    /// <summary>The codes in the first column of the reference table.</summary>
    private static List<string> DocumentedCodes()
    {
        var lines = File.ReadAllLines(RepositoryFiles.PathOf("docs", "TOOLS.md"));
        var start = Array.IndexOf(lines, ReferenceHeading);
        start.Should().BeGreaterThan(0, "docs/TOOLS.md has a '{0}' section", ReferenceHeading);

        var codes = lines
            .Skip(start + 1)
            .TakeWhile(line => !line.StartsWith("#", StringComparison.Ordinal))
            .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries)[1])
            .SelectMany(cell => Regex.Matches(cell, "`([^`]+)`").Select(m => m.Groups[1].Value))
            .ToList();
        codes.Should().NotBeEmpty("the error code reference is a table of codes");
        return codes;
    }

    /// <summary>
    /// Every errorCode value src/ can produce: the *ErrorCode enum members it uses, the codes of
    /// ToolErrorReporting, and string literals assigned or passed as an error code.
    /// </summary>
    private static HashSet<string> ReturnedCodes()
    {
        var sources = ServerSources();
        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var enumType in ServerAssembly.GetTypes().Where(t => t.IsEnum && t.Name.EndsWith("ErrorCode", StringComparison.Ordinal)))
        {
            foreach (var member in Enum.GetNames(enumType))
            {
                if (sources.Any(source => UsesEnumMember(source.Text, enumType, member)))
                {
                    codes.Add(member);
                }
            }
        }

        // ToolErrorReporting.ErrorCode, ToolErrorCode and InvalidArgumentsErrorCode
        foreach (var field in typeof(ToolErrorReporting).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("ErrorCode", StringComparison.Ordinal)))
        {
            codes.Add((string)field.GetRawConstantValue()!);
        }

        // Literal codes, e.g. ErrorCode: "Something", errorCode = "Something", new ToolErrorException("Something", ...)
        foreach (var source in sources)
        {
            foreach (System.Text.RegularExpressions.Match match in Regex.Matches(source.Text,
                         @"(?:ErrorCode\s*[:=]\s*|new\s+ToolErrorException\(\s*)""(?<code>[A-Za-z]+)"""))
            {
                codes.Add(match.Groups["code"].Value);
            }
        }

        return codes;
    }

    /// <summary>
    /// True when the source uses <c>Enum.Member</c>, or imports the enum with <c>using static</c>
    /// and uses the bare member name.
    /// </summary>
    private static bool UsesEnumMember(string source, Type enumType, string member)
    {
        if (Regex.IsMatch(source, $@"\b{enumType.Name}\.{member}\b"))
        {
            return true;
        }

        return Regex.IsMatch(source, $@"using\s+static\s+[\w.]*\b{enumType.Name}\s*;") &&
               Regex.IsMatch(source, $@"(?<![\w.""]){member}(?![\w""])");
    }

    /// <summary>The server's C# sources, without comment lines (XML docs mention codes in prose).</summary>
    private static List<(string Path, string Text)> ServerSources()
    {
        var root = RepositoryFiles.PathOf("src", "WindowsAppManagerMcp");
        var separator = Path.DirectorySeparatorChar;

        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}") && !path.Contains($"{separator}bin{separator}"))
            .Select(path => (Path: path, Text: string.Join('\n', File.ReadLines(path)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))))
            .ToList();
        sources.Should().NotBeEmpty("the scan must see the server's sources");
        return sources;
    }
}
