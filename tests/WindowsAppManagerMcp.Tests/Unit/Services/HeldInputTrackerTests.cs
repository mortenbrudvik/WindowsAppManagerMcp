namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The held-input bookkeeping of the input session (roadmap M1.10): which keys and buttons a batch leaves
/// down, given how many of its events SendInput inserted, and the events that release them. Pure.
/// </summary>
public class HeldInputTrackerTests
{
    private const uint KeyDown = 0x0000, ExtendedKey = 0x0001, KeyUp = 0x0002;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, Move = 0x0001;
    private const uint XDown = 0x0080, XUp = 0x0100, XButton1 = 1, XButton2 = 2;
    private const ushort VkControl = 0x11, VkMenu = 0x12, VkC = 0x43, VkRControl = 0xA3, VkLWin = 0x5B, VkLShift = 0xA0;

    private static readonly InputEvent[] CtrlC =
    [
        InputEvent.Key(VkControl, KeyDown), InputEvent.Key(VkC, KeyDown), InputEvent.Key(VkC, KeyUp), InputEvent.Key(VkControl, KeyUp)
    ];

    [Fact]
    public void Apply_WholeBatch_LeavesNothingHeld()
    {
        var tracker = new HeldInputTracker();

        tracker.Apply(CtrlC, CtrlC.Length);

        tracker.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(1, new[] { "Ctrl" })]
    [InlineData(2, new[] { "Ctrl", "C" })]
    [InlineData(3, new[] { "Ctrl" })]
    public void Apply_ShortCount_HoldsTheKeysWhoseDownWentInButNotTheirUp(int inserted, string[] expected)
    {
        var tracker = new HeldInputTracker();

        tracker.Apply(CtrlC, inserted);

        tracker.Held.Select(h => h.Name).Should().Equal(expected);
    }

    [Fact]
    public void Apply_MouseButtons_AreTrackedByTheirFlags()
    {
        var tracker = new HeldInputTracker();
        InputEvent[] click = [InputEvent.MouseMoveAbsolute(100, 100), InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp)];

        tracker.Apply(click, 2);
        var afterDown = tracker.Held.Select(h => h.Name).ToList();
        tracker.Apply([InputEvent.Mouse(LeftUp)], 1);

        afterDown.Should().Equal("left mouse button");
        tracker.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Apply_XButtons_AreTrackedByTheButtonInMouseData()
    {
        // XDOWN and XUP name the button in mouseData, so X1 and X2 are two buttons under the same flag (roadmap M4.3)
        var tracker = new HeldInputTracker();

        tracker.Apply([InputEvent.Mouse(XDown, XButton1), InputEvent.Mouse(XDown, XButton2)], 2);
        var bothDown = tracker.Held.Select(h => h.Name).ToList();
        tracker.Apply([InputEvent.Mouse(XUp, XButton1)], 1);

        bothDown.Should().Equal("X1 mouse button", "X2 mouse button");
        tracker.Held.Should().ContainSingle().Which.Name.Should().Be("X2 mouse button");
    }

    [Fact]
    public void Apply_AnXEventNamingBothButtons_PressesBoth()
    {
        var tracker = new HeldInputTracker();

        tracker.Apply([InputEvent.Mouse(XDown, XButton1 | XButton2)], 1);

        tracker.Held.Select(h => h.Name).Should().Equal("X1 mouse button", "X2 mouse button");
    }

    [Fact]
    public void Apply_UnicodeCharacters_AreTrackedUntilTheirUp()
    {
        var tracker = new HeldInputTracker();

        tracker.Apply([InputEvent.Unicode('x', KeyDown), InputEvent.Unicode('x', KeyUp)], 1);

        tracker.Held.Should().ContainSingle().Which.Name.Should().Be("character 'x'");
    }

    [Fact]
    public void Apply_AnUpOfAKeyThatIsNotHeld_ChangesNothing()
    {
        var tracker = new HeldInputTracker();

        tracker.Apply([InputEvent.Key(VkC, KeyUp)], 1);

        tracker.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ReleaseEvents_SendsOrdinaryKeysFirst_ThenModifiersLastPressedFirst()
    {
        var held = new[] { HeldInput.Key(VkControl), HeldInput.Key(VkLShift), HeldInput.Key(VkC) };

        var events = HeldInputTracker.ReleaseEvents(held);

        // The modifiers, held without a scan code, go up with their own (below)
        events.Should().Equal(InputEvent.Key(VkC, KeyUp), InputEvent.Key(VkLShift, KeyUp, 0x2A), InputEvent.Key(VkControl, KeyUp, 0x1D));
    }

    [Fact]
    public void ReleaseEvents_KeepsTheExtendedKeyFlag()
    {
        var events = HeldInputTracker.ReleaseEvents([HeldInput.Key(VkRControl, extended: true)]);

        events.Should().Equal(InputEvent.Key(VkRControl, ExtendedKey | KeyUp, 0x1D));
    }

    [Theory]
    [InlineData((ushort)0x10, (ushort)0x2A, false)] // Shift: the left one's
    [InlineData((ushort)0x11, (ushort)0x1D, false)] // Ctrl: the left one's
    [InlineData((ushort)0xA1, (ushort)0x36, false)] // right Shift
    [InlineData((ushort)0xA3, (ushort)0x1D, true)] // right Ctrl: E0 1D
    [InlineData((ushort)0xA5, (ushort)0x38, true)] // right Alt: E0 38
    [InlineData((ushort)0x5C, (ushort)0x5C, true)] // right Windows: E0 5C
    public void ReleaseEvents_AModifierHeldWithoutAScanCode_GoesUpWithTheScanCodeAndExtendedFlagThatKeyHasOnEveryKeyboard(
        ushort key, ushort scan, bool extended)
    {
        // A modifier counted held without the key-down that pressed it (HeldInput.Key): its release must still reach the
        // remote desktop and VM clients and games that track keys by scan code, whichever path releases it
        var events = HeldInputTracker.ReleaseEvents([HeldInput.Key(key)]);

        events[^1].Should().Be(InputEvent.Key(key, (extended ? ExtendedKey : 0) | KeyUp, scan));
    }

    [Fact]
    public void ReleaseEvents_AnOrdinaryKeyHeldWithoutAScanCode_GoesUpWithout()
    {
        // Only a modifier's scan code is the same on every layout
        HeldInputTracker.ReleaseEvents([HeldInput.Key(VkC)]).Should().Equal(InputEvent.Key(VkC, KeyUp));
    }

    [Fact]
    public void Apply_KeepsTheScanCodeAndTheExtendedFlagOfTheKeyDown_AndTheReleaseSendsThem()
    {
        // The presses carry the layout's scan codes (roadmap M4.1); remote desktop and VM clients and games track keys by
        // them, so a key-up without one would leave the key down there
        var tracker = new HeldInputTracker();
        InputEvent[] batch =
        [
            InputEvent.Key(VkLShift, KeyDown, 0x2A), InputEvent.Key(VkRControl, ExtendedKey, 0x1D), InputEvent.Key(VkC, KeyDown, 0x2E),
            InputEvent.Key(VkC, KeyUp, 0x2E)
        ];

        tracker.Apply(batch, 3);

        tracker.Held.Should().Equal(
            HeldInput.Key(VkLShift) with { Scan = 0x2A }, HeldInput.Key(VkRControl, extended: true) with { Scan = 0x1D },
            HeldInput.Key(VkC) with { Scan = 0x2E });
        HeldInputTracker.ReleaseEvents(tracker.Held).Should().Equal(
            InputEvent.Key(VkC, KeyUp, 0x2E), InputEvent.Key(VkRControl, ExtendedKey | KeyUp, 0x1D), InputEvent.Key(VkLShift, KeyUp, 0x2A));
    }

    [Fact]
    public void Pressed_KeepsTheScanCodes()
    {
        // What a batch that threw pressed is released with the scan codes it was pressed with
        HeldInputTracker.Pressed([InputEvent.Key(VkControl, KeyDown, 0x1D), InputEvent.Key(VkC, KeyDown, 0x2E)])
            .Should().Equal(HeldInput.Key(VkControl) with { Scan = 0x1D }, HeldInput.Key(VkC) with { Scan = 0x2E });
    }

    [Fact]
    public void ReleaseEvents_AnAltReleaseWithItsScanCode_IsStillMasked()
    {
        var events = HeldInputTracker.ReleaseEvents([HeldInput.Key(VkMenu) with { Scan = 0x38 }]);

        events.Should().Equal(
            InputEvent.Key(HeldInputTracker.MaskKey, KeyDown), InputEvent.Key(HeldInputTracker.MaskKey, KeyUp),
            InputEvent.Key(VkMenu, KeyUp, 0x38));
    }

    [Theory]
    [InlineData((ushort)0xA0, (ushort)0x2A, false)] // left Shift
    [InlineData((ushort)0xA1, (ushort)0x36, false)] // right Shift
    [InlineData((ushort)0xA2, (ushort)0x1D, false)] // left Ctrl
    [InlineData((ushort)0xA3, (ushort)0x1D, true)] // right Ctrl: E0 1D
    [InlineData((ushort)0xA4, (ushort)0x38, false)] // left Alt
    [InlineData((ushort)0xA5, (ushort)0x38, true)] // right Alt: E0 38
    [InlineData((ushort)0x5B, (ushort)0x5B, true)] // left Windows: E0 5B
    [InlineData((ushort)0x5C, (ushort)0x5C, true)] // right Windows: E0 5C
    public void Modifier_AKeyWindowsReportsDown_HasTheScanCodeThatKeyHasOnEveryKeyboard(ushort key, ushort scan, bool extended)
    {
        // The user's (or a stuck) modifier: the server did not press it, so its release needs the key's own scan code
        HeldInput.Modifier(key).Should().Be(HeldInput.Key(key, extended) with { Scan = scan });
    }

    [Theory]
    [InlineData(VkMenu)]
    [InlineData(VkLWin)]
    public void ReleaseEvents_AltOrWindows_IsMaskedSoNoMenuOpens(ushort key)
    {
        // Alt or Windows going up with no key pressed since it went down opens the menu bar or the Start menu
        var events = HeldInputTracker.ReleaseEvents([HeldInput.Key(key)]);

        events.Should().HaveCount(3);
        events[0].Should().Be(InputEvent.Key(HeldInputTracker.MaskKey, KeyDown));
        events[1].Should().Be(InputEvent.Key(HeldInputTracker.MaskKey, KeyUp));
        events[2].Code.Should().Be(key);
        (events[2].Flags & KeyUp).Should().Be(KeyUp);
    }

    [Fact]
    public void ReleaseEvents_CtrlAndShift_AreNotMasked()
    {
        HeldInputTracker.ReleaseEvents([HeldInput.Key(VkControl), HeldInput.Key(VkLShift)])
            .Should().NotContain(e => e.Code == HeldInputTracker.MaskKey);
    }

    [Fact]
    public void ReleaseEvents_MouseButtonsAndCharacters_GetTheirUpEvents()
    {
        var events = HeldInputTracker.ReleaseEvents([HeldInput.Button(RightDown), new HeldInput(InputEventKind.Unicode, 'x')]);

        events.Should().Equal(InputEvent.Unicode('x', KeyUp), InputEvent.Mouse(RightUp));
    }

    [Fact]
    public void ReleaseEvents_XButtons_GetAnXUpNamingTheirButton()
    {
        var events = HeldInputTracker.ReleaseEvents([HeldInput.XButton(XButton2), HeldInput.Button(LeftDown)]);

        events.Should().Equal(InputEvent.Mouse(XUp, XButton2), InputEvent.Mouse(LeftUp));
    }

    [Fact]
    public void Pressed_ListsEveryDownOfTheBatch()
    {
        InputEvent[] events = [.. CtrlC, InputEvent.Mouse(Move | LeftDown), InputEvent.Mouse(LeftUp)];

        HeldInputTracker.Pressed(events).Select(p => p.Name).Should().Equal("Ctrl", "C", "left mouse button");
    }

    [Fact]
    public void StateKey_MapsButtonsToTheirKeyStateCodes()
    {
        HeldInputTracker.StateKey(HeldInput.Button(LeftDown)).Should().Be((ushort)0x01);
        HeldInputTracker.StateKey(HeldInput.Button(RightDown)).Should().Be((ushort)0x02);
        HeldInputTracker.StateKey(HeldInput.XButton(XButton1)).Should().Be((ushort)0x05);
        HeldInputTracker.StateKey(HeldInput.XButton(XButton2)).Should().Be((ushort)0x06);
        HeldInputTracker.StateKey(HeldInput.Key(VkControl)).Should().Be(VkControl);
        HeldInputTracker.StateKey(new HeldInput(InputEventKind.Unicode, 'x')).Should().BeNull();
    }

    [Fact]
    public void MarkHeld_AddsEachOnce()
    {
        var tracker = new HeldInputTracker();

        tracker.MarkHeld([HeldInput.Key(VkControl), HeldInput.Key(VkControl, extended: true)]);

        tracker.Held.Should().ContainSingle();
    }
}
