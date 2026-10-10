using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// The fixture's main window: an edit box, a check box, a combo box, a button that opens a modal dialog,
/// a button that opens a document with unsaved changes, a context menu, solid color swatches and a
/// virtualized list of <see cref="VirtualListItemCount"/> items. Every element the tests use has a fixed
/// AutomationId (<see cref="Ids"/>), and every change is written to the event log.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly FixtureOptions _options;
    private readonly FixtureEventLog _events;
    private readonly string _dpiAwareness;
    private readonly List<(string Id, FrameworkElement Element)> _reportedElements = [];
    private readonly TextBox _editBox = new() { Width = 320, Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBlock _statusText = new() { Margin = new Thickness(0, 12, 0, 0), Text = "Starting" };
    private DocumentWindow? _document;
    private bool _readyReported;

    public MainWindow(FixtureOptions options, FixtureEventLog events, string dpiAwareness)
    {
        _options = options;
        _events = events;
        _dpiAwareness = dpiAwareness;

        Title = MainTitle(options.Instance);
        Width = 760;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = !options.NoActivate;
        UseLayoutRounding = true;
        Background = Brushes.White;
        Content = BuildContent();

        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), handledEventsToo: true);
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Closed += (_, _) => ShuttingDown = true;
    }

    /// <summary>True once the main window has closed; the document then closes without its save prompt.</summary>
    public static bool ShuttingDown { get; private set; }

    private UIElement BuildContent()
    {
        var left = new StackPanel { Margin = new Thickness(12), Width = 360 };

        left.Children.Add(new TextBlock { Text = "Edit box" });
        _editBox.TextChanged += (_, _) => Log(Events.TextChanged, ("id", Ids.EditBox), ("text", _editBox.Text));
        left.Children.Add(Report(Ids.EditBox, _editBox, "Edit box"));

        var checkBox = new CheckBox { Content = "Check box", Margin = new Thickness(0, 0, 0, 12) };
        checkBox.Checked += (_, _) => Log(Events.CheckedChanged, ("id", Ids.CheckBox), ("isChecked", true));
        checkBox.Unchecked += (_, _) => Log(Events.CheckedChanged, ("id", Ids.CheckBox), ("isChecked", false));
        left.Children.Add(Report(Ids.CheckBox, checkBox));

        var comboBox = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = ComboBoxItems,
            SelectedIndex = 0
        };
        comboBox.SelectionChanged += (_, _) => Log(
            Events.SelectionChanged,
            ("id", Ids.ComboBox),
            ("index", comboBox.SelectedIndex),
            ("value", comboBox.SelectedItem as string));
        left.Children.Add(Report(Ids.ComboBox, comboBox, "Combo box"));

        left.Children.Add(Report(Ids.OpenModalButton, CreateButton("Open modal dialog...", Ids.OpenModalButton, OpenModal)));
        left.Children.Add(Report(Ids.OpenDocumentButton, CreateButton("Open document...", Ids.OpenDocumentButton, OpenDocument)));

        var contextMenuTarget = new Label
        {
            Content = "Right-click here for a context menu",
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 12),
            ContextMenu = BuildContextMenu()
        };
        left.Children.Add(Report(Ids.ContextMenuTarget, contextMenuTarget));

        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var swatch in Swatches)
        {
            var label = new Label
            {
                Width = 64,
                Height = 40,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(swatch.R, swatch.G, swatch.B))
            };
            swatches.Children.Add(Report(swatch.Id, label, swatch.Id));
        }
        left.Children.Add(swatches);

        left.Children.Add(Report(Ids.StatusText, _statusText));

        var list = new ListBox
        {
            Margin = new Thickness(12),
            ItemsSource = Enumerable.Range(0, VirtualListItemCount).Select(VirtualListItem).ToList()
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true);
        list.SelectionChanged += (_, _) => Log(
            Events.SelectionChanged,
            ("id", Ids.VirtualList),
            ("index", list.SelectedIndex),
            ("value", list.SelectedItem as string));
        Report(Ids.VirtualList, list, "Virtual list");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(list, 1);
        grid.Children.Add(left);
        grid.Children.Add(list);
        return grid;
    }

    private Button CreateButton(string text, string id, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 0, 12)
        };
        button.Click += (_, _) =>
        {
            Log(Events.ButtonClicked, ("id", id));
            onClick();
        };
        return button;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        AutomationProperties.SetAutomationId(menu, Ids.ContextMenu);
        menu.Opened += (_, _) => Log(Events.ContextMenuOpened);

        foreach (var (id, header) in new[]
                 {
                     (Ids.ContextMenuCopy, "Copy"),
                     (Ids.ContextMenuPaste, "Paste"),
                     (Ids.ContextMenuDelete, "Delete")
                 })
        {
            var item = new MenuItem { Header = header };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => Log(Events.ContextMenuItemClicked, ("id", id));
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Gives <paramref name="element"/> its AutomationId and reports its rectangle in the ready event.</summary>
    private T Report<T>(string id, T element, string? automationName = null) where T : FrameworkElement
    {
        AutomationProperties.SetAutomationId(element, id);
        if (automationName != null)
        {
            AutomationProperties.SetName(element, automationName);
        }
        _reportedElements.Add((id, element));
        return element;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Logical focus on the edit box, so it gets keyboard focus whenever the window is activated.
        // Only take keyboard focus now if the window was shown activated: SetFocus activates a window.
        FocusManager.SetFocusedElement(this, _editBox);
        if (!_options.NoActivate)
        {
            _editBox.Focus();
        }
    }

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var id = e.NewFocus is DependencyObject element ? AutomationProperties.GetAutomationId(element) : null;
        _events.Write(Events.KeyboardFocus, ("id", id ?? ""));
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (_readyReported)
        {
            return;
        }
        _readyReported = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.GetWindowRect(hwnd, out var window);
        NativeMethods.GetClientRect(hwnd, out var client);
        var clientOrigin = new NativeMethods.POINT();
        NativeMethods.ClientToScreen(hwnd, ref clientOrigin);

        var elements = new JsonObject();
        foreach (var (id, element) in _reportedElements)
        {
            elements[id] = ScreenRect(element);
        }

        _statusText.Text = "Ready";
        _events.Write(
            Events.Ready,
            ("pid", Environment.ProcessId),
            ("hwnd", (long)hwnd),
            ("title", Title),
            ("dpi", (int)NativeMethods.GetDpiForWindow(hwnd)),
            ("dpiAwareness", _dpiAwareness),
            ("windowRect", FixtureEventLog.Rect(window.Left, window.Top, window.Right - window.Left, window.Bottom - window.Top)),
            ("clientRect", FixtureEventLog.Rect(clientOrigin.X, clientOrigin.Y, client.Right - client.Left, client.Bottom - client.Top)),
            ("elements", elements));

        if (_options.OpenDocument)
        {
            OpenDocument();
        }
        if (_options.OpenModal)
        {
            // After the ready event: ShowDialog does not return until the dialog closes.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OpenModal));
        }
    }

    /// <summary>The element's rectangle in the fixture's screen coordinates (device pixels).</summary>
    private static JsonObject ScreenRect(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        var x = (int)Math.Round(topLeft.X);
        var y = (int)Math.Round(topLeft.Y);
        return FixtureEventLog.Rect(x, y, (int)Math.Round(bottomRight.X) - x, (int)Math.Round(bottomRight.Y) - y);
    }

    private void OpenModal()
    {
        if (OwnedWindows.OfType<ModalDialog>().Any())
        {
            return;
        }

        var dialog = new ModalDialog(_options, _events) { Owner = this };
        var result = dialog.ShowDialog();
        Log(Events.ModalClosed, ("result", result == true ? "ok" : "cancel"));
    }

    private void OpenDocument()
    {
        if (_document != null)
        {
            return;
        }

        _document = new DocumentWindow(_options, _events);
        _document.Closed += (_, _) => _document = null;
        _document.Show();
    }

    private void Log(string name, params (string Key, JsonNode? Value)[] fields)
    {
        _statusText.Text = name;
        _events.Write(name, fields);
    }
}
