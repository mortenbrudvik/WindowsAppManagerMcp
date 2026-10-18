namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// send_keys with a modifier it holds down from an earlier batch (<c>{CTRL down}</c>) and the key of a paste or copy in a
/// later one: that batch's keys are pressed with the modifier, so the paste rule checks the clipboard again right before
/// it (content the user copied in between, private content included, is not pasted), and the clipboard origin counts the
/// copy as the agent's. With the real <see cref="ClipboardPasteGuard"/> and <see cref="ClipboardOrigin"/> over a fake
/// clipboard; the native input layer is <see cref="FakeInputWrapper"/>, so nothing reaches the desktop or the real clipboard.
/// </summary>
public class SendKeysHeldModifierAcrossBatchesTests
{
    private const uint KeyUp = 0x0002;
    private const ushort VkControl = 0x11, VkLControl = 0xA2, VkC = 0x43, VkV = 0x56;
    private const int AppProcessId = 5150;

    private readonly FakeInputWrapper _input = new();
    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly InputSession _session;
    private readonly ClipboardOrigin _origin;

    public SendKeysHeldModifierAcrossBatchesTests()
    {
        _session = new InputSession(_input);
        _origin = new ClipboardOrigin(_clipboard, new ManualClock(), Timeout.InfiniteTimeSpan);
    }

    private InputSimulationService CreateSut(bool allowUserClipboard = false) =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, _session,
            clipboardOrigin: _origin,
            pasteGuard: new ClipboardPasteGuard(_clipboard, _origin, new ClipboardOptions(allowUserClipboard), new Mock<INativeWindowWrapper>().Object),
            keyboardLayout: new FakeKeyboardLayoutWrapper());

    /// <summary>The clipboard holds set_clipboard's text: the agent may paste it.</summary>
    private void AgentsTextOnTheClipboard()
    {
        var written = _clipboard.WriteText("the agent's text");
        _origin.ServerWrote(written.SequenceNumber, "the agent's text");
    }

    /// <summary>Runs <paramref name="meanwhile"/> while SendInput inserts the call's first batch: after that batch was checked.</summary>
    private void WhileTheFirstBatchGoesIn(Action meanwhile) =>
        _input.Insert = (events, call) =>
        {
            if (call == 0)
            {
                meanwhile();
            }

            return (uint)events.Count;
        };

    private static bool IsKeyDown(InputEvent e, ushort virtualKey) =>
        e.Kind == InputEventKind.VirtualKey && e.Code == virtualKey && (e.Flags & KeyUp) == 0;

    private void VerifyThePasteWasNotSent(SendKeysResult result, string reason)
    {
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Error.Should().StartWith("Sending stopped after").And.Contain(reason);
        _input.Events.Should().NotContain(e => IsKeyDown(e, VkV), "the batch with the paste must not go out");
        _input.Batches.Skip(1).SelectMany(b => b).Should().OnlyContain(e => (e.Flags & KeyUp) != 0,
            "only the release of what the first batch left down follows it");
        result.KeysSent.Should().BeGreaterThan(0);
        _session.ServerHeld.Should().BeEmpty();
        _input.IsKeyDown(VkControl).Should().BeFalse("the Ctrl the call held is released when it stops");
    }

    #region The paste

    [Fact]
    public async Task APasteWhoseCtrlWentDownInAnEarlierBatch_TheUserCopiesMeanwhile_IsRefusedBeforeItsBatch()
    {
        // Arrange - Ctrl goes down in the first batch, V in the second; the user copies while the first goes in
        AgentsTextOnTheClipboard();
        WhileTheFirstBatchGoesIn(() => _clipboard.UserCopies("the user's text"));

        // Act
        var result = await CreateSut().SendKeysAsync("{CTRL down}{RIGHT 100}v{CTRL up}");

        // Assert
        VerifyThePasteWasNotSent(result, "the user or another application copied it");
    }

    [Fact]
    public async Task APasteWhoseCtrlWentDownInAnEarlierBatch_APasswordManagerCopiesMeanwhile_IsRefusedBeforeItsBatch()
    {
        // Arrange - the user let the agent paste their own copies, but never content marked private
        _clipboard.UserCopies("the user's text");
        WhileTheFirstBatchGoesIn(() => _clipboard.AppCopies(7000, "hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing));

        // Act
        var result = await CreateSut(allowUserClipboard: true).SendKeysAsync("{CTRL down}{RIGHT 100}v{CTRL up}");

        // Assert
        VerifyThePasteWasNotSent(result, "marked private");
    }

    [Fact]
    public async Task APasteWhoseCtrlWentDownInAnEarlierStep_WithADelay_IsCheckedAgainRightBeforeIt()
    {
        // With delayMs every key is a batch of its own: {CTRL down}, then v, then {CTRL up}
        AgentsTextOnTheClipboard();
        WhileTheFirstBatchGoesIn(() => _clipboard.UserCopies("the user's text"));

        var result = await CreateSut().SendKeysAsync("{CTRL down}v{CTRL up}", delayMs: 1);

        VerifyThePasteWasNotSent(result, "the user or another application copied it");
        result.KeysSent.Should().Be(1);
    }

    [Fact]
    public async Task APasteWhoseCtrlWentDownInAnEarlierBatch_TheClipboardUnchanged_IsSent()
    {
        AgentsTextOnTheClipboard();

        var result = await CreateSut().SendKeysAsync("{CTRL down}{RIGHT 100}v{CTRL up}");

        result.Success.Should().BeTrue(result.Error);
        _input.Batches.Should().HaveCountGreaterThan(1);
        _input.Events.Should().Contain(e => IsKeyDown(e, VkV));
        _input.IsKeyDown(VkControl).Should().BeFalse();
    }

    #endregion

    #region The copy

    [Fact]
    public async Task ACopyWhoseCtrlWentDownInAnEarlierBatch_IsTheAgentsCopy()
    {
        // Arrange - the app copies on a C that goes down while Ctrl is down, whichever batch pressed Ctrl
        _input.BeforeSend = batch =>
        {
            var ctrl = _input.IsKeyDown(VkControl);
            foreach (var e in batch.Where(e => e.Kind == InputEventKind.VirtualKey))
            {
                if (e.Code is VkControl or VkLControl)
                {
                    ctrl = (e.Flags & KeyUp) == 0;
                }
                else if (ctrl && IsKeyDown(e, VkC))
                {
                    _clipboard.AppCopies(AppProcessId, "the selected text");
                }
            }
        };

        // Act
        var result = await CreateSut().SendKeysAsync("{CTRL down}{RIGHT 100}c{CTRL up}");

        // Assert - Ctrl went down in the first batch and C in a later one, and get_clipboard may read the copy
        result.Success.Should().BeTrue(result.Error);
        _input.Batches.Should().HaveCountGreaterThan(1);
        _input.Batches[0].Should().NotContain(e => IsKeyDown(e, VkC));
        _clipboard.Text.Should().Be("the selected text");
        _origin.Classify(_clipboard.GetSequenceNumber()).Should().Be(ClipboardOriginKind.AgentInput);
    }

    #endregion
}
