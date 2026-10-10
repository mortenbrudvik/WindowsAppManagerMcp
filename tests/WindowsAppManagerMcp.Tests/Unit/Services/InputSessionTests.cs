namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The input session (roadmap M1.10): the user's pause, and the release of keys and buttons the server left
/// down after a short SendInput count, an exception, a pause or on request. The native input layer is a
/// fake (<see cref="FakeInputWrapper"/>), so nothing reaches the desktop.
/// </summary>
public class InputSessionTests
{
    private const uint KeyDown = 0x0000, KeyUp = 0x0002, LeftDown = 0x0002, LeftUp = 0x0004;
    private const ushort VkControl = 0x11, VkMenu = 0x12, VkC = 0x43, VkLControl = 0xA2, VkLMenu = 0xA4, VkRShift = 0xA1;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly InputEvent[] CtrlC =
    [
        InputEvent.Key(VkControl, KeyDown), InputEvent.Key(VkC, KeyDown), InputEvent.Key(VkC, KeyUp), InputEvent.Key(VkControl, KeyUp)
    ];

    private readonly FakeInputWrapper _input = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    private InputSession CreateSut() => new(_input, PauseHotkeyOptions.Default, _time);

    #region Send

    [Fact]
    public void Send_WhileRunning_SendsTheBatchAndHoldsNothing()
    {
        var sut = CreateSut();

        var outcome = sut.Send(CtrlC);

        outcome.Complete.Should().BeTrue();
        _input.Batches.Should().ContainSingle().Which.Should().Equal(CtrlC);
        sut.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public void Send_WhilePaused_SendsNothing()
    {
        var sut = CreateSut();
        sut.Pause(InputControlSources.Hotkey);

        var outcome = sut.Send(CtrlC);

        outcome.PausedByUser.Should().BeTrue();
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void Send_ShortCount_ReleasesTheKeysTheInsertedEventsLeftDown()
    {
        // Arrange - SendInput stops after Ctrl and C went down (a desktop switch or input block mid-batch)
        _input.Insert = (_, call) => call == 0 ? 2u : uint.MaxValue;
        var sut = CreateSut();

        // Act
        var outcome = sut.Send(CtrlC);

        // Assert - C and then Ctrl are sent up at once, and nothing stays down
        outcome.Complete.Should().BeFalse();
        outcome.Inserted.Should().Be(2);
        _input.Batches.Should().HaveCount(2);
        _input.Batches[1].Should().Equal(InputEvent.Key(VkC, KeyUp), InputEvent.Key(VkControl, KeyUp));
        _input.IsKeyDown(VkControl).Should().BeFalse();
        sut.ServerHeld.Should().BeEmpty();
        sut.LastRelease!.Reason.Should().Contain("2 of 4");
        sut.LastRelease.Released.Should().Equal("C", "Ctrl");
    }

    [Fact]
    public void Send_ShortCountAndTheReleaseFailsToo_KeepsTheKeysForTheNextRelease()
    {
        // Arrange - the batch stops after Ctrl down, and the release inserts nothing (the desktop is gone)
        _input.Insert = (_, call) => call switch { 0 => 1u, 1 => 0u, _ => uint.MaxValue };
        var sut = CreateSut();

        // Act
        sut.Send(CtrlC);
        var heldAfterTheFailure = sut.ServerHeld.Select(h => h.Name).ToList();
        var stillHeld = sut.LastRelease!.StillHeld;
        var retry = sut.ReleaseHeldInput("retry");

        // Assert
        heldAfterTheFailure.Should().Equal("Ctrl");
        stillHeld.Should().Equal("Ctrl");
        retry.Complete.Should().BeTrue();
        retry.Released.Should().Equal("Ctrl");
        _input.IsKeyDown(VkControl).Should().BeFalse();
    }

    [Fact]
    public void Send_Exception_ReleasesTheKeysOfTheBatchThatAreDown_AndRethrows()
    {
        // Arrange - SendInput throws after inserting Ctrl down; Windows reports Ctrl down, C is up
        _input.Insert = (events, call) =>
        {
            if (call == 0)
            {
                _input.Press(VkControl);
                throw new InvalidOperationException("native failure");
            }

            return (uint)events.Count;
        };
        var sut = CreateSut();

        // Act
        var act = () => sut.Send(CtrlC);

        // Assert - only Ctrl is released; a stray key-up for a key that never went down is not sent
        act.Should().Throw<InvalidOperationException>();
        _input.Batches.Should().HaveCount(2);
        _input.Batches[1].Should().Equal(InputEvent.Key(VkControl, KeyUp));
        sut.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public void Send_Exception_WithNothingDown_SendsNoRelease()
    {
        _input.Insert = (_, _) => throw new InvalidOperationException("native failure");
        var sut = CreateSut();

        var act = () => sut.Send(CtrlC);

        act.Should().Throw<InvalidOperationException>();
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public void Send_ABatchThatLeavesAButtonDown_IsTrackedUntilReleased()
    {
        var sut = CreateSut();

        sut.Send([InputEvent.Mouse(LeftDown)]);
        var held = sut.ServerHeld.Select(h => h.Name).ToList();
        var release = sut.ReleaseHeldInput("end of the call");

        held.Should().Equal("left mouse button");
        release.Released.Should().Equal("left mouse button");
        _input.Batches[^1].Should().Equal(InputEvent.Mouse(LeftUp));
    }

    [Fact]
    public void ReleaseHeldInput_WithNothingHeld_SendsNothing()
    {
        var sut = CreateSut();

        var outcome = sut.ReleaseHeldInput("end of the call");

        outcome.Should().BeSameAs(InputReleaseOutcome.Nothing);
        _input.Batches.Should().BeEmpty();
        sut.LastRelease.Should().BeNull();
    }

    [Fact]
    public void ReleaseHeldInput_ThatThrows_ReportsInsteadOfThrowing()
    {
        // A release runs on failure paths; it must not replace the original outcome with its own exception
        var sut = CreateSut();
        sut.Send([InputEvent.Key(VkControl, KeyDown)]);
        _input.Insert = (_, _) => throw new InvalidOperationException("desktop gone");

        var outcome = sut.ReleaseHeldInput("end of the call");

        outcome.StillHeld.Should().Equal("Ctrl");
        outcome.Error.Should().Contain("desktop gone");
    }

    #endregion

    #region Pause

    [Fact]
    public void Pause_CancelsThePauseToken_AndRecordsHowAndWhen()
    {
        var sut = CreateSut();
        var token = sut.PauseToken;

        var paused = sut.Pause(InputControlSources.Hotkey);

        paused.Should().BeTrue();
        SpinWait.SpinUntil(() => token.IsCancellationRequested, Timeout).Should().BeTrue();
        sut.IsPaused.Should().BeTrue();
        sut.PauseState.Should().Be(new InputPauseState(true, "hotkey", _time.Now));
    }

    [Fact]
    public void Pause_ReleasesWhatTheServerHolds_ButNotTheUsersKeys()
    {
        // Arrange - the server left Ctrl down; the user holds Right Shift
        var sut = CreateSut();
        sut.Send([InputEvent.Key(VkControl, KeyDown)]);
        _input.Press(VkRShift);

        // Act
        sut.Pause(InputControlSources.Hotkey);

        // Assert
        _input.Batches[^1].Should().Equal(InputEvent.Key(VkControl, KeyUp));
        _input.IsKeyDown(VkRShift).Should().BeTrue();
        sut.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public void Pause_Twice_ReportsThatItWasAlreadyPaused()
    {
        var sut = CreateSut();
        sut.Pause(InputControlSources.Hotkey);
        _time.Advance(TimeSpan.FromMinutes(1));

        sut.Pause(InputControlSources.Hotkey).Should().BeFalse();
        sut.PauseState.PausedAt.Should().Be(_time.Now - TimeSpan.FromMinutes(1), "the first pause is kept");
    }

    [Fact]
    public void Resume_HandsOutAFreshToken_AndSendWorksAgain()
    {
        var sut = CreateSut();
        var before = sut.PauseToken;
        sut.Pause(InputControlSources.Hotkey);

        var resumed = sut.Resume(InputControlSources.ResumeInput);

        resumed.Should().BeTrue();
        SpinWait.SpinUntil(() => before.IsCancellationRequested, Timeout).Should().BeTrue("a call that started before the pause stays stopped");
        sut.PauseToken.IsCancellationRequested.Should().BeFalse();
        sut.PauseState.Should().Be(new InputPauseState(false, "hotkey", _time.Now, "resume_input", _time.Now));
        sut.Send(CtrlC).Complete.Should().BeTrue();
    }

    [Fact]
    public void Resume_WhenRunning_DoesNothing()
    {
        CreateSut().Resume(InputControlSources.ResumeInput).Should().BeFalse();
    }

    [Fact]
    public void TogglePauseByHotkey_PausesThenResumes()
    {
        var sut = CreateSut();

        sut.TogglePauseByHotkey();
        var pausedAfterFirst = sut.IsPaused;
        sut.TogglePauseByHotkey();

        pausedAfterFirst.Should().BeTrue();
        sut.IsPaused.Should().BeFalse();
        sut.PauseState.ResumedBy.Should().Be(InputControlSources.Hotkey);
    }

    [Fact]
    public async Task Pause_WhileABatchIsBeingSent_ReturnsOnlyAfterIt_AndNoLaterBatchIsSent()
    {
        // Arrange - the first batch is held inside SendInput
        var inSend = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        _input.BeforeSend = _ =>
        {
            if (!inSend.IsSet)
            {
                inSend.Set();
                letGo.Wait(Timeout);
            }
        };
        var sut = CreateSut();
        var sending = Task.Run(() => sut.Send(CtrlC));
        inSend.Wait(Timeout).Should().BeTrue();

        // Act
        var pausing = Task.Run(() => sut.Pause(InputControlSources.Hotkey));
        var pausedWhileSending = await Task.WhenAny(pausing, Task.Delay(150)) == pausing;
        letGo.Set();
        await Task.WhenAll(sending, pausing).WaitAsync(Timeout);
        var next = sut.Send(CtrlC);

        // Assert - the pause waited for the batch in flight, and the next batch was refused
        pausedWhileSending.Should().BeFalse();
        (await sending).Complete.Should().BeTrue();
        next.PausedByUser.Should().BeTrue();
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public void SetCursorPos_WhilePaused_DoesNotMoveTheCursor()
    {
        var sut = CreateSut();
        sut.Pause(InputControlSources.Hotkey);

        sut.SetCursorPos(10, 10).Should().BeNull();
        _input.CursorMoves.Should().Be(0);
    }

    #endregion

    #region Modifiers

    [Fact]
    public void ReadModifiersDown_ReportsTheLeftAndRightKeysWindowsReportsDown()
    {
        _input.Press(VkLControl);
        _input.Press(VkRShift);

        CreateSut().ReadModifiersDown().Select(k => k.Name).Should().Equal("LeftCtrl", "RightShift");
    }

    [Fact]
    public void ReleaseModifiers_SendsTheirUps_MaskingAlt()
    {
        _input.Press(VkLMenu);
        var sut = CreateSut();

        var outcome = sut.ReleaseModifiers(sut.ReadModifiersDown(), "releaseHeldModifiers");

        outcome.Released.Should().Equal("LeftAlt");
        _input.Batches.Single().Should().Equal(
            InputEvent.Key(HeldInputTracker.MaskKey, KeyDown),
            InputEvent.Key(HeldInputTracker.MaskKey, KeyUp),
            InputEvent.Key(VkLMenu, KeyUp));
        sut.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public void ReleaseModifiers_WhilePaused_IsRefused()
    {
        // The user paused: the server leaves the user's keys alone
        _input.Press(VkLControl);
        var sut = CreateSut();
        sut.Pause(InputControlSources.Hotkey);

        var outcome = sut.ReleaseModifiers(sut.ReadModifiersDown(), "releaseHeldModifiers");

        outcome.PausedByUser.Should().BeTrue();
        _input.IsKeyDown(VkLControl).Should().BeTrue();
    }

    [Fact]
    public void ReleaseHeldInput_WhilePaused_StillReleasesTheServersKeys()
    {
        var sut = CreateSut();
        _input.Insert = (_, call) => call == 1 ? 0u : uint.MaxValue; // the pause's own release fails
        sut.Send([InputEvent.Key(VkMenu, KeyDown)]);
        sut.Pause(InputControlSources.Hotkey);
        _input.Insert = null;

        var outcome = sut.ReleaseHeldInput("release_all_input");

        outcome.Released.Should().Equal("Alt");
        _input.IsKeyDown(VkMenu).Should().BeFalse();
    }

    #endregion

    #region Pause chord

    [Theory]
    [InlineData("^%+{PAUSE}", "Ctrl+Alt+Shift+Pause")]
    [InlineData("^c", null)]
    public void FindPauseChord_FindsTheUsersChord(string keys, string? expected)
    {
        CreateSut().FindPauseChord(SendKeysParser.Parse(keys)).Should().Be(expected);
    }

    [Fact]
    public void FindPauseChord_WithTheHotkeyOff_FindsNothing()
    {
        var off = PauseHotkeyOptions.FromEnvironment(_ => "none");

        new InputSession(_input, off).FindPauseChord(SendKeysParser.Parse("^%+{PAUSE}")).Should().BeNull();
    }

    #endregion

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by) => Now += by;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
