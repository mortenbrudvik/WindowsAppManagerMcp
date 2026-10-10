namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// get_ui_elements against the fixture: the server's UI Automation walk must see the fixture's
/// AutomationIds, control types and positions. No-input tier.
/// </summary>
public class FixtureUiElementsTests
{
    /// <summary>UI Automation bounds may be off by rounding (the server truncates to whole pixels).</summary>
    private const int BoundsTolerance = 2;

    private static readonly IReadOnlyDictionary<string, string> MainWindowControlTypes = new Dictionary<string, string>
    {
        [FixtureContract.Ids.EditBox] = "Edit",
        [FixtureContract.Ids.CheckBox] = "CheckBox",
        [FixtureContract.Ids.ComboBox] = "ComboBox",
        [FixtureContract.Ids.OpenModalButton] = "Button",
        [FixtureContract.Ids.OpenDocumentButton] = "Button",
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

    [DesktopNoInputFact]
    public void GetUIElements_ReportsTheFixtureAutomationIdsAndControlTypes()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

        // Act
        var result = e2e.Server.UiElements.GetUIElements(e2e.Fixture.MainWindowHandle);

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

    [DesktopNoInputFact]
    public void GetUIElements_BoundsMatchWhereTheFixtureDrawsItsElements()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var ready = e2e.Fixture.Ready;

        // Act
        var result = e2e.Server.UiElements.GetUIElements(e2e.Fixture.MainWindowHandle);

        // Assert - both sides are DPI aware, so both report physical screen pixels
        result.Success.Should().BeTrue(result.Error);
        var byId = UiaReads.Flatten(result.Elements)
            .Where(e => e.AutomationId != null)
            .GroupBy(e => e.AutomationId!)
            .ToDictionary(g => g.Key, g => g.First().Bounds);
        foreach (var (id, expected) in ready.Elements)
        {
            byId.Should().ContainKey(id);
            var actual = byId[id];
            _output.WriteLine($"{id}: UI Automation ({actual.X}, {actual.Y}, {actual.Width}x{actual.Height}), fixture {expected}");
            actual.X.Should().BeCloseTo(expected.X, BoundsTolerance, "x of '{0}'", id);
            actual.Y.Should().BeCloseTo(expected.Y, BoundsTolerance, "y of '{0}'", id);
            actual.Width.Should().BeCloseTo(expected.Width, BoundsTolerance, "width of '{0}'", id);
            actual.Height.Should().BeCloseTo(expected.Height, BoundsTolerance, "height of '{0}'", id);
        }
    }

    [DesktopNoInputFact]
    public void GetUIElements_VirtualizedList_ExposesOnlyTheRealizedItems()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

        // Act
        var result = e2e.Server.UiElements.GetUIElements(e2e.Fixture.MainWindowHandle);

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
