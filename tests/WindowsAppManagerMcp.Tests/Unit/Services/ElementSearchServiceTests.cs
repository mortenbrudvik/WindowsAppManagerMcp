using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// find_ui_elements' service (roadmap M3.4) over an in-memory UI Automation tree: the criteria, the refs, click points and
/// detail levels, withinRef, and the failures. No real window is read.
/// </summary>
public class ElementSearchServiceTests
{
    private static readonly nint Handle = 0x7100;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;

    public ElementSearchServiceTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
    }

    private ElementSearchService CreateSut() => new(_tree, _worker, _registry, _windows.Object);

    private Task<FindElementsResult> Find(ElementQuery query) => CreateSut().FindAsync(query);

    private static ElementQuery Query(
        string? name = null, ElementNameMatch nameMatch = ElementNameMatch.Exact, string? automationId = null,
        string[]? controlTypes = null, string? className = null, bool visibleOnly = true, bool enabledOnly = false,
        int maxResults = 20, ElementDetail detail = ElementDetail.Minimal, string? withinRef = null) =>
        new(Handle, withinRef, name, nameMatch, automationId, controlTypes, className, visibleOnly, enabledOnly, maxResults, detail,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// Window "Editor"
    ///   ToolBar "Standard" > Button "Save" (SaveBtn), Button "Save As" (SaveAsBtn), Button "Open" (disabled)
    ///   Pane "Body" > Group "Options" > CheckBox "Wrap" (on), Edit "Search" (focus, value), Edit "Password"
    ///   List "Hidden" (offscreen) > ListItem "Gone"
    /// </summary>
    private (FakeUiaNode Window, FakeUiaNode Save, FakeUiaNode Options) BuildEditor()
    {
        var save = new FakeUiaNode("Button", "Save")
        {
            AutomationId = "SaveBtn", ClassName = "Button", Bounds = new UiaRect(100, 40, 80, 24), ActionPatterns = ["Invoke"],
            ClickablePoint = new UiaPoint(120, 50)
        };
        var options = new FakeUiaNode("Group", "Options").With(
            new FakeUiaNode("CheckBox", "Wrap") { ActionPatterns = ["Toggle"] }.WithPattern<IUiaTogglePattern>(new FakeTogglePattern(UiaToggleState.On)),
            new FakeUiaNode("Edit", "Search")
            {
                AutomationId = "SearchBox", ActionPatterns = ["Value"], IsKeyboardFocusable = true, HasKeyboardFocus = true,
                Value = new string('x', 250), FrameworkId = "WPF", ProcessId = 300, HelpText = "Type to search", AccessKey = "Alt+S",
                LocalizedControlType = "edit"
            },
            new FakeUiaNode("Edit", "Password") { ActionPatterns = ["Value"], IsPassword = true, Value = "secret" });
        var window = new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("ToolBar", "Standard").With(
                save,
                new FakeUiaNode("Button", "Save As") { AutomationId = "SaveAsBtn", Bounds = new UiaRect(200, 40, 80, 24) },
                new FakeUiaNode("Button", "Open") { IsEnabled = false }),
            new FakeUiaNode("Pane", "Body").With(options),
            new FakeUiaNode("List", "Hidden") { IsOffscreen = true }.With(new FakeUiaNode("ListItem", "Gone") { IsOffscreen = true }));
        _tree.WithWindow(Handle, window);
        return (window, save, options);
    }

    #region Criteria

    [Fact]
    public async Task Find_ByExactName_IgnoringCase_ReturnsTheMatchWithARefClickPointAndPatterns()
    {
        // Arrange
        BuildEditor();

        // Act
        var result = await Find(Query(name: "save"));

        // Assert
        result.Success.Should().BeTrue();
        result.WindowTitle.Should().Be("Editor");
        result.MatchCount.Should().Be(1);
        result.Truncated.Should().BeFalse();
        result.Elements.Should().ContainSingle().Which.Should().BeEquivalentTo(new FoundElement(
            Ref: "w1e1", Name: "Save", ControlType: "Button", AutomationId: "SaveBtn", ClassName: "Button",
            Bounds: new BoundsDto(100, 40, 80, 24), ClickPoint: new ScreenPoint(120, 50), IsEnabled: true, IsOffscreen: false,
            Patterns: ["Invoke"]));
    }

    [Fact]
    public async Task Find_WithoutAClickablePoint_UsesTheCentreOfTheBounds()
    {
        BuildEditor();

        var result = await Find(Query(automationId: "saveasbtn"));

        result.Elements!.Single().ClickPoint.Should().Be(new ScreenPoint(240, 52));
    }

    [Fact]
    public async Task Find_ByContainsAndRegex_TestsTheNamesOfTheOtherCriteriasMatches()
    {
        BuildEditor();

        (await Find(Query(name: "SAVE", nameMatch: ElementNameMatch.Contains))).Elements!.Select(e => e.Name).Should().Equal("Save", "Save As");
        (await Find(Query(name: "^save$|^open$", nameMatch: ElementNameMatch.Regex, enabledOnly: true))).Elements!
            .Select(e => e.Name).Should().Equal("Save");
    }

    [Fact]
    public async Task Find_ByControlTypes_MatchesAnyOfThem()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["checkbox", "Edit"]));

        result.Elements!.Select(e => e.Name).Should().Equal("Wrap", "Search", "Password");
    }

    [Fact]
    public async Task Find_WithAnUnknownControlType_FailsWithInvalidControlType()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["Buton"]));

        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidControlType);
        result.Error.Should().Contain("Valid names");
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Find_WhenTheEngineCannotSearchForTheControlType_TestsItOnTheServer()
    {
        // Arrange - the managed client throws for AppBar (it has no id for it); every part is searched by the provider here
        var bars = new FakeUiaNode("Pane", "Shell").With(Enumerable.Range(1, 60)
            .Select(i => new FakeUiaNode(i == 30 ? "AppBar" : "Button", $"Item {i}")).ToArray());
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "Shell window").With(bars));
        _tree.OnFindAll = (_, condition) =>
        {
            if (Compares(condition, UiaProperty.ControlType))
            {
                throw new NotSupportedException("The managed client cannot search for AppBar elements.");
            }
        };

        // Act
        var result = await Find(Query(controlTypes: ["AppBar"]));

        // Assert
        result.Elements!.Select(e => e.Name).Should().Equal("Item 30");
    }

    [Fact]
    public async Task Find_VisibleOnly_LeavesOutOffscreenElements_AndFalseReturnsThem()
    {
        BuildEditor();

        (await Find(Query(name: "Gone"))).MatchCount.Should().Be(0);
        (await Find(Query(name: "Gone", visibleOnly: false))).Elements!.Single().Should().Match<FoundElement>(e =>
            e.IsOffscreen && e.ClickPoint == null);
    }

    [Fact]
    public async Task Find_BeyondMaxResults_CountsEveryMatch_AndSaysItIsTruncated()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["Button"], maxResults: 2));

        result.MatchCount.Should().Be(3);
        result.Elements.Should().HaveCount(2);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.MaxResults);
    }

    [Fact]
    public async Task Find_WithNoMatch_SucceedsWithAnEmptyList()
    {
        BuildEditor();

        var result = await Find(Query(name: "Print"));

        result.Success.Should().BeTrue();
        result.MatchCount.Should().Be(0);
        result.Elements.Should().BeEmpty();
    }

    [Fact]
    public async Task Find_WithARegexThatRunsTooLong_ThrowsTheRegexTimeout()
    {
        // The tool turns it into InvalidArguments
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(new FakeUiaNode("Text", new string('a', 40) + "!")));

        await FluentActions.Awaiting(() => Find(Query(name: "^(a+)+$", nameMatch: ElementNameMatch.Regex)))
            .Should().ThrowAsync<RegexMatchTimeoutException>();
    }

    #endregion

    private static bool Compares(UiaCondition condition, UiaProperty property) => condition switch
    {
        UiaPropertyCondition p => p.Property == property,
        UiaAndCondition and => and.Conditions.Any(c => Compares(c, property)),
        UiaOrCondition or => or.Conditions.Any(c => Compares(c, property)),
        UiaNotCondition not => Compares(not.Condition, property),
        _ => false
    };

    #region Refs

    [Fact]
    public async Task Refs_AreTheSameForTheSameElement_InFindAndInTheTreeWalk()
    {
        // Arrange
        var (_, save, _) = BuildEditor();
        var walk = new UIElementService(_tree, _worker, _windows.Object, _registry);

        // Act
        var found = await Find(Query(name: "Save"));
        var tree = await walk.GetUIElementsAsync(Handle);
        var again = await Find(Query(automationId: "SaveBtn"));

        // Assert
        var saveRef = found.Elements!.Single().Ref;
        Flatten(tree.Elements!).Single(e => e.Name == "Save").Ref.Should().Be(saveRef);
        again.Elements!.Single().Ref.Should().Be(saveRef);
        (await _registry.ResolveAsync(saveRef!, TimeSpan.FromSeconds(5), CancellationToken.None)).Element!.Node.Should().BeSameAs(save);

        static IEnumerable<UIElementInfo> Flatten(IEnumerable<UIElementInfo> elements) =>
            elements.SelectMany(e => Flatten(e.Children ?? []).Prepend(e));
    }

    [Fact]
    public async Task Find_WithinRef_SearchesOnlyBelowThatElement()
    {
        // Arrange
        var (_, _, options) = BuildEditor();
        var optionsRef = _registry.Register(Handle, _tree.ReadElement(options), new ElementOrigin(1));

        // Act
        var result = await Find(Query(controlTypes: ["Edit", "Button"], withinRef: optionsRef));

        // Assert
        result.WithinRef.Should().Be(optionsRef);
        result.Elements!.Select(e => e.Name).Should().Equal("Search", "Password");
    }

    [Fact]
    public async Task Find_WithinAStaleRef_FailsWithElementStale()
    {
        // Arrange
        var (_, save, _) = BuildEditor();
        var saveRef = _registry.Register(Handle, _tree.ReadElement(save), new ElementOrigin(1));
        save.Vanish();
        _tree.FromHandle(Handle).Should().NotBeNull();

        // Act - no element has its automation id or name any more
        var result = await Find(Query(controlTypes: ["Button"], withinRef: saveRef));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        result.Error.Should().StartWith("withinRef:");
    }

    #endregion

    #region Detail

    [Fact]
    public async Task Find_DetailStandard_ReadsValueStateAndFocus_ButNeverAPasswordsValue()
    {
        BuildEditor();

        var result = await Find(Query(controlTypes: ["Edit", "CheckBox"], detail: ElementDetail.Standard));

        var byName = result.Elements!.ToDictionary(e => e.Name!);
        byName["Wrap"].ToggleState.Should().Be(UiaToggleState.On);
        byName["Search"].Should().Match<FoundElement>(e =>
            e.Value!.Length == 200 && e.ValueTruncated == true && e.HasKeyboardFocus == true && e.IsKeyboardFocusable == true);
        byName["Password"].Should().Match<FoundElement>(e => e.IsPassword == true && e.Value == null);
        _tree.Reads.Should().NotContain("Value:Password", "a password field's value is never read, not even to mask it");
        byName["Search"].FrameworkId.Should().BeNull("framework and process are detail full");
    }

    [Fact]
    public async Task Find_DetailFull_AddsFrameworkProcessHelpTextAndKeys()
    {
        BuildEditor();

        var result = await Find(Query(automationId: "SearchBox", detail: ElementDetail.Full));

        result.Elements!.Single().Should().Match<FoundElement>(e =>
            e.FrameworkId == "WPF" && e.ProcessId == 300 && e.HelpText == "Type to search" && e.AccessKey == "Alt+S" &&
            e.LocalizedControlType == "edit" && e.NativeWindowHandle == null);
    }

    [Fact]
    public async Task Find_DetailMinimal_MakesNoReadButTheClickPoint()
    {
        BuildEditor();

        await Find(Query(name: "Save"));

        _tree.Reads.Where(r => !r.StartsWith("Read:", StringComparison.Ordinal))
            .Should().Equal("Clickable:Save");
    }

    #endregion

    #region Failures

    [Fact]
    public async Task Find_InAHungWindow_ReadsNothing()
    {
        BuildEditor();
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);

        var result = await Find(Query(name: "Save"));

        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
        _tree.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task Find_WithAZeroHandle_OrAWindowUiAutomationCannotRead_Fails()
    {
        (await CreateSut().FindAsync(Query() with { WindowHandle = 0, Name = "x" })).ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
        (await Find(Query(name: "Save"))).ErrorCode.Should().Be(UIElementErrorCode.WindowNotFound);
        _tree.Available = false;
        (await Find(Query(name: "Save"))).ErrorCode.Should().Be(UIElementErrorCode.AutomationUnavailable);
    }

    [Fact]
    public async Task Find_WhenTheTimeoutPassesBeforeAnyMatch_FailsWithOperationTimedOut()
    {
        // Arrange - the deadline passes during the first part's search
        var lists = Enumerable.Range(1, 3).Select(i => new FakeUiaNode("List", $"L{i}").With(
            Enumerable.Range(1, 60).Select(j => new FakeUiaNode("Text", $"T{j}")).ToArray())).ToArray();
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(lists));
        var sut = CreateSut();
        using var cancel = new CancellationTokenSource();
        lists[0].OnFindAll = _ => Thread.Sleep(1200);

        // Act
        var result = await sut.FindAsync(Query(controlTypes: ["Button"]) with { Timeout = TimeSpan.FromMilliseconds(1000) });

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.OperationTimedOut);
        result.Error.Should().Contain("before any element");
    }

    [Fact]
    public async Task Find_WhenTheTimeoutPassesAfterSomeMatches_ReturnsThemTruncated()
    {
        // Arrange
        var lists = Enumerable.Range(1, 3).Select(i => new FakeUiaNode("List", $"L{i}").With(
            Enumerable.Range(1, 60).Select(j => new FakeUiaNode("Button", $"B{i}.{j}")).ToArray())).ToArray();
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(lists));
        lists[1].OnFindAll = _ => Thread.Sleep(1200);

        // Act
        var result = await CreateSut().FindAsync(Query(controlTypes: ["Button"]) with { Timeout = TimeSpan.FromMilliseconds(1000) });

        // Assert
        result.Success.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
        result.MatchCount.Should().Be(120);
        result.Elements.Should().HaveCount(20);
        result.Elements!.Should().OnlyContain(e => e.Ref != null);
    }

    [Fact]
    public async Task Find_WhenTheWorkerAbandonsTheSearch_ReturnsWhatItFound_WithoutReadingFurther()
    {
        // Arrange - the search is stuck in the second list's call (a provider that does not answer) when the worker gives up
        var lists = Enumerable.Range(1, 2).Select(i => new FakeUiaNode("List", $"L{i}").With(
            Enumerable.Range(1, 60).Select(j => new FakeUiaNode("Button", $"B{i}.{j}")).ToArray())).ToArray();
        _tree.WithWindow(Handle, new FakeUiaNode("Window", "W").With(lists));
        using var stuck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        lists[1].OnFindAll = _ =>
        {
            stuck.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var sut = new ElementSearchService(_tree, new AbandoningWorker(stuck), _registry, _windows.Object);

        try
        {
            // Act
            var result = await sut.FindAsync(Query(controlTypes: ["Button"]));

            // Assert
            result.Success.Should().BeTrue();
            result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
            result.MatchCount.Should().Be(60);
            result.Elements!.Should().HaveCount(20).And.OnlyContain(e => e.Ref != null && e.ClickPoint != null);
            _tree.Reads.Should().NotContain(r => r.StartsWith("Clickable:", StringComparison.Ordinal),
                "nothing is read on the caller's thread");
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// Runs the read on a thread of its own and gives up once <paramref name="stuck"/> is set, as the real worker gives up on
    /// a read stuck past its deadline: the read is abandoned, and the caller gets a TimeoutException.
    /// </summary>
    private sealed class AbandoningWorker(ManualResetEventSlim stuck) : IUiaWorker
    {
        public async Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
        {
            var reading = Task.Run(() => read(cancellationToken));
            var gaveUp = Task.Run(() => stuck.Wait(TimeSpan.FromSeconds(10)));
            if (await Task.WhenAny(reading, gaveUp) == reading)
            {
                return await reading;
            }

            throw new TimeoutException("UI Automation did not answer in time.");
        }
    }

    #endregion
}
