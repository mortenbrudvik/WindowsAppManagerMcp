using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// A separate, unowned top-level window holding a read-only WPF DataGrid of <see cref="TableRowCount"/> rows, with column
/// headers (<see cref="TableColumns"/>) and row headers (<see cref="TableRowHeader"/>), roadmap M3.F4. The grid virtualizes
/// its rows, so UI Automation sees only the rows around what it shows: what read_table's realized-rows path reads (a WPF
/// grid's GetItem would scroll, so the server never asks it).
/// </summary>
internal sealed class TableWindow : Window
{
    public TableWindow(FixtureOptions options, FixtureEventLog events)
    {
        Title = TableTitle(options.Instance);
        Width = 520;
        Height = 360;
        FixturePlacement.CenterOnPrimaryMonitor(this);
        ShowActivated = !options.NoActivate;

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.All,
            EnableRowVirtualization = true,
            Margin = new Thickness(8),
            ItemsSource = Enumerable.Range(0, TableRowCount).Select(i => new TableRowItem(TableRow(i))).ToList()
        };
        VirtualizingPanel.SetIsVirtualizing(grid, true);
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        AutomationProperties.SetAutomationId(grid, Ids.DataGrid);
        AutomationProperties.SetName(grid, "Data grid");

        foreach (var (header, path) in TableColumns.Zip([nameof(TableRowItem.Name), nameof(TableRowItem.Quantity), nameof(TableRowItem.Price)]))
        {
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path) });
        }

        // A recycled row is loaded again for its new item, so its header always names the row it shows
        grid.LoadingRow += (_, e) => e.Row.Header = TableRowHeader(e.Row.GetIndex());

        Content = grid;
        ContentRendered += (_, _) => events.Write(Events.TableOpened, ("hwnd", (long)new WindowInteropHelper(this).Handle));
    }

    /// <summary>One row of the grid: the cells of <see cref="FixtureContract.TableRow"/>, as properties to bind to.</summary>
    internal sealed class TableRowItem(IReadOnlyList<string> cells)
    {
        public string Name { get; } = cells[0];

        public string Quantity { get; } = cells[1];

        public string Price { get; } = cells[2];
    }
}
