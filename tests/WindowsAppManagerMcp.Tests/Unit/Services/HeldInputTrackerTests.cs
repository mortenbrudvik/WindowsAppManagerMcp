namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The held-input bookkeeping of the input session (roadmap M1.10): which keys and buttons a batch leaves
/// down, given how many of its events SendInput inserted, and the events that release them. Pure.
/// </summary>
public class HeldInputTrackerTests
{
    private const uint KeyDown = 0x0000, ExtendedKey = 0x0001, KeyUp = 0x0002;
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, Move = 0x0001;
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

        events.Should().Equal(InputEvent.Key(VkC, KeyUp), InputEvent.Key(VkLShift, KeyUp), InputEvent.Key(VkControl, KeyUp));
    }

    [Fact]
    public void ReleaseEvents_KeepsTheExtendedKeyFlag()
    {
        var events = HeldInputTracker.ReleaseEvents([HeldInput.Key(VkRControl, extended: true)]);

        events.Should().Equal(InputEvent.Key(VkRControl, ExtendedKey | KeyUp));
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
