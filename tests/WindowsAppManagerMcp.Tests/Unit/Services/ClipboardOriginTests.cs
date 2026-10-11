namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Which clipboard content counts as the agent's (roadmap M2.16): the content set_clipboard put there, and the content
/// the clipboard reached within the copy window after the agent's input that can copy. A fake clipboard and a manual
/// clock; the copy window's timer is off, and the tests poll by hand.
/// </summary>
public class ClipboardOriginTests
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
    private const ushort VkControl = 0x11, VkShift = 0x10, VkC = 0x43, VkV = 0x56, VkInsert = 0x2D, VkDelete = 0x2E;

    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly ManualClock _clock = new();
    private readonly ClipboardOrigin _sut;

    public ClipboardOriginTests()
    {
        _sut = new ClipboardOrigin(_clipboard, _clock, Timeout.InfiniteTimeSpan);
    }

    private void CopyKeysSent()
    {
        var mark = _sut.BeforeInput(SendKeysParser.Parse("^c"));
        mark.Should().NotBeNull();
        _sut.InputSent(mark!);
    }

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
        ClipboardOrigin.CanCopy([InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp)])
            .Should().BeTrue("a click on a Copy menu item copies");
        ClipboardOrigin.CanCopy([InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp)])
            .Should().BeFalse();
        ClipboardOrigin.CanCopy([InputEvent.MouseMoveAbsolute(1, 1), InputEvent.Wheel(-120)]).Should().BeFalse();
        ClipboardOrigin.CanCopy([InputEvent.Unicode('c', 0), InputEvent.Unicode('c', 2)]).Should().BeFalse("typed text copies nothing");
        ClipboardOrigin.CanCopy([InputEvent.Key(VkV, 0), InputEvent.Key(VkV, 2)]).Should().BeFalse();
        ClipboardOrigin.CanCopy([InputEvent.Key(VkShift, 0), InputEvent.Key(VkInsert, 0), InputEvent.Key(VkInsert, 2), InputEvent.Key(VkShift, 2)])
            .Should().BeFalse("Shift+Insert pastes");
        ClipboardOrigin.CanCopy([InputEvent.Key(VkControl, 0), InputEvent.Key(VkDelete, 0), InputEvent.Key(VkDelete, 2), InputEvent.Key(VkControl, 2)])
            .Should().BeFalse("Ctrl+Delete deletes a word");
        ClipboardOrigin.CanCopy([InputEvent.Key(VkControl, 0), InputEvent.Key(VkC, 0), InputEvent.Key(VkC, 2), InputEvent.Key(VkControl, 2)])
            .Should().BeTrue();
    }

    [Fact]
    public void BeforeInput_ReadsTheSequenceNumberOnlyForInputThatCanCopy()
    {
        var reads = 0;
        _clipboard.OnSequenceRead = () => reads++;

        _sut.BeforeInput(SendKeysParser.Parse("hello")).Should().BeNull();
        reads.Should().Be(0);

        _sut.BeforeInput(SendKeysParser.Parse("^c")).Should().Be(new ClipboardCopyMark(100));
        reads.Should().Be(1);
    }

    [Fact]
    public void TheUsersContent_IsNotFromTheAgent()
    {
        _clipboard.UserCopies("hunter2");

        _sut.Classify(_clipboard.Sequence).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void WhatSetClipboardWrote_IsTheAgents_UntilTheClipboardChanges()
    {
        _sut.ServerWrote(120);

        _sut.Classify(120).Should().Be(ClipboardOriginKind.SetClipboard);
        _sut.Classify(121).Should().Be(ClipboardOriginKind.NotFromAgent, "the user copied after it");
    }

    [Fact]
    public void ASequenceNumberOfZero_CannotBeTold_SoItIsNotTheAgents()
    {
        _sut.ServerWrote(0);

        _sut.Classify(0).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void RightAfterTheCopyKeys_TheUnchangedClipboard_IsCopyPending()
    {
        CopyKeysSent();

        _sut.Classify(100).Should().Be(ClipboardOriginKind.CopyPending);
    }

    [Fact]
    public void AChangeWithinTheCopyWindow_IsTheAgentsCopy_AndStaysItsAfterTheWindow()
    {
        // Arrange - the app copies 2 s after the keys
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(2));
        _clipboard.UserCopies("the selection");
        _sut.Poll();

        // Act - the agent reads a minute later
        _clock.Advance(TimeSpan.FromMinutes(1));
        _sut.Poll();

        // Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void AReadWithinTheCopyWindow_TakesTheChangeAsTheAgentsCopy_WithoutAPoll()
    {
        CopyKeysSent();
        _clipboard.UserCopies("the selection");

        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
        _clock.Advance(TimeSpan.FromMinutes(1));
        _sut.Classify(101).Should().Be(ClipboardOriginKind.AgentInput);
    }

    [Fact]
    public void AChangeAfterTheCopyWindow_IsTheUsers()
    {
        // Arrange - the keys copied nothing (no selection); 10 s later the user copies a password
        CopyKeysSent();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _sut.Poll();
        _clock.Advance(TimeSpan.FromSeconds(9));
        _clipboard.UserCopies("hunter2");
        _sut.Poll();

        // Act & Assert
        _sut.Classify(101).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void ALaterUserCopy_IsNotTheAgents_EvenThoughAnEarlierCopyWas()
    {
        CopyKeysSent();
        _clipboard.UserCopies("the selection");
        _sut.Poll();
        _clock.Advance(TimeSpan.FromSeconds(30));
        _sut.Poll();
        _clipboard.UserCopies("hunter2");

        _sut.Classify(102).Should().Be(ClipboardOriginKind.NotFromAgent);
    }

    [Fact]
    public void AfterInputThatCanCopy_ATimerWatchesTheSequenceNumber()
    {
        // The copy window must see a change even when nothing reads the clipboard within it
        var reads = 0;
        _clipboard.OnSequenceRead = () => Interlocked.Increment(ref reads);
        using var origin = new ClipboardOrigin(_clipboard, TimeProvider.System, TimeSpan.FromMilliseconds(5));

        origin.InputSent(new ClipboardCopyMark(100));

        SpinWait.SpinUntil(() => Volatile.Read(ref reads) >= 3, TimeSpan.FromSeconds(5)).Should().BeTrue();
    }
}
