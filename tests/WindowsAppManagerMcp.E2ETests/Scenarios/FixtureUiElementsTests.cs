namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// get_ui_elements against the fixture: the server's UI Automation walk must see the fixture's
/// AutomationIds, control types and positions. No-input tier.
/// </summary>
/// <remarks>
/// Every test runs with both UI Automation engines (roadmap M3.2), whatever WAM_UIA_ENGINE says, so one gated run reads
/// the fixture with each: these are among the scenarios of the engine release gate (TESTING.md).
/// </remarks>
public class FixtureUiElementsTests
{
    /// <summary>UI Automation bounds may be off by rounding (the server truncates to whole pixels).</summary>
    private const int BoundsTolerance = 2;

    /// <summary>The control type of each element of the main window that the ready event reports.</summary>
    internal static readonly IReadOnlyDictionary<string, string> MainWindowControlTypes = new Dictionary<string, string>
    {
        [FixtureContract.Ids.EditBox] = "Edit",
        [FixtureContract.Ids.PasswordBox] = "Edit",
        [FixtureContract.Ids.CheckBox] = "CheckBox",
        [FixtureContract.Ids.ComboBox] = "ComboBox",
        [FixtureContract.Ids.OpenModalButton] = "Button",
        [FixtureContract.Ids.OpenDocumentButton] = "Button",
        [FixtureContract.Ids.SaveAsButton] = "Button",
        [FixtureContract.Ids.ContextMenuTarget] = "Text",
        [FixtureContract.Ids.SwatchRed] = "Text",
        [FixtureContract.Ids.SwatchGreen] = "Text",
        [FixtureContract.Ids.SwatchBlue] = "Text",
        [FixtureContract.Ids.StatusText] = "Text",
        [FixtureContract.Ids.VirtualList] = "List"
    };

    private readonly ITestOutputHelper _output;

    public FixtureUiElementsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public async Task GetUIElements_ReportsTheFixtureAutomationIdsAndControlTypes(UiaEngine engine)
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        e2e.Server.Get<IUiaTree>().Engine.Should().Be(engine);

        // Act
        var result = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.WindowTitle.Should().Be(FixtureContract.MainTitle(e2e.Fixture.Instance));
        var byId = UiaReads.Flatten(result.Elements)
            .Where(e => e.AutomationId != null)
            .GroupBy(e => e.AutomationId!)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var (id, controlType) in MainWindowControlTypes)
        {
            byId.Should().ContainKey(id);
            byId[id].ControlType.Should().Be(controlType, "element '{0}'", id);
        }
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public async Task GetUIElements_NamesTheWpfFramework_AndWarmsNothingUpInANativeWindow(UiaEngine engine)
    {
        // Arrange - roadmap M3.3: the fixture is a WPF window without web content
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        e2e.Server.Get<IUiaTree>().Engine.Should().Be(engine);
        var fixtureIds = e2e.Fixture.Ready.Elements.Keys.ToHashSet();

        // Act
        var result = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);

        // Assert - the fixture's own elements inherit the window's framework, so they name none of their own. Not every
        // element is the fixture's: UI Automation adds the window's title bar and its buttons (the non-client area, from
        // the window's Win32 proxy), which can name a framework of their own (Win32)
        result.Success.Should().BeTrue(result.Error);
        result.FrameworkId.Should().Be("WPF");
        result.AccessibilityWarmup.Should().BeNull("a native window has no web content to wait for");
        var elements = UiaReads.Flatten(result.Elements).ToList();
        foreach (var other in elements.Where(e => e.FrameworkId != null))
        {
            _output.WriteLine($"{other.ControlType} '{other.Name}' aid={other.AutomationId} names its framework: {other.FrameworkId}");
        }

        var fixtureElements = elements.Where(e => e.AutomationId != null && fixtureIds.Contains(e.AutomationId)).ToList();
        fixtureElements.Select(e => e.AutomationId).Distinct().Should().BeEquivalentTo(fixtureIds);
        fixtureElements.Should().OnlyContain(e => e.FrameworkId == null);
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public async Task GetUIElements_BoundsMatchWhereTheFixtureDrawsItsElements(UiaEngine engine)
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        e2e.Server.Get<IUiaTree>().Engine.Should().Be(engine);
        var ready = e2e.Fixture.Ready;

        // Act
        var result = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);

        // Assert - both sides are DPI aware, so both report physical screen pixels
        result.Success.Should().BeTrue(result.Error);
        var byId = UiaReads.Flatten(result.Elements)
            .Where(e => e.AutomationId != null)
            .GroupBy(e => e.AutomationId!)
            .ToDictionary(g => g.Key, g => g.First().Bounds);
        foreach (var (id, expected) in ready.Elements)
        {
            byId.Should().ContainKey(id);
            var actual = byId[id]!;
            actual.Should().NotBeNull("'{0}' is on screen, so UI Automation reports its bounds", id);
            _output.WriteLine($"{id}: UI Automation ({actual.X}, {actual.Y}, {actual.Width}x{actual.Height}), fixture {expected}");
            actual.X.Should().BeCloseTo(expected.X, BoundsTolerance, "x of '{0}'", id);
            actual.Y.Should().BeCloseTo(expected.Y, BoundsTolerance, "y of '{0}'", id);
            actual.Width.Should().BeCloseTo(expected.Width, BoundsTolerance, "width of '{0}'", id);
            actual.Height.Should().BeCloseTo(expected.Height, BoundsTolerance, "height of '{0}'", id);
        }
    }

    [DesktopNoInputTheory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public async Task GetUIElements_VirtualizedList_ExposesOnlyTheRealizedItems(UiaEngine engine)
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, uiaEngine: engine);
        e2e.Server.Get<IUiaTree>().Engine.Should().Be(engine);

        // Act
        var result = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);

        // Assert - WPF realizes the visible items and a small cache, not all 10,000; finding the rest needs
        // ItemContainerPattern (roadmap M3), which this list is here to test
        result.Success.Should().BeTrue(result.Error);
        var list = UiaReads.Flatten(result.Elements).Single(e => e.AutomationId == FixtureContract.Ids.VirtualList);
        var items = (list.Children ?? []).Where(c => c.ControlType == "ListItem").ToList();
        _output.WriteLine($"Realized list items: {items.Count}; total elements: {result.TotalElementCount}.");
        items.Should().NotBeEmpty();
        items.Count.Should().BeLessThan(FixtureContract.VirtualListItemCount / 10);
        items[0].Name.Should().Be(FixtureContract.VirtualListItem(0));
    }
}
