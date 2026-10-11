using System.Text;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// get_clipboard's privacy guard and set_clipboard (roadmap M2.16): only the agent's own content is returned by default,
/// content marked private never, and the user's clipboard only with their setting or consent. A fake clipboard; the real
/// one is the user's.
/// </summary>
public class ClipboardServiceTests
{
    /// <summary>The process of the app the agent's input goes to.</summary>
    private const int AppProcessId = 2001;

    private const uint LeftDown = 0x0002, LeftUp = 0x0004;

    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ManualClock _clock = new();
    private readonly ClipboardOrigin _origin;

    public ClipboardServiceTests()
    {
        _origin = new ClipboardOrigin(_clipboard, _clock, Timeout.InfiniteTimeSpan);
    }

    private ClipboardService CreateSut(bool allowUserClipboard = false, TimeSpan? copyWait = null) =>
        new(_clipboard, _origin, new ClipboardOptions(allowUserClipboard), copyWait: copyWait ?? TimeSpan.FromMilliseconds(200));

    private Task<GetClipboardResult> ReadAsync(ClipboardService sut, ClipboardFormat format = ClipboardFormat.Text, int maxLength = 20_000,
        ClipboardConsent? consent = null) => sut.ReadAsync(format, maxLength, consent, CancellationToken.None);

    private void CopyKeysSent() => _origin.InputSent(_origin.BeforeInput(SendKeysParser.Parse("^c"), AppProcessId)!);

    private void ClickSent() => _origin.InputSent(_origin.BeforeInput(
        [InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp)], AppProcessId)!);

    private void AgentCopies(string text)
    {
        CopyKeysSent();
        _clipboard.AppCopies(AppProcessId, text);
    }

    #region The agent's content

    [Fact]
    public async Task WhatSetClipboardPut_IsReturned_AlthoughItIsMarkedPrivate()
    {
        // Arrange - set_clipboard keeps its text out of clipboard history, with the same markers password managers use
        var sut = CreateSut();
        sut.SetText("Meeting notes").Success.Should().BeTrue();

        // Act
        var result = await ReadAsync(sut);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("Meeting notes");
        result.Origin.Should().Be(ClipboardContentOrigin.SetClipboard);
        result.Length.Should().Be(13);
        result.Truncated.Should().BeFalse();
        _clipboard.LastReadPrivate.Should().BeTrue("only the server's own private-marked content is read");
    }

    [Fact]
    public async Task TheAgentsCopy_IsReturned_WithItsOrigin()
    {
        var sut = CreateSut();
        AgentCopies("the selected log line");

        var result = await ReadAsync(sut);

        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("the selected log line");
        result.Origin.Should().Be(ClipboardContentOrigin.AgentInput);
        _clipboard.LastReadPrivate.Should().BeFalse();
    }

    [Fact]
    public async Task RightAfterTheCopyKeys_TheReadWaitsForTheApplicationToCopy()
    {
        // Arrange - the app copies while the read waits
        var sut = CreateSut(copyWait: TimeSpan.FromSeconds(5));
        CopyKeysSent();
        var sequenceReads = 0;
        _clipboard.OnSequenceRead = () =>
        {
            if (++sequenceReads == 6)
            {
                _clipboard.AppCopies(AppProcessId, "copied late");
            }
        };

        // Act
        var result = await ReadAsync(sut);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("copied late");
        result.Origin.Should().Be(ClipboardContentOrigin.AgentInput);
    }

    [Fact]
    public async Task CopyKeysThatCopyNothing_AreNothingCopied_AfterTheWait_AndNothingIsRead()
    {
        // Arrange - nothing was selected: the clipboard keeps the user's content
        _clipboard.UserCopies("hunter2");
        CopyKeysSent();
        var sut = CreateSut(copyWait: TimeSpan.FromMilliseconds(100));

        // Act
        var result = await ReadAsync(sut);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ClipboardErrorCode.NothingCopied);
        result.Error.Should().Contain("did not change").And.Contain("Check the selection");
        result.Text.Should().BeNull();
        _clipboard.Reads.Should().Be(0, "the user's content is not even read");
    }

    [Fact]
    public async Task CopyKeysThatCopyNothing_AfterAnEarlierCopy_AreNothingCopied_NotTheEarlierCopy()
    {
        // Arrange - the agent copied one line, then pressed Ctrl+C again with nothing selected
        AgentCopies("the first line");
        CopyKeysSent();
        var sut = CreateSut(copyWait: TimeSpan.FromMilliseconds(100));

        // Act
        var result = await ReadAsync(sut);

        // Assert - returning the first line would look like the second copy
        result.ErrorCode.Should().Be(ClipboardErrorCode.NothingCopied);
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task TheAgentsCopy_IsStillReturned_AfterALaterClick()
    {
        // Arrange - the agent copies, then clicks the paste target: a click that copies nothing
        AgentCopies("the selected log line");
        ClickSent();
        var sut = CreateSut(copyWait: TimeSpan.FromSeconds(5));

        // Act
        var started = DateTime.UtcNow;
        var result = await ReadAsync(sut);

        // Assert - no wait for a copy after a click, and no question about the agent's own copy
        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("the selected log line");
        result.Origin.Should().Be(ClipboardContentOrigin.AgentInput);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AUserCopyAfterTheAgentsCopy_IsNotReturned()
    {
        // Arrange - inside the copy window, the user copies in another app
        AgentCopies("the selected log line");
        _clipboard.UserCopies("hunter2");

        // Act
        var result = await ReadAsync(CreateSut());

        // Assert
        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardNotFromAgent);
        result.Text.Should().BeNull();
        _clipboard.Reads.Should().Be(0);
    }

    #endregion

    #region The user's content

    [Fact]
    public async Task TheUsersClipboard_IsClipboardNotFromAgent_WithoutReadingIt()
    {
        _clipboard.UserCopies("hunter2");

        var result = await ReadAsync(CreateSut());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardNotFromAgent);
        result.Error.Should().Contain("did not put there").And.Contain("send_keys '^c'").And.NotContain("hunter2");
        result.Text.Should().BeNull();
        result.Length.Should().BeNull("not even the size of the user's content is told");
        _clipboard.Reads.Should().Be(0);
    }

    [Fact]
    public async Task TheUsersClipboard_IsReturned_WhenTheUserAllowsItInTheSettings()
    {
        _clipboard.UserCopies("a shared link");

        var result = await ReadAsync(CreateSut(allowUserClipboard: true));

        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("a shared link");
        result.Origin.Should().Be(ClipboardContentOrigin.User);
    }

    [Fact]
    public async Task TheUsersClipboard_IsReturned_WithTheirConsentToThatContent()
    {
        _clipboard.UserCopies("a shared link");
        var sut = CreateSut();

        var result = await ReadAsync(sut, consent: new ClipboardConsent(sut.SequenceNumber()));

        result.Success.Should().BeTrue(result.Error);
        result.Origin.Should().Be(ClipboardContentOrigin.User);
    }

    [Fact]
    public async Task ContentThatReplacedWhatTheUserAgreedTo_IsNotReturned()
    {
        // Arrange - the user said yes to what was there; something else was copied while they answered
        _clipboard.UserCopies("a shared link");
        var sut = CreateSut();
        var consent = new ClipboardConsent(sut.SequenceNumber());
        _clipboard.UserCopies("hunter2");

        // Act
        var result = await ReadAsync(sut, consent: consent);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardNotFromAgent);
        result.Error.Should().Contain("changed after the user allowed");
        result.Text.Should().BeNull();
    }

    [Theory]
    [InlineData(ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing, "ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData(ClipboardPrivacyMarkers.NotForClipboardHistory, "CanIncludeInClipboardHistory = 0")]
    [InlineData(ClipboardPrivacyMarkers.ClipboardViewerIgnore, "Clipboard Viewer Ignore")]
    public async Task ContentMarkedPrivate_IsNeverReturned_EvenWhenTheUserAllowsTheirClipboard(ClipboardPrivacyMarkers marker, string named)
    {
        // Arrange - a password manager copied a password
        _clipboard.UserCopies("hunter2", marker);

        // Act
        var result = await ReadAsync(CreateSut(allowUserClipboard: true));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardPrivate);
        result.Error.Should().Contain(named).And.NotContain("hunter2");
        result.Text.Should().BeNull();
        _clipboard.LastReadPrivate.Should().BeFalse("private content is not even read into the server");
    }

    [Fact]
    public async Task ContentMarkedPrivate_IsNotReturned_WhenTheAgentsCopyKeysCopiedIt()
    {
        // The agent pressed Ctrl+C in a password manager
        CopyKeysSent();
        _clipboard.AppCopies(AppProcessId, "hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);

        var result = await ReadAsync(CreateSut());

        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardPrivate);
        result.Text.Should().BeNull();
    }

    [Theory]
    [InlineData(false, ClipboardErrorCode.ClipboardNotFromAgent)]
    [InlineData(true, ClipboardErrorCode.ClipboardPrivate)]
    public async Task ContentThatReplacesTheServersOwnBeforeTheRead_IsJudgedByWhatWasRead(bool allowUserClipboard, ClipboardErrorCode expected)
    {
        // Arrange - set_clipboard's text; a password manager copies between the check and the open clipboard
        var sut = CreateSut(allowUserClipboard);
        sut.SetText("Meeting notes");
        _clipboard.OnRead = () => _clipboard.UserCopies("hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);

        // Act
        var result = await ReadAsync(sut);

        // Assert - the content read was not the server's, so it is the user's (and private): never returned, and its
        // number was not set_clipboard's, so it was not even read
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(expected);
        result.Text.Should().BeNull();
        _clipboard.LastPrivateContentSequence.Should().NotBe(0u, "set_clipboard's own text may be read");
        _clipboard.LastReadPrivate.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, ClipboardErrorCode.ClipboardNotFromAgent)]
    [InlineData(true, ClipboardErrorCode.ClipboardPrivate)]
    public async Task ContentWithSetClipboardsNumber_ThatIsNotItsText_IsNotTheServers(bool allowUserClipboard, ClipboardErrorCode expected)
    {
        // Arrange - another app replaced set_clipboard's text right before the server read the number it reports
        var sut = CreateSut(allowUserClipboard);
        sut.SetText("Meeting notes");
        _clipboard.Text = "hunter2";

        // Act
        var result = await ReadAsync(sut);

        // Assert - the private-content exemption holds only for the server's own text
        result.ErrorCode.Should().Be(expected);
        result.Text.Should().BeNull();
    }

    [Fact]
    public async Task ContentWithSetClipboardsNumber_AndOtherFormats_IsNotTheServers()
    {
        var sut = CreateSut();
        sut.SetText("Meeting notes");
        _clipboard.Html = Encoding.UTF8.GetBytes("<b>hunter2</b>");

        var result = await ReadAsync(sut, ClipboardFormat.Html);

        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardNotFromAgent, "set_clipboard puts only text there");
        _clipboard.LastPrivateContentSequence.Should().Be(0u, "only text may be read as set_clipboard's");
    }

    [Fact]
    public async Task SetClipboardsText_ReadAsHtml_IsFormatNotAvailable()
    {
        var sut = CreateSut();
        sut.SetText("Meeting notes");

        var result = await ReadAsync(sut, ClipboardFormat.Html);

        result.ErrorCode.Should().Be(ClipboardErrorCode.FormatNotAvailable);
        result.AvailableFormats.Should().Equal(ClipboardFormat.Text);
    }

    [Fact]
    public async Task SetClipboardsText_CutToMaxLength_IsStillTheServers()
    {
        var sut = CreateSut();
        sut.SetText("Meeting notes");

        var result = await ReadAsync(sut, maxLength: 7);

        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("Meeting");
        result.Origin.Should().Be(ClipboardContentOrigin.SetClipboard);
    }

    [Fact]
    public void ADecline_IsRememberedForThatContentOnly()
    {
        var sut = CreateSut();

        sut.UserDeclined(105);

        sut.WasDeclined(105).Should().BeTrue();
        sut.WasDeclined(106).Should().BeFalse("a new copy is new content");
        sut.UserDeclined(0);
        sut.WasDeclined(0).Should().BeFalse("0 cannot be told apart");
        sut.WasDeclined(105).Should().BeFalse("only the last decline is kept");
    }

    #endregion

    #region Formats and limits

    [Fact]
    public async Task LongText_IsCutToMaxLength_AndSaysSo()
    {
        var sut = CreateSut();
        sut.SetText(new string('a', 50));

        var result = await ReadAsync(sut, maxLength: 20);

        result.Text.Should().HaveLength(20);
        result.Length.Should().Be(50);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task TheCut_NeverSplitsASurrogatePair()
    {
        var sut = CreateSut();
        sut.SetText("abc\U0001F600def");

        var result = await ReadAsync(sut, maxLength: 4);

        result.Text.Should().Be("abc");
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Html_ReturnsTheFragment_AndTheSourceUrl()
    {
        // Arrange - a browser's copy, by the agent
        AgentCopies("Hello world");
        _clipboard.Html = ClipboardHtmlTests.CfHtml("<b>Hello</b> world", "https://example.com/page");

        // Act
        var result = await ReadAsync(CreateSut(), ClipboardFormat.Html);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Format.Should().Be(ClipboardFormat.Html);
        result.Text.Should().Be("<b>Hello</b> world");
        result.SourceUrl.Should().Be("https://example.com/page");
    }

    [Fact]
    public async Task Files_ReturnsThePaths_UntilMaxLength()
    {
        // Arrange - File Explorer's copy of three files, by the agent
        AgentCopies("ignored");
        _clipboard.Text = null;
        _clipboard.Files = [@"C:\a.txt", @"C:\b.txt", @"C:\c.txt"];

        // Act
        var result = await ReadAsync(CreateSut(), ClipboardFormat.Files, maxLength: 18);

        // Assert
        result.Files.Should().Equal(@"C:\a.txt", @"C:\b.txt");
        result.Length.Should().Be(3);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task AFormatTheClipboardDoesNotHold_IsFormatNotAvailable_WithWhatItHolds()
    {
        AgentCopies("plain text only");

        var result = await ReadAsync(CreateSut(), ClipboardFormat.Files);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ClipboardErrorCode.FormatNotAvailable);
        result.AvailableFormats.Should().Equal(ClipboardFormat.Text);
        result.Error.Should().Contain("holds text");
    }

    [Fact]
    public async Task AnEmptyClipboard_IsFormatNotAvailable()
    {
        AgentCopies("x");
        _clipboard.Text = null;

        var result = await ReadAsync(CreateSut());

        result.ErrorCode.Should().Be(ClipboardErrorCode.FormatNotAvailable);
        result.AvailableFormats.Should().BeEmpty();
    }

    [Fact]
    public async Task AClipboardAnotherAppHolds_IsClipboardBusy()
    {
        AgentCopies("x");
        _clipboard.CanOpen = false;

        var result = await ReadAsync(CreateSut());

        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardBusy);
    }

    [Fact]
    public async Task DataThatCannotBeRead_IsClipboardFailed()
    {
        AgentCopies("x");
        _clipboard.ReadError = "GetClipboardData failed (Win32 error 5: Access is denied.)";

        var result = await ReadAsync(CreateSut());

        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardFailed);
        result.Error.Should().Contain("Win32 error 5");
    }

    #endregion

    #region set_clipboard

    [Fact]
    public void SetText_PutsTheTextOnTheClipboard_KeptOutOfHistory()
    {
        var result = CreateSut().SetText("Meeting notes");

        result.Success.Should().BeTrue();
        result.Length.Should().Be(13);
        result.ExcludedFromHistory.Should().BeTrue();
        _clipboard.Text.Should().Be("Meeting notes");
    }

    [Fact]
    public void SetText_ClipboardHeldByAnotherApp_IsClipboardBusy_AndIsNotTheAgents()
    {
        _clipboard.CanOpen = false;

        var result = CreateSut().SetText("x");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardBusy);
        _origin.Classify(_clipboard.Sequence).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void SetText_RefusedByWindows_IsClipboardFailed()
    {
        _clipboard.WriteError = "SetClipboardData failed (Win32 error 1418)";

        var result = CreateSut().SetText("x");

        result.ErrorCode.Should().Be(ClipboardErrorCode.ClipboardFailed);
        result.Error.Should().Contain("1418");
    }

    #endregion

    [Fact]
    public void AValueOfTheSettingThatIsNotUnderstood_IsLogged()
    {
        var logger = new RecordingLogger<ClipboardService>();

        _ = new ClipboardService(_clipboard, _origin, ClipboardOptions.FromEnvironment(_ => "maybe"), logger);

        logger.Entries.Should().ContainSingle().Which.Message.Should().Contain("WAM_ALLOW_USER_CLIPBOARD 'maybe'");
    }
}
