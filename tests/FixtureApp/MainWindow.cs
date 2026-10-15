using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// The fixture's main window: an edit box, a password box, a check box, a combo box, a button that opens a modal
/// dialog, a button that opens a document with unsaved changes, buttons that show a Save As dialog and a task dialog, a
/// context menu, solid color swatches and a virtualized list of <see cref="VirtualListItemCount"/> items. Every element the tests use has a fixed
/// AutomationId (<see cref="Ids"/>), and every change is written to the event log, except the password box's content.
/// Only when asked (roadmap M6.4) does it also show, right of the swatches, a change panel that changes once after a delay
/// and a spinner that turns for a while; without those options the window is exactly as before.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly FixtureOptions _options;
    private readonly FixtureEventLog _events;
    private readonly string _dpiAwareness;
    private readonly List<(string Id, FrameworkElement Element)> _reportedElements = [];
    private readonly TextBox _editBox = new() { Width = 320, Margin = new Thickness(0, 4, 0, 12) };
    // "Ready" from the first frame, not set when the ready event is written: that change would only reach the screen a
    // frame after the event, so a test's pixel baseline taken at once could still catch it (roadmap M6.4: after the ready
    // event the change panel or spinner must be the only region that changes)
    private readonly TextBlock _statusText = new() { Margin = new Thickness(0, 12, 0, 0), Text = "Ready" };
    private DocumentWindow? _document;
    private TableWindow? _table;
    private MenuWindow? _menuWindow;
    private WebViewWindow? _webViewWindow;
    private OverlayWindow? _overlay;
    private CanvasWindow? _canvas;
    private Label? _changePanel;
    private FrameworkElement? _spinner;
    private readonly RotateTransform _spinnerTurn = new(0, TimedPanelSize / 2.0, TimedPanelSize / 2.0);
    private bool _readyReported;
    private NoActivateWhileShown? _noActivateWhileShown;

    public MainWindow(FixtureOptions options, FixtureEventLog events, string dpiAwareness)
    {
        _options = options;
        _events = events;
        _dpiAwareness = dpiAwareness;

        Title = MainTitle(options.Instance);
        Width = 760;
        Height = 540;
        FixturePlacement.CenterOnPrimaryMonitor(this);

        // On a monitor at another scale WPF's DPI change inside Show would activate the window (roadmap M3.F30)
        if (options.OtherScaleMonitor && FixturePlacement.CenterOnMonitorAtAnotherScale(this) && options.NoActivate)
        {
            _noActivateWhileShown = new NoActivateWhileShown();
        }
        ShowActivated = !options.NoActivate;
        UseLayoutRounding = true;
        Background = Brushes.White;
        Content = BuildContent();

        // Logical focus on the edit box, so it gets keyboard focus whenever the window is activated (WPF moves the focus that
        // activation gives the window on to its focused element). Set before the window is shown, while the edit box cannot
        // take keyboard focus: FocusManager.SetFocusedElement also calls Keyboard.Focus when nothing in the process has
        // keyboard focus, and the Win32 SetFocus under it activates the window. A process the test host just started may
        // still take the foreground, so with --no-activate that brought the fixture to the front over the terminal.
        FocusManager.SetFocusedElement(this, _editBox);

        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), handledEventsToo: true);
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);
        AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler(OnPreviewMouseUp), handledEventsToo: true);
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

        // UI Automation reports it as a password field (IsPassword), whose value the server never reads. Its content is
        // not logged.
        left.Children.Add(new TextBlock { Text = "Password box" });
        var passwordBox = new PasswordBox { Width = 320, Margin = new Thickness(0, 4, 0, 12) };
        left.Children.Add(Report(Ids.PasswordBox, passwordBox, "Password box"));

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

        // The common dialogs (roadmap M3.F4) side by side, so the column keeps its height
        var dialogButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var saveAs = CreateButton("Save as...", Ids.SaveAsButton, ShowSaveAs);
        saveAs.Margin = new Thickness(0, 0, 8, 12);
        dialogButtons.Children.Add(Report(Ids.SaveAsButton, saveAs));
        dialogButtons.Children.Add(Report(Ids.TaskDialogButton, CreateButton("Task dialog...", Ids.TaskDialogButton, ShowTaskDialog)));
        left.Children.Add(dialogButtons);

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
        if (_options.ChangeAfter != null)
        {
            _changePanel = new Label
            {
                Width = TimedPanelSize,
                Height = TimedPanelSize,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = SwatchBrush(ChangePanelBefore),
                Content = ChangePanelBeforeText
            };
            swatches.Children.Add(Report(Ids.ChangePanel, _changePanel, "Change panel"));
        }
        if (_options.AnimateFor != null)
        {
            _spinner = BuildSpinner();
            swatches.Children.Add(Report(Ids.Spinner, _spinner));
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

    private static SolidColorBrush SwatchBrush(Swatch swatch) => new(Color.FromRgb(swatch.R, swatch.G, swatch.B));

    /// <summary>
    /// The spinner: a dark hand on a light disc that <see cref="StartTimedChanges"/> turns. Shapes and a panel have no
    /// automation peer, so UI Automation does not see it; its rectangle is in the ready event.
    /// </summary>
    private FrameworkElement BuildSpinner()
    {
        var spinner = new Canvas { Width = TimedPanelSize, Height = TimedPanelSize };
        spinner.Children.Add(new Ellipse
        {
            Width = TimedPanelSize,
            Height = TimedPanelSize,
            Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0))
        });
        spinner.Children.Add(new Line
        {
            X1 = TimedPanelSize / 2.0,
            Y1 = TimedPanelSize / 2.0,
            X2 = TimedPanelSize / 2.0,
            Y2 = 4,
            Stroke = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x60)),
            StrokeThickness = 5,
            RenderTransform = _spinnerTurn
        });
        return spinner;
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
        // Only take keyboard focus now if the window was shown activated: SetFocus activates a window. Without activation
        // nothing here focuses anything (see the constructor), so the window stays behind the one in front
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

    /// <summary>
    /// Logs every press in the window, with the pixel it went down at (roadmap M4.2: a click lands on its pixel) and the
    /// modifier keys held (roadmap M4.3).
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The button message's own point: exact physical pixels, with no DIP conversion in between
        var position = NativeMethods.GetMessagePos();
        _events.Write(
            Events.MouseDown,
            ("button", ButtonName(e.ChangedButton)),
            ("clickCount", e.ClickCount),
            ("x", (int)(short)(position & 0xFFFF)),
            ("y", (int)(short)(position >> 16)),
            ("id", AutomationIdAt(e.OriginalSource as DependencyObject)),
            ("modifiers", ModifierNames(Keyboard.Modifiers)));
    }

    /// <summary>Logs every release in the window: where a drag dropped, with the modifier keys held (roadmap M4.3).</summary>
    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var position = NativeMethods.GetMessagePos();
        _events.Write(
            Events.MouseUp,
            ("button", ButtonName(e.ChangedButton)),
            ("x", (int)(short)(position & 0xFFFF)),
            ("y", (int)(short)(position >> 16)),
            ("id", AutomationIdAt(e.OriginalSource as DependencyObject)),
            ("modifiers", ModifierNames(Keyboard.Modifiers)));
    }

    internal static string ButtonName(MouseButton button) => button.ToString().ToLowerInvariant().Replace("xbutton", "x");

    /// <summary>The AutomationId of <paramref name="element"/>, or of the nearest element above it that has one; empty when none has.</summary>
    private static string AutomationIdAt(DependencyObject? element)
    {
        var id = "";
        for (; element != null && id.Length == 0;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            id = AutomationProperties.GetAutomationId(element) ?? "";
        }

        return id;
    }

    /// <summary>"ctrl", "shift", "alt" and "win" joined by '+', in that order; empty for none.</summary>
    private static string ModifierNames(ModifierKeys modifiers) => string.Join("+", new[]
    {
        (ModifierKeys.Control, "ctrl"), (ModifierKeys.Shift, "shift"), (ModifierKeys.Alt, "alt"), (ModifierKeys.Windows, "win")
    }.Where(m => modifiers.HasFlag(m.Item1)).Select(m => m.Item2));

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // Shown: tests may activate it from now on
        _noActivateWhileShown?.Release();
        _noActivateWhileShown = null;

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

        StartTimedChanges();

        if (_options.OpenDocument)
        {
            OpenDocument();
        }
        if (_options.OpenTable)
        {
            OpenTable();
        }
        if (_options.OpenMenuWindow)
        {
            OpenMenuWindow();
        }
        if (_options.OpenWebView)
        {
            OpenWebView();
        }

        // The overlay before the canvas: on a monitor at another scale the canvas hooks the windows created after it until
        // it has rendered (NoActivateWhileShown), and the overlay's topmost style must not go through that hook
        if (_options.OpenOverlay)
        {
            OpenOverlay();
        }
        if (_options.OpenCanvas)
        {
            OpenCanvas();
        }
        if (_options.OpenModal)
        {
            // After the ready event: ShowDialog does not return until the dialog closes.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OpenModal));
        }
    }

    /// <summary>
    /// Starts the change panel's one change and the spinner (roadmap M6.4), timed from the ready event. They write to the
    /// event log only, not through <see cref="Log"/>: the status text must not change too, or a pixel check would see a
    /// second region change.
    /// </summary>
    private void StartTimedChanges()
    {
        if (_options.ChangeAfter is { } changeAfter && _changePanel is { } panel)
        {
            var change = new DispatcherTimer { Interval = changeAfter };
            change.Tick += (_, _) =>
            {
                change.Stop();
                panel.Background = SwatchBrush(ChangePanelAfter);
                panel.Content = ChangePanelAfterText;
                _events.Write(Events.PanelChanged, ("id", Ids.ChangePanel), ("rect", ScreenRect(panel)));
            };
            change.Start();
        }

        if (_options.AnimateFor is { } animateFor && _spinner is { } spinner)
        {
            var running = Stopwatch.StartNew();
            var turn = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SpinnerIntervalMs) };
            turn.Tick += (_, _) =>
            {
                if (running.Elapsed < animateFor)
                {
                    _spinnerTurn.Angle = (_spinnerTurn.Angle + 30) % 360;
                    return;
                }

                // Still from here on, at its starting angle
                turn.Stop();
                _spinnerTurn.Angle = 0;
                _events.Write(Events.AnimationStopped, ("id", Ids.Spinner));
            };
            _events.Write(
                Events.AnimationStarted,
                ("id", Ids.Spinner),
                ("rect", ScreenRect(spinner)),
                ("durationMs", (long)animateFor.TotalMilliseconds));
            turn.Start();
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

    /// <summary>
    /// Shows the common Save As dialog (IFileSaveDialog, through WPF's SaveFileDialog) modal on this window, then writes
    /// <see cref="SavedFileText"/> to the path it returns, but only in the fixture's save folder (<see cref="FixtureSaveFolder"/>).
    /// The dialog creates no test file and adds nothing to the recent files.
    /// </summary>
    private void ShowSaveAs()
    {
        var folder = FixtureSaveFolder.For(_options);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = SaveAsTitle(_options.Instance),
            FileName = "fixture.txt",
            DefaultExt = ".txt",
            Filter = "Text files (*.txt)|*.txt",
            OverwritePrompt = true,
            CreateTestFile = false,
            AddToRecent = false
        };
        if (folder != null)
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) != true)
        {
            Log(Events.SaveDialogClosed, ("result", "cancel"));
            return;
        }

        var path = dialog.FileName;
        if (!FixtureSaveFolder.Allows(folder, path, NativeMethods.LongPath))
        {
            Log(Events.SaveDialogClosed, ("result", "refused"), ("path", path));
            return;
        }

        try
        {
            File.WriteAllText(path, SavedFileText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(Events.SaveDialogClosed, ("result", "failed"), ("path", path), ("message", ex.Message));
            return;
        }

        Log(Events.SaveDialogClosed, ("result", "saved"), ("path", path));
    }

    /// <summary>Shows the task dialog (<see cref="FixtureTaskDialog"/>) modal on this window and logs the button that closed it.</summary>
    private void ShowTaskDialog()
    {
        int pressed;
        try
        {
            pressed = FixtureTaskDialog.Show(
                new WindowInteropHelper(this).Handle,
                _options.Instance,
                dialog => _events.Write(Events.TaskDialogOpened, ("hwnd", (long)dialog)));
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or EntryPointNotFoundException or DllNotFoundException)
        {
            // Logged as a close, so a test waiting for the dialog fails with the reason instead of a timeout
            Log(Events.TaskDialogClosed, ("button", 0), ("message", ex.Message));
            return;
        }

        Log(Events.TaskDialogClosed, ("button", pressed));
    }

    private void OpenTable()
    {
        if (_table != null)
        {
            return;
        }

        _table = new TableWindow(_options, _events);
        _table.Closed += (_, _) => _table = null;
        _table.Show();
    }

    private void OpenMenuWindow()
    {
        if (_menuWindow != null)
        {
            return;
        }

        _menuWindow = new MenuWindow(_options, _events);
        _menuWindow.Closed += (_, _) => _menuWindow = null;
        _menuWindow.Show();
    }

    /// <summary>Opens the WebView2 window: the only place the fixture starts WebView2 (see <see cref="WebViewWindow"/>).</summary>
    private void OpenWebView()
    {
        if (_webViewWindow != null)
        {
            return;
        }

        _webViewWindow = new WebViewWindow(_options, _events);
        _webViewWindow.Closed += (_, _) => _webViewWindow = null;
        _webViewWindow.Show();
    }

    /// <summary>Opens the topmost overlay (roadmap M6.7, see <see cref="OverlayWindow"/>).</summary>
    private void OpenOverlay()
    {
        if (_overlay != null)
        {
            return;
        }

        _overlay = new OverlayWindow(_options, _events);
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    /// <summary>Opens the canvas window (roadmap M6.3, see <see cref="CanvasWindow"/>).</summary>
    private void OpenCanvas()
    {
        if (_canvas != null)
        {
            return;
        }

        _canvas = new CanvasWindow(_options, _events);
        _canvas.Closed += (_, _) => _canvas = null;
        _canvas.Show();
    }

    private void Log(string name, params (string Key, JsonNode? Value)[] fields)
    {
        _statusText.Text = name;
        _events.Write(name, fields);
    }
}
