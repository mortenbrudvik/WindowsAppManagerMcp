namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// read_table's service (roadmap M3.9) over an in-memory UI Automation tree: grids with and without the Table pattern, the
/// row walk, paging, the limits and the failures. No real window is read.
/// </summary>
public class ElementReadServiceTableTests
{
    private static readonly nint Handle = 0x7400;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;
    private readonly FakeUiaNode _window = new("Window", "Task Manager");

    public ElementReadServiceTableTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        _tree.WithWindow(Handle, _window);
    }

    private ElementReadService CreateSut() =>
        new(_tree, _worker, _registry, new ElementSearchService(_tree, _worker, _registry, _windows.Object), _windows.Object);

    private string Add(FakeUiaNode element)
    {
        _window.With(element);
        return _registry.Register(Handle, _tree.ReadElement(element), new ElementOrigin(_registry.NextGeneration()))!;
    }

    private Task<ElementTableResult> Read(string elementRef, int startRow = 0, int maxRows = 50, int maxColumns = 20, TimeSpan? timeout = null) =>
        CreateSut().ReadTableAsync(new ElementTableRequest(elementRef, startRow, maxRows, maxColumns, timeout ?? Timeout));

    private static FakeUiaNode Cell(string? name) => new("DataItem", name);

    /// <summary>A grid of <paramref name="rows"/> x <paramref name="columns"/> cells named "r{row}c{column}".</summary>
    private static List<IReadOnlyList<FakeUiaNode?>> Cells(int rows, int columns) =>
        Enumerable.Range(0, rows)
            .Select(r => (IReadOnlyList<FakeUiaNode?>)Enumerable.Range(0, columns).Select(c => (FakeUiaNode?)Cell($"r{r}c{c}")).ToList())
            .ToList();

    private static FakeUiaNode Grid(IReadOnlyList<IReadOnlyList<FakeUiaNode?>> cells, params string[] headers)
    {
        var grid = new FakeUiaNode("DataGrid", "Processes") { OtherPatterns = UiaPatterns.Grid | (headers.Length > 0 ? UiaPatterns.Table : 0) }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(cells));
        if (headers.Length > 0)
        {
            grid.WithPattern<IUiaTablePattern>(new FakeTablePattern(headers.Select(h => new FakeUiaNode("HeaderItem", h)).ToArray()));
        }

        return grid;
    }

    #region Grid

    [Fact]
    public async Task ReadTable_OfAGridWithATablePattern_ReadsTheHeadersAndEveryCell()
    {
        // Arrange
        var elementRef = Add(Grid(Cells(3, 2), "Name", "CPU"));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeTrue();
        result.Ref.Should().Be(elementRef);
        result.Source.Should().Be(ElementTableSource.Grid);
        result.Headers.Should().Equal("Name", "CPU");
        result.Rows.Should().HaveCount(3);
        result.Rows![0].Should().Equal("r0c0", "r0c1");
        result.Rows[2].Should().Equal("r2c0", "r2c1");
        result.TotalRows.Should().Be(3);
        result.TotalColumns.Should().Be(2);
        result.Truncated.Should().BeFalse();
        result.TruncationReason.Should().BeNull();
        result.NextStartRow.Should().BeNull();
    }

    [Fact]
    public async Task ReadTable_ReturnsThePageAskedFor_AndWhereTheNextStarts()
    {
        // Arrange
        var grid = new FakeGridPattern(Cells(10, 2));
        var elementRef = Add(new FakeUiaNode("DataGrid", "Rows") { OtherPatterns = UiaPatterns.Grid }.WithPattern<IUiaGridPattern>(grid));

        // Act
        var result = await Read(elementRef, startRow: 4, maxRows: 3);

        // Assert - only the page's cells are asked for
        result.StartRow.Should().Be(4);
        result.Rows!.Select(r => r[0]).Should().Equal("r4c0", "r5c0", "r6c0");
        result.TotalRows.Should().Be(10);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementTableTruncation.MaxRows);
        result.NextStartRow.Should().Be(7);
        grid.Items.Select(i => i.Row).Distinct().Should().Equal(4, 5, 6);
    }

    [Fact]
    public async Task ReadTable_PastTheLastRow_ReturnsNoRows()
    {
        var elementRef = Add(Grid(Cells(2, 2), "A", "B"));

        var result = await Read(elementRef, startRow: 5);

        result.Success.Should().BeTrue();
        result.Rows.Should().BeEmpty();
        result.TotalRows.Should().Be(2);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadTable_WithMoreColumnsThanMaxColumns_ReturnsTheFirstOnes()
    {
        var elementRef = Add(Grid(Cells(2, 5), "A", "B", "C", "D", "E"));

        var result = await Read(elementRef, maxColumns: 2);

        result.Headers.Should().Equal("A", "B");
        result.Rows!.Should().OnlyContain(row => row.Count == 2);
        result.TotalColumns.Should().Be(5);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementTableTruncation.MaxColumns);
        result.NextStartRow.Should().BeNull();
    }

    [Fact]
    public async Task ReadTable_OfAGridWithoutATablePattern_TakesTheHeadersFromItsHeaderRow()
    {
        // Arrange - a list view: its first child is a Header with a HeaderItem per column
        var list = new FakeUiaNode("List", "Items View") { OtherPatterns = UiaPatterns.Grid }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(Cells(2, 2)))
            .With(new FakeUiaNode("Header").With(new FakeUiaNode("HeaderItem", "Name"), new FakeUiaNode("HeaderItem", "Size")));
        var elementRef = Add(list);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Headers.Should().Equal("Name", "Size");
        result.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadTable_ACellWithoutAName_GivesItsValue_ThenItsChildrensText_AndAMissingCellIsNull()
    {
        // Arrange
        var cells = new List<IReadOnlyList<FakeUiaNode?>>
        {
            new List<FakeUiaNode?>
            {
                new FakeUiaNode("Edit") { ActionPatterns = ["Value"], Value = "42" },
                new FakeUiaNode("DataItem").With(new FakeUiaNode("Text", "Running")),
                null
            }
        };
        var elementRef = Add(Grid(cells, "Count", "Status", "Note"));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows![0].Should().Equal("42", "Running", null);
    }

    [Fact]
    public async Task ReadTable_NeverReadsTheValueOfAPasswordCell()
    {
        var cells = new List<IReadOnlyList<FakeUiaNode?>>
        {
            new List<FakeUiaNode?> { Cell("svc"), new FakeUiaNode("Edit") { ActionPatterns = ["Value"], IsPassword = true, Value = "secret" } }
        };
        var elementRef = Add(Grid(cells, "User", "Password"));

        var result = await Read(elementRef);

        result.Rows![0].Should().Equal("svc", null);
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    [Fact]
    public async Task ReadTable_CutsLongCells_AndSaysSo()
    {
        var cells = new List<IReadOnlyList<FakeUiaNode?>> { new List<FakeUiaNode?> { Cell(new string('x', 2000)) } };
        var elementRef = Add(Grid(cells, "Body"));

        var result = await Read(elementRef);

        result.Rows![0][0]!.Length.Should().Be(ElementReadService.MaxCellLength);
        result.CellsTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task ReadTable_StopsAtTheSizeLimit_WithTheRowsReadSoFar()
    {
        // Arrange - 50 rows of 10 cells of 400 characters: 200,000 characters, far over one result's limit
        var cells = Enumerable.Range(0, 50)
            .Select(_ => (IReadOnlyList<FakeUiaNode?>)Enumerable.Range(0, 10).Select(_ => (FakeUiaNode?)Cell(new string('y', 400))).ToList())
            .ToList();
        var elementRef = Add(Grid(cells));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementTableTruncation.Size);
        result.Rows!.Sum(r => r.Sum(c => c?.Length ?? 0)).Should().BeLessThanOrEqualTo(ElementReadService.MaxTableTextLength);
        result.NextStartRow.Should().Be(result.Rows!.Count);
    }

    [Fact]
    public async Task ReadTable_WhenTheTimeoutPasses_ReturnsTheRowsReadSoFar()
    {
        // Arrange - the third row's first cell takes longer than the whole timeout
        var cells = Cells(5, 2);
        ((FakeUiaNode)cells[2][0]!).OnRead = () => Thread.Sleep(400);
        var elementRef = Add(Grid(cells, "A", "B"));

        // Act
        var result = await Read(elementRef, timeout: TimeSpan.FromMilliseconds(200));

        // Assert - only whole rows are returned
        result.Success.Should().BeTrue();
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementTableTruncation.Timeout);
        result.Rows!.Count.Should().BeLessThan(5);
        result.NextStartRow.Should().Be(result.Rows.Count);
    }

    #endregion

    #region Rows walk

    [Fact]
    public async Task ReadTable_OfAListWithoutAGrid_WalksItsRows_AndTheirCells()
    {
        // Arrange - a WPF data grid that exposes rows (DataItem) with cells, and a header row, but no Grid pattern
        var table = new FakeUiaNode("Table", "Messages").With(
            new FakeUiaNode("Header").With(new FakeUiaNode("HeaderItem", "From"), new FakeUiaNode("HeaderItem", "Subject")),
            new FakeUiaNode("DataItem", "Row 1").With(new FakeUiaNode("Text", "Ann"), new FakeUiaNode("Text", "Budget")),
            new FakeUiaNode("DataItem", "Row 2").With(new FakeUiaNode("Text", "Bo"), new FakeUiaNode("Text", "Lunch")),
            new FakeUiaNode("ScrollBar", "Vertical"));
        var elementRef = Add(table);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Source.Should().Be(ElementTableSource.Rows);
        result.Headers.Should().Equal("From", "Subject");
        result.Rows.Should().HaveCount(2);
        result.Rows![0].Should().Equal("Ann", "Budget");
        result.Rows[1].Should().Equal("Bo", "Lunch");
        result.TotalRows.Should().Be(2);
        result.TotalColumns.Should().Be(2);
        _tree.CachedFetches.Should().Contain("Messages", "the rows are read in one cached call");
    }

    [Fact]
    public async Task ReadTable_OfASimpleList_GivesOneCellPerItem_ItsName()
    {
        var list = new FakeUiaNode("List", "Recent").With(
            new FakeUiaNode("ListItem", "report.docx"), new FakeUiaNode("ListItem", "budget.xlsx"), new FakeUiaNode("ListItem", "notes.txt"));
        var elementRef = Add(list);

        var result = await Read(elementRef, startRow: 1, maxRows: 1);

        result.Headers.Should().BeNull();
        result.Rows.Should().ContainSingle().Which.Should().Equal("budget.xlsx");
        result.TotalRows.Should().Be(3);
        result.TruncationReason.Should().Be(ElementTableTruncation.MaxRows);
        result.NextStartRow.Should().Be(2);
    }

    [Fact]
    public async Task ReadTable_ARowOfHeaderItems_IsTheHeaderRow_NotARow()
    {
        // Arrange - web tables: the first row's cells are column headers
        var table = new FakeUiaNode("Table", "Prices").With(
            new FakeUiaNode("DataItem").With(new FakeUiaNode("HeaderItem", "Item"), new FakeUiaNode("HeaderItem", "Price")),
            new FakeUiaNode("DataItem").With(new FakeUiaNode("DataItem", "Tea"), new FakeUiaNode("DataItem", "3")));
        var elementRef = Add(table);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Headers.Should().Equal("Item", "Price");
        result.Rows.Should().ContainSingle().Which.Should().Equal("Tea", "3");
    }

    [Fact]
    public async Task ReadTable_OfAnEmptyList_ReturnsNoRows()
    {
        var elementRef = Add(new FakeUiaNode("List", "Empty"));

        var result = await Read(elementRef);

        result.Success.Should().BeTrue();
        result.Rows.Should().BeEmpty();
        result.TotalRows.Should().Be(0);
    }

    [Fact]
    public async Task ReadTable_OfAnElementThatIsNoTable_FailsWithPatternNotSupported()
    {
        var elementRef = Add(new FakeUiaNode("Button", "Save"));

        var result = await Read(elementRef);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.PatternNotSupported);
        result.Error.Should().Contain("Button").And.Contain("read_element_text");
    }

    #endregion

    #region Failures

    [Fact]
    public async Task ReadTable_OfAStaleRef_FailsWithElementStale()
    {
        var grid = Grid(Cells(1, 1), "A");
        var elementRef = Add(grid);
        grid.Vanish();

        var result = await Read(elementRef);

        result.ErrorCode.Should().Be(UIElementErrorCode.ElementStale);
        result.Rows.Should().BeNull();
    }

    [Fact]
    public async Task ReadTable_OfAWindowThatDoesNotRespond_ReadsNothing()
    {
        var elementRef = Add(Grid(Cells(1, 1), "A"));
        _windows.Setup(w => w.IsHungAppWindow(Handle)).Returns(true);

        var result = await Read(elementRef);

        result.ErrorCode.Should().Be(UIElementErrorCode.NotResponding);
    }

    [Fact]
    public async Task ReadTable_OfARefInAWindowOfADeniedApp_IsRefused_BeforeAnyCellIsRead()
    {
        // Arrange - a password manager's entry list, with its passwords shown
        var elementRef = Add(Grid(Cells(3, 2), "Title", "Password"));
        _windows.Setup(w => w.GetProcessName(300u)).Returns("KeePass");
        var policy = new TargetPolicy(TargetPolicySettings.Default);
        var sut = new ElementReadService(_tree, _worker, _registry,
            new ElementSearchService(_tree, _worker, _registry, _windows.Object, policy: policy), _windows.Object, policy: policy);
        var readsBefore = _tree.Reads.Count;

        // Act
        var result = await sut.ReadTableAsync(new ElementTableRequest(elementRef, 0, 50, 20, Timeout));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Rows.Should().BeNull();
        _tree.Reads.Should().HaveCount(readsBefore, "no cell, header or pattern is read");
        _worker.Calls.Should().Be(0);
    }

    #endregion
}
