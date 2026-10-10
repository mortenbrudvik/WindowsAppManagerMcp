namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Verified activation and the user's pause (roadmap M1.10): while input is paused EnsureForeground
/// (focus_window, apply_layout and the keyboard guard) activates nothing, and its own input, the
/// zero-distance input unlock and the Esc that closes a menu, goes through the input session, so a pause
/// stops it too. Mocked windows and a fake input layer; nothing reaches the desktop.
/// </summary>
public class ForegroundServicePauseTests
{
    private static readonly nint Target = 0x100;
    private static readonly nint Terminal = 0x200;
    private const uint ActivationThread = 11, TerminalThread = 22, TargetThread = 33;
    private const uint GuiInMenuMode = 0x00000004;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeThreadInputWrapper> _threads = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();
    private readonly Mock<IWindowService> _windowService = new();
    private readonly FakeInputWrapper _input = new();
    private readonly InputSession _session;
    private nint _foreground = Terminal;
    private Func<nint, bool> _setForeground = _ => false;

    public ForegroundServicePauseTests()
    {
        _session = new InputSession(_input);
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => _foreground);
        _windows.Setup(w => w.GetAncestor(It.IsAny<nint>(), It.IsAny<WindowAncestor>())).Returns<nint, WindowAncestor>((h, _) => h);
        _windows.Setup(w => w.GetWindow(It.IsAny<nint>(), It.IsAny<uint>())).Returns(nint.Zero);
        _windows.Setup(w => w.SetForegroundWindow(It.IsAny<nint>())).Returns<nint>(h => _setForeground(h));
        _threads.Setup(t => t.GetCurrentThreadId()).Returns(ActivationThread);
        _threads.Setup(t => t.GetWindowThreadId(Terminal)).Returns(TerminalThread);
        _threads.Setup(t => t.GetWindowThreadId(Target)).Returns(TargetThread);
        _security.SetupGet(s => s.ServerIntegrity).Returns(Medium(1));
        _security.Setup(s => s.GetIntegrity(It.IsAny<int>())).Returns<int>(pid => Medium(pid));
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(true);
        _windowService.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns<nint>(h => TestDataFactory.CreateWindowInfo(handleValue: (int)h));
    }

    private ForegroundService CreateSut() =>
        new(_windows.Object, _threads.Object, new Mock<INativeInputWrapper>(MockBehavior.Strict).Object, _security.Object,
            _windowService.Object, new InlineForegroundThread(),
            new ForegroundTiming(TimeSpan.Zero, TimeSpan.FromMilliseconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(5)),
            logger: null, session: _session);

    private static ProcessIntegrity Medium(int pid) =>
        new(pid, ProcessIntegrity.LevelFromRid(ProcessIntegrity.MediumRid), ProcessIntegrity.MediumRid, false, null);

    [Fact]
    public void WhilePaused_ActivatesNothing()
    {
        // Arrange
        _session.Pause(InputControlSources.Hotkey);

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - activating would take the keyboard focus from the user
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.PausedByUser);
        outcome.Error.Should().Contain("pause hotkey");
        _windows.Verify(w => w.SetForegroundWindow(It.IsAny<nint>()), Times.Never);
        _windows.Verify(w => w.ShowWindow(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void APauseDuringActivation_StopsTheInputUnlock()
    {
        // Arrange - the foreground lock refuses, the attach is refused, and the user presses the hotkey meanwhile
        _threads.Setup(t => t.AttachThreadInput(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>()))
            .Returns(() =>
            {
                _session.Pause(InputControlSources.Hotkey);
                return false;
            });

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - no zero-distance move was sent
        outcome.ErrorCode.Should().Be(WindowErrorCode.PausedByUser);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void APauseDuringAStep_StopsTheLaterSteps()
    {
        // Arrange - the foreground lock refuses step 1, and the user presses the hotkey during it
        _setForeground = _ =>
        {
            _session.Pause(InputControlSources.Hotkey);
            return false;
        };

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 1000);

        // Assert - step 2 neither attaches nor activates, and step 3 sends nothing
        outcome.ErrorCode.Should().Be(WindowErrorCode.PausedByUser);
        _windows.Verify(w => w.SetForegroundWindow(It.IsAny<nint>()), Times.Once);
        _threads.Verify(t => t.AttachThreadInput(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>()), Times.Never);
        _windows.Verify(w => w.BringWindowToTop(It.IsAny<nint>()), Times.Never);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void APauseWhileTheWindowComesToTheFront_IsReportedAsThePause_NotAsSuccess()
    {
        // The window is in front (that cannot be undone), but the caller must not go on as if input were running
        _setForeground = h =>
        {
            _foreground = h;
            _session.Pause(InputControlSources.Hotkey);
            return true;
        };

        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        outcome.ErrorCode.Should().Be(WindowErrorCode.PausedByUser);
        outcome.Error.Should().Contain("stopped part-way");
    }

    [Fact]
    public void ThePauseStopsTheEscThatClosesAMenu()
    {
        // Arrange - the target comes to the front in menu mode, and the user presses the hotkey as it does
        _setForeground = h =>
        {
            _foreground = h;
            _session.Pause(InputControlSources.Hotkey);
            return true;
        };
        _threads.Setup(t => t.GetGuiThreadInfo(TargetThread))
            .Returns(new GuiThreadInfo(GuiInMenuMode, Target, Target, nint.Zero, Target));

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.ErrorCode.Should().Be(WindowErrorCode.PausedByUser);
        _input.Batches.Should().BeEmpty();
    }

    [Fact]
    public void WhileRunning_TheInputUnlockGoesThroughTheSession()
    {
        // Arrange - only input unlocks the foreground lock
        _setForeground = h =>
        {
            if (_input.Batches.Count == 0)
            {
                return false;
            }

            _foreground = h;
            return true;
        };
        _threads.Setup(t => t.AttachThreadInput(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>())).Returns(false);

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.InputUnlock);
        _input.Batches.Should().ContainSingle().Which.Should().Equal(InputEvent.Mouse(0x0001));
    }
}
