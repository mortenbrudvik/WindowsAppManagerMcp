namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The key-ups the input session sends to release what is down carry the scan code the key went down with (roadmap M4.1
/// sends every press with the layout's scan code): after a short SendInput count, a SendInput exception, the user's pause,
/// the end of a call, and for releaseHeldModifiers the scan code the modifier key has on every keyboard. Remote desktop and
/// virtual machine clients and games track keys by scan code, so a key-up with scan code 0 would leave Ctrl down there.
/// The native input layer is <see cref="FakeInputWrapper"/>, so nothing reaches the desktop.
/// </summary>
public class ReleaseScanCodeTests
{
    private const uint KeyDown = 0x0000, Extended = 0x0001, KeyUp = 0x0002;
    private const ushort VkControl = 0x11, VkLShift = 0xA0, VkLControl = 0xA2, VkRControl = 0xA3, VkRMenu = 0xA5, VkQ = 0x51;

    private readonly FakeInputWrapper _input = new();

    private static InputEvent[] MaskKey =>
        [InputEvent.Key(HeldInputTracker.MaskKey, KeyDown), InputEvent.Key(HeldInputTracker.MaskKey, KeyUp)];

    [Fact]
    public void ShortInsert_ReleasesWhatWentDown_WithItsScanCodes()
    {
        // Arrange - AltGr+Q as a keyboard sends it; SendInput inserts the left Ctrl, the right Alt and Q's down only
        _input.Insert = (events, call) => call == 0 ? 3u : (uint)events.Count;
        var session = new InputSession(_input);

        // Act
        session.Send(
        [
            InputEvent.Key(VkLControl, KeyDown, 0x1D), InputEvent.Key(VkRMenu, Extended, 0x38), InputEvent.Key(VkQ, KeyDown, 0x10),
            InputEvent.Key(VkQ, KeyUp, 0x10), InputEvent.Key(VkRMenu, Extended | KeyUp, 0x38), InputEvent.Key(VkLControl, KeyUp, 0x1D)
        ]);

        // Assert
        _input.Batches[1].Should().Equal([
            .. MaskKey,
            InputEvent.Key(VkQ, KeyUp, 0x10), InputEvent.Key(VkRMenu, Extended | KeyUp, 0x38), InputEvent.Key(VkLControl, KeyUp, 0x1D)]);
        session.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public void SendInputThrows_ReleasesWhatWindowsReportsDown_WithItsScanCodes()
    {
        _input.Insert = (_, call) =>
        {
            if (call == 0)
            {
                _input.Press(VkLShift);
                throw new InvalidOperationException("SendInput failed");
            }

            return uint.MaxValue;
        };
        var session = new InputSession(_input);

        var act = () => session.Send([InputEvent.Key(VkLShift, KeyDown, 0x2A), InputEvent.Key('A', KeyDown, 0x1E)]);

        act.Should().Throw<InvalidOperationException>();
        _input.Batches[1].Should().Equal(InputEvent.Key(VkLShift, KeyUp, 0x2A));
    }

    [Fact]
    public void ThePause_ReleasesWhatTheServerHolds_WithItsScanCodes()
    {
        var session = new InputSession(_input);
        session.Send([InputEvent.Key(VkRControl, Extended, 0x1D)]);

        session.Pause(InputControlSources.Hotkey);

        _input.Batches[1].Should().Equal(InputEvent.Key(VkRControl, Extended | KeyUp, 0x1D));
    }

    [Fact]
    public void ReleaseModifiers_TheUsersKeys_GoUpWithTheScanCodesOfThoseKeys()
    {
        // releaseHeldModifiers: Windows reports them down, so the server knows only which keys they are
        _input.Press(VkRControl);
        _input.Press(VkLShift);
        var session = new InputSession(_input);

        var outcome = session.ReleaseModifiers(session.ReadModifiersDown(), "releaseHeldModifiers");

        outcome.Released.Should().BeEquivalentTo("RightCtrl", "LeftShift");
        _input.Batches.Single().Should().Equal(
            InputEvent.Key(VkLShift, KeyUp, 0x2A), InputEvent.Key(VkRControl, Extended | KeyUp, 0x1D));
    }

    [Fact]
    public async Task SendKeys_ShortInsert_ReleasesWithTheLayoutsScanCodes()
    {
        // Arrange - on the US layout: Ctrl down, A down and up, then B down only
        _input.Insert = (events, call) => call == 0 ? 4u : (uint)events.Count;
        var session = new InputSession(_input);
        var sut = new InputSimulationService(TestDataFactory.CreateMockMonitorService().Object, _input,
            TestDataFactory.CreateAllowingInputGuard().Object, session, keyboardLayout: new FakeKeyboardLayoutWrapper());

        // Act
        var result = await sut.SendKeysAsync("^(ab)c");

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        _input.Batches[1].Should().Equal(InputEvent.Key('B', KeyUp, 0x30), InputEvent.Key(VkControl, KeyUp, 0x1D));
        session.ServerHeld.Should().BeEmpty();
    }
}
