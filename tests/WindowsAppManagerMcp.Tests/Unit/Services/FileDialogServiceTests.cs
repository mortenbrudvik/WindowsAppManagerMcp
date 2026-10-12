namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// file_dialog's service (roadmap M3.14): the file name box and the default button found by their control ids (never a
/// caption), the path set and the button pressed only through element_action's service, the replace prompt answered by
/// overwrite through respond_to_dialog's service, other prompts handed back, and the refusals before anything is typed.
/// Windows, popups, the search, the action and the dialog service are fakes; the paths are in a temporary folder.
/// </summary>
public sealed class FileDialogServiceTests : IDisposable
{
    private static readonly nint Dialog = 0x5000;
    private static readonly nint Prompt = 0x6000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WamFileDialog", Guid.NewGuid().ToString("N"));
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IElementSearchService> _search = new();
    private readonly Mock<IElementActionService> _actions = new();
    private readonly Mock<IPopupService> _popups = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IInputTargetGuard> _guard = new();
    private readonly Mock<IInputSession> _session = new();
    private readonly HashSet<nint> _open = [Dialog];
    private readonly List<ElementActionRequest> _acted = [];
    private readonly List<ElementQuery> _queries = [];
    private readonly List<DialogResponseRequest> _answers = [];
    // A Save As dialog: the file name Edit (1001) inside the ComboBox FileNameControlHost; a search box elsewhere
    private IReadOnlyList<FoundElement> _edits = [Element("w2e2", "Search", "SearchEditBox", "Edit")];
    private IReadOnlyList<FoundElement> _combos = [Element("w2e4", "File name:", "FileNameControlHost", "ComboBox")];
    private Func<ElementActionRequest, ElementActionResult>? _press;
    private DialogContent? _promptContent;

    public FileDialogServiceTests()
    {
        Directory.CreateDirectory(_folder);
        _dialogs.Setup(d => d.Locate(It.IsAny<nint>())).Returns(new DialogLocation(Dialog));
        _dialogs.Setup(d => d.ReadAsync(Prompt, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DialogRead(_promptContent));
        _dialogs.Setup(d => d.RespondAsync(It.IsAny<DialogResponseRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DialogResponseRequest request, CancellationToken _) => _answers.Add(request))
            .ReturnsAsync((DialogResponseRequest request, CancellationToken _) =>
            {
                // Yes replaces the file: the prompt and the file dialog close; No closes only the prompt
                _open.Remove(Prompt);
                if (request.Button == "yes")
                {
                    _open.Remove(Dialog);
                }

                return new DialogResponseResult(true, (long)Prompt, DialogClosed: true);
            });
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Callback((ElementQuery query, CancellationToken _) => _queries.Add(query))
            .ReturnsAsync((ElementQuery query, CancellationToken _) =>
            {
                IReadOnlyList<FoundElement> found = query switch
                {
                    { AutomationId: FileDialogService.DefaultButtonId } => [Element("w2e9", "Speichern", "1", "Button")],
                    { WithinRef: not null } => [Element("w2e5", null, "1001", "Edit")],
                    { ControlTypes: ["ComboBox"] } => _combos,
                    _ => _edits
                };
                return new FindElementsResult(true, (long)query.WindowHandle, "Speichern unter", found.Count, false, found, 1);
            });
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _acted.Add(request))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) => request.Action == ElementActionKind.SetValue
                ? new ElementActionResult(true, "set_value", request.Ref, (long)Dialog, ElementActionStatus.Done, ElementActionMethod.Value,
                    OutcomeVerified: true)
                : Press(request));
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns((nint h) => _open.Contains(h));
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => _open.Contains(h));
        _popups.Setup(p => p.GetPopups(Dialog)).Returns(() => _open.Contains(Prompt)
            ? [new PopupWindow(Prompt, PopupKind.Dialog, "Speichern unter bestätigen", "#32770", 4242, Dialog, PopupRelation.Owned, null, true)]
            : []);
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>()))
            .Returns((InputTarget target) => InputGuardResult.Allow(target, target.WindowHandle ?? 0, null, target.WindowHandle ?? 0));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ElementActionResult Press(ElementActionRequest request)
    {
        if (_press != null)
        {
            return _press(request);
        }

        _open.Remove(Dialog);
        return new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.Done, ElementActionMethod.Invoke);
    }

    private FileDialogService CreateSut(IWindowService? windowService = null) => new(
        _dialogs.Object, _search.Object, _actions.Object, _popups.Object, _windows.Object, _guard.Object, _session.Object, windowService)
    {
        PollInterval = TimeSpan.FromMilliseconds(5)
    };

    private static FoundElement Element(string elementRef, string? name, string automationId, string controlType) =>
        new(elementRef, name, controlType, automationId, null, null, null, true, false, []);

    private static DialogContent PromptContent(params DialogButton[] buttons) =>
        new((long)Prompt, "Speichern unter bestätigen", "#32770", ["report.txt ist bereits vorhanden. Möchten Sie sie ersetzen?"], buttons);

    private static readonly DialogButton Yes = new("w3e1", "Ja", "CommandButton_6", "yes", true);
    private static readonly DialogButton No = new("w3e2", "Nein", "CommandButton_7", "no", true);

    private string NewFile => Path.Combine(_folder, "report.txt");

    private string ExistingFile()
    {
        File.WriteAllText(NewFile, "x");
        return NewFile;
    }

    private Task<FileDialogResult> CompleteAsync(string path, FileDialogAction action = FileDialogAction.Save, bool overwrite = false,
        TimeSpan? timeout = null) =>
        CreateSut().CompleteAsync(new FileDialogRequest(Dialog, path, action, overwrite, Timeout: timeout ?? TimeSpan.FromSeconds(5)));

    /// <summary>The press waits for the replace prompt it opened (a Win32 button's Invoke); the file may appear meanwhile.</summary>
    private void PressOpensThePrompt(bool fileAppears = false) => _press = request =>
    {
        if (fileAppears)
        {
            File.WriteAllText(NewFile, "x");
        }

        _open.Add(Prompt);
        return new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.InvokedPendingModal,
            ElementActionMethod.Invoke, OpenedPopup: new OpenedWindow((long)Prompt, "Speichern unter bestätigen", "#32770", 4242, "notepad"));
    };

    [Fact]
    public async Task Save_SetsTheFullPathByTheBoxsId_PressesButtonOne_AndWaitsForTheDialogToClose()
    {
        var result = await CompleteAsync(NewFile);

        result.Success.Should().BeTrue(result.Error);
        (result.Action, result.Completed, result.FinalPath, result.PromptHandled).Should().Be(("save", true, NewFile, false));
        result.DialogHandle.Should().Be((long)Dialog);
        _acted.Select(a => (a.Ref, a.Action, a.Value)).Should().Equal(
            ("w2e5", ElementActionKind.SetValue, NewFile), ("w2e9", ElementActionKind.Invoke, null));
        _acted[0].Mode.Should().Be(ElementValueMode.Value);
        _acted[1].Timeout.Should().BeLessThanOrEqualTo(FileDialogService.PressTimeout);
        _queries.Should().OnlyContain(q => q.WindowHandle == Dialog);
        _queries.Should().Contain(q => q.WithinRef == "w2e4");
        _queries.Should().Contain(q => q.AutomationId == "1" && q.ControlTypes!.Contains("Button"));
        _answers.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOpenDialogsEdit1148_IsUsed_WithoutItsComboBox()
    {
        _combos = [];
        _edits = [Element("w2e2", "Search", "SearchEditBox", "Edit"), Element("w2e1", "File name:", "1148", "Edit")];

        var result = await CompleteAsync(NewFile);

        result.Success.Should().BeTrue(result.Error);
        _acted[0].Ref.Should().Be("w2e1");
    }

    [Fact]
    public async Task ADialogWithoutAFileNameBox_IsDialogNotFound_AndNothingIsTyped()
    {
        _combos = [];
        _edits = [];

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
        result.Error.Should().Contain("no file name box");
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOrdinaryDialogWithAnEdit1001AndAnOkButton_IsNoFileDialog_AndNothingIsTyped()
    {
        // A rename or enter-a-value dialog: resource ids start at 1000, and its OK is id 1 as well
        _combos = [];
        _edits = [Element("w2e1", "New name:", "1001", "Edit")];

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task AFindDialogsEdit1152_IsNoFileDialog_ButAnOldStyleFileDialogsIs()
    {
        // The common Find dialog's Find What box is edt1 (1152) too; its IDOK is Find Next
        _combos = [];
        _edits = [Element("w2e1", "Find what:", "1152", "Edit")];

        var find = await CompleteAsync(NewFile);

        find.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
        _acted.Should().BeEmpty();

        // The old-style file dialog has it next to its drive ComboBox (cmb2 = 1137)
        _combos = [Element("w2e7", "Drives:", "1137", "ComboBox")];

        var old = await CompleteAsync(NewFile);

        old.Success.Should().BeTrue(old.Error);
        _acted[0].Ref.Should().Be("w2e1");
    }

    [Fact]
    public async Task ADialogTheGuardRefuses_IsStillNotedForTheAuditLine()
    {
        var windowService = new Mock<IWindowService>();
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) =>
            InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "Denied.", target));

        await CreateSut(windowService.Object).CompleteAsync(new FileDialogRequest(Dialog, NewFile, FileDialogAction.Save, Timeout: TimeSpan.FromSeconds(5)));

        windowService.Verify(w => w.GetWindowInfo(Dialog), Times.Once);
    }

    [Fact]
    public async Task ASaveThatClosesTheDialog_WithNoFileAtThePath_Warns()
    {
        var result = await CompleteAsync(NewFile);

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().ContainSingle(w => w.Contains("no file is at finalPath"));
    }

    [Fact]
    public async Task Save_WithOverwrite_AnswersTheReplacePromptYes_AndCompletes()
    {
        var file = ExistingFile();
        PressOpensThePrompt();
        _promptContent = PromptContent(Yes, No);

        var result = await CompleteAsync(file, overwrite: true);

        result.Success.Should().BeTrue(result.Error);
        result.Completed.Should().BeTrue();
        result.PromptHandled.Should().BeTrue();
        _answers.Should().ContainSingle().Which.Should().Be(new DialogResponseRequest(Prompt, "yes", false, _answers[0].Timeout));
    }

    [Fact]
    public async Task Save_WithoutOverwrite_AnswersAReplacePromptNo_AndReportsFileExists()
    {
        // The server saw no file when it checked the path, but the file was written before the application asked
        PressOpensThePrompt(fileAppears: true);
        _promptContent = PromptContent(Yes, No);

        var result = await CompleteAsync(NewFile);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.FileExists));
        (result.Completed, result.PromptHandled).Should().Be((false, true));
        _answers.Should().ContainSingle().Which.Button.Should().Be("no");
    }

    [Fact]
    public async Task Save_OfAnExistingFile_WithoutOverwrite_IsRefusedBeforeAnythingIsTyped()
    {
        var result = await CompleteAsync(ExistingFile());

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.FileExists));
        _dialogs.Verify(d => d.Locate(It.IsAny<nint>()), Times.Never);
        _acted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AYesNoPrompt_WhileNoFileIsAtThePath_IsNoReplacePrompt_AndIsHandedBackUnanswered(bool overwrite)
    {
        // "You don't have permission to save in this location. Save in the Pictures folder instead?"
        PressOpensThePrompt();
        _promptContent = PromptContent(Yes, No);

        var result = await CompleteAsync(NewFile, overwrite: overwrite);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotCompleted));
        result.Prompt.Should().Be(_promptContent);
        result.PromptHandled.Should().BeFalse();
        _answers.Should().BeEmpty();
    }

    [Fact]
    public async Task APromptTheGuardRefuses_IsNeitherReadNorAnswered()
    {
        // A credential prompt of another process, on the user's deny list
        PressOpensThePrompt();
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) => target.WindowHandle == Prompt
            ? InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "The user's policy denies CredentialUIBroker.", target)
            : InputGuardResult.Allow(target, target.WindowHandle ?? 0, null, target.WindowHandle ?? 0));

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(InputErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("neither read nor answered");
        result.Prompt.Should().BeNull();
        _dialogs.Verify(d => d.ReadAsync(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        _answers.Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherPrompt_IsHandedBack_Unanswered()
    {
        // An error box with only OK: "The file name is not valid", in the user's language
        PressOpensThePrompt();
        _promptContent = PromptContent(new DialogButton("w3e3", "OK", "2", "cancel", true));

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotCompleted));
        result.Prompt.Should().Be(_promptContent);
        result.Error.Should().Contain("respond_to_dialog");
        _answers.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_NeverAnswersAYesNoPrompt()
    {
        File.WriteAllText(NewFile, "x");
        PressOpensThePrompt();
        _promptContent = PromptContent(Yes, No);

        var result = await CompleteAsync(NewFile, FileDialogAction.Open);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotCompleted));
        _answers.Should().BeEmpty();
    }

    [Fact]
    public async Task ADialogThatStaysOpen_IsDialogNotCompleted_AfterTheTimeout()
    {
        _press = request => new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.Done, ElementActionMethod.Invoke);

        var result = await CompleteAsync(_folder + @"\new.txt", timeout: TimeSpan.FromMilliseconds(300));

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotCompleted));
        result.Error.Should().Contain("still open");
        result.FinalPath.Should().Be(Path.Combine(_folder, "new.txt"));
    }

    [Fact]
    public async Task APressStillRunningAtItsTimeout_IsWatched_AndCompletesWhenTheDialogCloses()
    {
        _press = request =>
        {
            _open.Remove(Dialog);
            return new ElementActionResult(false, "invoke", request.Ref, (long)Dialog, Error: "Invoke had not returned.", ErrorCode: "ActionTimedOut");
        };

        var result = await CompleteAsync(NewFile);

        result.Success.Should().BeTrue(result.Error);
        result.Warnings.Should().ContainSingle(w => w.Contains("had not returned"));
    }

    [Fact]
    public async Task ATargetTheGuardRefuses_IsNeitherReadNorTyped()
    {
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) =>
            InputGuardResult.Refuse(InputErrorCode.TargetIsAgentHost, "The dialog belongs to the agent's host.", target));

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(InputErrorCode.TargetIsAgentHost));
        _search.VerifyNoOtherCalls();
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedSetValue_PressesNothing()
    {
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _acted.Add(request))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) =>
                new ElementActionResult(false, "set_value", request.Ref, Error: "Input is paused.", ErrorCode: "PausedByUser"));

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be("PausedByUser");
        _acted.Should().ContainSingle();
    }

    [Fact]
    public async Task APathThatDoesNotReadBack_PressesNothing()
    {
        _actions.Setup(a => a.ActAsync(It.Is<ElementActionRequest>(r => r.Action == ElementActionKind.SetValue), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _acted.Add(request))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) => new ElementActionResult(true, "set_value", request.Ref,
                Status: ElementActionStatus.Done, Method: ElementActionMethod.Value, OutcomeVerified: false));

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotCompleted));
        _acted.Should().ContainSingle();
    }

    [Fact]
    public async Task WhileInputIsPaused_NothingIsRead()
    {
        _session.Setup(s => s.IsPaused).Returns(true);

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _dialogs.Verify(d => d.Locate(It.IsAny<nint>()), Times.Never);
    }

    [Theory]
    [InlineData(FileDialogAction.Save)]
    [InlineData(FileDialogAction.Open)]
    public async Task WithoutNamingTheDialog_IsInvalidArguments_AndNothingIsLocatedOrTyped(FileDialogAction action)
    {
        // The file dialog in front may be another application's, which would then save its document under the path
        File.WriteAllText(NewFile, "x");

        var result = await CreateSut().CompleteAsync(new FileDialogRequest(0, NewFile, action, Overwrite: true, Timeout: TimeSpan.FromSeconds(5)));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("handle").And.Contain("window").And.Contain("Nothing was done");
        _dialogs.Verify(d => d.Locate(It.IsAny<nint>()), Times.Never);
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task TheWindowNamed_IsWhereTheFileDialogIsLocated()
    {
        await CompleteAsync(NewFile);

        _dialogs.Verify(d => d.Locate(Dialog), Times.Once);
    }

    [Fact]
    public async Task NoFileDialog_IsTheLocatorsError()
    {
        _dialogs.Setup(d => d.Locate(It.IsAny<nint>())).Returns(DialogLocation.Failed(nameof(UIElementErrorCode.DialogNotFound), "No dialog."));

        var result = await CompleteAsync(NewFile);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
    }
}
