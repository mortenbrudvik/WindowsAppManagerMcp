namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The clipboard type_text's paste lends and gives back (roadmap M4.4): only a clipboard that can be put back exactly is
/// lent (empty, or plain text not marked private), the lent text is the agent's while it is there, and giving back never
/// overwrites what another application copied meanwhile. The clipboard is <see cref="FakeClipboardWrapper"/>: the real
/// one is the user's.
/// </summary>
public class PasteClipboardTests
{
    private const string Pasted = "line one\r\nline two";
    private static readonly nint Window = 0x5150;

    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ClipboardOrigin _origin;
    private readonly Mock<INativeWindowWrapper> _windows = new();

    public PasteClipboardTests()
    {
        _origin = new ClipboardOrigin(_clipboard, new ManualClock(), Timeout.InfiniteTimeSpan);
    }

    private PasteClipboard CreateSut() => new(_clipboard, _origin, _windows.Object);

    private void WindowIs(string className, string processName)
    {
        _windows.Setup(w => w.GetClassName(Window)).Returns(className);
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(77u);
        _windows.Setup(w => w.GetProcessName(77u)).Returns(processName);
    }

    #region Lend and give back

    [Fact]
    public void Lend_AnEmptyClipboard_PutsTheTextThere_AsTheAgents_AndGiveBack_EmptiesItAgain()
    {
        var sut = CreateSut();

        var loan = sut.Lend(Pasted);

        loan.Lent.Should().BeTrue(loan.Refusal);
        _clipboard.Text.Should().Be(Pasted);
        _clipboard.Markers.Should().NotBe(ClipboardPrivacyMarkers.None, "the lent text stays out of clipboard history");
        _origin.Classify(_clipboard.Sequence).Should().Be(ClipboardOriginKind.SetClipboard, "the paste rule pastes only the agent's content");

        var back = sut.GiveBack(loan);

        back.Should().Be(new ClipboardReturn(true));
        _clipboard.Text.Should().BeNull();
        _clipboard.Replacements.Should().Equal(Pasted, null);
    }

    [Fact]
    public void GiveBack_PutsTheUsersTextBack_AndItIsNotTheAgents()
    {
        // Arrange - the user's own copy: the agent may neither read nor paste it
        _clipboard.UserCopies("the user's text");
        var sut = CreateSut();

        // Act
        var loan = sut.Lend(Pasted);
        var back = sut.GiveBack(loan);

        // Assert
        back.Restored.Should().BeTrue();
        _clipboard.Text.Should().Be("the user's text");
        _origin.Classify(_clipboard.Sequence).Should().Be(ClipboardOriginKind.NotFromAgent);
        _origin.IsServerText(Pasted, Pasted.Length).Should().BeFalse("the pasted text is forgotten");
    }

    [Fact]
    public void GiveBack_SetClipboardsText_StaysTheAgents()
    {
        // Arrange - set_clipboard's text, which carries the privacy markers on purpose
        var written = _clipboard.WriteText("the agent's text");
        _origin.ServerWrote(written.SequenceNumber, "the agent's text");
        var sut = CreateSut();

        // Act
        var loan = sut.Lend(Pasted);
        sut.GiveBack(loan);

        // Assert
        _clipboard.Text.Should().Be("the agent's text");
        _origin.Classify(_clipboard.Sequence).Should().Be(ClipboardOriginKind.SetClipboard);
        _origin.IsServerText("the agent's text", "the agent's text".Length).Should().BeTrue();
    }

    [Fact]
    public void GiveBack_PutsALongTextBackWhole()
    {
        var text = new string('u', 300_000);
        _clipboard.UserCopies(text);
        var sut = CreateSut();

        sut.GiveBack(sut.Lend(Pasted)).Restored.Should().BeTrue();

        _clipboard.Text.Should().Be(text);
        _clipboard.LastPrivateContentSequence.Should().Be(0, "the user's content is not the agent's, so nothing marked private is read");
    }

    #endregion

    #region What is never lent

    [Fact]
    public void Lend_ContentMarkedPrivate_IsRefused_NotRead_AndNotReplaced()
    {
        // Arrange - a password manager's copy
        _clipboard.UserCopies("hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);
        var sequence = _clipboard.Sequence;

        // Act
        var loan = CreateSut().Lend(Pasted);

        // Assert
        loan.Lent.Should().BeFalse();
        loan.Refusal.Should().Contain("marked private");
        _clipboard.LastReadPrivate.Should().BeFalse("private content is not even read");
        _clipboard.Replacements.Should().BeEmpty();
        _clipboard.Sequence.Should().Be(sequence);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("html")]
    [InlineData("files")]
    public void Lend_MoreThanPlainText_IsRefused_AndNothingChanges(string what)
    {
        // Arrange - text with an image, formatted text, or copied files: only the text could be put back
        _clipboard.UserCopies("caption");
        switch (what)
        {
            case "image":
                _clipboard.OtherFormats = true;
                break;
            case "html":
                _clipboard.Html = [1, 2, 3];
                break;
            default:
                _clipboard.Files = [@"C:\a.txt"];
                break;
        }

        // Act
        var loan = CreateSut().Lend(Pasted);

        // Assert
        loan.Lent.Should().BeFalse();
        loan.Refusal.Should().Contain("more than plain text");
        _clipboard.Replacements.Should().BeEmpty();
        _clipboard.Text.Should().Be("caption");
    }

    [Fact]
    public void Lend_TextLongerThanTheServerSaves_IsRefused()
    {
        _clipboard.UserCopies(new string('u', PasteClipboard.MaxRestorableLength + 1));

        var loan = CreateSut().Lend(Pasted);

        loan.Lent.Should().BeFalse();
        loan.Refusal.Should().Contain("4,000,000");
        _clipboard.Replacements.Should().BeEmpty();
    }

    [Fact]
    public void Lend_AClipboardThatCannotBeOpened_IsRefused()
    {
        _clipboard.CanOpen = false;

        var loan = CreateSut().Lend(Pasted);

        loan.Lent.Should().BeFalse();
        loan.Refusal.Should().Contain("held the clipboard open");
    }

    [Fact]
    public void Lend_WhenTheClipboardChangesWhileItIsSaved_KeepsTheNewCopy()
    {
        // Arrange - the user copies between the read and the write
        _clipboard.UserCopies("old");
        _clipboard.OnReplace = _ =>
        {
            _clipboard.OnReplace = null;
            _clipboard.UserCopies("new");
        };

        // Act
        var loan = CreateSut().Lend(Pasted);

        // Assert
        loan.Lent.Should().BeFalse();
        loan.Refusal.Should().Contain("changed the clipboard");
        _clipboard.Text.Should().Be("new");
    }

    [Fact]
    public void Lend_AFailedWrite_IsRefused()
    {
        _clipboard.UserCopies("old");
        _clipboard.WriteError = "SetClipboardData failed (Win32 error 5)";

        var loan = CreateSut().Lend(Pasted);

        loan.Lent.Should().BeFalse();
        loan.Refusal.Should().Contain("Win32 error 5");
        _clipboard.Text.Should().Be("old");
    }

    #endregion

    #region Giving back what changed

    [Fact]
    public void GiveBack_WhenAnotherApplicationCopiedDuringThePaste_LeavesItsCopy()
    {
        // Arrange
        _clipboard.UserCopies("old");
        var sut = CreateSut();
        var loan = sut.Lend(Pasted);
        _clipboard.UserCopies("copied meanwhile");

        // Act
        var back = sut.GiveBack(loan);

        // Assert
        back.Restored.Should().BeFalse();
        back.Warning.Should().Contain("left as it is");
        _clipboard.Text.Should().Be("copied meanwhile");
    }

    [Fact]
    public void GiveBack_WhenTheClipboardCannotBeOpened_TriesTwice_AndSaysThePastedTextIsStillThere()
    {
        // Arrange
        _clipboard.UserCopies("old");
        var sut = CreateSut();
        var loan = sut.Lend(Pasted);
        _clipboard.CanOpen = false;

        // Act
        var back = sut.GiveBack(loan);

        // Assert
        back.Restored.Should().BeFalse();
        back.Warning.Should().Contain("still on it").And.Contain("Tell the user");
        _clipboard.Replacements.Should().Equal(Pasted, "old", "old");
        _origin.Classify(_clipboard.Sequence).Should().Be(ClipboardOriginKind.SetClipboard, "the pasted text is still the agent's");
    }

    [Fact]
    public void GiveBack_ALoanThatWasRefused_DoesNothing()
    {
        _clipboard.CanOpen = false;
        var sut = CreateSut();
        var loan = sut.Lend(Pasted);
        _clipboard.CanOpen = true;

        sut.GiveBack(loan).Restored.Should().BeTrue();

        _clipboard.Replacements.Should().BeEmpty();
    }

    #endregion

    #region Windows

    [Theory]
    [InlineData("ConsoleWindowClass", "conhost", PasteWindowKind.Terminal)]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal", PasteWindowKind.Terminal)]
    [InlineData("PuTTY", "putty", PasteWindowKind.Terminal)]
    [InlineData("Window Class", "alacritty", PasteWindowKind.Terminal)]
    [InlineData("Chrome_WidgetWin_1", "Code", PasteWindowKind.EditorWithTerminal)]
    [InlineData("Chrome_WidgetWin_1", "Cursor", PasteWindowKind.EditorWithTerminal)]
    [InlineData("Chrome_WidgetWin_1", "msedge", PasteWindowKind.Application)]
    [InlineData("Notepad", "notepad", PasteWindowKind.Application)]
    public void Classify_TellsTerminalsAndEditorsWithATerminal(string className, string processName, PasteWindowKind expected)
    {
        WindowIs(className, processName);

        CreateSut().Classify(Window).Should().Be(expected);
    }

    #endregion
}
