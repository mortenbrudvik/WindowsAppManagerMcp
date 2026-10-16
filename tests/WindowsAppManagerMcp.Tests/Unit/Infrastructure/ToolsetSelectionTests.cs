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

    /// <summary>The same toolsets, with info_tool as the tool that is always served.</summary>
    private static readonly ToolsetCatalog WithInfoTool = new("core", Catalog.Toolsets, alwaysServedTool: "info_tool");

    /// <summary>
    /// The same toolsets after three tools moved out of basic into toolsets that are off by default, as roadmap M6.1 moved
    /// tools into windows-batch and deprecated: batch_act without a replacement, old_look and old_act replaced.
    /// </summary>
    private static readonly ToolsetCatalog WithMoves = new(
        "core",
        [
            .. Catalog.Toolsets,
            new Toolset("batch", OnByDefault: false, "Several at once", ["batch_act"]),
            new Toolset("old", OnByDefault: false, "Replaced tools", ["old_look", "old_act"]),
        ],
        moved:
        [
            new MovedTool("batch_act", "basic"),
            new MovedTool("old_look", "basic", "look_tool"),
            new MovedTool("old_act", "basic", "act_tool with mode 'x'"),
        ]);

    private static ToolsetSelection Select(
        string? toolsets = null, string? exclude = null, string? readOnly = null, ToolsetCatalog? catalog = null) =>
        ToolsetSelection.FromEnvironment(
            name => name switch
            {
                ToolsetSelection.ToolsetsVariable => toolsets,
                ToolsetSelection.ExcludeToolsVariable => exclude,
                ToolsetSelection.ReadOnlyVariable => readOnly,
                _ => null
            },
            catalog ?? Catalog);

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

    [Theory]
    [InlineData("widnows")]
    [InlineData("widnows,+xtra")]
    [InlineData("widnows,+extra")]
    public void AnUnknownPlainName_WithNoValidOne_ServesOnlyTheAlwaysOnToolset(string value)
    {
        // The user set the variable to restrict the server to the toolsets named: a typo must not give the agent the
        // whole default (fail closed). The always-on toolset stays, so the agent can still read the warning in
        // get_server_info.
        var selection = Select(value);

        selection.Enabled.Should().Equal("core");
        selection.Source.Should().Be(ToolsetSelection.ToolsetsVariable);
        Served(selection).Should().Equal("info_tool");
        selection.Warnings.Should().Contain(w => w.Contains("'widnows'") && w.Contains("only the 'core' toolset"));
    }

    [Theory]
    [InlineData("-bsic")]
    [InlineData("default,-bsic")]
    [InlineData("all,-extra,-bsic")]
    [InlineData("basic,-bsic")]
    public void AnUnknownRemoval_ServesOnlyTheAlwaysOnToolset(string value)
    {
        // Ignoring '-bsic' would serve the toolset the user may have meant to remove
        var selection = Select(value);

        selection.Enabled.Should().Equal("core");
        selection.Warnings.Should().Contain(w => w.Contains("'-bsic'") && w.Contains("only the 'core' toolset"));
    }

    [Theory]
    [InlineData("+xtra", new[] { "core", "basic" })]
    [InlineData("basic,+xtra", new[] { "core", "basic" })]
    [InlineData("-basic,+xtra", new[] { "core" })]
    public void AnUnknownAddition_IsIgnored_BecauseIgnoringItServesLess(string value, string[] expected)
    {
        var selection = Select(value);

        selection.Enabled.Should().Equal(expected);
        selection.Warnings.Should().Contain(w => w.Contains("'+xtra'")).And.NotContain(w => w.Contains("to be safe"));
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

    [Theory]
    [InlineData("act_tool,kill_everything")]
    [InlineData("act_tol")]
    public void AnExcludedNameThatIsNoToolOrToolset_FailsClosed_ToTheAlwaysOnToolset(string exclude)
    {
        // Review MCP-S-2: the user meant to remove something; a typo must not leave it served
        var selection = Select(exclude: exclude);

        selection.Enabled.Should().Equal("core");
        selection.Source.Should().Be(ToolsetSelection.ExcludeToolsVariable);
        Served(selection).Should().Equal("info_tool");
        selection.Warnings.Should().Contain(w => w.Contains(ToolsetSelection.ExcludeToolsVariable) && w.Contains("is not a tool or toolset"));
        selection.Warnings.Should().Contain(w => w.Contains("only the 'core' toolset is on"));
    }

    [Fact]
    public void AnExcludedToolsetName_ExcludesEveryToolOfIt()
    {
        var selection = Select(exclude: "BASIC");

        selection.Enabled.Should().Equal("core", "basic");
        selection.ExcludedTools.Should().Equal("look_tool", "act_tool");
        selection.Warnings.Should().BeEmpty();
        Served(selection).Should().Equal("info_tool");
    }

    [Fact]
    public void ExcludingTheAlwaysOnToolset_KeepsTheAlwaysServedTool()
    {
        var selection = Select(exclude: "core", catalog: WithInfoTool);

        selection.ExcludedTools.Should().BeEmpty();
        selection.Serves("info_tool", readOnlyHint: true).Should().BeTrue();
        selection.Warnings.Should().ContainSingle().Which.Should().Contain("always served");
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
    public void TheAlwaysServedTool_CannotBeExcluded()
    {
        // It is the tool that tells the agent which tools are off, and why
        var selection = Select(exclude: "info_tool,act_tool", catalog: WithInfoTool);

        selection.ExcludedTools.Should().Equal("act_tool");
        selection.Serves("info_tool", readOnlyHint: true).Should().BeTrue();
        selection.Warnings.Should().ContainSingle().Which.Should()
            .Contain(ToolsetSelection.ExcludeToolsVariable).And.Contain("'info_tool'").And.Contain("always served");
    }

    [Fact]
    public void TheCatalog_NeedsItsAlwaysServedToolInTheAlwaysOnToolset()
    {
        var act = () => new ToolsetCatalog("core", Catalog.Toolsets, alwaysServedTool: "look_tool");

        act.Should().Throw<ArgumentException>().WithMessage("*'look_tool'*");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("all", null, null)]
    [InlineData("default,-extra", null, null)]
    [InlineData(null, "extra_tool", null)]
    public void TheInstructionsNote_IsNull_WhenNothingTheDefaultServesIsOff(string? toolsets, string? exclude, string? readOnly)
    {
        // An opt-in toolset that is off is not something the user turned off, and the instructions do not name its tools;
        // nor is a tool the toolsets in force would not serve anyway turned off by excluding it (roadmap M6.1)
        Select(toolsets, exclude, readOnly).InstructionsNote.Should().BeNull();
    }

    [Fact]
    public void TheInstructionsNote_SaysWhatTheUserTurnedOff()
    {
        var note = Select("-basic", "info_tool", "1").InstructionsNote;

        note.Should().NotBeNull();
        note.Should().Contain("basic").And.Contain("read-only").And.Contain("info_tool")
            .And.Contain("missing from your tool list").And.Contain("tell the user");
        note.Should().NotContain("extra", "the opt-in toolset was never on");
        note.Should().NotContain("\n", "it is one line of the instructions");
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

    #region Tools that moved out of a toolset (roadmap M6.1)

    [Fact]
    public void AMovedTool_IsOffByDefault_AndTheMovedNotesSayWhereItWent()
    {
        var selection = Select(catalog: WithMoves);

        Served(selection, WithMoves).Should().Equal("info_tool", "look_tool", "act_tool");
        selection.Warnings.Should().BeEmpty();
        selection.InstructionsNote.Should().BeNull("an opt-in toolset that is off is not something the user turned off");
        selection.Moved.Should().Equal(
            "In the batch toolset, which is off (WAM_TOOLSETS=+batch serves it): batch_act.",
            "In the old toolset, which is off (WAM_TOOLSETS=+old serves it): old_look (use look_tool), old_act (use act_tool with mode 'x').");
    }

    [Theory]
    [InlineData("+old")]
    [InlineData("all")]
    [InlineData("basic,old")]
    [InlineData("default,+old,+batch")]
    public void AMovedTool_IsServed_WithItsToolsetAndTheToolsetItCameFrom(string value)
    {
        var selection = Select(value, catalog: WithMoves);

        selection.Serves("old_look", readOnlyHint: true).Should().BeTrue();
        selection.Serves("old_act", readOnlyHint: false).Should().BeTrue();
        (selection.Moved ?? []).Should().NotContain(note => note.Contains("old_"));
    }

    [Theory]
    [InlineData("old,batch")]
    [InlineData("core,old,batch")]
    [InlineData("all,-basic")]
    [InlineData("-basic,+old,+batch")]
    public void AMovedTool_IsNotServed_WithoutTheToolsetItCameFrom_SoItsToolsetNeverWidensAConfiguration(string value)
    {
        // A list without basic did not serve old_act before it moved, so turning old on must not serve it now: a user who
        // removed the input toolset never gets the deprecated click back through 'all' or '+deprecated'
        var selection = Select(value, catalog: WithMoves);

        selection.Serves("old_look", readOnlyHint: true).Should().BeFalse();
        selection.Serves("old_act", readOnlyHint: false).Should().BeFalse();
        selection.Serves("batch_act", readOnlyHint: false).Should().BeFalse();
        selection.Moved.Should().BeNull("the toolset they came from is off, so nothing moved out of the user's reach");
        selection.Warnings.Should().Contain(w => w.Contains(ToolsetSelection.ToolsetsVariable) && w.Contains("old_look") &&
                                                 w.Contains("old_act") && w.Contains("'basic'") && w.Contains("'old'"));
    }

    [Fact]
    public void ServingAReplacedTool_Warns_AndNamesItsReplacement()
    {
        Select("+old", catalog: WithMoves).Warnings.Should().ContainSingle().Which.Should()
            .Contain("old_look (use look_tool)").And.Contain("old_act (use act_tool with mode 'x')").And.Contain("removed");
        Select("+batch", catalog: WithMoves).Warnings.Should().BeEmpty("batch_act moved, but nothing replaces it");
    }

    [Theory]
    [InlineData("old_look")]
    [InlineData("OLD_LOOK,batch_act")]
    [InlineData("old")]
    public void ExcludingAMovedToolThatIsNotServed_IsNoError_AndTurnsNothingElseOff(string exclude)
    {
        // A WAM_EXCLUDE_TOOLS written before the move names a tool the default no longer serves: it must not fail closed
        var selection = Select(exclude: exclude, catalog: WithMoves);

        selection.Source.Should().Be(ToolsetSelection.DefaultKeyword);
        selection.Enabled.Should().Equal("core", "basic");
        selection.Warnings.Should().BeEmpty();
        selection.InstructionsNote.Should().BeNull("the toolsets in force did not serve it anyway");
        Served(selection, WithMoves).Should().Equal("info_tool", "look_tool", "act_tool");
        (selection.Moved ?? []).Should().NotContain(note => note.Contains("old_look"), "the user does not want it back");
    }

    [Fact]
    public void ExcludingTheToolsetAToolCameFrom_ExcludesTheMovedToolToo()
    {
        // WAM_EXCLUDE_TOOLS=input written before the move meant every input tool, the deprecated click included
        var selection = Select("+old,+batch", exclude: "basic", catalog: WithMoves);

        selection.ExcludedTools.Should().Equal("look_tool", "act_tool", "batch_act", "old_look", "old_act");
        Served(selection, WithMoves).Should().Equal("info_tool");
    }

    [Fact]
    public void All_ServesTheMovedTools_WithoutAWarning()
    {
        var selection = ToolsetSelection.All(WithMoves);

        Served(selection, WithMoves).Should().HaveCount(7);
        selection.Moved.Should().BeNull();
        selection.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nowhere_tool", "basic")]
    [InlineData("old_look", "nothing")]
    [InlineData("old_look", "old")]
    public void TheCatalog_RefusesAMoveItCannotServe(string tool, string from)
    {
        var act = () => new ToolsetCatalog("core", WithMoves.Toolsets, moved: [new MovedTool(tool, from)]);

        act.Should().Throw<ArgumentException>().WithMessage($"*'{tool}'*");
    }

    #endregion

    private static IEnumerable<string> Served(ToolsetSelection selection, ToolsetCatalog? catalog = null) =>
        (catalog ?? Catalog).Toolsets.SelectMany(t => t.Tools).Where(tool => selection.Serves(tool, readOnlyHint: false));
}
