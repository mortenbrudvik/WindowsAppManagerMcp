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
    public void CopyKeysFollowedByAClickToTheSameApp_StayPending_UntilTheAppCopies()
    {
        // The click joins the copy keys' run; the keys may still wait in the app's queue
        CopyKeysSent();
        ClickSent();

        _sut.IsCopyPending(100).Should().BeTrue("the click does not undo the copy keys");
        _sut.IsUnchangedSinceCopyKeys(100).Should().BeTrue();
        AppCopies("the selection");
        _sut.IsCopyPending(101).Should().BeFalse();
    }

    [Fact]
    public void CopyKeys_StayPending_AfterAnotherAppChangesTheClipboard()
    {
        // Arrange - another app changes the clipboard before the target has acted on the keys
        CopyKeysSent();
        _clipboard.UserCopies("something of the user's");

        // Act & Assert - the target has not copied yet
        _sut.IsCopyPending(101).Should().BeTrue("the change was not the target's");
        _sut.IsUnchangedSinceCopyKeys(101).Should().BeFalse("get_clipboard reads (or refuses) the other app's content");
        AppCopies("the selection");
        _sut.IsCopyPending(102).Should().BeFalse();
        _sut.Classify(102).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void CopyKeys_AreNoLongerPending_AfterTheCopyKeysWindow()
    {
        CopyKeysSent();
        _clipboard.UserCopies("something of the user's");
        _clock.Advance(CopyKeysWindowAndABit);

        _sut.IsCopyPending(101).Should().BeFalse();
        _sut.IsCopyPending(100).Should().BeFalse();
    }

    [Fact]
    public void CopyKeysThatOpenNoWindow_AreStillPending_UntilTheAppCopies()
    {
        // Arrange - after two empty windows, copy keys within the quiet time open no copy window: the app's copy is not
        // the agent's, but the keys still wait in its queue
        for (var i = 0; i < 3; i++)
        {
            CopyKeysSent();
            _clock.Advance(TimeSpan.FromSeconds(5.1));
        }

        // Act
        CopyKeysSent();

        // Assert
        _sut.IsCopyPending(100).Should().BeTrue("a paste now would follow the queued copy keys");
        AppCopies("hunter2");
        _sut.IsCopyPending(101).Should().BeFalse("the app the keys went to copied");
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
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

    #region Harmless input does not keep the window open (review PRIVA-2)

    [Fact]
    public void RepeatedCopyKeysThatCopyNothing_DoNotMoveTheWindowsStart()
    {
        // Arrange - empty ^c every 4 s: each lands inside the window the first one opened
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(4));
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(2));

        // Act - 6 s after the first keys, 2 s after the last, the user copies in the same app
        AppCopies("hunter2");

        // Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ClicksEvery1_9Seconds_DoNotKeepTheWindowOpen()
    {
        // Arrange - a click every 1.9 s for 19 s; none copies
        for (var i = 0; i < 10; i++)
        {
            ClickSent();
            _clock.Advance(TimeSpan.FromSeconds(1.9));
            _sut.Poll();
        }

        // Act - the user copies in the same app, 0.1 s before the next click would come
        AppCopies("hunter2");

        // Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ClicksRightAfterAnEmptyClickWindow_OpenNoNewWindow_UntilTheAgentStopsClicking()
    {
        // Arrange - the first click's window passes without a copy; the agent keeps clicking
        ClickSent();
        _clock.Advance(ClipboardOrigin.ClickWindow - TimeSpan.FromMilliseconds(100));
        ClickSent();
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        _sut.Poll();
        ClickSent();
        AppCopies("hunter2");

        // Assert - not the agent's: the click came within the cool-down
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);

        // Act - once the agent has not clicked for the click window, a click opens one again
        _clock.Advance(ClipboardOrigin.ClickWindow + TimeSpan.FromMilliseconds(100));
        ClickSent();
        AppCopies("the selection");
        _sut.Classify(102).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void CopyKeysEvery5_1Seconds_OpenNoWindowAfterTheSecondEmptyOne()
    {
        // Arrange - ^c every 5.1 s: every new window would start just as the last one ends. A retry after the first empty
        // window still opens one (the agent fixed the selection); after the second, the quiet time has doubled
        for (var i = 0; i < 3; i++)
        {
            CopyKeysSent();
            _clock.Advance(TimeSpan.FromSeconds(5.1));
        }

        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Act
        AppCopies("hunter2");

        // Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
        _sut.IsCopyPending(101).Should().BeFalse();
    }

    [Fact]
    public void ARetryOfCopyKeysAfterOneEmptyWindow_StillCounts()
    {
        CopyKeysSent();
        _clock.Advance(CopyKeysWindowAndABit);
        _sut.Poll();

        CopyKeysSent();
        AppCopies("the selection");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void ACopy_ClearsTheEmptyWindowsBefore()
    {
        // Arrange - two empty windows, then a copy (after the quiet time)
        CopyKeysSent();
        _clock.Advance(CopyKeysWindowAndABit);
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(10.5));
        CopyKeysSent();
        AppCopies("the selection");
        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
        _clock.Advance(TimeSpan.FromSeconds(1));

        // Act - copy keys right after the copy settled
        CopyKeysSent();
        AppCopies("the next selection");

        // Assert
        _sut.Classify(102).Should().Be(ClipboardOriginKind.AgentInput);
    }

    private static TimeSpan CopyKeysWindowAndABit => ClipboardOrigin.CopyKeysWindow + TimeSpan.FromMilliseconds(100);

    [Fact]
    public void CopyKeysAfterEmptyClicks_StillOpenAWindow()
    {
        // Arrange - the agent clicks to place the cursor and Shift-clicks to select; then it copies
        ClickSent();
        _clock.Advance(TimeSpan.FromSeconds(1.5));
        ClickSent();
        _clock.Advance(TimeSpan.FromSeconds(1.5));
        _sut.Poll();

        // Act
        CopyKeysSent();
        AppCopies("the selection");

        // Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void CopyKeysAfterAClick_InOneRun_HaveTheCopyKeysWindow_FromTheRunsStart()
    {
        // A selection click, then ^c a second later: the app has until 5 s after the click
        ClickSent();
        _clock.Advance(TimeSpan.FromSeconds(1));
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(3.5));
        AppCopies("the selection");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void ACopyRunLastsAtMostTheCopyKeysWindow_WhateverInputJoinsIt()
    {
        ClickSent();
        _clock.Advance(TimeSpan.FromSeconds(1));
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(3.5));
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(1));

        AppCopies("hunter2");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent, "5.5 s after the run's first input");
    }

    #endregion

    #region The window the input went to must be in front (review PRIVA-2)

    private static readonly nint AgentWindow = 0x1000, UserWindow = 0x2000, MenuPopup = 0x3000;

    private sealed class Desktop
    {
        public Desktop()
        {
            Mock.Setup(w => w.GetForegroundWindow()).Returns(() => Foreground);
            Mock.Setup(w => w.GetAncestor(It.IsAny<nint>(), It.IsAny<WindowAncestor>())).Returns((nint h, WindowAncestor _) => h);
            Mock.Setup(w => w.GetWindow(It.IsAny<nint>(), It.IsAny<uint>()))
                .Returns((nint h, uint command) => command == 4 && Owners.TryGetValue(h, out var owner) ? owner : 0);
            Mock.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns((nint h) => !Closed.Contains(h));
            Mock.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => !Closed.Contains(h));
        }

        public nint Foreground { get; set; } = AgentWindow;

        public Dictionary<nint, nint> Owners { get; } = [];

        public HashSet<nint> Closed { get; } = [];

        public Mock<INativeWindowWrapper> Mock { get; } = new();
    }

    private (ClipboardOrigin Origin, Desktop Desktop) WithWindows()
    {
        var desktop = new Desktop();
        return (new ClipboardOrigin(_clipboard, _clock, Timeout.InfiniteTimeSpan, desktop.Mock.Object), desktop);
    }

    private static void Sent(ClipboardOrigin origin, string keys, nint receiving, nint anchor = 0) =>
        origin.InputSent(origin.BeforeInput(SendKeysParser.Parse(keys), AppProcessId, receiving, anchor)!);

    [Fact]
    public void ACopyWhileTheWindowTheKeysWentToIsInFront_IsTheAgents()
    {
        var (origin, _) = WithWindows();
        Sent(origin, "^c", AgentWindow);

        AppCopies("the selection");

        origin.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void ACopyInAnotherWindowOfTheSameApp_IsNotTheAgents_AndEndsTheWindow()
    {
        // Arrange - the agent's ^c in its browser window copied nothing; the user copies in their own browser window
        var (origin, desktop) = WithWindows();
        Sent(origin, "^c", AgentWindow);
        desktop.Foreground = UserWindow;
        AppCopies("hunter2");

        // Act
        origin.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);

        // Assert - and the window is over: back in the agent's window, a later copy does not count either
        desktop.Foreground = AgentWindow;
        AppCopies("more of the user's");
        origin.Classify(102).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ACopyWhileNoWindowIsInFront_IsDecidedOnALaterRead()
    {
        var (origin, desktop) = WithWindows();
        Sent(origin, "^c", AgentWindow);
        desktop.Foreground = 0;
        AppCopies("the selection");
        origin.Poll();

        desktop.Foreground = AgentWindow;

        origin.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void ACopyFromAMenuThatClosed_CountsWhileItsOwnerIsInFront()
    {
        // Arrange - a click on a context menu's Copy item: the menu closes, its owner is in front again
        var (origin, desktop) = WithWindows();
        desktop.Owners[MenuPopup] = AgentWindow;
        origin.InputSent(origin.BeforeInput(LeftClick, AppProcessId, MenuPopup)!);
        desktop.Closed.Add(MenuPopup);

        // Act
        AppCopies("the selection");

        // Assert
        origin.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void ACopyFromAMenuThatIsStillOpen_DoesNotCountForItsOwner()
    {
        // The popup is still shown, so its owner in front is not where the input went
        var (origin, desktop) = WithWindows();
        desktop.Owners[MenuPopup] = AgentWindow;
        origin.InputSent(origin.BeforeInput(LeftClick, AppProcessId, MenuPopup)!);

        AppCopies("hunter2");

        origin.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ACopyWhileTheTargetedWindowIsInFront_Counts()
    {
        // The input went to a dialog of the window the caller targeted, which is in front again when the app copies
        var (origin, _) = WithWindows();
        Sent(origin, "^c", MenuPopup, anchor: AgentWindow);

        AppCopies("the selection");

        origin.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void WithWindows_InputWithoutAKnownWindow_CopiesNothingThatCounts()
    {
        var (origin, _) = WithWindows();
        Sent(origin, "^c", receiving: 0);

        AppCopies("the selection");

        origin.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    #endregion

    #region Paste (review PRIVA-1)

    [Theory]
    [InlineData("^v")]
    [InlineData("^V")]
    [InlineData("^+v")] // paste as plain text
    [InlineData("^%v")] // paste special
    [InlineData("+{INSERT}")]
    [InlineData("+{INS}")]
    [InlineData("hello^v")]
    [InlineData("\u0016")] // SYN: an edit control takes it as Ctrl+V
    public void PasteKeys_Paste(string keys)
    {
        ClipboardOrigin.PasteTrigger(SendKeysParser.Parse(keys)).Should().Be(ClipboardPasteKind.Paste);
    }

    [Theory]
    [InlineData("v")]
    [InlineData("^c")]
    [InlineData("{INSERT}")]
    [InlineData("%v")]
    [InlineData("^{INSERT}")]
    public void OtherKeys_DoNotPaste(string keys)
    {
        ClipboardOrigin.PasteTrigger(SendKeysParser.Parse(keys)).Should().BeNull();
    }

    [Fact]
    public void WinV_OpensClipboardHistory()
    {
        ClipboardOrigin.PasteTrigger(SendKeysParser.Parse("#v", windowsModifier: true)).Should().Be(ClipboardPasteKind.History);
    }

    [Fact]
    public void ARightClickPastes_OnlyInATerminal()
    {
        InputEvent[] rightClick = [InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp)];

        ClipboardOrigin.PasteTrigger(rightClick, "ConsoleWindowClass").Should().Be(ClipboardPasteKind.Paste);
        ClipboardOrigin.PasteTrigger(rightClick, "CASCADIA_HOSTING_WINDOW_CLASS").Should().Be(ClipboardPasteKind.Paste);
        ClipboardOrigin.PasteTrigger(rightClick, "Notepad").Should().BeNull();
        ClipboardOrigin.PasteTrigger(rightClick).Should().BeNull();
        ClipboardOrigin.PasteTrigger(LeftClick, "ConsoleWindowClass").Should().BeNull();
    }

    /// <summary>
    /// Terminals whose window class is not their own (roadmap M2 follow-up F2): the class of Electron apps
    /// (Chrome_WidgetWin_1) is shared with every Chromium browser, and Alacritty keeps winit's default class ("Window
    /// Class"), so their process decides. WezTerm's default class (org.wezfurlong.wezterm) is its own, but its --class
    /// option replaces it, so its process counts too.
    /// </summary>
    [Theory]
    [InlineData("Chrome_WidgetWin_1", "Code")]
    [InlineData("Chrome_WidgetWin_1", "Code - Insiders")]
    [InlineData("Chrome_WidgetWin_1", "VSCodium")]
    [InlineData("Chrome_WidgetWin_1", "Cursor")]
    [InlineData("Chrome_WidgetWin_1", "Windsurf")]
    [InlineData("Chrome_WidgetWin_1", "Hyper")]
    [InlineData("Chrome_WidgetWin_1", "Tabby")]
    [InlineData("Chrome_WidgetWin_1", "code")]
    [InlineData("Window Class", "alacritty")]
    [InlineData("org.wezfurlong.wezterm", "wezterm-gui")]
    [InlineData("org.wezfurlong.wezterm", null)]
    [InlineData("my-wezterm-class", "wezterm-gui")]
    public void ARightClickPastes_InTheVsCodeElectronAlacrittyAndWezTermTerminals(string className, string? processName)
    {
        InputEvent[] rightClick = [InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp)];

        ClipboardOrigin.PasteTrigger(rightClick, className, processName).Should().Be(ClipboardPasteKind.Paste);
        ClipboardOrigin.CopiesBeforePaste(SendKeysParser.Parse("^c").Concat(rightClick).ToList(), className, processName)
            .Should().BeTrue();
        ClipboardOrigin.IsRightClickPasteWindow(className, processName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Chrome_WidgetWin_1", "chrome")]
    [InlineData("Chrome_WidgetWin_1", "msedge")]
    [InlineData("Chrome_WidgetWin_1", "slack")]
    [InlineData("Chrome_WidgetWin_1", null)]
    [InlineData("Window Class", "some-winit-app")]
    [InlineData("Window Class", null)]
    [InlineData(null, null)]
    public void ARightClick_InABrowserOrAnotherAppSharingTheClass_DoesNotPaste(string? className, string? processName)
    {
        InputEvent[] rightClick = [InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp)];

        ClipboardOrigin.PasteTrigger(rightClick, className, processName).Should().BeNull();
        ClipboardOrigin.IsRightClickPasteWindow(className, processName).Should().BeFalse();
    }

    [Fact]
    public void ContentTheUserAllowed_IsRemembered_UntilOtherContentIsAllowed()
    {
        _sut.UserAllowed(130);

        _sut.IsUserAllowed(130).Should().BeTrue();
        _sut.IsUserAllowed(131).Should().BeFalse();
        _sut.IsUserAllowed(0).Should().BeFalse();

        _sut.UserAllowed(0);
        _sut.IsUserAllowed(130).Should().BeFalse();
    }

    #endregion
}
