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
