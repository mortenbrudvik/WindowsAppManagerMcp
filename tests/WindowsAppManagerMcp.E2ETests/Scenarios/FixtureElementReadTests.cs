namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// read_element_text and read_table (roadmap M3.9) against the fixture: the edit box read by find_ui_elements' criteria
/// (through its Text or Value pattern, never as its label), and the virtualized list read as a one-column table of the
/// items it has realized. No-input tier: it only reads.
/// </summary>
public class FixtureElementReadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public FixtureElementReadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public async Task ReadElementText_ByCriteria_ReadsTheEditBox_ThroughAPattern()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var reads = e2e.Server.Get<IElementReadService>();
        var query = new ElementQuery(e2e.Fixture.MainWindowHandle, AutomationId: FixtureContract.Ids.EditBox, MaxResults: 5, Timeout: Timeout);

        // Act
        var result = await reads.ReadTextAsync(new ElementTextRequest(null, query, 0, 20_000, Timeout));

        // Assert - a WPF TextBox has both patterns; the Text pattern comes first unless the box is empty
        result.Success.Should().BeTrue(result.Error);
        _output.WriteLine($"{result.Ref} {result.ControlType} source={result.Source} text='{result.Text}' total={result.TotalLength}");
        result.ControlType.Should().Be("Edit");
        result.Source.Should().BeOneOf(ElementTextSource.Text, ElementTextSource.Value);
        result.Text.Should().NotBe("Edit box", "an edit box is never read as its label");
        result.Ref.Should().NotBeNull();
    }

    [DesktopNoInputFact]
    public async Task ReadTable_OfTheVirtualList_GivesTheRealizedItemsAsRows()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var found = await e2e.Server.Get<IElementSearchService>().FindAsync(
            new ElementQuery(e2e.Fixture.MainWindowHandle, AutomationId: FixtureContract.Ids.VirtualList, Timeout: Timeout));
        found.Success.Should().BeTrue(found.Error);
        var list = found.Elements.Should().ContainSingle().Which;

        // Act
        var table = await e2e.Server.Get<IElementReadService>().ReadTableAsync(new ElementTableRequest(list.Ref!, 0, 5, 20, Timeout));

        // Assert - a WPF ListBox has no Grid pattern: its ListItem children are the rows, each item one cell
        table.Success.Should().BeTrue(table.Error);
        _output.WriteLine($"{table.Source} rows={table.Rows!.Count} total={table.TotalRows} columns={table.TotalColumns} {table.TruncationReason}");
        table.Rows.Should().NotBeEmpty();
        table.Rows![0][0].Should().Be(FixtureContract.VirtualListItem(0));
        table.TotalRows.Should().BeLessThan(FixtureContract.VirtualListItemCount, "only the realized items are in the tree");
    }
}
