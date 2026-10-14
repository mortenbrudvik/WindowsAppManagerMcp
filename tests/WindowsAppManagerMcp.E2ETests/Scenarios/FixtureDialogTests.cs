using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// respond_to_dialog and element_action's menu_path (roadmap M3.5) against the fixture, input tier: the document's save
/// prompt is a Win32 message box (#32770, WPF's MessageBox), answered by its standard id; the WPF modal dialog is answered
/// by its button's text; the context menu's Delete is invoked by a one-item menu path; a three-item menu path opens the
/// menu window's Win32 menu bar and its submenu, each a #32768 popup window (roadmap M3.F4). Every press goes through
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
