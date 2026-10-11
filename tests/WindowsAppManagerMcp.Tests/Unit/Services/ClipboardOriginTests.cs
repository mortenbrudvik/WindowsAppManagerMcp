namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Which clipboard content counts as the agent's (roadmap M2.16): the content set_clipboard put there, and the first copy
/// the application the agent's copy keys or click went to made within the copy window. A fake clipboard and a manual
/// clock; the copy window's timer is off, and the tests poll by hand (or let a read poll).
/// </summary>
public class ClipboardOriginTests
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
    private const ushort VkControl = 0x11, VkShift = 0x10, VkC = 0x43, VkV = 0x56, VkInsert = 0x2D, VkDelete = 0x2E;

    /// <summary>The process of the app the agent's input goes to.</summary>
    private const int AppProcessId = 2001;

    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ManualClock _clock = new();
    private readonly ClipboardOrigin _sut;

    public ClipboardOriginTests()
    {
        _sut = new ClipboardOrigin(_clipboard, _clock, Timeout.InfiniteTimeSpan);
    }

    private static InputEvent[] LeftClick => [InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp)];

    private void CopyKeysSent(int targetProcessId = AppProcessId)
    {
        var mark = _sut.BeforeInput(SendKeysParser.Parse("^c"), targetProcessId);
        mark.Should().NotBeNull();
        _sut.InputSent(mark!);
    }

    private void ClickSent()
    {
        var mark = _sut.BeforeInput(LeftClick, AppProcessId);
        mark.Should().NotBeNull();
        _sut.InputSent(mark!);
    }

    private void AppCopies(string text) => _clipboard.AppCopies(AppProcessId, text);

    #region Input that can copy

    [Theory]
    [InlineData("^c")]
    [InlineData("^x")]
    [InlineData("^+c")] // a terminal's copy
    [InlineData("^{INSERT}")]
    [InlineData("+{DELETE}")]
    [InlineData("{HOME}+{END}^c")]
    public void CopyKeys_CanCopy(string keys)
    {
        ClipboardOrigin.CanCopy(SendKeysParser.Parse(keys)).Should().BeTrue();
        ClipboardOrigin.CopyTrigger(SendKeysParser.Parse(keys)).Should().Be(ClipboardCopyTrigger.CopyKeys);
    }

    [Theory]
    [InlineData("^v")]
    [InlineData("c")]
    [InlineData("{INSERT}")]
    [InlineData("{DELETE}")]
    [InlineData("%c")]
    public void OtherKeys_CannotCopy(string keys)
    {
        ClipboardOrigin.CanCopy(SendKeysParser.Parse(keys)).Should().BeFalse();
    }

    [Fact]
    public void ALeftButtonPress_CanCopy_AndAWheelOrRightClickCannot()
    {
        ClipboardOrigin.CopyTrigger(LeftClick).Should().Be(ClipboardCopyTrigger.Click, "a click on a Copy menu item copies");
        ClipboardOrigin.CanCopy([InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp)])
            .Should().BeFalse();
        ClipboardOrigin.CanCopy([InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Wheel(-120)]).Should().BeFalse();
        ClipboardOrigin.CanCopy([InputEvent.Unicode('c', 0), InputEvent.Unicode('c', 2)]).Should().BeFalse("typed text copies nothing");
        ClipboardOrigin.CanCopy([InputEvent.Key(VkV, 0), InputEvent.Key(VkV, 2)]).Should().BeFalse();
        ClipboardOrigin.CanCopy([InputEvent.Key(VkShift, 0), InputEvent.Key(VkInsert, 0), InputEvent.Key(VkInsert, 2), InputEvent.Key(VkShift, 2)])
            .Should().BeFalse("Shift+Insert pastes");
        ClipboardOrigin.CanCopy([InputEvent.Key(VkControl, 0), InputEvent.Key(VkDelete, 0), InputEvent.Key(VkDelete, 2), InputEvent.Key(VkControl, 2)])
            .Should().BeFalse("Ctrl+Delete deletes a word");
        ClipboardOrigin.CopyTrigger([InputEvent.Key(VkControl, 0), InputEvent.Key(VkC, 0), InputEvent.Key(VkC, 2), InputEvent.Key(VkControl, 2)])
            .Should().Be(ClipboardCopyTrigger.CopyKeys);
    }

    [Fact]
    public void BeforeInput_ReadsTheSequenceNumberOnlyForInputThatCanCopy()
    {
        var reads = 0;
        _clipboard.OnSequenceRead = () => reads++;

        _sut.BeforeInput(SendKeysParser.Parse("hello"), AppProcessId).Should().BeNull();
        reads.Should().Be(0);

        _sut.BeforeInput(SendKeysParser.Parse("^c"), AppProcessId).Should().Be(new ClipboardCopyMark(100, ClipboardCopyTrigger.CopyKeys, AppProcessId));
        reads.Should().Be(1);
    }

    #endregion

    #region set_clipboard

    [Fact]
    public void TheUsersContent_IsNotFromTheAgent()
    {
        _clipboard.UserCopies("hunter2");

        _sut.Classify(_clipboard.Sequence).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void WhatSetClipboardWrote_IsTheAgents_UntilTheClipboardChanges()
    {
        _sut.ServerWrote(120, "Meeting notes");

        _sut.Classify(120).Should().Be(ClipboardOriginKind.SetClipboard);
        _sut.Classify(121).Should().Be(ClipboardOriginKind.NotFromAgent, "the user copied after it");
    }

    [Fact]
    public void ASequenceNumberOfZero_CannotBeTold_SoItIsNotTheAgents()
    {
        _sut.ServerWrote(0, "Meeting notes");

        _sut.Classify(0).Should().Be(ClipboardOriginKind.NotFromAgent);
        _sut.IsServerText("Meeting notes", 13).Should().BeFalse("the write's number was not the server's");
    }

    [Fact]
    public void IsServerText_TakesTheWholeText_OrAPrefixOfTheSameLength()
    {
        _sut.ServerWrote(120, "Meeting notes");

        _sut.IsServerText("Meeting notes", 13).Should().BeTrue();
        _sut.IsServerText("Meeting", 13).Should().BeTrue("a read cut to maxLength");
        _sut.IsServerText("Meeting", 7).Should().BeFalse("another text that starts the same");
        _sut.IsServerText("hunter2 notes", 13).Should().BeFalse();
        _sut.IsServerText(null, 0).Should().BeFalse();
    }

    #endregion

    #region The agent's copy

    [Fact]
    public void RightAfterTheCopyKeys_TheUnchangedClipboard_IsPending_AndNotTheAgents()
    {
        CopyKeysSent();

        _sut.IsCopyPending(100).Should().BeTrue();
        _sut.Classify(100).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void AfterAClick_NothingIsPending()
    {
        // Most clicks copy nothing: a read after one does not wait for a copy
        ClickSent();

        _sut.IsCopyPending(100).Should().BeFalse();
    }

    [Fact]
    public void ACopyByTheTargetWithinTheCopyWindow_IsTheAgents_AndStaysItsAfterTheWindow()
    {
        // Arrange - the app copies 2 s after the keys
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(2));
        AppCopies("the selection");
        _sut.Poll();

        // Act - the agent reads a minute later
        _clock.Advance(TimeSpan.FromMinutes(1));
        _sut.Poll();

        // Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
        _sut.IsCopyPending(101).Should().BeFalse();
    }

    [Fact]
    public void AReadWithinTheCopyWindow_TakesTheCopy_WithoutAPoll()
    {
        CopyKeysSent();
        AppCopies("the selection");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
        _clock.Advance(TimeSpan.FromMinutes(1));
        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void ACopyAfterTheCopyWindow_IsNotTheAgents()
    {
        // Arrange - the keys copied nothing (no selection); 10 s later the user copies a password in the same app
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _sut.Poll();
        _clock.Advance(TimeSpan.FromSeconds(9));
        AppCopies("hunter2");
        _sut.Poll();

        // Act & Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void TheCopyWindowOfAClick_IsShorterThanOfCopyKeys()
    {
        ClickSent();
        _clock.Advance(ClipboardOrigin.ClickWindow + TimeSpan.FromMilliseconds(100));
        AppCopies("copied late");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ACopyByTheTargetAfterAClick_IsTheAgents()
    {
        // A click on a Copy menu item
        ClickSent();
        AppCopies("the selection");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void AUserCopyInAnotherApp_WithinTheCopyWindow_IsNotTheAgents()
    {
        // Arrange - the agent's copy keys copied nothing; the user copies a password elsewhere a second later
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _clipboard.UserCopies("hunter2");

        // Act & Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void AUserCopyAfterTheAgentsCopy_WithinTheCopyWindow_IsNotTheAgents()
    {
        CopyKeysSent();
        AppCopies("the selection");
        _sut.Poll();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        _clipboard.UserCopies("hunter2");

        _sut.Classify(102).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ALaterCopyInTheSameApp_AfterTheCopySettled_IsNotTheAgents()
    {
        // Only the first copy counts: the user copying in the same app a second later is not the agent
        CopyKeysSent();
        AppCopies("the selection");
        _sut.Poll();
        _clock.Advance(TimeSpan.FromSeconds(1));
        AppCopies("hunter2");

        _sut.Classify(102).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ChangesOfOneCopy_RightAfterEachOther_AreTheAgents()
    {
        // An app can change the clipboard a few times in one copy
        CopyKeysSent();
        AppCopies("the selection");
        _sut.Poll();
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        AppCopies("the selection, rendered");

        _sut.Classify(102).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void AChangeByAnotherApp_BeforeTheTargetCopies_DoesNotEndTheWindow()
    {
        CopyKeysSent();
        _clipboard.UserCopies("something of the user's");
        _sut.Poll();
        AppCopies("the selection");

        _sut.Classify(102).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void TheAgentsCopy_StaysItsAfterALaterClick()
    {
        // Arrange - the agent copies, then clicks the paste target
        CopyKeysSent();
        AppCopies("the selection");
        _sut.Poll();
        ClickSent();

        // Act & Assert - not pending, and still the agent's
        _sut.IsCopyPending(101).Should().BeFalse();
        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void CopyKeysAfterACopy_ArePending_UntilTheAppCopiesAgain()
    {
        CopyKeysSent();
        AppCopies("the first line");
        _sut.Poll();
        CopyKeysSent();

        _sut.IsCopyPending(101).Should().BeTrue("the second copy has not happened yet");
        AppCopies("the second line");
        _sut.IsCopyPending(102).Should().BeFalse();
        _sut.Classify(102).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void ACopyOfTheMarkBefore_IsSeenBeforeANewMarkReplacesIt()
    {
        // The app copies after the keys, and the agent clicks before any poll saw the copy
        CopyKeysSent();
        AppCopies("the selection");
        ClickSent();

        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void WithoutAKnownTarget_NoCopyCounts()
    {
        CopyKeysSent(targetProcessId: 0);
        _clipboard.AppCopies(0, "the selection");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ACopyWithoutAClipboardOwner_DoesNotCount()
    {
        // An app that empties the clipboard without a window of its own: who copied cannot be told
        CopyKeysSent();
        _clipboard.AppCopies(0, "the selection");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void AnOwnerReadWhileTheClipboardChanges_DoesNotCount()
    {
        // Arrange - the user's copy (101) is read, and the app copies (102) right before the owner is read: the owner
        // then is the app's, but the number read is the user's
        CopyKeysSent();
        _clipboard.UserCopies("hunter2");
        _clipboard.OnOwnerRead = () =>
        {
            _clipboard.OnOwnerRead = null;
            AppCopies("the selection");
        };

        // Act
        _sut.Poll();

        // Assert - the user's 101 was not taken with the app's owner; the app's 102 counts once read consistently
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
        _sut.Classify(102).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void AfterInputThatCanCopy_ATimerWatchesTheSequenceNumber()
    {
        // The copy window must see a change even when nothing reads the clipboard within it
        var reads = 0;
        _clipboard.OnSequenceRead = () => Interlocked.Increment(ref reads);
        using var origin = new ClipboardOrigin(_clipboard, TimeProvider.System, TimeSpan.FromMilliseconds(5));

        origin.InputSent(new ClipboardCopyMark(100, ClipboardCopyTrigger.CopyKeys, AppProcessId));

        SpinWait.SpinUntil(() => Volatile.Read(ref reads) >= 3, TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    #endregion
}
