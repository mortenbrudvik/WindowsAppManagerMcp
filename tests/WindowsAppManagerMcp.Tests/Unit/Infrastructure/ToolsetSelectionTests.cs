using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// How WAM_TOOLSETS, WAM_EXCLUDE_TOOLS and WAM_READ_ONLY choose the tools a server serves (roadmap M2.14), over a
/// fixture catalog with an always-on toolset, a toolset that is on by default and an opt-in one. Pure: the variables
/// come from a lookup function.
/// </summary>
public class ToolsetSelectionTests
{
    private static readonly ToolsetCatalog Catalog = new(
        "core",
        [
            new Toolset("core", OnByDefault: true, "The server's own state", ["info_tool"]),
            new Toolset("basic", OnByDefault: true, "Looking and acting", ["look_tool", "act_tool"]),
            new Toolset("extra", OnByDefault: false, "An opt-in group", ["extra_tool"]),
        ]);

    private static ToolsetSelection Select(string? toolsets = null, string? exclude = null, string? readOnly = null) =>
        ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => toolsets,
                ToolsetSelection.ExcludeToolsVariable => exclude,
                ToolsetSelection.ReadOnlyVariable => readOnly,
                _ => null
            },
            Catalog);

    [Fact]
    public void NothingSet_ServesTheDefaultToolsets()
    {
        var selection = Select();

        selection.Enabled.Should().Equal("core", "basic");
        selection.Disabled.Should().Equal("extra");
        selection.Source.Should().Be("default");
        selection.ReadOnly.Should().BeFalse();
        selection.ExcludedTools.Should().BeEmpty();
        selection.Warnings.Should().BeEmpty();
        Served(selection).Should().Equal("info_tool", "look_tool", "act_tool");
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("EXTRA")]
    [InlineData(" extra ")]
    public void AToolsetName_ServesThatToolsetAndTheAlwaysOnOne(string value)
    {
        var selection = Select(value);

        selection.Enabled.Should().Equal("core", "extra");
        selection.Source.Should().Be(ToolsetSelection.ToolsetsVariable);
        Served(selection).Should().Equal("info_tool", "extra_tool");
    }

    [Theory]
    [InlineData("all")]
    [InlineData("default,extra")]
    [InlineData("default; extra")]
    [InlineData("basic extra")]
    public void TheKeywordsAndLists_Combine(string value)
    {
        Select(value).Enabled.Should().Equal("core", "basic", "extra");
    }

    [Theory]
    [InlineData("+extra", new[] { "core", "basic", "extra" })]
    [InlineData("basic,+extra", new[] { "core", "basic", "extra" })]
    [InlineData("extra,+basic,-extra", new[] { "core", "basic" })]
    public void APlus_AddsAToolset_ToTheDefaultWhenNoToolsetIsNamedPlainly(string value, string[] expected)
    {
        Select(value).Enabled.Should().Equal(expected);
    }

    [Theory]
    [InlineData("-basic")]
    [InlineData("default,-basic")]
    [InlineData("all,-basic,-extra")]
    public void AMinus_RemovesAToolset_FromTheDefaultWhenNothingElseIsNamed(string value)
    {
        Select(value).Enabled.Should().Equal("core");
    }

    [Fact]
    public void TheAlwaysOnToolset_CannotBeRemoved()
    {
        var selection = Select("-core");

        selection.Enabled.Should().Contain("core");
        selection.Warnings.Should().ContainSingle().Which.Should().Contain("core").And.Contain("always");
    }

    [Fact]
    public void AnUnknownName_IsIgnoredWithAWarning()
    {
        var selection = Select("basic,widnows");

        selection.Enabled.Should().Equal("core", "basic");
        selection.Warnings.Should().ContainSingle().Which.Should()
            .Contain(ToolsetSelection.ToolsetsVariable).And.Contain("'widnows'").And.Contain("basic, core, extra");
    }

    [Fact]
    public void OnlyUnknownNames_KeepTheDefaultToolsets()
    {
        // A typo must not leave the agent with nothing but the always-on toolset
        var selection = Select("widnows");

        selection.Enabled.Should().Equal("core", "basic");
        selection.Warnings.Should().HaveCount(2).And.Contain(w => w.Contains("default toolsets"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankValue_IsNotSet(string value)
    {
        Select(value).Source.Should().Be("default");
    }

    [Fact]
    public void ExcludedTools_AreNotServed()
    {
        var selection = Select(exclude: "act_tool, INFO_TOOL");

        selection.ExcludedTools.Should().Equal("act_tool", "info_tool");
        Served(selection).Should().Equal("look_tool");
    }

    [Fact]
    public void AnExcludedNameThatIsNoTool_IsIgnoredWithAWarning()
    {
        var selection = Select(exclude: "act_tool,kill_everything");

        selection.ExcludedTools.Should().Equal("act_tool");
        selection.Warnings.Should().ContainSingle().Which.Should()
            .Contain(ToolsetSelection.ExcludeToolsVariable).And.Contain("'kill_everything'");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    public void ReadOnly_ServesOnlyToolsAnnotatedReadOnly(string value)
    {
        var selection = Select(readOnly: value);

        selection.ReadOnly.Should().BeTrue();
        selection.Warnings.Should().BeEmpty();
        selection.Serves("look_tool", readOnlyHint: true).Should().BeTrue();
        selection.Serves("act_tool", readOnlyHint: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("")]
    public void ReadOnly_Off(string value)
    {
        var selection = Select(readOnly: value);

        selection.ReadOnly.Should().BeFalse();
        selection.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void ReadOnly_AnUnrecognizedValue_TurnsItOnWithAWarning()
    {
        // The user asked for something; the safe reading is read-only
        var selection = Select(readOnly: "observer");

        selection.ReadOnly.Should().BeTrue();
        selection.Warnings.Should().ContainSingle().Which.Should().Contain(ToolsetSelection.ReadOnlyVariable).And.Contain("'observer'");
    }

    [Fact]
    public void AToolInNoToolset_IsServedUnlessExcludedOrReadOnly()
    {
        // The production catalog names every tool (ToolsetCatalogTests); a tool outside it is not the toolsets' to hide
        Select().Serves("unlisted_tool", readOnlyHint: false).Should().BeTrue();
        Select(readOnly: "1").Serves("unlisted_tool", readOnlyHint: false).Should().BeFalse();
    }

    [Fact]
    public void All_ServesEveryToolset_AndNothingElseIsSet()
    {
        var selection = ToolsetSelection.All(Catalog);

        selection.Enabled.Should().Equal("core", "basic", "extra");
        selection.Disabled.Should().BeEmpty();
        selection.ReadOnly.Should().BeFalse();
        selection.Source.Should().Be(ToolsetSelection.AllKeyword);
    }

    [Fact]
    public void TheCatalog_RefusesAToolInTwoToolsets()
    {
        var act = () => new ToolsetCatalog("a", [new Toolset("a", true, "A", ["x"]), new Toolset("b", true, "B", ["x"])]);

        act.Should().Throw<ArgumentException>().WithMessage("*'x'*");
    }

    [Fact]
    public void TheCatalog_NeedsItsAlwaysOnToolsetOnByDefault()
    {
        var act = () => new ToolsetCatalog("a", [new Toolset("a", OnByDefault: false, "A", ["x"])]);

        act.Should().Throw<ArgumentException>();
    }

    private static IEnumerable<string> Served(ToolsetSelection selection) =>
        Catalog.Toolsets.SelectMany(t => t.Tools).Where(tool => selection.Serves(tool, readOnlyHint: false));
}
