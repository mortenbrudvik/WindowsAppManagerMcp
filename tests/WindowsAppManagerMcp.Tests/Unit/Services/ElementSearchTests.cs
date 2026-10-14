namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The bounded element search (roadmap M3.4): the provider evaluates the condition, the tree is split so the search can
/// stop between parts at the deadline or the candidate limit, and what it found is kept. Over an in-memory tree.
/// </summary>
public class ElementSearchTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly FakeUiaTree _tree = new();

    private static UiaCondition IsButton => UiaCondition.PropertyEquals(UiaProperty.ControlType, "Button");

    private ElementSearchOutcome Search(FakeUiaNode root, UiaCondition condition, Func<UiaElement, bool>? filter = null, int keep = 50,
        CancellationToken? token = null) =>
        new ElementSearch(_tree, condition, filter, keep).Run(root, token ?? None);

    private static FakeUiaNode Chain(int depth, FakeUiaNode leaf)
    {
        var node = leaf;
        for (var i = 0; i < depth; i++)
        {
            node = new FakeUiaNode("Pane", $"Level {depth - i}").With(node);
        }

        return node;
    }

    private static FakeUiaNode WideList(string name, int count, string controlType = "Button") =>
        new FakeUiaNode("List", name).With(Enumerable.Range(1, count).Select(i => new FakeUiaNode(controlType, $"{name} {i}")).ToArray());

    [Fact]
    public void Run_FindsMatchesAtAnyDepth_NotTheRootItself()
    {
        // Arrange - far below any walk's depth limit
        var root = new FakeUiaNode("Button", "Root button").With(
            Chain(25, new FakeUiaNode("Button", "Deep")),
            new FakeUiaNode("Button", "Shallow"));

        // Act
        var outcome = Search(root, IsButton);

        // Assert - the shallow match first
        outcome.Matches.Select(m => m.Properties.Name).Should().Equal("Shallow", "Deep");
        outcome.MatchCount.Should().Be(2);
        outcome.Complete.Should().BeTrue();
    }

    [Fact]
    public void Run_SplitsTheTree_SoNoSingleCallSearchesTheWholeWindow()
    {
        // Arrange - a window with a big client area
        var client = new FakeUiaNode("Pane", "Client").With(
            Enumerable.Range(1, 6).Select(i => new FakeUiaNode("Group", $"Group {i}").With(WideList($"List {i}", 60))).ToArray());
        var window = new FakeUiaNode("Window", "App").With(new FakeUiaNode("TitleBar", "Title"), client);

        // Act
        var outcome = Search(window, IsButton);

        // Assert - each wide list is searched by the provider on its own; the window never in one call
        outcome.MatchCount.Should().Be(360);
        _tree.Finds.Should().NotContain("App:Descendants").And.Contain("List 1:Descendants").And.Contain("List 6:Descendants");
        _tree.ChildListings.Count.Should().BeLessThanOrEqualTo(ElementSearch.MaxExpansions);
    }

    [Fact]
    public void Run_StopsBetweenPartsAtTheDeadline_KeepingWhatItFound()
    {
        // Arrange - the deadline passes during the second list's search
        using var deadline = new CancellationTokenSource();
        var first = WideList("First", 60);
        var second = WideList("Second", 60);
        var third = WideList("Third", 60);
        second.OnFindAll = _ => deadline.Cancel();
        var window = new FakeUiaNode("Window", "App").With(first, second, third);

        // Act
        var outcome = Search(window, IsButton, token: deadline.Token);

        // Assert - the fake's FindAll, like the real one, finishes the call it is in
        outcome.Stop.Should().Be(ElementSearchStop.Timeout);
        outcome.MatchCount.Should().Be(120);
        _tree.Finds.Should().NotContain("Third:Descendants");
    }

    [Fact]
    public void Run_StopsAtTheCandidateLimit()
    {
        // Arrange - three lists of 1000 buttons each
        var window = new FakeUiaNode("Window", "App").With(WideList("A", 1000), WideList("B", 1000), WideList("C", 1000));

        // Act - every button is a candidate; the filter keeps few
        var outcome = Search(window, IsButton, e => e.Properties.Name!.EndsWith(" 7", StringComparison.Ordinal));

        // Assert
        outcome.Stop.Should().Be(ElementSearchStop.CandidateLimit);
        outcome.Candidates.Should().Be(ElementSearch.MaxCandidates);
        outcome.Matches.Select(m => m.Properties.Name).Should().Equal("A 7", "B 7");
        _tree.Finds.Should().NotContain("C:Descendants");
    }

    [Fact]
    public void Run_StopsAtTheCandidateLimit_WithinOnePartsResults()
    {
        // Arrange - one list too wide to split: a single FindAll returns all of it
        var window = new FakeUiaNode("Window", "App").With(WideList("Rows", 5000));
        var filtered = 0;

        // Act
        var outcome = Search(window, IsButton, _ =>
        {
            filtered++;
            return false;
        });

        // Assert - the client filter (a name pattern) never runs past the limit
        outcome.Stop.Should().Be(ElementSearchStop.CandidateLimit);
        outcome.Candidates.Should().Be(ElementSearch.MaxCandidates);
        filtered.Should().Be(ElementSearch.MaxCandidates);
    }

    [Fact]
    public void Run_StopsAtTheDeadline_WithinOnePartsResults()
    {
        // Arrange
        using var deadline = new CancellationTokenSource();
        var window = new FakeUiaNode("Window", "App").With(WideList("Rows", 500));
        var filtered = 0;

        // Act - the deadline passes while the tenth element is tested
        var outcome = Search(window, IsButton, _ =>
        {
            if (++filtered == 10)
            {
                deadline.Cancel();
            }

            return true;
        }, token: deadline.Token);

        // Assert
        outcome.Stop.Should().Be(ElementSearchStop.Timeout);
        filtered.Should().Be(10);
        outcome.MatchCount.Should().Be(10);
    }

    [Fact]
    public void Run_ExpandsAChainOfSingleChildren_WithoutUsingUpTheSplit()
    {
        // Arrange - the wrapper panes of a browser window above a document with wide sections
        var document = new FakeUiaNode("Document", "Page").With(
            Enumerable.Range(1, 6).Select(i => new FakeUiaNode("Group", $"Section {i}").With(WideList($"List {i}", 60))).ToArray());
        var window = new FakeUiaNode("Window", "Browser").With(Chain(20, document));

        // Act
        var outcome = Search(window, IsButton);

        // Assert - the lists are the parts, not a pane near the top
        outcome.MatchCount.Should().Be(360);
        _tree.Finds.Should().Contain("List 1:Descendants").And.Contain("List 6:Descendants");
        _tree.Finds.Should().NotContain(f => f.StartsWith("Level", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_KeepsTheFirstMatches_AndCountsTheRest()
    {
        var window = new FakeUiaNode("Window", "App").With(WideList("A", 80));

        var outcome = Search(window, IsButton, keep: 3);

        outcome.Matches.Should().HaveCount(3);
        outcome.MatchCount.Should().Be(80);
    }

    [Fact]
    public void Run_SkipsAPartThatFails_AndSearchesTheOthers()
    {
        // Arrange
        var broken = WideList("Broken", 60);
        broken.FindAllException = new System.Runtime.InteropServices.COMException("provider failed");
        var window = new FakeUiaNode("Window", "App").With(broken, WideList("Good", 60));

        // Act
        var outcome = Search(window, IsButton);

        // Assert
        outcome.SkippedParts.Should().Be(1);
        outcome.MatchCount.Should().Be(60);
        outcome.Complete.Should().BeFalse();
    }

    [Fact]
    public void Run_WhenAProviderDoesNotAnswerInTime_StopsWithWhatItFound()
    {
        // Arrange - the UIA3 engine's transaction timeout ends the second part's call
        var slow = WideList("Slow", 60);
        slow.FindAllException = new TimeoutException("UIA_E_TIMEOUT");
        var window = new FakeUiaNode("Window", "App").With(WideList("Fast", 60), slow, WideList("Never", 60));

        // Act
        var outcome = Search(window, IsButton);

        // Assert
        outcome.Stop.Should().Be(ElementSearchStop.Timeout);
        outcome.MatchCount.Should().Be(60);
    }

    [Fact]
    public void Run_WithAConditionTheElementsDoNotCarry_SearchesTheRootInOneCall()
    {
        // Arrange - a process id is not read with an element, so the levels cannot be tested here
        var window = new FakeUiaNode("Window", "App").With(new FakeUiaNode("Button", "OK") { ProcessId = 7 });

        // Act
        var outcome = Search(window, UiaCondition.PropertyEquals(UiaProperty.ProcessId, 7));

        // Assert
        outcome.MatchCount.Should().Be(1);
        _tree.Finds.Should().Equal("App:Descendants");
        _tree.ChildListings.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenTheEngineCannotExpressTheCondition_Throws()
    {
        // The managed client cannot search for AppBar: the caller tests the control type itself instead
        var window = new FakeUiaNode("Window", "App").With(WideList("A", 60));
        _tree.OnFindAll = (_, _) => throw new UiaConditionNotSupportedException("uia2");

        FluentActions.Invoking(() => Search(window, IsButton)).Should().Throw<UiaConditionNotSupportedException>();
    }

    [Fact]
    public void Run_APartWhoseProviderDoesNotSupportTheSearch_IsSkipped_AndTheOthersAreSearched()
    {
        // Arrange - the second part's provider answers UIA_E_NOTSUPPORTED, which the UIA3 engine turns into a
        // NotSupportedException: that provider's failure, not the engine's
        var second = WideList("Second", 60);
        second.FindAllException = new NotSupportedException("The element's provider does not support this (HRESULT 0x80040204).");
        var window = new FakeUiaNode("Window", "App").With(WideList("First", 60), second, WideList("Third", 60));

        // Act
        var outcome = Search(window, IsButton, keep: 200);

        // Assert
        outcome.MatchCount.Should().Be(120);
        outcome.Matches.Select(m => m.Properties.Name).Should().Contain("First 1").And.Contain("Third 60");
        outcome.SkippedParts.Should().Be(1);
    }

    [Fact]
    public void Run_OfARootThatIsGone_Throws()
    {
        var window = new FakeUiaNode("Window", "App") { ChildrenException = FakeUiaTree.Vanished() };

        FluentActions.Invoking(() => Search(window, IsButton)).Should().Throw<UiaElementUnavailableException>();
    }

    [Fact]
    public void Run_OfATreeThatListsASubtreeTwice_CountsAndKeepsEachElementOnce()
    {
        // Arrange - as a WinUI 3 window lists its WebView2's page under two parents (roadmap M3.F43): the same elements (same
        // runtime ids) reached through two paths
        var buttonId = FakeUiaNode.NewRuntimeId();
        var documentId = FakeUiaNode.NewRuntimeId();
        FakeUiaNode Page() => new FakeUiaNode("Document", "WAM page") { RuntimeId = documentId }.With(
            new FakeUiaNode("Button", "Page button") { RuntimeId = buttonId });
        var window = new FakeUiaNode("Window", "App").With(
            new FakeUiaNode("Pane", "Bridge").With(new FakeUiaNode("Pane", "Web").With(Page())),
            new FakeUiaNode("Pane", "XAML").With(new FakeUiaNode("Button", "Native button"), new FakeUiaNode("Pane", "WebView2").With(Page())));

        // Act
        var outcome = Search(window, IsButton);

        // Assert
        outcome.Matches.Select(m => m.Properties.Name).Should().Equal("Native button", "Page button");
        outcome.MatchCount.Should().Be(2);
        outcome.Candidates.Should().Be(2);
    }

    [Fact]
    public void Run_CountsElementsWithoutARuntimeIdEachTime_TheyCannotBeToldApart()
    {
        // Arrange
        var window = new FakeUiaNode("Window", "App").With(
            new FakeUiaNode("Button", "One") { RuntimeId = null },
            new FakeUiaNode("Button", "One") { RuntimeId = null });

        // Act
        var outcome = Search(window, IsButton);

        // Assert
        outcome.MatchCount.Should().Be(2);
    }

    [Fact]
    public void Snapshot_FromAnotherThread_HasWhatWasFoundSoFar()
    {
        // Arrange - the caller reads the snapshot while the search is in its second part (as when the worker abandons it)
        ElementSearchOutcome? during = null;
        var second = WideList("Second", 60);
        var window = new FakeUiaNode("Window", "App").With(WideList("First", 60), second);
        var search = new ElementSearch(_tree, IsButton, null, 10);
        second.OnFindAll = _ => during = Task.Run(search.Snapshot).Result;

        // Act
        search.Run(window, None);

        // Assert
        during!.MatchCount.Should().Be(60);
        during.Matches.Should().HaveCount(10);
    }

    #region A name that contains a text, narrowed by the provider (roadmap M3.F12)

    private static UiaCondition ContainsSave => UiaCondition.PropertyContains(UiaProperty.Name, "save", ignoreCase: true);

    private static Func<UiaElement, bool> NameContains(string text) =>
        e => (e.Properties.Name ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Window "Documents" > List "Recent" (60 buttons, every seventh "Save n", the rest "Open n"), List "Pinned" (the same)
    /// and a Button "SAVE all" (a level read while the tree is split).
    /// </summary>
    private static FakeUiaNode Documents()
    {
        FakeUiaNode List(string name) => new FakeUiaNode("List", name).With(Enumerable.Range(1, 60)
            .Select(i => new FakeUiaNode("Button", i % 7 == 0 ? $"Save {name} {i}" : $"Open {name} {i}")).ToArray());
        return new FakeUiaNode("Window", "Documents").With(List("Recent"), List("Pinned"), new FakeUiaNode("Button", "SAVE all"));
    }

    private static ElementSearchOutcome ServerOnly(FakeUiaNode root, int keep = 50) =>
        new ElementSearch(new FakeUiaTree { Engine = UiaEngine.Uia2 }, IsButton, NameContains("save"), keep).Run(root, None);

    private ElementSearchOutcome Narrowed(FakeUiaNode root, int keep = 50) =>
        new ElementSearch(_tree, IsButton, NameContains("save"), keep, narrowing: ContainsSave).Run(root, None);

    private static void ShouldBeTheSame(ElementSearchOutcome narrowed, ElementSearchOutcome serverOnly)
    {
        narrowed.Matches.Select(m => m.Properties.Name).Should().Equal(serverOnly.Matches.Select(m => m.Properties.Name));
        narrowed.MatchCount.Should().Be(serverOnly.MatchCount);
        narrowed.Stop.Should().Be(serverOnly.Stop);
        narrowed.SkippedParts.Should().Be(serverOnly.SkippedParts);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(3)]
    public void Run_WithANarrowing_TheProviderTestsTheSubstring_AndFindsWhatTheServerAloneFinds(int keep)
    {
        // Arrange
        var conditions = new List<UiaCondition>();
        _tree.OnFindAll = (_, condition) => conditions.Add(condition);

        // Act
        var serverOnly = ServerOnly(Documents(), keep);
        var narrowed = Narrowed(Documents(), keep);

        // Assert - the same matches, in the same order, and the same count beyond the kept ones; the provider returned
        // only the elements whose name has the text
        ShouldBeTheSame(narrowed, serverOnly);
        narrowed.MatchCount.Should().Be(17);
        narrowed.Candidates.Should().Be(17);
        serverOnly.Candidates.Should().Be(121);
        conditions.Should().NotBeEmpty().And.OnlyContain(c => FakeUiaTree.HasSubstring(c));
    }

    [Fact]
    public void Run_WithANarrowing_TheServerStillTestsEveryElementTheProviderReturns()
    {
        // Arrange - a provider that ignores the flag (matches every name), and the name's test on the server is stricter
        // than the narrowing (a narrowing is only a part of the text)
        _tree.SubstringTest = (_, _) => true;
        var search = new ElementSearch(_tree, IsButton, NameContains("save recent"), keep: 50,
            narrowing: UiaCondition.PropertyContains(UiaProperty.Name, "save", ignoreCase: true));

        // Act
        var outcome = search.Run(Documents(), None);

        // Assert
        outcome.Matches.Select(m => m.Properties.Name).Should().Equal(
            Enumerable.Range(1, 8).Select(i => $"Save Recent {i * 7}"));
    }

    [Fact]
    public void Run_WithAProviderThatMisappliesTheNarrowing_FindsWhatTheServerAloneFinds()
    {
        // A provider that adds elements the text is not in (here the "Open" rows ending in 1) is caught by the server's test.
        // One that left out a match could not be caught, which is why the narrowing is made only of characters whose case
        // every provider folds alike (ElementCriteria.NameNarrowing)
        _tree.SubstringTest = (node, _) => node.Name?.StartsWith("Open", StringComparison.Ordinal) != true || node.Name.EndsWith('1');

        ShouldBeTheSame(Narrowed(Documents()), ServerOnly(Documents()));
    }

    [Fact]
    public void Run_WhenTheEngineRefusesTheNarrowing_SearchesOnWithoutIt_AndFindsWhatTheServerAloneFinds()
    {
        // Arrange - Windows before 10 1809 refuses PropertyConditionFlags_MatchSubstring
        var conditions = new List<UiaCondition>();
        _tree.OnFindAll = (_, condition) => conditions.Add(condition);
        _tree.RefusesSubstrings = true;

        // Act
        var narrowed = Narrowed(Documents());

        // Assert - asked once; the rest of the search went without it, and the split was not done again
        ShouldBeTheSame(narrowed, ServerOnly(Documents()));
        conditions.Count(FakeUiaTree.HasSubstring).Should().Be(1);
        conditions.Should().HaveCount(3);
        _tree.ChildListings.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Run_WhenTheEngineRefusesTheBaseConditionToo_Throws()
    {
        // Without the narrowing it is the caller's fallback (the managed client and AppBar), as before
        _tree.OnFindAll = (_, _) => throw new UiaConditionNotSupportedException("uia2");

        FluentActions.Invoking(() => Narrowed(Documents())).Should().Throw<UiaConditionNotSupportedException>();
    }

    [Fact]
    public void Run_UnderTheManagedEngine_PassesNoNarrowing()
    {
        // Arrange - System.Windows.Automation has no substring flag
        _tree.Engine = UiaEngine.Uia2;
        var conditions = new List<UiaCondition>();
        _tree.OnFindAll = (_, condition) => conditions.Add(condition);

        // Act
        var narrowed = Narrowed(Documents());

        // Assert
        ShouldBeTheSame(narrowed, ServerOnly(Documents()));
        conditions.Should().NotBeEmpty().And.NotContain(c => FakeUiaTree.HasSubstring(c));
    }

    [Fact]
    public void Run_WithANarrowing_ReachesFurtherBeforeTheCandidateLimit_KeepingTheServerOnlyMatchesFirst()
    {
        // Arrange - the limit counts what the provider matched: 3000 buttons without the narrowing, 30 with it
        var window = new FakeUiaNode("Window", "App").With(WideList("A", 1000), WideList("B", 1000), WideList("C", 1000));
        var sevens = UiaCondition.PropertyContains(UiaProperty.Name, "77", ignoreCase: true);

        // Act
        var serverOnly = new ElementSearch(new FakeUiaTree(), IsButton, NameContains("77"), 100).Run(window, None);
        var narrowed = new ElementSearch(_tree, IsButton, NameContains("77"), 100, narrowing: sevens).Run(window, None);

        // Assert - the cost limit stops the server-only search after two lists; the narrowed one finishes, and what the
        // server-only search found comes first, in the same order
        serverOnly.Stop.Should().Be(ElementSearchStop.CandidateLimit);
        narrowed.Complete.Should().BeTrue();
        narrowed.Matches.Select(m => m.Properties.Name).Should().StartWith(serverOnly.Matches.Select(m => m.Properties.Name));
        narrowed.MatchCount.Should().Be(3 * 19);
    }

    [Theory]
    [InlineData("Save", "Save")]
    [InlineData("save as", "save as")]
    [InlineData("Find in files", "nd ")]
    [InlineData("Café menu", " menu")]
    [InlineData("FILE", "LE")]
    [InlineData("i", null)]
    [InlineData("日本語", null)]
    public void NameNarrowing_ForContains_IsTheLongestRunOfCharactersEveryProviderFoldsAlike(string name, string? expected)
    {
        // ASCII only (no other character equals an ASCII one ignoring case ordinally), and no i or I: a Turkish or Azerbaijani
        // comparison folds them to the dotted and dotless I, so a provider that compares by the user's locale could miss them
        var narrowing = new ElementCriteria(Name: name, NameMatch: ElementNameMatch.Contains).NameNarrowing;

        if (expected == null)
        {
            narrowing.Should().BeNull();
        }
        else
        {
            narrowing.Should().Be(UiaCondition.PropertyContains(UiaProperty.Name, expected, ignoreCase: true));
        }
    }

    [Fact]
    public void NameNarrowing_ForAnExactNameAPatternOrNoName_IsNull()
    {
        // An exact name is already the provider's condition; a pattern has no text every match contains
        new ElementCriteria(Name: "Save").NameNarrowing.Should().BeNull();
        new ElementCriteria(Name: "Save", NameMatch: ElementNameMatch.Regex).NameNarrowing.Should().BeNull();
        new ElementCriteria(AutomationId: "SaveBtn", NameMatch: ElementNameMatch.Contains).NameNarrowing.Should().BeNull();
    }

    [Fact]
    public void NameNarrowing_IsNotPartOfTheCompiledCondition_TheServersTestStays()
    {
        var (condition, filter) = new ElementCriteria(Name: "Save", NameMatch: ElementNameMatch.Contains).Compile();

        FakeUiaTree.HasSubstring(condition).Should().BeFalse();
        filter.Should().NotBeNull();
    }

    [Fact]
    public void Conditions_ASubstring_IsOfAStringPropertyAndNotEmpty()
    {
        UiaCondition.PropertyContains(UiaProperty.Name, "Save", ignoreCase: true)
            .Should().Be(new UiaPropertyContainsCondition(UiaProperty.Name, "Save", IgnoreCase: true));
        FluentActions.Invoking(() => UiaCondition.PropertyContains(UiaProperty.Name, "")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UiaCondition.PropertyContains(UiaProperty.ControlType, "Button")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UiaCondition.PropertyContains(UiaProperty.ProcessId, "7")).Should().Throw<ArgumentException>();
    }

    #endregion

    #region The condition evaluator

    private static UiaElement Element(string controlType, string? name = null, string? automationId = null, string? className = null,
        int? controlTypeId = null, bool enabled = true, bool offscreen = false, IReadOnlyList<int>? runtimeId = null) =>
        new(new FakeUiaNode(controlType, name),
            new UiaNodeProperties(name, controlType, automationId, UiaRect.Empty, enabled, offscreen),
            className, IsKeyboardFocusable: false, UiaPatterns.None, runtimeId, controlTypeId);

    [Fact]
    public void Evaluator_ComparesStringsOrdinally_IgnoringCaseOnlyWhenAsked_AndAMissingStringAsEmpty()
    {
        var save = Element("Button", "Save", "SaveBtn", "Button");

        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.Name, "save", ignoreCase: true), save).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.Name, "save"), save).Should().BeFalse();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.AutomationId, "SaveBtn"), save).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.ClassName, "button", ignoreCase: true), save).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.Name, ""), Element("Pane")).Should().BeTrue();
    }

    [Fact]
    public void Evaluator_TestsASubstringOrdinally_IgnoringCaseOnlyWhenAsked_AndAMissingStringAsEmpty()
    {
        var save = Element("Button", "Save As", "SaveAsBtn", "Button");

        UiaConditionEvaluator.Matches(UiaCondition.PropertyContains(UiaProperty.Name, "E A", ignoreCase: true), save).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyContains(UiaProperty.Name, "e a"), save).Should().BeFalse();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyContains(UiaProperty.AutomationId, "AsB"), save).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyContains(UiaProperty.ClassName, "UTT", ignoreCase: true), save).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyContains(UiaProperty.Name, "a"), Element("Pane")).Should().BeFalse();
        UiaConditionEvaluator.CanEvaluate(UiaCondition.And(IsButton, UiaCondition.PropertyContains(UiaProperty.Name, "a"))).Should().BeTrue();
        UiaConditionEvaluator.CanEvaluate(UiaCondition.PropertyContains(UiaProperty.FrameworkId, "WPF")).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_ComparesControlTypesByIdWhenTheEngineReadOne_AndByNameOtherwise()
    {
        var appBarById = Element("AppBar", controlTypeId: 50040);
        var appBarByLocalizedName = Element("AppBar", controlTypeId: null);

        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.ControlType, "appbar"), appBarById).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.ControlType, "AppBar"), appBarByLocalizedName).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.ControlType, "Button"), appBarById).Should().BeFalse();
    }

    [Fact]
    public void Evaluator_HandlesStatesRuntimeIdsAndCombinations()
    {
        var element = Element("Button", "OK", enabled: false, offscreen: true, runtimeId: [42, 7]);
        var condition = UiaCondition.And(
            UiaCondition.Or(IsButton, UiaCondition.PropertyEquals(UiaProperty.ControlType, "Edit")),
            UiaCondition.Not(UiaCondition.PropertyEquals(UiaProperty.IsEnabled, true)),
            UiaCondition.PropertyEquals(UiaProperty.IsOffscreen, true),
            UiaCondition.PropertyEquals(UiaProperty.RuntimeId, new[] { 42, 7 }));

        UiaConditionEvaluator.Matches(condition, element).Should().BeTrue();
        UiaConditionEvaluator.Matches(UiaCondition.PropertyEquals(UiaProperty.RuntimeId, new[] { 42, 8 }), element).Should().BeFalse();
        UiaConditionEvaluator.CanEvaluate(condition).Should().BeTrue();
        UiaConditionEvaluator.CanEvaluate(UiaCondition.Not(UiaCondition.PropertyEquals(UiaProperty.FrameworkId, "WPF"))).Should().BeFalse();
    }

    #endregion
}
