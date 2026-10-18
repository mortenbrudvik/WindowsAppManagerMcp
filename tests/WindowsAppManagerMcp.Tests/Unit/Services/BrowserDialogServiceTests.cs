using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// browser_handle_dialog's service (roadmap M5.8, dialogs) over the JavaScript dialog trees Chrome 155 and Edge 155 showed
/// on a real desktop (<see cref="FakeChromiumDialog"/>), with the real element registry: the dialog is found by its views'
/// classes and structure, never by a name, a page's look-alike is never taken for it, a confirm or prompt is accepted only
/// once its text was read, and the press and the prompt's text go through element_action's service (mocked here).
/// </summary>
public class BrowserDialogServiceTests
{
    private static readonly nint Browser = 0x610AEE;
    private const uint BrowserPid = 4242;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IElementActionService> _actions = new();
    private readonly ElementRegistry _registry;
    private readonly List<ElementActionRequest> _requests = [];
    private ITargetPolicy? _policy;
    private FakeChromiumDialog? _shown;

    /// <summary>How many presses Chromium ignores first (a press right after the dialog was shown).</summary>
    private int _ignoredPresses;

    /// <summary>A dialog the page opens once the shown one is answered.</summary>
    private FakeChromiumDialog? _next;

    public BrowserDialogServiceTests()
    {
        _windows.Setup(w => w.IsWindow(Browser)).Returns(true);
        _windows.Setup(w => w.GetAncestor(Browser, WindowAncestor.Root)).Returns(Browser);
        _windows.Setup(w => w.GetWindowThreadProcessId(Browser)).Returns(BrowserPid);
        _windows.Setup(w => w.GetProcessName(BrowserPid)).Returns("chrome");
        _windows.Setup(w => w.RespondsWithin(Browser, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);

        // element_action's service: a press closes the dialog, as the real one did; the prompt's text is set
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ElementActionRequest request, CancellationToken _) =>
            {
                _requests.Add(request);
                var node = (FakeUiaNode)_registry.Resolve(request.Ref, CancellationToken.None).Element!.Node;
                if (request.Action == ElementActionKind.SetValue)
                {
                    node.PatternObjects[typeof(IUiaValuePattern)].As<FakeValuePattern>().Value = request.Value;
                }
                else if (_ignoredPresses > 0)
                {
                    _ignoredPresses--;
                }
                else
                {
                    _shown?.Close();
                    if (_next != null)
                        _shown!.RootView.With(_next.Dialog);
                }

                return new ElementActionResult(true, request.Action.ToString(), request.Ref, (long)Browser, ElementActionStatus.Done);
            });
    }

    private BrowserDialogService CreateSut() => new(_tree, _worker, _registry, _actions.Object, _windows.Object, _policy)
    {
        CloseWait = TimeSpan.FromMilliseconds(200), RepressWait = TimeSpan.FromMilliseconds(60), PollInterval = TimeSpan.FromMilliseconds(10)
    };

    private FakeChromiumDialog Show(FakeChromiumDialog dialog)
    {
        _shown = dialog;
        _tree.WithWindow(Browser, dialog.Window);
        return dialog;
    }

    /// <summary>The node the request with <paramref name="index"/> named.</summary>
    private FakeUiaNode Target(int index) => (FakeUiaNode)_registry.Resolve(_requests[index].Ref, CancellationToken.None).Element!.Node;

    public static TheoryData<bool, bool> Browsers() => new() { { false, false }, { false, true }, { true, false }, { true, true } };

    #region Reading

    [Theory]
    [MemberData(nameof(Browsers))]
    public async Task Read_AConfirm_GivesItsTypeAndMessage_InChromeAndEdge_InEveryLanguage(bool edge, bool norwegian)
    {
        Show(new FakeChromiumDialog("confirm", edge, norwegian, message: "Delete the draft?"));

        var result = await CreateSut().HandleAsync(Browser, accept: null, promptText: null);

        result.Should().Be(new BrowserDialogResult(true, Browser, "confirm", "Delete the draft?"));
        _requests.Should().BeEmpty("a read presses nothing");
    }

    [Theory]
    [MemberData(nameof(Browsers))]
    public async Task Read_APrompt_GivesItsMessage_FromItsFieldsName_AndTheFieldsText(bool edge, bool norwegian)
    {
        Show(new FakeChromiumDialog("prompt", edge, norwegian, message: "Your name?", defaultPrompt: "Ola"));

        var result = await CreateSut().HandleAsync(Browser, null, null);

        result.Should().Be(new BrowserDialogResult(true, Browser, "prompt", "Your name?", "Ola"));
    }

    [Fact]
    public async Task Read_AnAlert_HasOneButton()
    {
        Show(new FakeChromiumDialog("alert", message: "Saved."));

        var result = await CreateSut().HandleAsync(Browser, null, null);

        result.DialogType.Should().Be("alert");
        result.DialogMessage.Should().Be("Saved.");
    }

    [Fact]
    public async Task Read_LeavesOutTheHeading_WhichIsNotThePagesText()
    {
        Show(new FakeChromiumDialog("confirm", norwegian: true, message: "Slette utkastet?"));

        var result = await CreateSut().HandleAsync(Browser, null, null);

        result.DialogMessage.Should().Be("Slette utkastet?").And.NotContain("Denne siden sier");
    }

    [Fact]
    public async Task AWindowWithoutADialog_IsDialogNotFound_PointingNativeDialogsToRespondToDialog()
    {
        _tree.WithWindow(Browser, new FakeBrowserWindow(BrowserNames.ChromeEnglish).Window);

        var result = await CreateSut().HandleAsync(Browser, accept: false, promptText: null);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
        result.Error.Should().Contain("respond_to_dialog");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APagesElementWithTheDialogsClass_IsNeverTakenForTheDialog()
    {
        // Chromium exposes an HTML element's class attribute as its ClassName: a page can draw a look-alike of the dialog
        var page = new FakeBrowserWindow(BrowserNames.ChromeEnglish);
        page.Document.With(new FakeUiaNode("Group") { ClassName = BrowserDialogService.DialogViewClass }.With(
                new FakeUiaNode("Text", "Allow access to your files?")),
            new FakeUiaNode("Group") { ClassName = BrowserDialogService.ClientViewClass }.With(
                new FakeUiaNode("Button", "OK") { ActionPatterns = ["Invoke"] }));
        _tree.WithWindow(Browser, page.Window);

        var result = await CreateSut().HandleAsync(Browser, accept: true, promptText: null);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.DialogNotFound));
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ADeniedBrowser_IsNeitherReadNorAnswered()
    {
        Show(new FakeChromiumDialog("confirm"));
        _policy = new TargetPolicy(TargetPolicySettings.Default with { DeniedProcesses = ["chrome"] });

        var result = await CreateSut().HandleAsync(Browser, accept: false, promptText: null);

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.TargetDeniedByPolicy));
        result.DialogMessage.Should().BeNull();
        _requests.Should().BeEmpty();
    }

    #endregion

    #region Answering

    [Theory]
    [MemberData(nameof(Browsers))]
    public async Task Accept_OfAConfirmThatWasRead_PressesItsFirstButton(bool edge, bool norwegian)
    {
        var dialog = Show(new FakeChromiumDialog("confirm", edge, norwegian));
        var sut = CreateSut();
        await sut.HandleAsync(Browser, null, null);

        var result = await sut.HandleAsync(Browser, accept: true, promptText: null);

        result.Should().Be(new BrowserDialogResult(true, Browser, "confirm", dialog.Message, Accepted: true, DialogClosed: true));
        _requests.Should().ContainSingle().Which.Action.Should().Be(ElementActionKind.Invoke);
        Target(0).Should().BeSameAs(dialog.Ok);
    }

    [Theory]
    [MemberData(nameof(Browsers))]
    public async Task Dismiss_PressesTheLastButton_WithoutAnEarlierRead(bool edge, bool norwegian)
    {
        var dialog = Show(new FakeChromiumDialog("confirm", edge, norwegian));

        var result = await CreateSut().HandleAsync(Browser, accept: false, promptText: null);

        result.Accepted.Should().BeFalse();
        result.DialogClosed.Should().BeTrue();
        Target(0).Should().BeSameAs(dialog.Cancel);
    }

    [Fact]
    public async Task TheButtonsNames_NeverDecide_WhichIsPressed()
    {
        // Named the other way round: the first button still accepts
        var dialog = Show(new FakeChromiumDialog("confirm", swappedNames: true));
        var sut = CreateSut();
        await sut.HandleAsync(Browser, null, null);

        await sut.HandleAsync(Browser, accept: true, promptText: null);

        Target(0).Should().BeSameAs(dialog.Ok);
        dialog.Ok.Name.Should().Be("Cancel");
    }

    [Fact]
    public async Task AnAlert_IsClosedByItsOnlyButton_AcceptedOrDismissed_WithoutAnEarlierRead()
    {
        var dialog = Show(new FakeChromiumDialog("alert"));

        var result = await CreateSut().HandleAsync(Browser, accept: true, promptText: null);

        result.Success.Should().BeTrue(result.Error);
        Target(0).Should().BeSameAs(dialog.Ok);
    }

    [Fact]
    public async Task Accept_OfAConfirmNobodyRead_IsRefused_WithItsText_AndTheNextAcceptPressesIt()
    {
        var dialog = Show(new FakeChromiumDialog("confirm", message: "Delete all 12 files?"));
        var sut = CreateSut();

        var refused = await sut.HandleAsync(Browser, accept: true, promptText: null);
        var accepted = await sut.HandleAsync(Browser, accept: true, promptText: null);

        refused.ErrorCode.Should().Be(nameof(BrowserErrorCode.DialogNotRead));
        refused.DialogMessage.Should().Be("Delete all 12 files?");
        refused.Accepted.Should().BeNull();
        accepted.Accepted.Should().BeTrue();
        _requests.Should().ContainSingle("the refusal pressed nothing");
        Target(0).Should().BeSameAs(dialog.Ok);
    }

    [Fact]
    public async Task Accept_OfADialogAnActionReported_NeedsNoFurtherRead()
    {
        // browser_click reported the dialog it opened, cut to its start
        var dialog = Show(new FakeChromiumDialog("confirm", message: "Delete the draft?   It cannot be restored."));
        var sut = CreateSut();
        sut.NoteShown(Browser, "Delete the draft? It cannot be restored.");

        var result = await sut.HandleAsync(Browser, accept: true, promptText: null);

        result.Accepted.Should().BeTrue(result.Error);
        Target(0).Should().BeSameAs(dialog.Ok);
    }

    [Fact]
    public async Task Accept_OfALongDialogAnActionReportedCut_NeedsNoFurtherRead()
    {
        // browser_click reported the start of a long message, cut with "..." as every reading of it is
        var message = "Delete the draft? " + string.Concat(Enumerable.Repeat("It cannot be restored. ", 12));
        var dialog = Show(new FakeChromiumDialog("confirm", message: message));
        var sut = CreateSut();
        sut.NoteShown(Browser, message[..(CdpDialogText.MaxDialogMessageLength - 3)] + "...");

        var result = await sut.HandleAsync(Browser, accept: true, promptText: null);

        result.Accepted.Should().BeTrue(result.Error);
        Target(0).Should().BeSameAs(dialog.Ok);
    }

    [Theory]
    [InlineData("Delete")]
    [InlineData("Delete...")]
    [InlineData("...")]
    public async Task Accept_OfALongerDialog_ThatStartsWithTheTextOfAShortOneThatWasRead_IsRefused(string read)
    {
        // A short dialog read in this window is not the start of another, longer one that shows there now
        Show(new FakeChromiumDialog("confirm", message: "Delete all repositories?"));
        var sut = CreateSut();
        sut.NoteShown(Browser, read);

        var result = await sut.HandleAsync(Browser, accept: true, promptText: null);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DialogNotRead));
        result.DialogMessage.Should().Be("Delete all repositories?");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Accept_OfADialogWhoseTextChangedSinceItWasRead_IsRefused()
    {
        Show(new FakeChromiumDialog("confirm", message: "Delete all 12 files?"));
        var sut = CreateSut();
        sut.NoteShown(Browser, "Save the draft?");

        var result = await sut.HandleAsync(Browser, accept: true, promptText: null);

        result.ErrorCode.Should().Be(nameof(BrowserErrorCode.DialogNotRead));
        _requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_OfAPromptWithText_SetsItsField_ThenPressesOk(bool edge)
    {
        var dialog = Show(new FakeChromiumDialog("prompt", edge, message: "Your name?"));
        var sut = CreateSut();
        await sut.HandleAsync(Browser, null, null);

        var result = await sut.HandleAsync(Browser, accept: true, promptText: "Kari Nordmann");

        result.Accepted.Should().BeTrue(result.Error);
        _requests.Select(r => r.Action).Should().Equal(ElementActionKind.SetValue, ElementActionKind.Invoke);
        _requests[0].Value.Should().Be("Kari Nordmann");
        _requests[0].Mode.Should().Be(ElementValueMode.Value, "no keystrokes");
        Target(0).Should().BeSameAs(dialog.Field);
        Target(1).Should().BeSameAs(dialog.Ok);
        dialog.Prompt!.Value.Should().Be("Kari Nordmann");
    }

    [Fact]
    public async Task APromptTextForADialogWithoutAField_IsInvalidArguments_AndNothingIsPressed()
    {
        Show(new FakeChromiumDialog("confirm"));

        var result = await CreateSut().HandleAsync(Browser, accept: true, promptText: "x");

        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task APressChromiumIgnores_RightAfterTheDialogWasShown_IsMadeAgain()
    {
        // Chromium ignores input that comes too soon after the dialog was shown (seen on Chrome 155: a confirm a click had
        // just opened stayed open after the first press)
        var dialog = Show(new FakeChromiumDialog("confirm"));
        _ignoredPresses = 1;

        var result = await CreateSut().HandleAsync(Browser, accept: false, promptText: null);

        result.DialogClosed.Should().BeTrue();
        result.Warning.Should().BeNull();
        _requests.Should().HaveCount(2);
        Target(1).Should().BeSameAs(dialog.Cancel);
    }

    [Fact]
    public async Task ADialogTheBrowserNeverTakes_IsPressedAtMostThreeTimes_AndSaid()
    {
        Show(new FakeChromiumDialog("confirm"));
        _ignoredPresses = int.MaxValue;

        var result = await CreateSut().HandleAsync(Browser, accept: false, promptText: null);

        result.Success.Should().BeTrue();
        result.DialogClosed.Should().BeFalse();
        result.Warning.Should().Contain("still shown");
        _requests.Should().HaveCount(BrowserDialogService.MaxPresses);
    }

    [Fact]
    public async Task AnotherDialogThePageOpensAfterThePress_IsNeverPressed()
    {
        Show(new FakeChromiumDialog("confirm"));
        _next = new FakeChromiumDialog("confirm", message: "Really delete everything?");

        var result = await CreateSut().HandleAsync(Browser, accept: false, promptText: null);

        result.DialogClosed.Should().BeFalse();
        result.Warning.Should().Contain("Another JavaScript dialog");
        _requests.Should().ContainSingle("the next dialog is read before it is answered");
    }

    [Fact]
    public async Task APressElementActionRefuses_IsItsError()
    {
        Show(new FakeChromiumDialog("confirm"));
        _actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementActionResult(false, "invoke", ErrorCode: nameof(WindowErrorCode.PausedByUser), Error: "The user paused input."));

        var result = await CreateSut().HandleAsync(Browser, accept: false, promptText: null);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        result.Accepted.Should().BeNull();
    }

    #endregion

    [Theory]
    [InlineData("Delete it?", "Delete it?", true)]
    [InlineData("Delete   it?\n", "Delete it?", true)]
    [InlineData("Delete it?", "Save it?", false)]
    [InlineData("", "Delete it?", false)]
    [InlineData("", "", true)]
    [InlineData("Delete", "Delete all repositories?", false)]
    [InlineData("Delete...", "Delete all repositories?", false)]
    [InlineData("...", "Delete all repositories?", false)]
    [InlineData("Delete the draft? It cannot...", "Delete the draft? It cannot be restored.", false)]
    public void SameText_TellsWhetherTwoReadingsAreTheSameMessage(string first, string second, bool same)
    {
        // Equal once runs of white space are one space: a short message is never the start of a longer one, even when it
        // ends with "..." (a page writes its own text, dots and all)
        BrowserDialogService.SameText(first, second).Should().Be(same);
        BrowserDialogService.SameText(second, first).Should().Be(same);
    }

    [Fact]
    public void SameText_AReadingCutToItsStart_IsTheStartOfTheWholeMessage_OnlyThatWay()
    {
        // Arrange - a message as long as the readings are cut at: its first characters and "..."
        var whole = "Delete the draft? " + string.Concat(Enumerable.Repeat("It   cannot be restored. ", 12));
        var cut = whole[..(CdpDialogText.MaxDialogMessageLength - 3)] + "...";

        // Assert - a stored cut reading matches the whole message and its own cut, never a message it does not start
        BrowserDialogService.SameText(cut, whole).Should().BeTrue();
        BrowserDialogService.SameText(cut, cut).Should().BeTrue();
        BrowserDialogService.SameText(cut, "Delete the draft? It cannot be restored.").Should().BeFalse();
        BrowserDialogService.SameText(whole, cut).Should().BeFalse("only the reading that was noted may be the cut one");
    }
}
