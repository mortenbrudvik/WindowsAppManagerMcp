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

    /// <summary>
    /// A WinForms grid (its GridPattern.GetItem only looks the cell up, so read_table asks for each cell by row and column) of
    /// <paramref name="cells"/>, with a Table pattern of <paramref name="headers"/> when there are any.
    /// </summary>
    private static FakeUiaNode Grid(IReadOnlyList<IReadOnlyList<FakeUiaNode?>> cells, params string[] headers)
    {
        var grid = new FakeUiaNode("DataGrid", "Processes")
            {
                OtherPatterns = UiaPatterns.Grid | (headers.Length > 0 ? UiaPatterns.Table : 0), FrameworkId = "WinForm"
            }
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
        var elementRef = Add(new FakeUiaNode("DataGrid", "Rows") { OtherPatterns = UiaPatterns.Grid, FrameworkId = "WinForm" }.WithPattern<IUiaGridPattern>(grid));

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
        // Arrange - a Win32 list view: its first child is a Header with a HeaderItem per column
        var list = new FakeUiaNode("List", "Items View") { OtherPatterns = UiaPatterns.Grid, FrameworkId = "Win32" }
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
    public async Task ReadTable_ACellWithAValue_GivesTheValue_NotItsNameThatDescribesIt()
    {
        // Arrange - a WinForms DataGridView cell is named "<header> Row <n>, Not sorted." and holds its text as its value
        var cells = new List<IReadOnlyList<FakeUiaNode?>>
        {
            new List<FakeUiaNode?> { new FakeUiaNode("DataItem", "Amount Row 1, Not sorted.") { ActionPatterns = ["Value"], Value = "42.50" } }
        };
        var elementRef = Add(Grid(cells, "Amount"));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows![0].Should().Equal("42.50");
    }

    [Fact]
    public async Task ReadTable_ACellWithAnEmptyValue_GivesItsName_AndACellWithoutAValue_ItsName()
    {
        // Arrange - an HTML cell has no Value pattern; an empty value falls back to the name
        var cells = new List<IReadOnlyList<FakeUiaNode?>>
        {
            new List<FakeUiaNode?> { Cell("Tea"), new FakeUiaNode("DataItem", "Pending") { ActionPatterns = ["Value"], Value = "" } }
        };
        var elementRef = Add(Grid(cells, "Item", "Status"));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows![0].Should().Equal("Tea", "Pending");
    }

    [Fact]
    public async Task ReadTable_OfRowsWhoseCellsAreNamedForTheirColumn_GivesTheValues()
    {
        // Arrange - Explorer's details rows: each cell is an Edit named for its column, holding the text as its value
        var list = new FakeUiaNode("List", "Items View").With(
            new FakeUiaNode("ListItem", "a.doc").With(
                new FakeUiaNode("Edit", "Name") { ActionPatterns = ["Value"], Value = "a.doc" },
                new FakeUiaNode("Edit", "Date modified") { ActionPatterns = ["Value"], Value = "8/25/2006" }));
        var elementRef = Add(list);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows.Should().ContainSingle().Which.Should().Equal("a.doc", "8/25/2006");
    }

    [Fact]
    public async Task ReadTable_ACellOfAWpfRowNamedByItsItemAndColumn_GivesItsValue()
    {
        // Arrange - a WPF DataGrid cell without plain text is named "Item: <item>, Column Display Index: <n>"
        var table = new FakeUiaNode("Table", "People").With(
            new FakeUiaNode("DataItem", "Contoso.Person").With(
                new FakeUiaNode("Custom", "Item: Contoso.Person, Column Display Index: 0") { ActionPatterns = ["Value"], Value = "Ann" }));
        var elementRef = Add(table);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows.Should().ContainSingle().Which.Should().Equal("Ann");
    }

    [Fact]
    public async Task ReadTable_ANamedPasswordCell_IsNull_AndItsValueIsNeverRead()
    {
        var cells = new List<IReadOnlyList<FakeUiaNode?>>
        {
            new List<FakeUiaNode?> { new FakeUiaNode("Edit", "Password") { ActionPatterns = ["Value"], IsPassword = true, Value = "secret" } }
        };
        var elementRef = Add(Grid(cells, "Password"));

        var result = await Read(elementRef);

        result.Rows![0].Should().Equal([null]);
        _tree.Reads.Should().NotContain(r => r.StartsWith("Value:"));
    }

    [Fact]
    public async Task ReadTable_ACellThatVanishesWhileItsValueIsRead_IsNull_AndTheRestIsRead()
    {
        // Arrange - the cell at row 1, column 0 is gone by the time its value is read
        var cells = Cells(3, 2);
        cells[1] = [new FakeUiaNode("DataItem") { ActionPatterns = ["Value"], ValueException = FakeUiaTree.Vanished() }, Cell("r1c1")];
        var elementRef = Add(Grid(cells, "A", "B"));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeTrue();
        result.Rows.Should().HaveCount(3);
        result.Rows![1].Should().Equal(null, "r1c1");
        result.Rows[2].Should().Equal("r2c0", "r2c1");
    }

    [Fact]
    public async Task ReadTable_ACellOfARowThatVanishesWhileItsValueIsRead_IsNull()
    {
        var list = new FakeUiaNode("List", "Files").With(
            new FakeUiaNode("ListItem", "a").With(
                new FakeUiaNode("Edit", "Name") { ActionPatterns = ["Value"], ValueException = FakeUiaTree.Vanished() },
                new FakeUiaNode("Text", "12 KB")),
            new FakeUiaNode("ListItem", "b").With(new FakeUiaNode("Text", "b.txt"), new FakeUiaNode("Text", "3 KB")));
        var elementRef = Add(list);

        var result = await Read(elementRef);

        result.Success.Should().BeTrue();
        result.Rows.Should().HaveCount(2);
        result.Rows![0].Should().Equal(null, "12 KB");
        result.Rows[1].Should().Equal("b.txt", "3 KB");
    }

    [Fact]
    public async Task ReadTable_WhenTheGridVanishesAfterRowsWereRead_ReturnsThoseRows_AndSaysTheTableIsGone()
    {
        // Arrange - the grid is gone from row 2 on
        var grid = new FakeGridPattern(Cells(5, 2)) { ItemFailure = (row, _) => row >= 2 ? FakeUiaTree.Vanished("grid") : null };
        var elementRef = Add(new FakeUiaNode("DataGrid", "Rows") { OtherPatterns = UiaPatterns.Grid, FrameworkId = "WinForm" }
            .WithPattern<IUiaGridPattern>(grid));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Success.Should().BeTrue();
        result.Rows.Should().HaveCount(2);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementTableTruncation.Disappeared);
        result.NextStartRow.Should().Be(2);
        result.Warning.Should().Contain("disappeared").And.Contain("find_ui_elements");
    }

    [Fact]
    public async Task ReadTable_WhenTheGridVanishesBeforeARowWasRead_FailsWithElementAccessFailed()
    {
        var grid = new FakeGridPattern(Cells(5, 2)) { ItemFailure = (_, _) => FakeUiaTree.Vanished("grid") };
        var elementRef = Add(new FakeUiaNode("DataGrid", "Rows") { OtherPatterns = UiaPatterns.Grid, FrameworkId = "WinForm" }
            .WithPattern<IUiaGridPattern>(grid));

        var result = await Read(elementRef);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.ElementAccessFailed);
        result.Rows.Should().BeNull();
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
        result.Rows!.Sum(r => r.Sum(c => c?.Length ?? 0)).Should().BeLessThanOrEqualTo(ElementReadService.MaxTableTextBytes);
        result.NextStartRow.Should().Be(result.Rows!.Count);
    }

    [Fact]
    public async Task ReadTable_CountsTheLimitInBytes_AndCutsAFirstRowThatAloneIsOverIt()
    {
        // Arrange - 3 rows of 50 Cyrillic cells of 500 characters: one row alone is 50,000 bytes of JSON
        var cells = Enumerable.Range(0, 3)
            .Select(_ => (IReadOnlyList<FakeUiaNode?>)Enumerable.Range(0, 50).Select(_ => (FakeUiaNode?)Cell(new string('ж', 500))).ToList())
            .ToList();
        var elementRef = Add(Grid(cells));

        // Act
        var first = await Read(elementRef, maxColumns: 50);
        var second = await Read(elementRef, startRow: first.NextStartRow!.Value, maxColumns: 50);

        // Assert - each page holds one row, cut to fit, so the read moves on
        first.Rows.Should().ContainSingle().Which.Should().HaveCount(50);
        first.Rows![0].Sum(c => System.Text.Encoding.UTF8.GetByteCount(c!) + 3).Should().BeLessThanOrEqualTo(ElementReadService.MaxTableTextBytes);
        first.CellsTruncated.Should().BeTrue();
        first.TruncationReason.Should().Be(ElementTableTruncation.Size);
        first.NextStartRow.Should().Be(1);
        second.Rows.Should().ContainSingle();
        second.NextStartRow.Should().Be(2);
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

    [Fact]
    public async Task ReadTable_WhenTheTimeoutPassesBeforeTheFirstRow_GivesNoNextStartRow_AndSaysToReadThePageAgain()
    {
        // Arrange - the page's first cell takes longer than the whole timeout (roadmap M3.F15)
        var cells = Cells(5, 2);
        ((FakeUiaNode)cells[3][0]!).OnRead = () => Thread.Sleep(400);
        var elementRef = Add(Grid(cells, "A", "B"));

        // Act
        var result = await Read(elementRef, startRow: 3, timeout: TimeSpan.FromMilliseconds(200));

        // Assert - a nextStartRow equal to startRow would send a paging agent round the same page for ever
        result.Success.Should().BeTrue();
        result.Rows.Should().BeEmpty();
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementTableTruncation.Timeout);
        result.NextStartRow.Should().BeNull();
        result.Warning.Should().Contain("startRow 3").And.Contain("timeoutMs");
    }

    #endregion

    #region Grids whose GetItem scrolls: read through the tree

    /// <summary>
    /// A virtualized grid of <paramref name="rowCount"/> rows of <paramref name="columns"/> columns, as WPF's DataGrid exposes it:
    /// the Grid and Table patterns, a Header child, and a DataItem child only for each realized row, whose cells
    /// ("r{row}c{column}") carry their GridItem row and column. GetItem would scroll it, so the test checks it is never called.
    /// With <paramref name="rowHeaders"/>, each row starts with a HeaderItem, named "#{row}" with
    /// <paramref name="namedRowHeaders"/> and unnamed (as WPF's blank row headers) without.
    /// </summary>
    private static (FakeUiaNode Grid, FakeGridPattern Pattern) VirtualizedGrid(
        string? framework, int rowCount, int columns, IEnumerable<int> realized, bool rowHeaders = false, bool namedRowHeaders = false)
    {
        var pattern = new FakeGridPattern(Enumerable.Repeat<IReadOnlyList<FakeUiaNode?>>([], rowCount).ToList(), columns);
        var headers = Enumerable.Range(0, columns).Select(c => new FakeUiaNode("HeaderItem", $"Column {c}")).ToArray();
        var grid = new FakeUiaNode("DataGrid", "People") { OtherPatterns = UiaPatterns.Grid | UiaPatterns.Table, FrameworkId = framework }
            .WithPattern<IUiaGridPattern>(pattern)
            .WithPattern<IUiaTablePattern>(new FakeTablePattern(headers))
            .With(new FakeUiaNode("Header").With(headers));
        foreach (var row in realized)
        {
            var item = new FakeUiaNode("DataItem", "Contoso.Person");
            if (rowHeaders)
            {
                item.With(new FakeUiaNode("HeaderItem", namedRowHeaders ? $"#{row}" : null));
            }

            item.With(Enumerable.Range(0, columns)
                .Select(c => new FakeUiaNode("Custom", $"r{row}c{c}").WithPattern<IUiaGridItemPattern>(new FakeGridItemPattern(row, c)))
                .ToArray());
            grid.With(item);
        }

        return (grid, pattern);
    }

    [Theory]
    [InlineData("WPF")] // DataGrid scrolls the cell into view, GridView the row
    [InlineData("XAML")] // the Windows Community Toolkit DataGrid scrolls too
    [InlineData("DirectUI")] // not checked: an unknown GetItem may scroll
    [InlineData(null)]
    public async Task ReadTable_OfAGridWhoseGetItemMayScroll_ReadsItsRealizedRowsThroughTheTree_AndNeverCallsGetItem(string? framework)
    {
        // Arrange - 1000 rows, of which the grid has realized the first 20
        var (grid, pattern) = VirtualizedGrid(framework, 1000, 8, Enumerable.Range(0, 20));
        var elementRef = Add(grid);

        // Act
        var result = await Read(elementRef, maxRows: 50);

        // Assert - the realized rows, then the page ends where the rows not realized start
        pattern.Items.Should().BeEmpty("GetItem would scroll the grid");
        result.Success.Should().BeTrue();
        result.Source.Should().Be(ElementTableSource.Grid);
        result.Headers.Should().HaveCount(8).And.StartWith("Column 0");
        result.Rows.Should().HaveCount(20);
        result.Rows![0].Should().Equal(Enumerable.Range(0, 8).Select(c => $"r0c{c}"));
        result.Rows[19][7].Should().Be("r19c7");
        result.TotalRows.Should().Be(1000);
        result.Truncated.Should().BeTrue();
        result.TruncationReason.Should().Be(ElementTableTruncation.NotRealized);
        result.NextStartRow.Should().Be(20);
        result.Warning.Should().Contain("element_action").And.Contain("scroll").And.Contain("startRow 20");
    }

    [Fact]
    public async Task ReadTable_OfAWpfGrid_PastItsRealizedRows_ReadsNothing_AndSaysWhereToScroll()
    {
        var (grid, pattern) = VirtualizedGrid("WPF", 1000, 8, Enumerable.Range(0, 20));
        var elementRef = Add(grid);

        var result = await Read(elementRef, startRow: 500);

        pattern.Items.Should().BeEmpty();
        result.Success.Should().BeTrue();
        result.Rows.Should().BeEmpty();
        result.TruncationReason.Should().Be(ElementTableTruncation.NotRealized);
        result.NextStartRow.Should().Be(500);
    }

    [Fact]
    public async Task ReadTable_OfAScrolledWpfGrid_PlacesTheRealizedRowsByTheirCellsGridRow()
    {
        // Arrange - scrolled down: the first realized row is row 495, not row 0, and row headers come first in each row
        var (grid, pattern) = VirtualizedGrid("WPF", 1000, 3, Enumerable.Range(495, 36), rowHeaders: true);
        var elementRef = Add(grid);

        // Act
        var result = await Read(elementRef, startRow: 500, maxRows: 10);

        // Assert
        pattern.Items.Should().BeEmpty();
        result.StartRow.Should().Be(500);
        result.Rows.Should().HaveCount(10);
        result.Rows![0].Should().Equal("r500c0", "r500c1", "r500c2");
        result.Rows[9][0].Should().Be("r509c0");
        result.RowHeaders.Should().BeNull("blank row headers say nothing");
        result.TruncationReason.Should().Be(ElementTableTruncation.MaxRows);
        result.NextStartRow.Should().Be(510);
    }

    [Fact]
    public async Task ReadTable_OfAWpfGrid_StopsAtAGapInItsRealizedRows()
    {
        // Arrange - rows 0-9 realized, and row 30 (a row WPF keeps realized, such as the focused one)
        var (grid, _) = VirtualizedGrid("WPF", 100, 2, Enumerable.Range(0, 10).Append(30));
        var elementRef = Add(grid);

        // Act
        var result = await Read(elementRef);

        // Assert - the page never skips a row: it ends before the first missing one
        result.Rows.Should().HaveCount(10);
        result.TruncationReason.Should().Be(ElementTableTruncation.NotRealized);
        result.NextStartRow.Should().Be(10);
    }

    [Theory]
    [InlineData("Win32")]
    [InlineData("WinForm")]
    [InlineData("Chrome")]
    public async Task ReadTable_OfAGridWhoseGetItemOnlyLooksUp_AsksForEachCell_RealizedOrNot(string framework)
    {
        // Arrange - the same grid as above, but of a framework whose GetItem was checked not to scroll
        var cells = Cells(30, 2);
        var grid = new FakeGridPattern(cells);
        var elementRef = Add(new FakeUiaNode("DataGrid", "Rows") { OtherPatterns = UiaPatterns.Grid, FrameworkId = framework }
            .WithPattern<IUiaGridPattern>(grid));

        // Act
        var result = await Read(elementRef, startRow: 25);

        // Assert
        grid.Items.Should().NotBeEmpty();
        result.Rows.Should().HaveCount(5);
        result.Rows![0].Should().Equal("r25c0", "r25c1");
        result.TruncationReason.Should().BeNull();
    }

    #endregion

    #region Header rows and multi-row headers

    [Fact]
    public async Task ReadTable_OfAnHtmlTable_DoesNotReturnItsHeaderRowAsARow()
    {
        // Arrange - Chromium: the header cells are grid row 0, and GetColumnHeaders returns the same elements
        var name = new FakeUiaNode("DataItem", "Name");
        var age = new FakeUiaNode("DataItem", "Age");
        var cells = new List<IReadOnlyList<FakeUiaNode?>> { new List<FakeUiaNode?> { name, age } };
        cells.AddRange(Cells(10, 2));
        var grid = new FakeUiaNode("Table", "People") { OtherPatterns = UiaPatterns.Grid | UiaPatterns.Table, FrameworkId = "Chrome" }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(cells))
            .WithPattern<IUiaTablePattern>(new FakeTablePattern(name, age));
        var elementRef = Add(grid);

        // Act
        var first = await Read(elementRef, maxRows: 3);
        var last = await Read(elementRef, startRow: 9);

        // Assert - startRow does not count the header row, as documented
        first.Headers.Should().Equal("Name", "Age");
        first.Rows![0].Should().Equal("r0c0", "r0c1");
        first.Rows.Should().NotContain(row => row.Contains("Name"));
        first.TotalRows.Should().Be(10);
        first.NextStartRow.Should().Be(3);
        last.Rows.Should().ContainSingle().Which.Should().Equal("r9c0", "r9c1");
        last.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadTable_PairsMultiRowHeadersWithTheirColumns_ByWhereEachHeaderSits()
    {
        // Arrange - Q1 and Q2 each span two columns over Revenue and Cost; Chromium lists a column's headers from the top,
        // and a spanning header once per column it spans
        static FakeUiaNode Header(string name, int row, int column, int columnSpan = 1) =>
            new FakeUiaNode("DataItem", name).WithPattern<IUiaGridItemPattern>(new FakeGridItemPattern(row, column, 1, columnSpan));
        var q1 = Header("Q1", 0, 0, 2);
        var q2 = Header("Q2", 0, 2, 2);
        var revenue1 = Header("Revenue", 1, 0);
        var cost1 = Header("Cost", 1, 1);
        var revenue2 = Header("Revenue", 1, 2);
        var cost2 = Header("Cost", 1, 3);
        var cells = new List<IReadOnlyList<FakeUiaNode?>>
        {
            new List<FakeUiaNode?> { q1, q1, q2, q2 },
            new List<FakeUiaNode?> { revenue1, cost1, revenue2, cost2 }
        };
        cells.AddRange(Cells(5, 4));
        var grid = new FakeUiaNode("Table", "Results") { OtherPatterns = UiaPatterns.Grid | UiaPatterns.Table, FrameworkId = "Chrome" }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(cells))
            .WithPattern<IUiaTablePattern>(new FakeTablePattern(q1, revenue1, q1, cost1, q2, revenue2, q2, cost2));
        var elementRef = Add(grid);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Headers.Should().Equal("Q1 / Revenue", "Q1 / Cost", "Q2 / Revenue", "Q2 / Cost");
        result.Rows![0].Should().Equal("r0c0", "r0c1", "r0c2", "r0c3");
        result.TotalRows.Should().Be(5);
    }

    [Fact]
    public async Task ReadTable_OfATableReadThroughTheTree_SkipsItsHeaderRow_AndCountsRowsWithoutIt()
    {
        // Arrange - an HTML table of a browser whose GetItem is not known to only read: its header row is a row child, and its
        // header cells sit in grid row 0
        var headers = new[] { "Item", "Price" }
            .Select((h, c) => new FakeUiaNode("HeaderItem", h).WithPattern<IUiaGridItemPattern>(new FakeGridItemPattern(0, c)))
            .ToArray();
        var table = new FakeUiaNode("Table", "Prices") { OtherPatterns = UiaPatterns.Grid | UiaPatterns.Table, FrameworkId = "Gecko" }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(Enumerable.Repeat<IReadOnlyList<FakeUiaNode?>>([], 3).ToList(), 2))
            .WithPattern<IUiaTablePattern>(new FakeTablePattern(headers))
            .With(new FakeUiaNode("DataItem").With(headers));
        foreach (var (item, row) in new[] { ("Tea", 1), ("Coffee", 2) })
        {
            table.With(new FakeUiaNode("DataItem").With(
                new FakeUiaNode("DataItem", item).WithPattern<IUiaGridItemPattern>(new FakeGridItemPattern(row, 0)),
                new FakeUiaNode("DataItem", "3").WithPattern<IUiaGridItemPattern>(new FakeGridItemPattern(row, 1))));
        }

        var elementRef = Add(table);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Headers.Should().Equal("Item", "Price");
        result.Rows.Should().HaveCount(2);
        result.Rows![0].Should().Equal("Tea", "3");
        result.TotalRows.Should().Be(2);
        result.Truncated.Should().BeFalse();
    }

    #endregion

    #region Row headers

    /// <summary>A WinForms grid as <see cref="Grid"/> makes it, whose Table pattern also gives a row header per row.</summary>
    private static FakeUiaNode GridWithRowHeaders(IReadOnlyList<IReadOnlyList<FakeUiaNode?>> cells, IEnumerable<FakeUiaNode> rowHeaders) =>
        new FakeUiaNode("DataGrid", "Week") { OtherPatterns = UiaPatterns.Grid | UiaPatterns.Table, FrameworkId = "WinForm" }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(cells))
            .WithPattern<IUiaTablePattern>(new FakeTablePattern(new FakeUiaNode("HeaderItem", "Hours"), new FakeUiaNode("HeaderItem", "Notes"))
            {
                RowHeaders = rowHeaders.ToList()
            });

    [Fact]
    public async Task ReadTable_OfAGridWithRowHeaders_ReturnsTheRowHeaderOfEachRowOfThePage()
    {
        // Arrange - a timesheet whose rows are headed by their day (roadmap M3.F15)
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri" }.Select(day => new FakeUiaNode("HeaderItem", day));
        var elementRef = Add(GridWithRowHeaders(Cells(5, 2), days));

        // Act
        var result = await Read(elementRef, startRow: 1, maxRows: 2);

        // Assert - one per row returned, in the same order; a row header is no column
        result.Headers.Should().Equal("Hours", "Notes");
        result.Rows!.Select(r => r[0]).Should().Equal("r1c0", "r2c0");
        result.RowHeaders.Should().Equal("Tue", "Wed");
        result.TotalColumns.Should().Be(2);
    }

    [Fact]
    public async Task ReadTable_ARowHeaderWithAValue_GivesTheValue_AndARowWithoutOne_Null()
    {
        // Arrange - three rows, two row headers: the first holds its text as its value, as a WinForms cell does
        var headers = new[]
        {
            new FakeUiaNode("HeaderItem", "Row 0") { ActionPatterns = ["Value"], Value = "Total" },
            new FakeUiaNode("HeaderItem", "Average")
        };
        var elementRef = Add(GridWithRowHeaders(Cells(3, 2), headers));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows.Should().HaveCount(3);
        result.RowHeaders.Should().Equal("Total", "Average", null);
    }

    [Fact]
    public async Task ReadTable_OfAGridWithoutRowHeaders_LeavesThemOut()
    {
        // Arrange
        var table = new FakeTablePattern(new FakeUiaNode("HeaderItem", "A"), new FakeUiaNode("HeaderItem", "B"));
        var elementRef = Add(new FakeUiaNode("DataGrid", "Rows") { OtherPatterns = UiaPatterns.Grid | UiaPatterns.Table, FrameworkId = "WinForm" }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(Cells(2, 2)))
            .WithPattern<IUiaTablePattern>(table));

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows.Should().HaveCount(2);
        result.RowHeaders.Should().BeNull();
        table.RowHeaderCalls.Should().Be(1);
    }

    [Fact]
    public async Task ReadTable_OfAGridWhoseRowHeadersAreBlank_LeavesThemOut()
    {
        var blank = Enumerable.Range(0, 3).Select(_ => new FakeUiaNode("HeaderItem", ""));
        var elementRef = Add(GridWithRowHeaders(Cells(3, 2), blank));

        var result = await Read(elementRef);

        result.Rows.Should().HaveCount(3);
        result.RowHeaders.Should().BeNull();
    }

    [Fact]
    public async Task ReadTable_RowHeadersThatAreCellsOfTheGrid_AreReadOnceInTheirRow_NotAgainAsRowHeaders()
    {
        // Arrange - an HTML table with a th scope=row in each row: Chromium gives it as a cell (column 0) and a row header
        var cells = Enumerable.Range(0, 3)
            .Select(r => (IReadOnlyList<FakeUiaNode?>)new List<FakeUiaNode?>
            {
                new FakeUiaNode("HeaderItem", $"Item {r}").WithPattern<IUiaGridItemPattern>(new FakeGridItemPattern(r, 0)),
                Cell($"r{r}c1")
            })
            .ToList();
        var grid = new FakeUiaNode("Table", "Prices") { OtherPatterns = UiaPatterns.Grid | UiaPatterns.Table, FrameworkId = "Chrome" }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(cells))
            .WithPattern<IUiaTablePattern>(new FakeTablePattern { RowHeaders = cells.Select(row => row[0]!).ToList() });
        var elementRef = Add(grid);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows![0].Should().Equal("Item 0", "r0c1");
        result.RowHeaders.Should().BeNull();
    }

    [Fact]
    public async Task ReadTable_OfAGridReadThroughTheTree_TakesEachRowsHeaderItemAsItsRowHeader()
    {
        // Arrange - a WPF DataGrid with row headers: each realized row starts with its DataGridRowHeader, a HeaderItem
        var (grid, _) = VirtualizedGrid("WPF", 10, 2, Enumerable.Range(0, 10), rowHeaders: true, namedRowHeaders: true);
        var elementRef = Add(grid);

        // Act
        var result = await Read(elementRef, startRow: 4, maxRows: 3);

        // Assert
        result.Rows!.Select(r => r[0]).Should().Equal("r4c0", "r5c0", "r6c0");
        result.Rows.Should().OnlyContain(row => row.Count == 2);
        result.RowHeaders.Should().Equal("#4", "#5", "#6");
    }

    [Fact]
    public async Task ReadTable_CountsRowHeadersInTheSizeLimit()
    {
        // Arrange - 100 rows of one short cell, each headed by a 400-character row header: 40,000 characters of headers alone
        var headers = Enumerable.Range(0, 100).Select(_ => new FakeUiaNode("HeaderItem", new string('h', 400)));
        var cells = Enumerable.Range(0, 100).Select(_ => (IReadOnlyList<FakeUiaNode?>)new List<FakeUiaNode?> { Cell("x") }).ToList();
        var elementRef = Add(GridWithRowHeaders(cells, headers));

        // Act
        var result = await Read(elementRef, maxRows: 200);

        // Assert
        result.TruncationReason.Should().Be(ElementTableTruncation.Size);
        result.RowHeaders.Should().HaveCount(result.Rows!.Count);
        result.RowHeaders!.Sum(h => h!.Length + 3).Should().BeLessThanOrEqualTo(ElementReadService.MaxTableTextBytes);
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
    public async Task ReadTable_OfATree_TakesAnExpandedItemsChildItemsAsNoCells()
    {
        // Arrange - Explorer's navigation pane: an expanded tree item's children are tree items of their own
        var tree = new FakeUiaNode("Tree", "Navigation").With(
            new FakeUiaNode("TreeItem", "This PC").With(new FakeUiaNode("TreeItem", "Desktop"), new FakeUiaNode("TreeItem", "Documents")),
            new FakeUiaNode("TreeItem", "Network"));
        var elementRef = Add(tree);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows.Should().HaveCount(2);
        result.Rows![0].Should().Equal("This PC");
        result.Rows[1].Should().Equal("Network");
        result.TotalColumns.Should().Be(1);
    }

    [Fact]
    public async Task ReadTable_OfATreeGridRow_KeepsItsDataItemCells_AndLeavesItsNestedRowOut()
    {
        // Arrange - a web treegrid's row: a TreeItem whose cells are DataItems, here with a nested row under it
        var tree = new FakeUiaNode("Tree", "Files").With(
            new FakeUiaNode("TreeItem", "src").With(
                new FakeUiaNode("DataItem", "a"), new FakeUiaNode("DataItem", "b"), new FakeUiaNode("TreeItem", "nested")));
        var elementRef = Add(tree);

        // Act
        var result = await Read(elementRef);

        // Assert
        result.Rows.Should().ContainSingle().Which.Should().Equal("a", "b");
        result.TotalColumns.Should().Be(2);
    }

    [Fact]
    public async Task ReadTable_OfAGroupedList_ReadsTheRowsInsideItsGroups_InOrder()
    {
        // Arrange - Explorer's details view grouped by date: no Grid pattern, the column headers, then a Group per group
        var list = new FakeUiaNode("List", "Items View").With(
            new FakeUiaNode("Header").With(new FakeUiaNode("HeaderItem", "Name"), new FakeUiaNode("HeaderItem", "Size")),
            new FakeUiaNode("Group", "Today").With(
                new FakeUiaNode("ListItem", "a.txt").With(new FakeUiaNode("Text", "a.txt"), new FakeUiaNode("Text", "1 KB")),
                new FakeUiaNode("ListItem", "b.txt").With(new FakeUiaNode("Text", "b.txt"), new FakeUiaNode("Text", "2 KB"))),
            new FakeUiaNode("Group", "Last week").With(
                new FakeUiaNode("ListItem", "c.txt").With(new FakeUiaNode("Text", "c.txt"), new FakeUiaNode("Text", "3 KB"))));
        var elementRef = Add(list);

        // Act
        var result = await Read(elementRef);

        // Assert (roadmap M3.F15)
        result.Success.Should().BeTrue(result.Error);
        result.Source.Should().Be(ElementTableSource.Rows);
        result.Headers.Should().Equal("Name", "Size");
        result.Rows!.Select(r => r[0]).Should().Equal("a.txt", "b.txt", "c.txt");
        result.Rows![2].Should().Equal("c.txt", "3 KB");
        result.TotalRows.Should().Be(3);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadTable_OfAGroupedList_NumbersItsRowsAcrossTheGroups_SoThatPagesFollowOn()
    {
        // Arrange - a group of three, and a group holding a group of two and a row
        var list = new FakeUiaNode("List", "Items View").With(
            new FakeUiaNode("Group", "Today").With(new FakeUiaNode("ListItem", "0"), new FakeUiaNode("ListItem", "1"), new FakeUiaNode("ListItem", "2")),
            new FakeUiaNode("Group", "Earlier").With(
                new FakeUiaNode("Group", "Last week").With(new FakeUiaNode("ListItem", "3"), new FakeUiaNode("ListItem", "4")),
                new FakeUiaNode("ListItem", "5")));
        var elementRef = Add(list);

        // Act
        var first = await Read(elementRef, maxRows: 2);
        var second = await Read(elementRef, startRow: first.NextStartRow!.Value, maxRows: 2);
        var third = await Read(elementRef, startRow: second.NextStartRow!.Value, maxRows: 2);

        // Assert
        first.Rows!.Select(r => r[0]).Should().Equal("0", "1");
        first.NextStartRow.Should().Be(2);
        second.Rows!.Select(r => r[0]).Should().Equal("2", "3");
        second.NextStartRow.Should().Be(4);
        third.Rows!.Select(r => r[0]).Should().Equal("4", "5");
        third.NextStartRow.Should().BeNull();
        third.TotalRows.Should().Be(6);
    }

    [Fact]
    public async Task ReadTable_OfAGroupedGridReadThroughTheTree_ReadsTheRealizedRowsInsideItsGroups()
    {
        // Arrange - a grouped WPF DataGrid: its rows sit in a GroupItem (Group) per group, placed by their cells' GridItem row
        var (grid, pattern) = VirtualizedGrid("WPF", 4, 2, []);
        foreach (var (name, rows) in new[] { ("Open", new[] { 0, 1 }), ("Closed", new[] { 2, 3 }) })
        {
            grid.With(new FakeUiaNode("Group", name).With(rows
                .Select(row => new FakeUiaNode("DataItem", "Contoso.Ticket").With(Enumerable.Range(0, 2)
                    .Select(c => new FakeUiaNode("Custom", $"r{row}c{c}").WithPattern<IUiaGridItemPattern>(new FakeGridItemPattern(row, c)))
                    .ToArray()))
                .ToArray()));
        }

        var elementRef = Add(grid);

        // Act
        var result = await Read(elementRef, startRow: 1);

        // Assert
        pattern.Items.Should().BeEmpty();
        result.Rows!.Select(r => r[0]).Should().Equal("r1c0", "r2c0", "r3c0");
        result.TotalRows.Should().Be(4);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ReadTable_OfAnElementWhoseGroupsHoldNoRows_FailsWithPatternNotSupported()
    {
        var pane = new FakeUiaNode("Pane", "Settings").With(new FakeUiaNode("Group", "Display").With(new FakeUiaNode("Button", "Apply")));
        var elementRef = Add(pane);

        var result = await Read(elementRef);

        result.ErrorCode.Should().Be(UIElementErrorCode.PatternNotSupported);
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

    [Fact]
    public async Task ReadTable_OfARefMovedToAReCreatedGrid_ReadsIt_AndSaysSo()
    {
        // Arrange - the grid's search proved it the only one; the view was rebuilt with a new (WinForms) grid
        var old = new FakeUiaNode("DataGrid", "Processes") { AutomationId = "Procs" };
        _window.With(old);
        var gridRef = _registry.Register(Handle, _tree.ReadElement(old),
            new ElementOrigin(_registry.NextGeneration(), new ElementCriteria(AutomationId: "Procs"), Unique: true))!;
        old.Vanish();
        _window.With(new FakeUiaNode("DataGrid", "Processes") { AutomationId = "Procs", OtherPatterns = UiaPatterns.Grid, FrameworkId = "WinForm" }
            .WithPattern<IUiaGridPattern>(new FakeGridPattern(Cells(2, 1))));

        // Act
        var result = await Read(gridRef);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Rows.Should().HaveCount(2);
        result.RefMoved.Should().BeTrue();
        result.Warning.Should().Contain("re-created");
    }

    #endregion
}
