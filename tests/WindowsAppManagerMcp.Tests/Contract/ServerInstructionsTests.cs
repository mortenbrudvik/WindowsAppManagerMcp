using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the server instructions every MCP client gets in the initialize (or server/discover) result, including
/// clients that never load the plugin skill (roadmap M2.11). They are short (15-20 lines), teach the locate, activate and
/// verify, act, verify loop, the coordinate rules and what the error codes that stop an agent mean, name only tools the
/// server serves, and never teach a destructive key chord. Reflection and string checks; the server is not started.
/// </summary>
public partial class ServerInstructionsTests
{
    private static readonly string[] Lines = ServerIdentity.Instructions.Split('\n');

    [GeneratedRegex(@"\b[a-z]+(?:_[a-z]+)+\b")]
    private static partial Regex SnakeCaseName();

    [Fact]
    public void TheInstructions_Are15To20Lines()
    {
        Lines.Should().OnlyContain(line => line.Trim().Length > 0, "no blank lines; every line counts");
        Lines.Length.Should().BeInRange(15, 20);
        ServerIdentity.Instructions.Length.Should().BeLessThan(3_000, "they sit in the agent's context for the whole session");
    }

    [Theory]
    [InlineData("locate")]
    [InlineData("activate and verify")]
    [InlineData("windowHandle")]
    [InlineData("physical virtual-screen pixels")]
    [InlineData("negative")]
    [InlineData("imageToScreen")]
    [InlineData("never with scaleFactor")]
    [InlineData("get_server_info")]
    [InlineData("untrusted")]
    [InlineData("not sleeping")]
    [InlineData("close_window")]
    public void TheInstructions_Teach(string text)
    {
        ServerIdentity.Instructions.Should().Contain(text);
    }

    [Theory]
    [InlineData("PausedByUser")]
    [InlineData("TargetIsAgentHost")]
    [InlineData("TargetElevated")]
    [InlineData("BlockedByModal")]
    [InlineData("TargetDeniedByPolicy")]
    [InlineData("DestructiveChordNeedsTarget")]
    [InlineData("SessionLocked")]
    public void TheInstructions_ExplainTheErrorCodesThatStopAnAgent(string errorCode)
    {
        // Each on a line of its own that says what to do
        Lines.Should().ContainSingle(line => line.Contains(errorCode + ":") || line.Contains(errorCode + ","));
    }

    [Fact]
    public void TheInstructions_NameOnlyToolsTheServerServes()
    {
        var named = SnakeCaseName().Matches(ServerIdentity.Instructions).Select(m => m.Value).Distinct().ToList();

        named.Should().NotBeEmpty();
        named.Should().BeSubsetOf(ServerToolCatalog.ServedToolNames, "an instruction that names a missing tool sends the agent looking for it");
    }

    [Fact]
    public void TheActRule_NamesEveryInputTool_AndTheWindowSelector()
    {
        // Review MCP-S-4: the rule must not read as a closed list that leaves out an input tool such as scroll
        var act = Lines.Single(line => line.StartsWith("- Act:", StringComparison.Ordinal));

        foreach (var tool in InputGateToolFilter.InputTools)
        {
            act.Should().MatchRegex($@"\b{tool}\b", "the windowHandle rule covers {0} too", tool);
        }

        act.Should().Contain("window selector");
    }

    [Fact]
    public void TheInstructions_DoNotTeachTheAltF4Chord()
    {
        ServerIdentity.Instructions.Should().NotContainAny("{F4}", "Alt+F4");
    }

    [Fact]
    public void TheServer_ConfiguresItsIdentityAndInstructions()
    {
        var program = Snapshots.ReadNormalized(RepositoryFiles.PathOf("src", "WindowsAppManagerMcp", "Program.cs"))!;

        program.Should().Contain("AddMcpServer(ServerIdentity.Configure)");
    }
}
