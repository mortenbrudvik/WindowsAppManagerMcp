using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// respond_to_dialog and element_action's menu_path (roadmap M3.5) against the fixture, input tier: the document's save
/// prompt is a Win32 message box (#32770, WPF's MessageBox), answered by its standard id; the WPF modal dialog is answered
/// by its button's text; the context menu's Delete is invoked by a one-item menu path. Against the M3.F4 surfaces: the task
/// dialog (TaskDialogIndirect) is answered by its common button's CommandButton_2 id and by a command link's text, and a
/// three-item menu path opens the menu window's Win32 menu bar and its submenu, each a #32768 popup window. Every press goes through
/// element_action's service, which the harness lets act only on the fixture's elements.
/// </summary>
public class FixtureDialogTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public FixtureDialogTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Log(DialogResponseResult result) =>
        _output.WriteLine($"  success={result.Success} dialog={result.DialogHandle} title='{result.Title}' text=[{string.Join(" | ", result.Text ?? [])}] " +
                          $"buttons=[{string.Join(", ", (result.Buttons ?? []).Select(b => $"{b.Name}/{b.AutomationId}/{b.Standard}"))}] " +
                          $"invoked={result.Invoked?.Name} closed={result.DialogClosed} next={result.NextDialog} error={result.ErrorCode}: {result.Error}");

    [DesktopFact]
    public async Task TheDocumentsSavePrompt_IsAnsweredCancel_ByItsStandardId_AndTheDocumentStaysOpen()
    {
        // Arrange - closing the document with unsaved changes shows the fixture's save prompt (Yes, No, Cancel)
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenDocument = true });
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        var closing = await e2e.Server.Get<WindowManagerTools>().CloseWindow(document, waitMs: 10_000);
        _output.WriteLine($"close_window {document}: {closing}");
        e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentClosePrompted, e => e.GetHandle("hwnd") == document);

        // Act - name the document: its modal prompt is the dialog answered
        var result = await e2e.Server.Get<IDialogService>().RespondAsync(new DialogResponseRequest(document, "cancel", Timeout: Timeout));
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().NotBe((long)document, "the prompt is a window of its own");
        result.Title.Should().StartWith(FixtureContract.SavePromptTitlePrefix);
        result.Text.Should().Contain(t => t.Contains("save changes"));
        result.Buttons!.Select(b => b.Standard).Should().Contain(["yes", "no", "cancel"], "a message box's buttons carry IDYES, IDNO, IDCANCEL");
        result.Invoked!.Standard.Should().Be("cancel");
        result.DialogClosed.Should().BeTrue();
        e2e.Server.Windows.IsValidWindow(document).Should().BeTrue("Cancel keeps the document open");
    }

    [DesktopFact]
    public async Task TheWpfModal_IsAnsweredByItsButtonsText_FromItsDisabledOwner()
    {
        // Arrange - open the modal through its button (element_action invoke)
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var search = e2e.Server.Get<IElementSearchService>();
        var open = await search.FindAsync(new ElementQuery(main, AutomationId: FixtureContract.Ids.OpenModalButton, Timeout: Timeout));
        open.Success.Should().BeTrue(open.Error);
        var opened = await e2e.Server.Get<IElementActionService>().ActAsync(
            new ElementActionRequest(open.Elements!.Single().Ref!, ElementActionKind.Invoke, Timeout: Timeout));
        opened.Success.Should().BeTrue(opened.Error);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ModalOpened);

        // Act - name the main window: the modal that disables it is answered; "Cancel" has no standard id in WPF, so its text decides
        var result = await e2e.Server.Get<IDialogService>().RespondAsync(new DialogResponseRequest(main, "Cancel", Timeout: Timeout));
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Invoked!.AutomationId.Should().Be(FixtureContract.Ids.ModalCancelButton);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ModalClosed, e => e.GetString("result") == "cancel");
    }

    /// <summary>Shows the task dialog through its button (element_action invoke) and returns it once it is shown.</summary>
    private async Task<nint> OpenTaskDialogAsync(E2ESession e2e)
    {
        var main = e2e.Fixture.MainWindowHandle;
        var button = await e2e.Server.Get<IElementSearchService>().FindAsync(
            new ElementQuery(main, AutomationId: FixtureContract.Ids.TaskDialogButton, Timeout: Timeout));
        button.Success.Should().BeTrue(button.Error);
        var invoked = await e2e.Server.Get<IElementActionService>().ActAsync(
            new ElementActionRequest(button.Elements.Should().ContainSingle().Which.Ref!, ElementActionKind.Invoke, Timeout: Timeout));
        invoked.Success.Should().BeTrue(invoked.Error);

        // The dialog exists at TDN_CREATED and is shown right after; it closes at once when it cannot be shown
        var opened = e2e.Fixture.WaitForAnyEvent([FixtureContract.Events.TaskDialogOpened, FixtureContract.Events.TaskDialogClosed], Timeout);
        opened.Name.Should().Be(FixtureContract.Events.TaskDialogOpened, "the task dialog could not be shown: {0}", opened.GetString("message"));
        var dialog = opened.GetHandle("hwnd");
        Wait.ForValue(() => e2e.Server.Windows.FindWindows(handle: dialog).FirstOrDefault(), Timeout)
            .Should().NotBeNull("the task dialog 0x{0:X} is shown after it is created", dialog);
        _output.WriteLine($"Task dialog 0x{dialog:X}: '{E2ENative.GetWindowText(dialog)}' ({E2ENative.GetClassName(dialog)}).");
        return dialog;
    }

    [DesktopFact]
    public async Task ATaskDialog_IsAnsweredCancel_ByItsCommonButtonsCommandButtonId()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var dialog = await OpenTaskDialogAsync(e2e);

        // Act
        var result = await e2e.Server.Get<IDialogService>().RespondAsync(new DialogResponseRequest(dialog, "cancel", Timeout: Timeout));
        Log(result);

        // Assert - a task dialog's common button carries CommandButton_ and its standard id; its command links are buttons too
        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().Be((long)dialog);
        result.Title.Should().Be(FixtureContract.TaskDialogTitle(e2e.Fixture.Instance));
        result.Text.Should().Contain(t => t.Contains(FixtureContract.TaskDialogInstruction));
        result.Buttons.Should().Contain(b => b.AutomationId == DialogButtons.TaskDialogPrefix + FixtureContract.TaskDialogLinks.CancelCommand && b.Standard == "cancel");
        result.Buttons.Should().Contain(b => b.Name != null && b.Name.Contains(FixtureContract.TaskDialogLinks.Keep));
        result.Buttons.Should().Contain(b => b.Name != null && b.Name.Contains(FixtureContract.TaskDialogLinks.Discard));
        result.Invoked!.Standard.Should().Be("cancel");
        result.DialogClosed.Should().BeTrue();
        e2e.Fixture.WaitForEvent(FixtureContract.Events.TaskDialogClosed).GetInt64("button").Should().Be(FixtureContract.TaskDialogLinks.CancelCommand);
    }

    [DesktopFact]
    public async Task ATaskDialogsCommandLink_IsPressedByItsText()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var dialog = await OpenTaskDialogAsync(e2e);

        // Act - a command link has no standard id: its text names it
        var result = await e2e.Server.Get<IDialogService>().RespondAsync(
            new DialogResponseRequest(dialog, FixtureContract.TaskDialogLinks.Discard, Timeout: Timeout));
        Log(result);

        // Assert - TaskDialogIndirect returns the link's own id
        result.Success.Should().BeTrue(result.Error);
        result.Invoked!.Name.Should().Contain(FixtureContract.TaskDialogLinks.Discard);
        result.Invoked.Standard.Should().BeNull("a command link is no standard button");
        result.DialogClosed.Should().BeTrue();
        e2e.Fixture.WaitForEvent(FixtureContract.Events.TaskDialogClosed).GetInt64("button").Should().Be(FixtureContract.TaskDialogLinks.DiscardCommand);
    }

    [DesktopFact]
    public async Task AMenuPath_OpensTheWin32MenuBarsMenuAndSubmenu_EachA32768Popup_AndRunsTheCommand()
    {
        // Arrange - the menu window's bar is a real HMENU; a Win32 menu opens from the window in front
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenMenuWindow = true });
        var window = e2e.Fixture.WaitForEvent(FixtureContract.Events.MenuWindowOpened).GetHandle("hwnd");
        e2e.Server.Foreground.EnsureForeground(window).IsForeground.Should().BeTrue();
        var path = FixtureContract.Win32Menu.CommandPaths[FixtureContract.Win32Menu.ExportAsTextCommand];

        // Act
        var result = await e2e.Server.Get<IMenuPathService>().RunAsync(new MenuPathRequest(window, null, MenuPaths.Parse(path, out _)!, Timeout: Timeout));
        _output.WriteLine($"menu_path {path}: success={result.Success} ref={result.Ref} method={result.Method} error={result.ErrorCode}: {result.Error}");
        foreach (var step in result.Steps ?? [])
        {
            _output.WriteLine($"  {step.Item}: ref={step.Ref} method={step.Method} popup={step.OpenedPopup} " +
                              $"class now={(step.OpenedPopup is { } popup ? E2ENative.GetClassName((nint)popup) : null)}");
        }

        // Assert - File and Export each opened a menu window of their own, and the fixture ran the command
        result.Success.Should().BeTrue(result.Error);
        result.Steps!.Select(s => s.Item).Should().Equal(
            FixtureContract.Win32Menu.File, FixtureContract.Win32Menu.Export, FixtureContract.Win32Menu.ExportAsText);
        foreach (var step in result.Steps!.Take(2))
        {
            step.OpenedPopup.Should().NotBeNull("'{0}' opens a Win32 submenu, a popup window of its own", step.Item);
        }

        var command = e2e.Fixture.WaitForEvent(FixtureContract.Events.MenuCommand);
        command.GetInt64("command").Should().Be(FixtureContract.Win32Menu.ExportAsTextCommand);
        command.GetString("path").Should().Be(path);
        e2e.Fixture.Events.ReadAll().Count(e => e.Name == FixtureContract.Events.MenuCommand)
            .Should().Be(1, "only the last item of a path is invoked");
    }

    [DesktopFact]
    public async Task AMenuPath_InvokesTheOpenContextMenusItem()
    {
        // Arrange - right-click the menu target in the fixture, in front
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var main = e2e.Fixture.MainWindowHandle;
        var target = e2e.Fixture.Ready.Element(FixtureContract.Ids.ContextMenuTarget);
        e2e.Server.Foreground.EnsureForeground(main).IsForeground.Should().BeTrue();
        var click = e2e.Server.Input.RightClick(target.CenterX, target.CenterY, InputTarget.Window(main), releaseHeldModifiers: false, CancellationToken.None);
        click.Success.Should().BeTrue(click.Error);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ContextMenuOpened);

        // Act - the default scope finds the item in the open menu while the fixture is in front
        var result = await e2e.Server.Get<IMenuPathService>().RunAsync(new MenuPathRequest(main, null, MenuPaths.Parse("Delete", out _)!, Timeout: Timeout));
        _output.WriteLine($"menu_path: success={result.Success} ref={result.Ref} method={result.Method} steps={result.Steps?.Count} error={result.ErrorCode}: {result.Error}");

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Steps.Should().ContainSingle().Which.Item.Should().Be("Delete");
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ContextMenuItemClicked, e => e.GetString("id") == FixtureContract.Ids.ContextMenuDelete);
    }
}
