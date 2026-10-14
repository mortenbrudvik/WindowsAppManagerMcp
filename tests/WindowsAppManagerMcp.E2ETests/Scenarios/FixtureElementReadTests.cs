namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// read_element_text and read_table (roadmap M3.9) against the fixture: the edit box read by find_ui_elements' criteria
/// (through its Text or Value pattern, never as its label), the virtualized list read as a one-column table of the
/// items it has realized, and the table window's WPF DataGrid read through its Grid pattern and its realized rows, with its
/// column and row headers (roadmap M3.F4, M3.F15). No-input tier: it only reads.
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

    /// <summary>The fixture's table window (opened at start) and its data grid's ref, found by AutomationId.</summary>
    private static async Task<(nint Window, string GridRef)> FindDataGridAsync(E2ESession e2e)
    {
        var window = e2e.Fixture.WaitForEvent(FixtureContract.Events.TableOpened).GetHandle("hwnd");
        var found = await e2e.Server.Get<IElementSearchService>().FindAsync(
            new ElementQuery(window, AutomationId: FixtureContract.Ids.DataGrid, Timeout: Timeout));
        found.Success.Should().BeTrue(found.Error);
        var grid = found.Elements.Should().ContainSingle().Which;
        grid.ControlType.Should().Be("DataGrid");
        return (window, grid.Ref!);
    }

    private void Log(ElementTableResult table)
    {
        _output.WriteLine($"{table.Source} rows={table.Rows?.Count} total={table.TotalRows} columns={table.TotalColumns} " +
                          $"truncated={table.TruncationReason} next={table.NextStartRow} warning={table.Warning} error={table.ErrorCode}: {table.Error}");
        _output.WriteLine($"  headers: {string.Join(" | ", table.Headers ?? [])}");
        for (var i = 0; i < (table.Rows?.Count ?? 0); i++)
        {
            _output.WriteLine($"  [{table.RowHeaders?[i]}] {string.Join(" | ", table.Rows![i])}");
        }
    }

    [DesktopNoInputFact]
    public async Task ReadTable_OfTheDataGrid_GivesItsHeaders_AndItsFirstRows_WithTheirRowHeaders()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenTable = true });
        var (_, gridRef) = await FindDataGridAsync(e2e);

        // Act
        var table = await e2e.Server.Get<IElementReadService>().ReadTableAsync(new ElementTableRequest(gridRef, 0, 5, 10, Timeout));
        Log(table);

        // Assert - the Grid pattern gives the counts and the Table pattern the column headers; a WPF grid's GetItem would
        // scroll, so its cells come from its realized rows, each placed by its first cell's row and starting with its row header
        table.Success.Should().BeTrue(table.Error);
        table.Source.Should().Be(ElementTableSource.Grid);
        table.Headers.Should().Equal(FixtureContract.TableColumns);
        table.TotalRows.Should().Be(FixtureContract.TableRowCount, "the Grid pattern counts every row, realized or not");
        table.TotalColumns.Should().Be(FixtureContract.TableColumns.Count);
        table.Rows.Should().HaveCount(5);
        for (var row = 0; row < 5; row++)
        {
            table.Rows![row].Should().Equal(FixtureContract.TableRow(row), "row {0}", row);
        }

        table.RowHeaders.Should().Equal(Enumerable.Range(0, 5).Select(FixtureContract.TableRowHeader),
            "a WPF DataGridRowHeader is the HeaderItem that starts its row (roadmap M3.F15)");
        table.TruncationReason.Should().Be(ElementTableTruncation.MaxRows);
        table.NextStartRow.Should().Be(5);
    }

    [DesktopNoInputFact]
    public async Task ReadTable_OfTheDataGrid_PastItsRealizedRows_EndsThePage_AndSaysToScroll()
    {
        // Arrange - the grid realizes only the rows around what it shows, far fewer than 900
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenTable = true });
        var (_, gridRef) = await FindDataGridAsync(e2e);

        // Act
        var table = await e2e.Server.Get<IElementReadService>().ReadTableAsync(new ElementTableRequest(gridRef, 900, 5, 10, Timeout));
        Log(table);

        // Assert - read_table never scrolls: the page ends at the first row that is not realized, and says how to read on
        table.Success.Should().BeTrue(table.Error);
        table.Source.Should().Be(ElementTableSource.Grid);
        table.Rows.Should().BeEmpty();
        table.TotalRows.Should().Be(FixtureContract.TableRowCount);
        table.TruncationReason.Should().Be(ElementTableTruncation.NotRealized);
        table.NextStartRow.Should().Be(900);
        table.Warning.Should().Contain("element_action");
    }
}
