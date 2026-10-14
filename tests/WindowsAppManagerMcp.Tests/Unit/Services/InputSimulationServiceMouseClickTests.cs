namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// mouse_click (roadmap M4.3): any button, one to three clicks and modifier keys, in the one batch of a click, with every
/// check click makes. The native input layer is <see cref="FakeInputWrapper"/> and the guard a mock, so nothing reaches the
/// desktop.
/// </summary>
public class InputSimulationServiceMouseClickTests
{
    private const uint KeyDown = 0x0000, KeyUp = 0x0002, ExtendedKey = 0x0001;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, MiddleDown = 0x0020, MiddleUp = 0x0040;
    private const uint XDown = 0x0080, XUp = 0x0100;
    private const ushort VkLShift = 0xA0, VkLControl = 0xA2, VkLMenu = 0xA4, VkLWin = 0x5B;

    private readonly FakeInputWrapper _input = new();
    private readonly Mock<IInputTargetGuard> _guard = TestDataFactory.CreateAllowingInputGuard();
    private readonly Mock<INativeSystemInfoWrapper> _systemInfo = new();
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService(
    [
        TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)
    ]);

    private readonly InputSession _session;

    public InputSimulationServiceMouseClickTests()
    {
        _session = new InputSession(_input);
        _systemInfo.Setup(s => s.GetVirtualScreen()).Returns(new VirtualScreenBounds(0, 0, 1920, 1080));
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(false);
    }

    private InputSimulationService CreateSut() =>
        new(_monitors.Object, _input, _guard.Object, _session, systemInfo: _systemInfo.Object);

    private static InputEvent MoveTo(int x, int y)
    {
        var (dx, dy) = InputBatchBuilder.ToAbsolute(x, y, new VirtualScreenBounds(0, 0, 1920, 1080));
        return InputEvent.MouseMoveAbsolute(dx, dy);
    }

    private void VerifyNothingIsDown()
    {
        _session.ServerHeld.Should().BeEmpty();
        foreach (var key in new ushort[] { 0x01, 0x02, 0x04, 0x05, 0x06, VkLControl, VkLShift, VkLMenu, VkLWin, 0x11, 0x10, 0x12 })
        {
            _input.IsKeyDown(key).Should().BeFalse($"key 0x{key:X2} must not stay down");
        }
    }

    [Fact]
    public void MouseClick_WithModifiers_SendsThemAroundTheClick_InOneBatch()
    {
        var result = CreateSut().MouseClick(500, 300, MouseButton.Primary, 1, [MouseModifier.Ctrl, MouseModifier.Shift]);

        result.Success.Should().BeTrue();
        result.ClickType.Should().Be("primary");
        result.ClickCount.Should().Be(1);
        result.Modifiers.Should().Equal("ctrl", "shift");
        _input.Batches.Should().ContainSingle().Which.Should().Equal(
            InputEvent.Key(VkLControl, KeyDown), InputEvent.Key(VkLShift, KeyDown),
            MoveTo(500, 300), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp),
            InputEvent.Key(VkLShift, KeyUp), InputEvent.Key(VkLControl, KeyUp));
        VerifyNothingIsDown();
    }

    [Fact]
    public void MouseClick_ATripleClick_SendsThreeClicksAtThePoint()
    {
        var result = CreateSut().MouseClick(40, 50, MouseButton.Primary, 3, []);

        result.Success.Should().BeTrue();
        result.ClickCount.Should().Be(3);
        result.Modifiers.Should().BeNull();
        _input.Batches.Should().ContainSingle().Which.Skip(1).Should().Equal(
            InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp),
            InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Theory]
    [InlineData(MouseButton.Middle, MiddleDown, MiddleUp, 0u)]
    [InlineData(MouseButton.X1, XDown, XUp, 1u)]
    [InlineData(MouseButton.X2, XDown, XUp, 2u)]
    public void MouseClick_OtherButtons_SendTheirOwnEvents(MouseButton button, uint down, uint up, uint xButton)
    {
        var result = CreateSut().MouseClick(40, 50, button, 1, []);

        result.Success.Should().BeTrue();
        _input.Batches.Should().ContainSingle().Which.Skip(1).Should().Equal(InputEvent.Mouse(down, xButton), InputEvent.Mouse(up, xButton));
        VerifyNothingIsDown();
    }

    [Fact]
    public void MouseClick_Secondary_WithSwappedButtons_PressesTheLeftButton()
    {
        _systemInfo.Setup(s => s.AreMouseButtonsSwapped()).Returns(true);

        CreateSut().MouseClick(40, 50, MouseButton.Secondary, 1, []);

        _input.Batches.Should().ContainSingle().Which.Skip(1).Should().Equal(InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp));
    }

    [Theory]
    [InlineData(MouseModifier.Alt, VkLMenu, 0u)]
    [InlineData(MouseModifier.Win, VkLWin, ExtendedKey)]
    public void MouseClick_AltOrWin_IsMaskedBeforeItsRelease_SoNoMenuOpens(MouseModifier modifier, ushort key, uint extended)
    {
        CreateSut().MouseClick(40, 50, MouseButton.Primary, 1, [modifier]);

        _input.Batches.Should().ContainSingle().Which.TakeLast(3).Should().Equal(
            InputEvent.Key(HeldInputTracker.MaskKey, KeyDown), InputEvent.Key(HeldInputTracker.MaskKey, KeyUp),
            InputEvent.Key(key, KeyUp | extended));
        VerifyNothingIsDown();
    }

    [Fact]
    public void MouseClick_ABatchCutShortAfterTheModifiers_ReleasesThem_AndSaysNothingWasClicked()
    {
        // SendInput inserts the two modifier key-downs and stops: the session sends them up at once
        _input.Insert = (events, call) => call == 0 ? 2u : (uint)events.Count;

        var result = CreateSut().MouseClick(40, 50, MouseButton.Primary, 1, [MouseModifier.Ctrl, MouseModifier.Shift]);

        result.ErrorCode.Should().Be(InputErrorCode.SendInputFailed);
        result.Error.Should().Contain("the modifier keys").And.Contain("the click was not sent");
        VerifyNothingIsDown();
    }

    [Fact]
    public void MouseClick_ModifiersNamedTwice_AreSentAndReportedOnce()
    {
        var result = CreateSut().MouseClick(40, 50, MouseButton.Primary, 1, [MouseModifier.Ctrl, MouseModifier.Ctrl]);

        result.Modifiers.Should().Equal("ctrl");
        _input.Batches.Single().Count(e => e.Kind == InputEventKind.VirtualKey).Should().Be(2);
    }

    [Fact]
    public void MouseClick_WhileTheUserHoldsAModifier_IsRefused_AndSendsNothing()
    {
        // The call's own modifiers are not the user's: a key already down is refused as for every input call
        _input.Press(VkLShift);

        var result = CreateSut().MouseClick(40, 50, MouseButton.Primary, 1, [MouseModifier.Ctrl]);

        result.ErrorCode.Should().Be(InputErrorCode.ModifiersHeld);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void MouseClick_WhileTheUserHoldsAnXButton_IsMouseInUse()
    {
        _input.Press(0x06);

        var result = CreateSut().MouseClick(40, 50, MouseButton.Primary, 1, []);

        result.ErrorCode.Should().Be(InputErrorCode.MouseInUse);
        result.Error.Should().Contain("X2 mouse button");
        _input.Batches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void MouseClick_ClickCountOutsideOneToThree_Throws(int count)
    {
        var act = () => CreateSut().MouseClick(40, 50, MouseButton.Primary, count, []);

        act.Should().Throw<ArgumentOutOfRangeException>();
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Click_RightClick_AndDoubleClick_SendNoModifiers_AndReportNoClickCount()
    {
        var sut = CreateSut();

        var results = new[] { sut.Click(10, 10), sut.RightClick(10, 10), sut.DoubleClick(10, 10) };

        results.Should().OnlyContain(r => r.Success && r.ClickCount == null && r.Modifiers == null);
        _input.Events.Should().NotContain(e => e.Kind == InputEventKind.VirtualKey);
        _input.Batches[1].Skip(1).Should().Equal(InputEvent.Mouse(RightDown), InputEvent.Mouse(RightUp));
    }
}
