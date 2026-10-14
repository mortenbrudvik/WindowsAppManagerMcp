using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// A separate, unowned top-level window holding a "document" that starts with unsaved changes. Closing it
/// (for example with WM_CLOSE) shows a Yes/No/Cancel save prompt, like Notepad does, until it is saved.
/// That is what verified close and prompt detection need to be tested against.
/// </summary>
internal sealed class DocumentWindow : Window
{
    private readonly FixtureOptions _options;
    private readonly FixtureEventLog _events;
    private bool _dirty = true;
    private string _closeResult = "clean";

    public DocumentWindow(FixtureOptions options, FixtureEventLog events)
    {
        _options = options;
        _events = events;

        Title = DocumentTitle(options.Instance, dirty: true);
        Width = 480;
        Height = 320;
        FixturePlacement.CenterOnPrimaryMonitor(this);
        ShowActivated = !options.NoActivate;

        var text = new TextBox
        {
            Text = "This document has unsaved changes.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(8)
        };
        AutomationProperties.SetAutomationId(text, Ids.DocumentTextBox);
        AutomationProperties.SetName(text, "Document text");
        text.TextChanged += (_, _) =>
        {
            events.Write(Events.TextChanged, ("id", Ids.DocumentTextBox), ("text", text.Text));
            SetDirty(true);
        };

        var save = new Button
        {
            Content = "Save",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8)
        };
        AutomationProperties.SetAutomationId(save, Ids.DocumentSaveButton);
        save.Click += (_, _) =>
        {
            events.Write(Events.ButtonClicked, ("id", Ids.DocumentSaveButton));
            Save();
        };

        var panel = new DockPanel();
        DockPanel.SetDock(save, Dock.Bottom);
        panel.Children.Add(save);
        panel.Children.Add(text);
        Content = panel;

        // The text box has keyboard focus when the window is activated, so typing into the document reaches it
        FocusManager.SetFocusedElement(this, text);

        ContentRendered += (_, _) => events.Write(Events.DocumentOpened, ("hwnd", (long)Handle));
        Closing += OnClosing;
        Closed += (_, _) => events.Write(Events.DocumentClosed, ("result", _closeResult));
    }

    private nint Handle => new WindowInteropHelper(this).Handle;

    private void Save()
    {
        SetDirty(false);
        _events.Write(Events.DocumentSaved);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        Title = DocumentTitle(_options.Instance, dirty);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }

        if (MainWindow.ShuttingDown)
        {
            // The fixture is exiting: never block that on a prompt.
            _closeResult = "discarded";
            return;
        }

        _events.Write(Events.DocumentClosePrompted, ("hwnd", (long)Handle));
        var answer = MessageBox.Show(
            this,
            "Do you want to save changes to Untitled?",
            SavePromptTitle(_options.Instance),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                Save();
                _closeResult = "saved";
                break;
            case MessageBoxResult.No:
                _closeResult = "discarded";
                break;
            default:
                e.Cancel = true;
                break;
        }
    }
}
