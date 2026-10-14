namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The paste rule of the clipboard's privacy guard (review PRIVA-1): the agent's input pastes only what get_clipboard
/// would return, because a paste followed by a read (get_ui_elements, a capture) discloses the clipboard. A fake
/// clipboard and a manual clock; no real clipboard or window is touched.
/// </summary>
public class ClipboardPasteGuardTests
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
    private const int AppProcessId = 2001;
    private static readonly nint Terminal = 0x4000, Editor = 0x5000;

    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ManualClock _clock = new();
    private readonly ClipboardOrigin _origin;
    private readonly Mock<INativeWindowWrapper> _windows = new();

    public ClipboardPasteGuardTests()
    {
        _origin = new ClipboardOrigin(_clipboard, _clock, Timeout.InfiniteTimeSpan);
        _windows.Setup(w => w.GetClassName(Terminal)).Returns("ConsoleWindowClass");
        _windows.Setup(w => w.GetClassName(Editor)).Returns("Notepad");
    }

    private ClipboardPasteGuard Guard(bool allowUserClipboard = false) =>
        new(_clipboard, _origin, new ClipboardOptions(allowUserClipboard), _windows.Object);

    private static List<InputEvent> Keys(string keys) => SendKeysParser.Parse(keys);

    private static InputEvent[] LeftClick => [InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp)];

    private static InputEvent[] RightClick =>[InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp)];

    [Theory]
    [InlineData("^v")]
    [InlineData("+{INSERT}")]
    [InlineData("^+v")]
    [InlineData("\u0016")]
    public void PastingTheUsersClipboard_IsRefused(string keys)
    {
        _clipboard.UserCopies("hunter2");

        var refusal = Guard().Check(Keys(keys), Editor);

        refusal.Should().NotBeNull().And.Contain("did not put there").And.Contain("Nothing was sent").And.Contain("set_clipboard");
        refusal.Should().NotContain("hunter2");
        _clipboard.Reads.Should().Be(0, "the user's content is refused without opening the clipboard");
    }

    [Theory]
    [InlineData("^a^c^v")]
    [InlineData("^x{END}^v")]
    public void CopyAndPasteInOneCall_IsRefused_WithAdviceToSplitIt(string keys)
    {
        // The copy has not happened when the paste is checked, so the clipboard still holds the user's content
        _clipboard.UserCopies("hunter2");

        var refusal = Guard().Check(Keys(keys), Editor);

        refusal.Should().NotBeNull().And.Contain("separate calls").And.Contain("Nothing was sent");
    }

    [Theory]
    [InlineData("^c^v")]
    [InlineData("^c{TAB}^v")]
    [InlineData("^x^v")]
    [InlineData("^{INSERT}+{INSERT}")]
    [InlineData("+{DELETE}^v")]
    [InlineData("^c\u0016")]
    [InlineData("\u0003\u0016")]
    [InlineData("\u0018^v")]
    public void CopyThenPasteInOneCall_IsRefused_EvenWhenTheClipboardHoldsSetClipboardsText(string keys)
    {
        // The paste would paste what the copy takes, which is never checked: a password manager's private copy included
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");
        var reads = 0;
        _clipboard.OnSequenceRead = () => reads++;

        var refusal = Guard().Check(Keys(keys), Editor);

        refusal.Should().NotBeNull().And.Contain("copies and then pastes").And.Contain("separate calls").And.Contain("Nothing was sent");
        reads.Should().Be(0, "the order of the keys alone refuses it");
    }

    [Fact]
    public void CopyThenPasteInOneCall_IsRefused_AfterTheAgentsOwnCopy_AndWithWamAllowUserClipboard()
    {
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);
        _clipboard.AppCopies(AppProcessId, "the selection");

        Guard().Check(Keys("^c^v"), Editor).Should().Contain("separate calls");
        Guard(allowUserClipboard: true).Check(Keys("^c^v"), Editor).Should().Contain("separate calls");
    }

    [Fact]
    public void CopyAfterThePaste_IsCheckedAsAPlainPaste()
    {
        // The paste comes first, so it pastes what is on the clipboard now, which the check reads
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");

        Guard().Check(Keys("^v^a^c"), Editor).Should().BeNull();

        _clipboard.UserCopies("hunter2");
        Guard().Check(Keys("^v^c"), Editor).Should().Contain("did not put there").And.NotContain("separate calls");
    }

    [Fact]
    public void ARightClickInATerminal_AfterCopyKeys_IsRefused()
    {
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");
        var batch = Keys("^c").Concat(RightClick).ToList();

        Guard().Check(batch, Terminal).Should().Contain("separate calls");
    }

    [Fact]
    public void APasteRightAfterCopyKeysTheAppHasNotActedOnYet_IsRefused_UntilTheCopyWindowPasses()
    {
        // The copy keys of the previous call wait in the app's queue ahead of the paste: checked now, the paste would
        // paste what the copy takes next, unchecked
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);

        Guard().Check(Keys("^v"), Editor).Should().Contain("not copied yet").And.Contain("Nothing was sent");

        _clock.Advance(ClipboardOrigin.CopyKeysWindow + TimeSpan.FromMilliseconds(1));
        Guard().Check(Keys("^v"), Editor).Should().BeNull("the copy window passed without a copy");
    }

    [Fact]
    public void APasteAfterCopyKeysAndAClickToTheSameApp_IsRefused_UntilTheAppCopies()
    {
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);
        _origin.InputSent(_origin.BeforeInput(LeftClick, AppProcessId)!);

        Guard().Check(Keys("^v"), Editor).Should().Contain("not copied yet");
    }

    [Fact]
    public void APasteAfterCopyKeysAndSetClipboard_IsRefused_UntilTheAppCopies()
    {
        // Another change of the clipboard after the keys does not mean the app has acted on them
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");

        Guard().Check(Keys("^v"), Editor).Should().Contain("not copied yet");

        _clipboard.AppCopies(AppProcessId, "the selection");
        Guard().Check(Keys("^v"), Editor).Should().BeNull("the app copied for the agent");
    }

    [Fact]
    public void APasteAfterCopyKeysThatOpenNoCopyWindow_IsRefused_UntilTheAppCopies()
    {
        // Arrange - two empty copy windows: copy keys within the quiet time open none, but still wait in the app's queue
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");
        for (var i = 0; i < 3; i++)
        {
            _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);
            _clock.Advance(TimeSpan.FromSeconds(5.1));
        }

        // Act
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);

        // Assert
        Guard().Check(Keys("^v"), Editor).Should().Contain("not copied yet");
    }

    [Fact]
    public void ThePrivateCopyOfThePreviousCall_IsRefusedInTheNextCall()
    {
        // The split the refusal asks for: the paste in its own call is checked against what the copy put there
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);
        _clipboard.AppCopies(AppProcessId, "hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);

        Guard().Check(Keys("^v"), Editor).Should().Contain("marked private");
    }

    [Fact]
    public void InputThatDoesNotPaste_IsNotChecked()
    {
        _clipboard.UserCopies("hunter2");
        var reads = 0;
        _clipboard.OnSequenceRead = () => reads++;

        Guard().Check(Keys("hello{ENTER}^c"), Editor).Should().BeNull();
        Guard().Check(RightClick, Editor).Should().BeNull("a right click pastes only in a terminal");
        reads.Should().Be(0);
    }

    [Fact]
    public void PastingWhatSetClipboardPut_IsAllowed()
    {
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");

        Guard().Check(Keys("^v"), Editor).Should().BeNull();
    }

    [Fact]
    public void ContentNumberedAsSetClipboards_ButWithOtherText_IsRefused()
    {
        // Another app replaced the text right after set_clipboard, and the server saw its number as its own
        _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(_clipboard.Sequence, "Other text");

        Guard().Check(Keys("^v"), Editor).Should().Contain("did not put there");
    }

    [Fact]
    public void PastingTheAgentsOwnCopy_IsAllowed()
    {
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);
        _clipboard.AppCopies(AppProcessId, "the selection");

        Guard().Check(Keys("^v"), Editor).Should().BeNull();
    }

    [Fact]
    public void PastingTheAgentsCopy_MarkedPrivate_IsRefused()
    {
        // The agent clicked a password manager's Copy button: get_clipboard would not return it, so it is not pasted
        _origin.InputSent(_origin.BeforeInput(Keys("^c"), AppProcessId)!);
        _clipboard.AppCopies(AppProcessId, "hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);

        Guard().Check(Keys("^v"), Editor).Should().Contain("marked private");
    }

    [Fact]
    public void PastingContentTheUserLetTheAgentRead_IsAllowed_AndOnlyThatContent()
    {
        _clipboard.UserCopies("a shared link");
        _origin.UserAllowed(_clipboard.Sequence);

        Guard().Check(Keys("^v"), Editor).Should().BeNull();

        _clipboard.UserCopies("hunter2");
        Guard().Check(Keys("^v"), Editor).Should().Contain("did not put there");
    }

    [Fact]
    public void WithWamAllowUserClipboard_TheUsersClipboardIsPasted_ButNeverPrivateContent()
    {
        _clipboard.UserCopies("a shared link");
        Guard(allowUserClipboard: true).Check(Keys("^v"), Editor).Should().BeNull();

        _clipboard.UserCopies("hunter2", ClipboardPrivacyMarkers.NotForClipboardHistory);
        Guard(allowUserClipboard: true).Check(Keys("^v"), Editor).Should().Contain("marked private");
    }

    [Fact]
    public void ARightClickInATerminal_IsAPaste()
    {
        _clipboard.UserCopies("rm -rf ~");

        Guard().Check(RightClick, Terminal).Should().Contain("right or middle click in a terminal");
    }

    [Fact]
    public void AMiddleClickInATerminal_IsAPaste()
    {
        _clipboard.UserCopies("rm -rf ~");

        Guard().Check([InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(0x0020), InputEvent.Mouse(0x0040)], Terminal)
            .Should().Contain("right or middle click in a terminal");
    }

    [Fact]
    public void ARightClickInVsCode_IsAPaste_ButNotInABrowserWithTheSameClass()
    {
        // VS Code's integrated terminal pastes on a right click; its window class is every Chromium browser's too
        const uint codeProcess = 7100, chromeProcess = 7200;
        nint code = 0x6000, chrome = 0x7000;
        _windows.Setup(w => w.GetClassName(It.Is<nint>(h => h == code || h == chrome))).Returns("Chrome_WidgetWin_1");
        _windows.Setup(w => w.GetWindowThreadProcessId(code)).Returns(codeProcess);
        _windows.Setup(w => w.GetWindowThreadProcessId(chrome)).Returns(chromeProcess);
        _windows.Setup(w => w.GetProcessName(codeProcess)).Returns("Code");
        _windows.Setup(w => w.GetProcessName(chromeProcess)).Returns("chrome");
        _clipboard.UserCopies("rm -rf ~");

        Guard().Check(RightClick, code).Should().Contain("right or middle click in a terminal");
        Guard().Check(Keys("^c").Concat(RightClick).ToList(), code).Should().Contain("copies and then pastes");
        Guard().Check(RightClick, chrome).Should().BeNull("a right click in a browser opens its context menu");
    }

    [Fact]
    public void TheProcessOfTheReceivingWindow_IsReadOnlyForARightClick()
    {
        _clipboard.UserCopies("hello");

        Guard().Check(Keys("^v"), Editor);
        Guard().Check(LeftClick, Editor);

        _windows.Verify(w => w.GetWindowThreadProcessId(It.IsAny<nint>()), Times.Never);
        _windows.Verify(w => w.GetProcessName(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public void WinV_ClipboardHistory_IsRefused_UnlessTheUserAllowsTheirClipboard()
    {
        // History holds the user's earlier copies, whatever is on the clipboard now
        var outcome = _clipboard.WriteText("Meeting notes");
        _origin.ServerWrote(outcome.SequenceNumber, "Meeting notes");
        var winV = SendKeysParser.Parse("#v", windowsModifier: true);

        Guard().Check(winV, Editor).Should().Contain("clipboard history");
        Guard(allowUserClipboard: true).Check(winV, Editor).Should().BeNull();
    }

    [Fact]
    public void AClipboardThatCannotBeOpened_RefusesThePaste()
    {
        _clipboard.UserCopies("a shared link");
        _clipboard.CanOpen = false;

        Guard(allowUserClipboard: true).Check(Keys("^v"), Editor).Should().Contain("could not be checked");
    }

    [Fact]
    public void AClipboardThatChangesWhileChecked_RefusesThePaste()
    {
        _clipboard.UserCopies("a shared link");
        _clipboard.OnRead = () => _clipboard.UserCopies("hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);

        Guard(allowUserClipboard: true).Check(Keys("^v"), Editor).Should().Contain("could not be checked");
    }
}
