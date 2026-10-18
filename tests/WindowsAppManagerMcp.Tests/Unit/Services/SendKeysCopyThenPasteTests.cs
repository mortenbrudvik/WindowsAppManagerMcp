namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// send_keys and press_keys with keys that copy in one batch and paste in a later one (delayMs, or a long call without it):
/// the paste rule reads the whole call before its first batch, so the call is refused before anything is sent, as one batch
/// would be. With the real <see cref="ClipboardPasteGuard"/> and <see cref="ClipboardOrigin"/> over a fake clipboard (a
/// guard that refuses every paste could not tell the whole call's check from the paste batch's own). The native input layer
/// is <see cref="FakeInputWrapper"/>, so nothing reaches the desktop or the real clipboard.
/// </summary>
public class SendKeysCopyThenPasteTests
{
    private readonly FakeInputWrapper _input = new();
    private readonly FakeClipboardWrapper _clipboard = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly ClipboardOrigin _origin;

    public SendKeysCopyThenPasteTests()
    {
        _origin = new ClipboardOrigin(_clipboard, new ManualClock(), Timeout.InfiniteTimeSpan);

        // The window's app copies what the copy keys select: a password its password field marks private
        _input.BeforeSend = batch =>
        {
            if (ClipboardOrigin.CopyTrigger(batch) == ClipboardCopyTrigger.CopyKeys)
            {
                _clipboard.AppCopies(5150, "hunter2", ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);
            }
        };
    }

    private InputSimulationService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _input, _guard.Object, new InputSession(_input),
            clipboardOrigin: _origin,
            pasteGuard: new ClipboardPasteGuard(_clipboard, _origin, new ClipboardOptions(false), new Mock<INativeWindowWrapper>().Object),
            keyboardLayout: new FakeKeyboardLayoutWrapper());

    private void VerifyRefusedBeforeAnythingWasSent(SendKeysResult result)
    {
        result.ErrorCode.Should().Be(InputErrorCode.PasteNotAllowed);
        result.Error.Should().Contain("copies and then pastes").And.Contain("Nothing was sent").And.Contain("separate calls");
        result.KeysSent.GetValueOrDefault().Should().Be(0);
        _input.Batches.Should().BeEmpty("the copy keys in the first batch must not go out either");
        _clipboard.Text.Should().BeNull("nothing was copied");
    }

    [Fact]
    public async Task SendKeys_WithADelay_ACopyInOneBatchAndThePasteInALaterOne_IsRefusedBeforeTheFirstBatch()
    {
        // ^c, {TAB} and ^v are three batches
        var result = await CreateSut().SendKeysAsync("^c{TAB}^v", delayMs: 1);

        VerifyRefusedBeforeAnythingWasSent(result);
    }

    [Fact]
    public async Task SendKeys_LongWithoutADelay_ACopyInOneBatchAndThePasteInALaterOne_IsRefusedBeforeTheFirstBatch()
    {
        // More keys than one batch takes: the copy goes in the first batch, the paste in the last
        var keys = "^c" + string.Concat(Enumerable.Repeat("{RIGHT 100}", 2)) + "^v";

        var result = await CreateSut().SendKeysAsync(keys);

        VerifyRefusedBeforeAnythingWasSent(result);
    }

    [Fact]
    public async Task PressKeys_WithADelay_ACopyInOneBatchAndThePasteInALaterOne_IsRefusedBeforeTheFirstBatch()
    {
        var result = await CreateSut().PressKeysAsync(KeyList.Of("ctrl+c tab ctrl+v"), 1, 0, 1, InputTarget.None);

        VerifyRefusedBeforeAnythingWasSent(result);
    }

    [Fact]
    public async Task SendKeys_TheCopyAlone_IsSent()
    {
        var result = await CreateSut().SendKeysAsync("^c{TAB}", delayMs: 1);

        result.Success.Should().BeTrue(result.Error);
        _clipboard.Text.Should().Be("hunter2");
    }
}
