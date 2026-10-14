namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// respond_to_dialog's service (roadmap M3.5): which dialog it answers, what it reads (bounded, button captions left out),
/// how it presses the button (only through element_action's service), what it refuses before reading anything, and how
/// it reports a dialog that closed, stayed open or opened another. Windows, popups, the search and the action are fakes.
/// </summary>
public class DialogServiceTests
{
    private const long WsCaption = 0x00C00000;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private static readonly nint App = 0x4000;
    private static readonly nint Dialog = 0x5000;
    private static readonly nint Prompt = 0x6000;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IPopupService> _popups = new();
    private readonly Mock<IElementSearchService> _search = new();
    private readonly Mock<IElementActionService> _actions = new();
    private readonly Mock<IInputTargetGuard> _guard = new();
    private readonly Mock<IInputSession> _session = new();
    private readonly Dictionary<nint, (string Class, long Style, nint Owner, string Title)> _shown = new();
    private readonly Dictionary<nint, List<PopupWindow>> _popupsOf = new();
    private readonly List<ElementActionRequest> _acted = [];
    private readonly List<ElementQuery> _queries = [];
    private IReadOnlyList<FoundElement> _buttons;
    private IReadOnlyList<FoundElement> _texts;
    private Func<ElementActionRequest, ElementActionResult> _act;
    private nint _foreground;

    public DialogServiceTests()
    {
        _shown[App] = ("Notepad", WsCaption, 0, "Untitled - Notepad");
        _shown[Dialog] = ("#32770", WsCaption, App, "Notepad");
        _foreground = Dialog;
        _buttons = [Element("w1e1", "Speichern", "6", "Button"), Element("w1e2", "Nicht speichern", "7", "Button"), Element("w1e3", "Abbrechen", "2", "Button")];
        _texts = [Element("w1e4", "Möchten Sie die Änderungen speichern?", "65535", "Text"), Element("w1e5", "Abbrechen", null, "Text")];
        _act = request =>
        {
            // The press closes the dialog
            _shown.Remove(Dialog);
            return new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.Done, ElementActionMethod.Invoke);
        };

        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => _foreground);
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns((nint h) => _shown.ContainsKey(h));
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => _shown.ContainsKey(h));
        _windows.Setup(w => w.GetClassName(It.IsAny<nint>())).Returns((nint h) => _shown.TryGetValue(h, out var w) ? w.Class : string.Empty);
        _windows.Setup(w => w.GetWindowText(It.IsAny<nint>())).Returns((nint h) => _shown.TryGetValue(h, out var w) ? w.Title : string.Empty);
        _windows.Setup(w => w.GetWindowLongPtr(It.IsAny<nint>(), GwlStyle))
            .Returns((nint h, int _) => _shown.TryGetValue(h, out var w) ? (nint)w.Style : 0);
        _windows.Setup(w => w.GetWindowLongPtr(It.IsAny<nint>(), GwlExStyle)).Returns(0);
        _windows.Setup(w => w.GetWindow(It.IsAny<nint>(), GwOwner))
            .Returns((nint h, uint _) => _shown.TryGetValue(h, out var w) ? w.Owner : 0);
        _windows.Setup(w => w.GetProcessName(It.IsAny<uint>())).Returns("notepad");

        _popups.Setup(p => p.GetPopups(It.IsAny<nint>()))
            .Returns((nint h) => _popupsOf.TryGetValue(h, out var list) ? list.Where(p => _shown.ContainsKey(p.Handle)).ToList() : []);
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Callback((ElementQuery query, CancellationToken _) => _queries.Add(query))
            .ReturnsAsync((ElementQuery query, CancellationToken _) =>
            {
                var found = query.ControlTypes!.Contains("Button") ? _buttons : _texts;
                return new FindElementsResult(true, (long)query.WindowHandle, "Notepad", found.Count, false, found, 1, Scope: ElementSearchScope.Window);
            });
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementActionRequest request, CancellationToken _) => _acted.Add(request))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) => _act(request));
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>()))
            .Returns((InputTarget target) => InputGuardResult.Allow(target, target.WindowHandle ?? 0, null, target.WindowHandle ?? 0));
    }

    private DialogService CreateSut(IWindowService? windowService = null) =>
        new(_windows.Object, _popups.Object, _search.Object, _actions.Object, _guard.Object, _session.Object, windowService)
    {
        CloseWait = TimeSpan.FromMilliseconds(200),
        PollInterval = TimeSpan.FromMilliseconds(5)
    };

    private static FoundElement Element(string elementRef, string? name, string? automationId, string controlType, bool enabled = true,
        BoundsDto? bounds = null) =>
        new(elementRef, name, controlType, automationId, null, bounds, null, enabled, false, controlType == "Button" ? ["Invoke"] : []);

    /// <summary>A Win32 dialog's title bar (0,0 to 400,30) and its caption buttons, as the control view shows them.</summary>
    private static FoundElement[] TitleBar(params string[] captionButtons) =>
    [
        Element("w1t0", null, "TitleBar", "TitleBar", bounds: new BoundsDto(0, 0, 400, 30)),
        .. captionButtons.Select((name, i) => Element($"w1t{i + 1}", name, name, "Button", bounds: new BoundsDto(300 + 30 * i, 2, 28, 26)))
    ];

    private static PopupWindow Popup(nint handle, PopupKind kind, nint owner, bool blocking = false, string? title = null) =>
        new(handle, kind, title, kind == PopupKind.Dialog ? "#32770" : "#32768", 4242, owner, PopupRelation.Owned, null, blocking);

    /// <summary>Answers <paramref name="window"/>: by default the dialog, named by its handle as a read returned it; zero for the dialog in front.</summary>
    private Task<DialogResponseResult> RespondAsync(string? button, nint? window = null) =>
        CreateSut().RespondAsync(new DialogResponseRequest(window ?? Dialog, button, Timeout: TimeSpan.FromSeconds(2)));

    #region Which dialog

    [Fact]
    public void Locate_WithoutAWindow_IsTheDialogInFront()
    {
        CreateSut().Locate(0).Dialog.Should().Be(Dialog);
    }

    [Fact]
    public void Locate_OfAnAppWindowThatAModalDialogDisables_IsThatDialog_DownToTheFrontOne()
    {
        // Notepad's Save As dialog, and the replace prompt the Save As dialog shows
        _shown[Prompt] = ("#32770", WsCaption, Dialog, "Confirm Save As");
        _popupsOf[App] = [Popup(Dialog, PopupKind.Dialog, App, blocking: true)];
        _popupsOf[Dialog] = [Popup(Prompt, PopupKind.Dialog, Dialog, blocking: true)];

        CreateSut().Locate(App).Dialog.Should().Be(Prompt);
    }

    [Fact]
    public void Locate_OfAnAppWindowWithAnOpenDialog_IsThatDialog()
    {
        _popupsOf[App] = [Popup(0x7000, PopupKind.Menu, App), Popup(Dialog, PopupKind.Dialog, App)];

        CreateSut().Locate(App).Dialog.Should().Be(Dialog);
    }

    [Fact]
    public void AnOwnedCaptionedWindow_IsADialog_ButAnUnownedMainWindowIsNot()
    {
        // A WPF dialog: its own class, owned, with a title bar
        _shown[0x7100] = ("HwndWrapper[Fixture]", WsCaption, App, "Options");
        CreateSut().Locate(0x7100).Dialog.Should().Be((nint)0x7100);

        _foreground = App;
        var location = CreateSut().Locate(0);
        location.Found.Should().BeFalse();
        location.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
        location.Error.Should().Contain("Untitled - Notepad").And.Contain("window in front");
    }

    [Fact]
    public void Locate_OfAWindowThatClosed_IsInvalidHandle_AndWithNothingInFront_DialogNotFound()
    {
        CreateSut().Locate(0x9999).ErrorCode.Should().Be(nameof(UIElementErrorCode.InvalidHandle));

        _foreground = 0;
        CreateSut().Locate(0).ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
    }

    #endregion

    #region Reading

    [Fact]
    public async Task Read_ReturnsTheTitleTextAndButtons_WithoutTheButtonCaptionsInTheText_ThroughFindUiElementsSearch()
    {
        var read = await CreateSut().ReadAsync(Dialog, TimeSpan.FromSeconds(1));

        var content = read.Content!;
        content.Title.Should().Be("Notepad");
        content.ClassName.Should().Be("#32770");
        content.Text.Should().Equal("Möchten Sie die Änderungen speichern?");
        content.Buttons.Select(b => (b.Ref, b.Name, b.Standard)).Should().Equal(
            ("w1e1", "Speichern", "yes"), ("w1e2", "Nicht speichern", "no"), ("w1e3", "Abbrechen", "cancel"));
        _queries.Should().HaveCount(2).And.OnlyContain(q =>
            q.WindowHandle == Dialog && q.Scope == ElementSearchScope.Window && q.VisibleOnly && q.MaxResults <= 26);
        _queries[0].ControlTypes.Should().Contain("TitleBar", "the title bar is read to leave its caption buttons out");
    }

    [Fact]
    public async Task Read_LeavesTheTitleBarsCaptionButtonsOut()
    {
        _buttons = [.. TitleBar("Help", "Close"), Element("w1e1", "OK", "1", "Button", bounds: new BoundsDto(150, 100, 80, 25))];

        var content = (await CreateSut().ReadAsync(Dialog, TimeSpan.FromSeconds(1))).Content!;

        content.Buttons.Select(b => b.Name).Should().Equal("OK");
    }

    [Fact]
    public async Task Read_LeavesACaptionButtonOut_ThatReachesPastTheTitleBarsEdge()
    {
        // Arrange - a MessageBox on a real desktop (roadmap M3.F3): its Close button reaches 5 pixels above the TitleBar and
        // 2 to its right, so it is not wholly inside it
        _buttons =
        [
            Element("w1t0", null, "TitleBar", "TitleBar", bounds: new BoundsDto(1802, 992, 264, 42)),
            Element("w1t1", "Close", "Close", "Button", bounds: new BoundsDto(2015, 987, 53, 45)),
            Element("w1e1", "OK", "2", "Button", bounds: new BoundsDto(1980, 1080, 75, 23))
        ];

        // Act
        var content = (await CreateSut().ReadAsync(Dialog, TimeSpan.FromSeconds(1))).Content!;

        // Assert
        content.Buttons.Select(b => b.Name).Should().Equal("OK");
    }

    [Fact]
    public async Task Read_CutsLongButtonNames()
    {
        _buttons = [Element("w1e1", new string('x', 500), "1", "Button")];

        var content = (await CreateSut().ReadAsync(Dialog, TimeSpan.FromSeconds(1))).Content!;

        content.Buttons.Single().Name.Should().HaveLength(DialogService.MaxButtonNameLength).And.EndWith("…", "a cut is marked (roadmap M3.F14)");
    }

    [Fact]
    public async Task Read_CutsLongAutomationIds_InTheButtonsAndInTheMessages()
    {
        // Web content in an Electron dialog: an HTML id is the AutomationId
        _buttons = [Element("w1e1", null, new string('i', 5000), "Button")];

        var content = (await CreateSut().ReadAsync(Dialog, TimeSpan.FromSeconds(1))).Content!;
        var refused = await RespondAsync("retry");

        content.Buttons.Single().AutomationId.Should().HaveLength(DialogService.MaxButtonNameLength).And.EndWith("…");
        refused.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
        refused.Error!.Length.Should().BeLessThan(1000);
    }

    [Fact]
    public async Task Read_BoundsTheText()
    {
        _texts = Enumerable.Range(0, 20).Select(i => Element($"w1t{i}", new string((char)('a' + i), 600), null, "Text")).ToList();

        var content = (await CreateSut().ReadAsync(Dialog, TimeSpan.FromSeconds(1))).Content!;

        content.Text.Should().OnlyContain(t => t.Length <= DialogService.MaxTextLength);
        content.Text.Sum(t => t.Length).Should().Be(DialogService.MaxTotalText);
        content.TextTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Read_ThatFails_ReportsTheSearchCode()
    {
        _search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(
            new FindElementsResult(false, (long)Dialog, null, 0, false, null, 1, Error: "The window is not responding.",
                ErrorCode: UIElementErrorCode.NotResponding));

        var read = await CreateSut().ReadAsync(Dialog, TimeSpan.FromSeconds(1));

        read.Content.Should().BeNull();
        read.ErrorCode.Should().Be("NotResponding");
    }

    #endregion

    #region Answering

    [Fact]
    public async Task Respond_PressesTheButtonByItsId_ThroughElementAction_AndSeesTheDialogClose()
    {
        var result = await RespondAsync("no");

        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().Be((long)Dialog);
        result.Invoked!.Name.Should().Be("Nicht speichern");
        result.Method.Should().Be(ElementActionMethod.Invoke);
        result.DialogClosed.Should().BeTrue();
        result.NextDialog.Should().BeNull();
        result.Text.Should().Equal("Möchten Sie die Änderungen speichern?");
        var acted = _acted.Should().ContainSingle().Which;
        (acted.Ref, acted.Action, acted.Timeout).Should().Be(("w1e2", ElementActionKind.Invoke, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Respond_WithAButtonTheDialogLacks_IsElementNotFound_ListingTheButtons_AndPressesNothing()
    {
        var result = await RespondAsync("retry");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
        result.Error.Should().Contain("'Speichern' (automationId 6, yes)").And.Contain("Nothing was pressed");
        result.Buttons.Should().HaveCount(3);
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_ToADialogWithMoreButtonsThanAreRead_ReturnsButtonsTruncated_ReadOrRefused()
    {
        // Arrange - a dialog with more buttons than a read lists
        _buttons = Enumerable.Range(0, DialogService.MaxButtons + 3).Select(i => Element($"w1e{i}", $"Option {i}", $"opt{i}", "Button")).ToList();

        // Act
        var read = await RespondAsync(null);
        var refused = await RespondAsync("Option 22");

        // Assert - the button asked for may be one that was not read: the result says more exist
        read.Success.Should().BeTrue(read.Error);
        read.Buttons.Should().HaveCount(DialogService.MaxButtons);
        read.ButtonsTruncated.Should().BeTrue();
        refused.ErrorCode.Should().Be(nameof(UIElementErrorCode.ElementNotFound));
        refused.ButtonsTruncated.Should().BeTrue();
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_ToADialogWhoseButtonsWereAllRead_LeavesButtonsTruncatedOut()
    {
        var result = await RespondAsync(null);

        result.Buttons.Should().HaveCount(3);
        result.ButtonsTruncated.Should().BeNull();
    }

    [Fact]
    public async Task Respond_WithTwoButtonsOfTheText_IsAmbiguous()
    {
        _buttons = [Element("w1e1", "OK", "a", "Button"), Element("w1e2", "OK", "b", "Button")];

        var result = await RespondAsync("OK");

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_ToADialogTheGuardRefuses_ReadsNothing()
    {
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) =>
            InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "The user's policy denies KeePass.", target));

        var result = await RespondAsync("ok");

        result.ErrorCode.Should().Be(nameof(InputErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("Nothing was read or pressed");
        result.Text.Should().BeNull();
        _search.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_WhileInputIsPaused_IsPausedByUser_AndReadsNothing()
    {
        _session.Setup(s => s.IsPaused).Returns(true);

        var result = await RespondAsync("ok");

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _search.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Respond_WhenElementActionRefuses_ReportsItsCode()
    {
        _act = request => new ElementActionResult(false, "invoke", request.Ref, (long)Dialog,
            Error: "The window is disabled by a modal dialog.", ErrorCode: "BlockedByModal");

        var result = await RespondAsync("yes");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("BlockedByModal");
        result.Error.Should().Contain("'Speichern' was not pressed");
        result.Buttons.Should().NotBeNull("the agent can name another button");
    }

    [Fact]
    public async Task Respond_ThatOpensAnotherModalDialog_ReportsItAsNextDialog()
    {
        // A Win32 button's Invoke waits for the prompt it opened
        _act = request => new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.InvokedPendingModal,
            ElementActionMethod.Invoke, OpenedPopup: new OpenedWindow((long)Prompt, "Confirm Save As", "#32770", 4242, "notepad"));

        var result = await RespondAsync("yes");

        result.Success.Should().BeTrue();
        result.DialogClosed.Should().BeFalse();
        result.NextDialog!.Handle.Should().Be((long)Prompt);
    }

    [Fact]
    public async Task Respond_ThatLeavesTheDialogOpen_SaysSo_AndNamesANewDialogOfIt()
    {
        _act = request =>
        {
            _shown[Prompt] = ("#32770", WsCaption, Dialog, "Error");
            _popupsOf[Dialog] = [Popup(Prompt, PopupKind.Dialog, Dialog, title: "Error")];
            return new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.Done, ElementActionMethod.Invoke);
        };

        var result = await RespondAsync("yes");

        result.Success.Should().BeTrue();
        result.DialogClosed.Should().BeFalse();
        result.NextDialog!.Handle.Should().Be((long)Prompt);
        result.Warnings.Should().ContainSingle(w => w.Contains("still open"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Respond_CutsTheNextDialogsLongTitle(bool reportedByThePress)
    {
        var title = new string('t', 5000);
        _act = request =>
        {
            _shown[Prompt] = ("#32770", WsCaption, Dialog, title);
            _popupsOf[Dialog] = [Popup(Prompt, PopupKind.Dialog, Dialog, title: title)];
            return reportedByThePress
                ? new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.InvokedPendingModal,
                    ElementActionMethod.Invoke, OpenedPopup: new OpenedWindow((long)Prompt, title, "#32770", 4242, "notepad"))
                : new ElementActionResult(true, "invoke", request.Ref, (long)Dialog, ElementActionStatus.Done, ElementActionMethod.Invoke);
        };

        var result = await RespondAsync("yes");

        result.NextDialog!.Title.Should().HaveLength(DialogService.MaxTitleLength).And.EndWith("…");
    }

    [Fact]
    public async Task Respond_Ok_ToAMessageBoxWhoseOnlyButtonIsCancel_PressesIt_NotTheTitleBarsClose()
    {
        // A localized MB_OK box: its lone button carries IDCANCEL so that Esc closes it; the title bar has its own Close
        _buttons = [.. TitleBar("Close"), Element("w1e1", "Aceptar", "2", "Button", bounds: new BoundsDto(150, 100, 80, 25))];

        var result = await RespondAsync("ok");

        result.Success.Should().BeTrue(result.Error);
        result.Invoked!.Ref.Should().Be("w1e1");
        result.Buttons.Should().ContainSingle();
        result.Warnings.Should().ContainSingle(w => w.Contains("only button"));
    }

    [Fact]
    public async Task Respond_Close_PressesTheDialogsCloseButton_NotTheTitleBars()
    {
        _buttons = [.. TitleBar("Close"), Element("w1e1", "Close", "2", "Button", bounds: new BoundsDto(150, 100, 80, 25))];

        var result = await RespondAsync("Close");

        result.Success.Should().BeTrue(result.Error);
        _acted.Should().ContainSingle().Which.Ref.Should().Be("w1e1");
    }

    [Fact]
    public async Task Respond_WithoutAButton_OnlyReadsTheDialogInFront_EvenWhilePaused()
    {
        _session.Setup(s => s.IsPaused).Returns(true);

        var result = await RespondAsync(null, window: 0);

        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().Be((long)Dialog);
        result.Text.Should().Equal("Möchten Sie die Änderungen speichern?");
        result.Buttons.Should().HaveCount(3);
        (result.Invoked, result.DialogClosed).Should().Be((null, null));
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_WithoutAButton_ToADialogTheGuardRefuses_ReadsNothing()
    {
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) =>
            InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "The user's policy denies KeePass.", target));

        var result = await RespondAsync(null);

        result.ErrorCode.Should().Be(nameof(InputErrorCode.TargetDeniedByPolicy));
        _search.Verify(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Respond_ToADialogTheGuardRefuses_NotesTheDialogForTheAuditLine()
    {
        var windowService = new Mock<IWindowService>();
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) =>
            InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "The user's policy denies KeePass.", target));

        await CreateSut(windowService.Object).RespondAsync(new DialogResponseRequest(Dialog, "ok", Timeout: TimeSpan.FromSeconds(1)));

        windowService.Verify(w => w.GetWindowInfo(Dialog), Times.Once);
    }

    [Fact]
    public async Task Respond_WhosePressHadNotReturned_CountsAsSent_AndReportsWhetherTheDialogClosed()
    {
        _act = request =>
        {
            _shown.Remove(Dialog);
            return new ElementActionResult(false, "invoke", request.Ref, (long)Dialog, Error: "Invoke had not returned; it may still take effect.",
                ErrorCode: nameof(UIElementErrorCode.ActionTimedOut));
        };

        var result = await RespondAsync("yes");

        result.Success.Should().BeTrue(result.Error);
        result.DialogClosed.Should().BeTrue();
        result.Invoked!.Ref.Should().Be("w1e1");
        result.Warnings.Should().ContainSingle(w => w.Contains("was sent but had not returned"));
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Respond_WithAnEmptyButton_IsInvalidArguments()
    {
        var result = await RespondAsync(" ");

        result.ErrorCode.Should().Be("InvalidArguments");
        _search.VerifyNoOtherCalls();
    }

    #endregion

    #region Only the dialog that was read is answered

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    [InlineData("ok")]
    [InlineData("2")]
    [InlineData("Abbrechen")]
    public async Task Respond_PressingAButton_WithoutNamingTheDialog_IsInvalidArguments_AndNothingIsReadOrPressed(string button)
    {
        // The dialog in front may not be the one the agent read: a prompt that came up meanwhile would be answered blind
        var result = await RespondAsync(button, window: 0);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("Read the dialog first").And.Contain("dialogHandle").And.Contain("Nothing was read or pressed");
        _search.VerifyNoOtherCalls();
        _acted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData(" Cancel ")]
    public async Task Respond_Cancel_MayBePressedInTheDialogInFront_Unnamed(string button)
    {
        var result = await RespondAsync(button, window: 0);

        result.Success.Should().BeTrue(result.Error);
        result.Invoked!.Standard.Should().Be("cancel");
        _acted.Should().ContainSingle().Which.Ref.Should().Be("w1e3");
    }

    [Fact]
    public async Task Respond_ToANamedDialog_ThatAModalDialogNowDisables_IsBlockedByModal_NamingIt_AndPressesNothing()
    {
        // The agent read the dialog; since then it opened a prompt of its own, which has a Yes button too
        _shown[Prompt] = ("#32770", WsCaption, Dialog, "Ersetzen bestätigen");
        _popupsOf[Dialog] = [Popup(Prompt, PopupKind.Dialog, Dialog, blocking: true, title: "Ersetzen bestätigen")];

        var result = await RespondAsync("yes");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(InputErrorCode.BlockedByModal));
        result.Error.Should().Contain($"{(long)Prompt}").And.Contain("'Ersetzen bestätigen'").And.Contain("Nothing was pressed");
        _search.VerifyNoOtherCalls();
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_WithoutAButton_ToANamedDialogThatAModalDialogDisables_ReadsThatModalDialog()
    {
        _shown[Prompt] = ("#32770", WsCaption, Dialog, "Ersetzen bestätigen");
        _popupsOf[Dialog] = [Popup(Prompt, PopupKind.Dialog, Dialog, blocking: true)];

        var result = await RespondAsync(null);

        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().Be((long)Prompt);
        _acted.Should().BeEmpty();
    }

    [Fact]
    public async Task Respond_ToANamedAppWindow_PressesInTheModalDialogThatDisablesIt()
    {
        // The app window is no dialog: naming it means its modal dialog, down to the front one of the chain
        _popupsOf[App] = [Popup(Dialog, PopupKind.Dialog, App, blocking: true)];

        var result = await RespondAsync("no", App);

        result.Success.Should().BeTrue(result.Error);
        result.DialogHandle.Should().Be((long)Dialog);
        _acted.Should().ContainSingle().Which.Ref.Should().Be("w1e2");
    }

    #endregion
}
