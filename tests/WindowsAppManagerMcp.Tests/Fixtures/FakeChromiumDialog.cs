namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A Chrome or Edge window with a JavaScript dialog open, as UIA3 read it from Chrome 155 and Edge 155 on a real desktop on
/// 2026-10-15 (roadmap M5.8, dialogs), in English and, through the throwaway profile's own Local State, Norwegian. Chromium
/// draws the dialog as views inside the browser window: no window of its own, no #32770. Only the names change with the
/// language; the classes, control types and the order of the buttons (OK first) do not.
/// <code>
/// Window "{page} - Google Chrome" (Chrome_WidgetWin_1, Win32)
///   Pane (Intermediate D3D Window, Win32)
///   Pane "This page says" (BrowserRootView)        while the dialog is open, the control view lists only the dialog here
///     Window "This page says" (RootView)
///       Pane (NonClientView) > Pane (BubbleFrameView)
///         Pane (BoxLayoutView) > Text "This page says" (Label)              the heading, outside the dialog's view
///         Pane (DialogClientView)
///           Pane (JavaScriptTabModalDialogViewViews) > Pane (MessageBoxView)
///             alert, confirm: Pane (ScrollView) > Pane (ScrollView::Viewport) > Pane (BoxLayoutView) > Text "{message}" (Label)
///             prompt: Pane (ScrollView), Edit "{message}" (Textfield, Value: the default text)
///           Chrome: Pane (DialogClientView::ButtonRowContainer) > Pane (View), Pane (View), Button "OK", Button "Cancel" (MdTextButton)
///           Edge:   Button "OK", Button "Cancel" (MdTextButton), right in the DialogClientView
/// </code>
/// </summary>
public sealed class FakeChromiumDialog
{
    /// <param name="kind">alert, confirm or prompt.</param>
    /// <param name="edge">Edge's shape (its buttons right in the DialogClientView), else Chrome's.</param>
    /// <param name="norwegian">The Norwegian UI's names.</param>
    /// <param name="message">The page's message.</param>
    /// <param name="defaultPrompt">A prompt's default text.</param>
    /// <param name="swappedNames">The accept button named like the cancel one and the other way round: names must not decide.</param>
    public FakeChromiumDialog(
        string kind, bool edge = false, bool norwegian = false, string message = "Delete it?", string defaultPrompt = "Ola",
        bool swappedNames = false)
    {
        var heading = norwegian ? "Denne siden sier" : "This page says";
        var ok = norwegian ? "OK" : "OK";
        var cancel = norwegian ? "Avbryt" : "Cancel";
        if (swappedNames)
            (ok, cancel) = (cancel, ok);

        Message = message;
        Ok = Button(ok);
        Cancel = kind == "alert" ? null : Button(cancel);
        var box = View("Pane", null, "MessageBoxView");
        if (kind == "prompt")
        {
            Field = new FakeUiaNode("Edit", message)
            {
                ClassName = "Textfield", FrameworkId = "Chrome", ActionPatterns = ["Value"], IsKeyboardFocusable = true, HasKeyboardFocus = true,
                Bounds = new UiaRect(438, 173, 408, 37)
            }.WithPattern<IUiaValuePattern>(Prompt = new FakeValuePattern(defaultPrompt));
            box.With(View("Pane", null, "ScrollView"), Field);
        }
        else
        {
            box.With(View("Pane", null, "ScrollView").With(View("Pane", null, "ScrollView::Viewport").With(
                View("Pane", null, "BoxLayoutView").With(View("Text", message, "Label")))));
        }

        DialogView = View("Pane", null, BrowserDialogService.DialogViewClass).With(box);
        var buttons = Cancel == null ? new[] { Ok } : [Ok, Cancel];
        var client = View("Pane", null, BrowserDialogService.ClientViewClass).With(DialogView);
        if (edge)
            client.With(buttons);
        else
            client.With(View("Pane", null, "DialogClientView::ButtonRowContainer").With(
                [View("Pane", null, "View"), View("Pane", null, "View"), .. buttons]));

        Dialog = new FakeUiaNode("Window", heading) { ClassName = "RootView", FrameworkId = "Chrome", Bounds = new UiaRect(411, 88, 463, 160) }.With(
            View("Pane", null, "NonClientView").With(View("Pane", null, "BubbleFrameView").With(
                View("Pane", null, "BoxLayoutView").With(View("Text", heading, "Label")),
                client)));
        RootView = View("Pane", heading, "BrowserRootView").With(Dialog);
        Window = new FakeUiaNode("Window", "WAM dialog - Google Chrome")
        {
            ClassName = "Chrome_WidgetWin_1", FrameworkId = "Win32", Bounds = new UiaRect(10, 10, 1265, 1372)
        }.With(new FakeUiaNode("Pane") { ClassName = "Intermediate D3D Window", FrameworkId = "Win32" }, RootView);
    }

    public FakeUiaNode Window { get; }

    public FakeUiaNode RootView { get; }

    /// <summary>The dialog's own root view (a Window control of class RootView).</summary>
    public FakeUiaNode Dialog { get; }

    public FakeUiaNode DialogView { get; }

    public FakeUiaNode Ok { get; }

    public FakeUiaNode? Cancel { get; }

    public FakeUiaNode? Field { get; }

    public FakeValuePattern? Prompt { get; }

    public string Message { get; }

    /// <summary>The dialog closes, as after a press: its views leave the window.</summary>
    public void Close() => Dialog.Vanish();

    private static FakeUiaNode Button(string name) => new FakeUiaNode("Button", name)
    {
        ClassName = "MdTextButton", FrameworkId = "Chrome", ActionPatterns = ["Invoke"], IsKeyboardFocusable = true, Bounds = new UiaRect(782, 181, 64, 39)
    };

    private static FakeUiaNode View(string controlType, string? name, string className) =>
        new(controlType, name) { ClassName = className, FrameworkId = "Chrome", Bounds = new UiaRect(418, 137, 448, 103) };
}
