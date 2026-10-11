namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The paste rule of the clipboard's privacy guard (review PRIVA-1): the agent's input pastes only what get_clipboard
/// would return, because a paste followed by a read (get_ui_elements, a capture) discloses the clipboard. A fake
/// clipboard and a manual clock; no real clipboard or window is touched.
/// </summary>
public class ClipboardPasteGuardTests
{
    private const uint RightDown = 0x0008, RightUp = 0x0010;
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

    private static InputEvent[] RightClick => [InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp)];

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

        refusal.Should().NotBeNull().And.Contain("in a call of their own");
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

        Guard().Check(RightClick, Terminal).Should().Contain("right click in a terminal");
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
