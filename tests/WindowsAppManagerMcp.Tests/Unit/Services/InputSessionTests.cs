using Microsoft.Extensions.Logging;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The input session (roadmap M1.10): the user's pause, shared by every server on the desktop, and the release
/// of keys and buttons the server left down after a short SendInput count, an exception, a pause or on request.
/// The native input layer is a fake (<see cref="FakeInputWrapper"/>), so nothing reaches the desktop, and the
/// shared pause is a <see cref="FakeDesktopPauseFlag"/>, never the real one.
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
    private readonly ManualClock _time = new();

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
    public void Send_AnXButtonLeftDown_IsReleasedWithItsOwnButton()
    {
        // The X buttons share XDOWN and XUP and differ in mouseData: X2 going up must not count as X1's release
        var sut = CreateSut();

        sut.Send([InputEvent.Mouse(0x0080, 1), InputEvent.Mouse(0x0080, 2), InputEvent.Mouse(0x0100, 2)]);
        var held = sut.ServerHeld.Select(h => h.Name).ToList();
        var release = sut.ReleaseHeldInput("end of the call");

        held.Should().Equal("X1 mouse button");
        release.Released.Should().Equal("X1 mouse button");
        _input.Batches[^1].Should().Equal(InputEvent.Mouse(0x0100, 1));
        _input.IsKeyDown(0x05).Should().BeFalse();
        sut.ServerHeld.Should().BeEmpty();
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
        sut.PauseState.Should().Be(new InputPauseState(true, "hotkey", _time.Now, PauseNumber: 1));
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
        sut.PauseState.Should().Be(new InputPauseState(false, "hotkey", _time.Now, "resume_input", _time.Now, PauseNumber: 1));
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
        _time.Advance(InputSession.HotkeyResumeDelay);
        sut.TogglePauseByHotkey();

        pausedAfterFirst.Should().BeTrue();
        sut.IsPaused.Should().BeFalse();
        sut.PauseState.ResumedBy.Should().Be(InputControlSources.Hotkey);
    }

    [Fact]
    public void TogglePauseByHotkey_ASecondPressRightAfterThePause_DoesNotResume()
    {
        // The hotkey gives no feedback, so a user may press it twice to be sure
        var sut = CreateSut();
        sut.TogglePauseByHotkey();

        _time.Advance(InputSession.HotkeyResumeDelay - TimeSpan.FromMilliseconds(1));
        sut.TogglePauseByHotkey();

        sut.IsPaused.Should().BeTrue();
        sut.PauseState.ResumedBy.Should().BeNull();
    }

    [Fact]
    public void TogglePauseByHotkey_AfterTheWallClockWentBack_StillResumes()
    {
        // A time sync (or the user) set the clock back an hour while input was paused; the delay after the pause
        // is measured on the monotonic clock, so the user's press 2 s after the pause still resumes
        var sut = CreateSut();
        sut.TogglePauseByHotkey();

        _time.SetWallClock(_time.Now - TimeSpan.FromHours(1));
        _time.Advance(InputSession.HotkeyResumeDelay);
        sut.TogglePauseByHotkey();

        sut.IsPaused.Should().BeFalse();
        sut.PauseState.ResumedBy.Should().Be(InputControlSources.Hotkey);
    }

    [Fact]
    public void TogglePauseByHotkey_ASecondPressRightAfterThePause_DoesNotResume_EvenIfTheWallClockJumpedAhead()
    {
        var sut = CreateSut();
        sut.TogglePauseByHotkey();

        _time.SetWallClock(_time.Now + TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        sut.TogglePauseByHotkey();

        sut.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void Resume_ForAnEarlierPause_IsRefused()
    {
        // resume_input asked about the first pause; the user resumed and paused again meanwhile
        var sut = CreateSut();
        sut.Pause(InputControlSources.Hotkey);
        var asked = sut.PauseState.PauseNumber;
        sut.Resume(InputControlSources.Hotkey);
        sut.Pause(InputControlSources.Hotkey);

        var resumed = sut.Resume(InputControlSources.ResumeInput, asked);

        resumed.Should().BeFalse();
        sut.IsPaused.Should().BeTrue();
        sut.Resume(InputControlSources.ResumeInput, sut.PauseState.PauseNumber).Should().BeTrue();
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

    #region Shared pause (several servers on one desktop)

    /// <summary>A second server on the same desktop: its own input layer and session, the same shared pause.</summary>
    private (InputSession Session, FakeInputWrapper Input) AnotherServer(FakeDesktopPauseFlag flag)
    {
        var input = new FakeInputWrapper();
        var session = new InputSession(input, PauseHotkeyOptions.Default, _time);
        session.ShareDesktopPause(flag);
        return (session, input);
    }

    [Fact]
    public void APauseInOneServer_PausesTheOther_AndStopsItsRunningCalls()
    {
        // Arrange - two servers; only the first one has the hotkey
        var flag = new FakeDesktopPauseFlag();
        using var first = CreateSut();
        first.ShareDesktopPause(flag);
        var (second, secondInput) = AnotherServer(flag);
        using var _ = second;
        var running = second.PauseToken;

        // Act - the user presses the hotkey, which the first server handles
        first.TogglePauseByHotkey();

        // Assert - the second server's running call is cancelled by its watcher, and its next batch is refused
        SpinWait.SpinUntil(() => running.IsCancellationRequested, Timeout).Should().BeTrue();
        second.IsPaused.Should().BeTrue();
        second.PauseState.PausedBy.Should().Be(InputControlSources.Hotkey);
        second.Send(CtrlC).PausedByUser.Should().BeTrue();
        secondInput.Batches.Should().BeEmpty();
    }

    [Fact]
    public void APauseInAnotherServer_IsInForceBeforeTheNextBatch_EvenBeforeTheWatcherRuns()
    {
        // The flag is read under the session's lock before every batch, not only by the watcher
        var flag = new FakeDesktopPauseFlag();
        using var sut = CreateSut();
        sut.ShareDesktopPause(flag);
        _input.BeforeSend = _ => flag.SetByAnotherServer();

        var during = sut.Send(CtrlC);
        var after = sut.Send(CtrlC);

        during.Complete.Should().BeTrue("the batch was already being sent");
        after.PausedByUser.Should().BeTrue();
        _input.Batches.Should().ContainSingle();
    }

    [Fact]
    public void AResumeInOneServer_ResumesTheOther()
    {
        var flag = new FakeDesktopPauseFlag();
        using var first = CreateSut();
        first.ShareDesktopPause(flag);
        var (second, _) = AnotherServer(flag);
        using var secondSession = second;
        first.TogglePauseByHotkey();
        SpinWait.SpinUntil(() => second.IsPaused, Timeout).Should().BeTrue();

        // The user confirms resume_input in the second server's client
        second.Resume(InputControlSources.ResumeInput).Should().BeTrue();

        first.IsPaused.Should().BeFalse();
        first.PauseState.ResumedBy.Should().Be(InputControlSources.OtherServer);
        first.PauseToken.IsCancellationRequested.Should().BeFalse("calls in the first server can run again");
        first.Send(CtrlC).Complete.Should().BeTrue();
    }

    [Fact]
    public void AServerThatStartsWhileTheDesktopIsPaused_StartsPaused()
    {
        var flag = new FakeDesktopPauseFlag();
        flag.SetByAnotherServer();
        using var sut = CreateSut();

        sut.ShareDesktopPause(flag);

        sut.IsPaused.Should().BeTrue();
        sut.Send(CtrlC).PausedByUser.Should().BeTrue();
    }

    [Fact]
    public void APauseMadeBeforeSharing_CarriesOverToTheOtherServers()
    {
        var flag = new FakeDesktopPauseFlag();
        using var sut = CreateSut();
        sut.Pause(InputControlSources.Hotkey);

        sut.ShareDesktopPause(flag);

        flag.IsSet.Should().BeTrue();
        sut.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void APauseInAnotherServer_ReleasesWhatThisServerHolds()
    {
        // Arrange - this server is in the middle of a call and holds Ctrl
        var flag = new FakeDesktopPauseFlag();
        using var sut = CreateSut();
        sut.ShareDesktopPause(flag);
        sut.Send([InputEvent.Key(VkControl, KeyDown)]);

        // Act
        flag.SetByAnotherServer();

        // Assert - the watcher released it without waiting for the call's next batch
        SpinWait.SpinUntil(() => !_input.IsKeyDown(VkControl), Timeout).Should().BeTrue();
        sut.LastRelease!.Reason.Should().Be("input was paused");
    }

    [Fact]
    public void AFlagThatCannotBeWritten_StillPausesAndResumesThisServer()
    {
        var flag = new FakeDesktopPauseFlag { WriteFails = new UnauthorizedAccessException("denied") };
        using var sut = CreateSut();
        sut.ShareDesktopPause(flag);

        sut.Pause(InputControlSources.Hotkey).Should().BeTrue();
        var pausedWhileTheFlagIsNot = sut.IsPaused;
        sut.Resume(InputControlSources.ResumeInput).Should().BeTrue();

        pausedWhileTheFlagIsNot.Should().BeTrue();
        sut.IsPaused.Should().BeFalse();
    }

    public static TheoryData<Exception> ReadFailures => new()
    {
        new IOException("the handle broke"),
        new UnauthorizedAccessException("denied"),
        new ObjectDisposedException("flag")
    };

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public void AFlagThatCannotBeRead_PausesInput_AndIsLoggedOnce(Exception failure)
    {
        // Arrange - another server's pause may be in the flag, so input fails closed (review F4)
        var logger = new RecordingLogger<InputSession>();
        var flag = new FakeDesktopPauseFlag { ReadFails = failure };
        using var sut = new InputSession(_input, PauseHotkeyOptions.Default, _time, logger);

        // Act
        sut.ShareDesktopPause(flag);

        // Assert
        sut.IsPaused.Should().BeTrue();
        sut.PauseState.PausedBy.Should().Be(InputControlSources.SharedPauseUnreadable);
        sut.Send(CtrlC).PausedByUser.Should().BeTrue();
        _input.Batches.Should().BeEmpty();
        // The watcher thread logs its own failure too: a copy, not the live list
        logger.Entries.ToArray().Count(e => e.Level == LogLevel.Error && e.Message.Contains("could not be read")).Should().Be(1,
            "an unreadable flag is logged once, not before every batch");
    }

    [Fact]
    public void AFlagThatCannotBeRead_StopsRunningCalls_AndReleasesWhatTheServerHolds()
    {
        // Arrange - a call is running and holds Ctrl
        var flag = new FakeDesktopPauseFlag();
        using var sut = CreateSut();
        sut.ShareDesktopPause(flag);
        sut.Send([InputEvent.Key(VkControl, KeyDown)]);
        var running = sut.PauseToken;

        // Act
        flag.ReadFails = new IOException("the handle broke");
        var next = sut.Send(CtrlC);

        // Assert
        next.PausedByUser.Should().BeTrue();
        running.IsCancellationRequested.Should().BeTrue();
        _input.IsKeyDown(VkControl).Should().BeFalse();
    }

    [Fact]
    public void AFlagThatCannotBeRead_StaysPaused_UntilTheUserResumes_EvenOnceItCanBeReadAgain()
    {
        // Arrange
        var flag = new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") };
        using var sut = CreateSut();
        sut.ShareDesktopPause(flag);

        // Act - the flag can be read again, and nobody paused
        flag.ReadFails = null;

        // Assert - the server cannot tell what it missed meanwhile: only the user resumes
        sut.IsPaused.Should().BeTrue();
        sut.Resume(InputControlSources.ResumeInput).Should().BeTrue();
        sut.IsPaused.Should().BeFalse();
        sut.Send(CtrlC).Complete.Should().BeTrue();
    }

    [Fact]
    public void AFlagThatCannotBeRead_CanBeResumedByTheUser_AndDoesNotPauseAgainUntilItWasReadable()
    {
        // The user confirmed resume_input: one unreadable flag pauses once, not before every batch
        var flag = new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") };
        using var sut = CreateSut();
        sut.ShareDesktopPause(flag);

        sut.Resume(InputControlSources.ResumeInput).Should().BeTrue();

        sut.IsPaused.Should().BeFalse();
        sut.Send(CtrlC).Complete.Should().BeTrue();
    }

    [Fact]
    public void AHotkeyPress_WhileTheServerPausedItselfForAnUnreadableFlag_KeepsThePause_AsTheUsersOwn()
    {
        // Arrange - nobody told the user the server paused itself, so a press of the hotkey asks for a pause: resuming
        // would turn the user's stop control into a go (review F4)
        var flag = new FakeDesktopPauseFlag { ReadFails = new IOException("the handle broke") };
        using var sut = CreateSut();
        sut.ShareDesktopPause(flag);
        _time.Advance(InputSession.HotkeyResumeDelay * 2);

        // Act
        sut.TogglePauseByHotkey();

        // Assert
        sut.IsPaused.Should().BeTrue();
        sut.PauseState.PausedBy.Should().Be(InputControlSources.Hotkey, "the pause is the user's own now");
        sut.PauseState.ResumedBy.Should().BeNull();
        sut.Send(CtrlC).PausedByUser.Should().BeTrue();

        // And the user's next press resumes, as after any pause they made
        _time.Advance(InputSession.HotkeyResumeDelay);
        sut.TogglePauseByHotkey();
        sut.IsPaused.Should().BeFalse();
        sut.PauseState.ResumedBy.Should().Be(InputControlSources.Hotkey);
    }

    [Fact]
    public void ShareDesktopPause_Twice_Throws()
    {
        using var sut = CreateSut();
        sut.ShareDesktopPause(new FakeDesktopPauseFlag());

        var again = () => sut.ShareDesktopPause(new FakeDesktopPauseFlag());

        again.Should().Throw<InvalidOperationException>();
        sut.SharesDesktopPause.Should().BeTrue();
    }

    [Fact]
    public void Dispose_StopsTheWatcher()
    {
        var flag = new FakeDesktopPauseFlag();
        var sut = CreateSut();
        sut.ShareDesktopPause(flag);
        var watcher = (Thread)typeof(InputSession)
            .GetField("_watcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(sut)!;

        sut.Dispose();

        watcher.IsAlive.Should().BeFalse();
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
            InputEvent.Key(VkLMenu, KeyUp, 0x38));
        sut.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public void ReleaseModifiers_ReportsOnlyItsOwnKeys()
    {
        // A key of the server that could not be released earlier is not reported again as one of the user's
        _input.Insert = (_, call) => call == 1 ? 0u : uint.MaxValue;
        var sut = CreateSut();
        sut.Send([InputEvent.Key(VkC, KeyDown)]);
        sut.ReleaseHeldInput("end of the call");
        _input.Press(VkRShift);

        var outcome = sut.ReleaseModifiers(sut.ReadModifiersDown(), "releaseHeldModifiers");

        outcome.Released.Should().Equal("RightShift");
        outcome.StillHeld.Should().BeEmpty();
        sut.ServerHeld.Select(h => h.Name).Should().Equal("C");
    }

    [Fact]
    public void AReleaseCutShortAfterTheMaskingKeyWentDown_ReportsAndTracksIt()
    {
        // Only the masking key's down went in: it is held now, and released with the next release
        _input.Press(VkLMenu);
        var sut = CreateSut();
        _input.Insert = (_, _) => 1u;

        var outcome = sut.ReleaseModifiers(sut.ReadModifiersDown(), "releaseHeldModifiers");
        _input.Insert = null;
        var retry = sut.ReleaseHeldInput("retry");

        outcome.StillHeld.Should().BeEquivalentTo("LeftAlt", "VK 0xE8");
        retry.Released.Should().Equal("VK 0xE8");
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

    [Theory]
    [InlineData(new ushort[] { 0xA2, 0xA4, 0xA0 }, true)] // LeftCtrl, LeftAlt, LeftShift
    [InlineData(new ushort[] { 0xA3, 0xA5, 0xA1 }, true)] // the right-hand ones
    [InlineData(new ushort[] { 0xA2, 0xA4, 0xA0, 0x5B }, true)] // and Windows too
    [InlineData(new ushort[] { 0xA2, 0xA4 }, true)] // Shift is not down yet: the user presses the modifiers one by one
    [InlineData(new ushort[] { 0xA2 }, true)] // Ctrl alone: the start of the chord, or a stuck key
    [InlineData(new ushort[] { 0xA1 }, true)] // RightShift alone
    [InlineData(new ushort[] { 0x5B }, false)] // the Windows key is not part of the chord
    [InlineData(new ushort[0], false)]
    public void IsPauseChordHeld_WhenAnyModifierOfTheChordIsDown(ushort[] down, bool expected)
    {
        var keys = down.Select(vk => HeldInput.Key(vk)).ToList();

        CreateSut().IsPauseChordHeld(keys).Should().Be(expected);
    }

    [Fact]
    public void IsPauseChordHeld_WithTheHotkeyOff_IsFalse()
    {
        var off = PauseHotkeyOptions.FromEnvironment(_ => "none");
        var keys = new ushort[] { 0xA2, 0xA4, 0xA0 }.Select(vk => HeldInput.Key(vk)).ToList();

        new InputSession(_input, off).IsPauseChordHeld(keys).Should().BeFalse();
    }

    #endregion

    #region Resume question

    [Fact]
    public void TryOpenResumeQuestion_OnlyOneAtATime_AndClosingLetsTheNextOneOpen()
    {
        var sut = CreateSut();

        var first = sut.TryOpenResumeQuestion();
        var whileOpen = sut.TryOpenResumeQuestion();
        first!.Dispose();
        var afterClose = sut.TryOpenResumeQuestion();

        first.Should().NotBeNull();
        whileOpen.Should().BeNull("one question is still open");
        afterClose.Should().NotBeNull();
    }

    [Fact]
    public void TryOpenResumeQuestion_ClosingAQuestionTwice_DoesNotCloseTheNextOne()
    {
        var sut = CreateSut();
        var first = sut.TryOpenResumeQuestion()!;
        first.Dispose();
        using var second = sut.TryOpenResumeQuestion();

        first.Dispose();

        second.Should().NotBeNull();
        sut.TryOpenResumeQuestion().Should().BeNull("the second question is still open");
    }

    [Fact]
    public void TryOpenResumeQuestion_IsPerSession()
    {
        using var open = CreateSut().TryOpenResumeQuestion();

        CreateSut().TryOpenResumeQuestion().Should().NotBeNull("each server's session has its own question");
    }

    #endregion
}
