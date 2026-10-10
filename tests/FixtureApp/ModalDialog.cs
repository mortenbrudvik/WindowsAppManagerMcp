using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// A modal dialog owned by the main window. While it is open (ShowDialog) the main window is disabled,
/// which is what the BlockedByModal checks of the input guard look for.
/// </summary>
internal sealed class ModalDialog : Window
{
    public ModalDialog(FixtureOptions options, FixtureEventLog events)
    {
        Title = ModalTitle(options.Instance);
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowActivated = !options.NoActivate;

        var textBox = new TextBox { Width = 260, Margin = new Thickness(0, 8, 0, 12) };
        AutomationProperties.SetAutomationId(textBox, Ids.ModalTextBox);
        AutomationProperties.SetName(textBox, "Modal text box");
        textBox.TextChanged += (_, _) => events.Write(Events.TextChanged, ("id", Ids.ModalTextBox), ("text", textBox.Text));

        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(ok, Ids.ModalOkButton);
        ok.Click += (_, _) =>
        {
            events.Write(Events.ButtonClicked, ("id", Ids.ModalOkButton));
            DialogResult = true;
        };

        // IsCancel closes the dialog with DialogResult false
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        AutomationProperties.SetAutomationId(cancel, Ids.ModalCancelButton);
        cancel.Click += (_, _) => events.Write(Events.ButtonClicked, ("id", Ids.ModalCancelButton));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "A modal dialog: the main window is disabled until it closes." });
        panel.Children.Add(textBox);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => FocusManager.SetFocusedElement(this, textBox);
        ContentRendered += (_, _) => events.Write(
            Events.ModalOpened,
            ("hwnd", (long)new WindowInteropHelper(this).Handle),
            ("ownerHwnd", Owner == null ? 0L : (long)new WindowInteropHelper(Owner).Handle));
    }
}
