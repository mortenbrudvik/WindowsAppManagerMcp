namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// file_dialog (roadmap M3.14) end to end against the fixture's Save As dialog (roadmap M3.F4), input tier: the main window's
/// Save As button shows the common IFileSaveDialog (WPF's SaveFileDialog) modal on the main window, file_dialog types a path
/// in the test's own folder into its file name box by its control id, presses Save, answers the replace prompt when asked
/// to, and sees the file the fixture then writes. The fixture writes only into that folder (FixtureSaveFolder), and the
/// dialog creates no test file of its own, so nothing is written anywhere else. Every action goes through element_action's
/// service, which the harness lets act only on the fixture's elements.
/// </summary>
public class FixtureFileDialogTests
{
    /// <summary>The bound of each search and action of the steps that open the dialog.</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The bound of the whole file_dialog call, the wait for the dialog to close and the file included.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;

    public FixtureFileDialogTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private void Log(FileDialogResult result) =>
        _output.WriteLine($"file_dialog: success={result.Success} dialog={result.DialogHandle} completed={result.Completed} " +
                          $"path='{result.FinalPath}' promptHandled={result.PromptHandled} prompt='{result.Prompt?.Title}' " +
                          $"warnings={string.Join(" | ", result.Warnings ?? [])} error={result.ErrorCode}: {result.Error}");

    /// <summary>Opens the Save As dialog through its button (element_action invoke) and returns it once it is shown.</summary>
    private static async Task<nint> OpenSaveAsAsync(E2ESession e2e)
    {
        var main = e2e.Fixture.MainWindowHandle;
        var button = await e2e.Server.Get<IElementSearchService>().FindAsync(
            new ElementQuery(main, AutomationId: FixtureContract.Ids.SaveAsButton, Timeout: StepTimeout));
        button.Success.Should().BeTrue(button.Error);
        var invoked = await e2e.Server.Get<IElementActionService>().ActAsync(
            new ElementActionRequest(button.Elements.Should().ContainSingle().Which.Ref!, ElementActionKind.Invoke, Timeout: StepTimeout));
        invoked.Success.Should().BeTrue(invoked.Error);
        e2e.Fixture.WaitForEvent(FixtureContract.Events.ButtonClicked, e => e.GetString("id") == FixtureContract.Ids.SaveAsButton);

        // Modal on the main window: respond_to_dialog's Locate finds it as the main window's enabled popup once it is shown
        var title = FixtureContract.SaveAsTitle(e2e.Fixture.Instance);
        var dialogs = e2e.Server.Get<IDialogService>();
        var located = Wait.ForValue(
            () => dialogs.Locate(main) is { Found: true } found && E2ENative.GetWindowText(found.Dialog) == title ? found : null,
            StepTimeout);
        located.Should().NotBeNull($"the Save As dialog '{title}' opens modal on the main window");
        return located!.Dialog;
    }

    [DesktopFact]
    public async Task FileDialog_Save_TypesAPathInTheTestsFolder_PressesSave_AndSeesTheFileTheFixtureWrites()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var dialog = await OpenSaveAsAsync(e2e);
        var path = Path.Combine(e2e.Fixture.WorkDirectory, "saved-by-file-dialog.txt");

        // Act - name the main window: its modal file dialog is the one completed
        var result = await e2e.Server.Get<IFileDialogService>().CompleteAsync(
            new FileDialogRequest(e2e.Fixture.MainWindowHandle, path, FileDialogAction.Save, Timeout: Timeout));
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().Be((long)dialog);
        result.FinalPath.Should().Be(path);
        result.PromptHandled.Should().BeFalse("no file was at the path, so the dialog asked nothing");
        result.Completed.Should().BeTrue("the dialog closed after Save and the fixture wrote the file");
        var closed = e2e.Fixture.WaitForEvent(FixtureContract.Events.SaveDialogClosed);
        closed.GetString("result").Should().Be("saved", "the path is in the fixture's own folder");
        Path.GetFileName(closed.GetString("path")).Should().Be(Path.GetFileName(path));
        File.ReadAllText(path).Should().Be(FixtureContract.SavedFileText);
    }

    [DesktopFact]
    public async Task FileDialog_SaveWithOverwrite_AnswersTheReplacePromptYes_AndTheFileIsRewritten()
    {
        // Arrange - a file at the path, so the dialog asks whether to replace it
        using var e2e = E2ESession.Start(DesktopTier.Input, _output);
        var path = Path.Combine(e2e.Fixture.WorkDirectory, "replaced-by-file-dialog.txt");
        File.WriteAllText(path, "An older version.");
        var dialog = await OpenSaveAsAsync(e2e);

        // Act
        var result = await e2e.Server.Get<IFileDialogService>().CompleteAsync(
            new FileDialogRequest(e2e.Fixture.MainWindowHandle, path, FileDialogAction.Save, Overwrite: true, Timeout: Timeout));
        Log(result);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().Be((long)dialog);
        result.PromptHandled.Should().BeTrue("the dialog asked to replace the file, and overwrite answers it Yes");
        result.Completed.Should().BeTrue("the fixture rewrote the file after the dialog closed");
        e2e.Fixture.WaitForEvent(FixtureContract.Events.SaveDialogClosed).GetString("result").Should().Be("saved");
        File.ReadAllText(path).Should().Be(FixtureContract.SavedFileText);
    }
}
