namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The activation steps of <see cref="ForegroundService"/>, against mocked wrappers that simulate the
/// Windows foreground lock: SetForegroundWindow succeeds only in the situations the lock allows
/// (<see cref="ForegroundLock"/>). Every window change, attach and input batch is recorded in order. Nothing here
/// reaches a real window or sends input.
/// </summary>
public class ForegroundServiceTests
{
    private static readonly nint Target = 0x100;
    private static readonly nint Terminal = 0x200;
    private static readonly nint Dialog = 0x300;
    private static readonly nint OtherDialog = 0x400;

    // Owned the WPF and WinForms way: overlapped, owned through GWLP_HWNDPARENT, not WS_POPUP. GA_ROOTOWNER
    // walks GetParent, which returns the owner only for WS_POPUP windows, so these are their own root owner.
    private static readonly nint WpfDialog = 0x500;
    private static readonly nint WpfNestedDialog = 0x600;
    private const uint ActivationThread = 11;
    private const uint TerminalThread = 22;
    private const uint TargetThread = 33;
    private const int TerminalPid = 2000;
    private const int TargetPid = 3000;
    private const long WsDisabled = 0x08000000;

    // Win32 values (NativeEnums is internal to the server)
    private const uint GW_OWNER = 4;
    private const uint GW_ENABLEDPOPUP = 6;
    private const int GWL_STYLE = -16;
    private const int SW_RESTORE = 9;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;
    private const ushort VK_LCONTROL = 0xA2;

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeThreadInputWrapper> _threads = new();
    private readonly Mock<INativeInputWrapper> _input = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();
    private readonly Mock<IWindowService> _windowService = new();
    private readonly InlineForegroundThread _activationThread = new();
    private readonly List<string> _calls = [];
    private readonly List<IReadOnlyList<InputEvent>> _sentInput = [];

    private nint _foreground = Terminal;
    private ForegroundLock _lock = ForegroundLock.Always;
    private bool _attached;
    private bool _serverSentInput;

    public ForegroundServiceTests()
    {
        var windows = new[] { Target, Terminal, Dialog, OtherDialog, WpfDialog, WpfNestedDialog };
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns<nint>(h => windows.Contains(h));
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns<nint>(h => windows.Contains(h));
        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => _foreground);
        _windows.Setup(w => w.GetAncestor(It.IsAny<nint>(), WindowAncestor.Root)).Returns<nint, WindowAncestor>((h, _) => h);
        // The Win32 dialogs (WS_POPUP) are owned by the target: their root owner is the target. The WPF
        // dialogs are owned too (GW_OWNER), but each is its own root owner.
        _windows.Setup(w => w.GetAncestor(It.IsAny<nint>(), WindowAncestor.RootOwner))
            .Returns<nint, WindowAncestor>((h, _) => h == Dialog || h == OtherDialog ? Target : h);
        _windows.Setup(w => w.GetWindow(Dialog, GW_OWNER)).Returns(Target);
        _windows.Setup(w => w.GetWindow(OtherDialog, GW_OWNER)).Returns(Target);
        _windows.Setup(w => w.GetWindow(WpfDialog, GW_OWNER)).Returns(Target);
        _windows.Setup(w => w.GetWindow(WpfNestedDialog, GW_OWNER)).Returns(WpfDialog);
        // GW_ENABLEDPOPUP returns the window itself when it has no enabled popup
        _windows.Setup(w => w.GetWindow(It.IsAny<nint>(), GW_ENABLEDPOPUP)).Returns<nint, uint>((h, _) => h);
        _windows.Setup(w => w.GetWindowThreadProcessId(Terminal)).Returns((uint)TerminalPid);
        _windows.Setup(w => w.GetWindowThreadProcessId(Target)).Returns((uint)TargetPid);
        _windows.Setup(w => w.GetWindowText(It.IsAny<nint>())).Returns<nint>(Title);
        _windows.Setup(w => w.SetForegroundWindow(It.IsAny<nint>())).Returns<nint>(SetForegroundWindow);
        _windows.Setup(w => w.BringWindowToTop(It.IsAny<nint>())).Returns<nint>(h =>
        {
            _calls.Add($"BringWindowToTop({Name(h)})");
            return true;
        });
        _windows.Setup(w => w.ShowWindow(It.IsAny<nint>(), It.IsAny<int>())).Returns<nint, int>((h, command) =>
        {
            _calls.Add($"ShowWindow({Name(h)}, {command})");
            return true;
        });

        _threads.Setup(t => t.GetCurrentThreadId()).Returns(ActivationThread);
        _threads.Setup(t => t.GetWindowThreadId(Terminal)).Returns(TerminalThread);
        _threads.Setup(t => t.GetWindowThreadId(Target)).Returns(TargetThread);
        _threads.Setup(t => t.GetWindowThreadId(Dialog)).Returns(TargetThread);
        _threads.Setup(t => t.GetWindowThreadId(OtherDialog)).Returns(TargetThread);
        _threads.Setup(t => t.GetWindowThreadId(WpfDialog)).Returns(TargetThread);
        _threads.Setup(t => t.GetWindowThreadId(WpfNestedDialog)).Returns(TargetThread);
        _threads.Setup(t => t.AttachThreadInput(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>()))
            .Returns<uint, uint, bool>((from, to, attach) =>
            {
                _calls.Add($"AttachThreadInput({from}, {to}, {(attach ? "attach" : "detach")})");
                _attached = attach;
                return true;
            });

        _input.Setup(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns<IReadOnlyList<InputEvent>>(events =>
        {
            _sentInput.Add(events.ToList());
            _calls.Add("SendInput(" + string.Join(", ", events.Select(Describe)) + ")");
            _serverSentInput = true;
            return (uint)events.Count;
        });

        _security.SetupGet(s => s.ServerIntegrity).Returns(Integrity(1, ProcessIntegrity.MediumRid));
        _security.Setup(s => s.GetIntegrity(It.IsAny<int>())).Returns<int>(pid => Integrity(pid, ProcessIntegrity.MediumRid));
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(true);

        _windowService.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns<nint>(h => TestDataFactory.CreateWindowInfo(
            handleValue: (int)h, title: Title(h), processName: h == Terminal ? "WindowsTerminal" : "app",
            processId: h == Terminal ? TerminalPid : TargetPid));
    }

    /// <summary>When the simulated foreground lock lets SetForegroundWindow through.</summary>
    public enum ForegroundLock
    {
        /// <summary>Never: every step is refused.</summary>
        Always,

        /// <summary>Always: the server may set the foreground.</summary>
        Open,

        /// <summary>Only while the activation thread is attached to the foreground thread.</summary>
        UntilAttached,

        /// <summary>Only after the server has sent input.</summary>
        UntilInput
    }

    #region Already in front, refusals

    [Fact]
    public void EnsureForeground_WhenTheTargetIsAlreadyInFront_ChangesNothing()
    {
        // Arrange
        _foreground = Target;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.Already);
        outcome.ForegroundHandle.Should().Be(Target);
        outcome.BlockingPopup.Should().BeNull();
        outcome.ErrorCode.Should().Be(WindowErrorCode.None);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenADialogOfTheTargetIsInFront_CountsAsInFrontByItsRootOwnerAndReportsTheDialog()
    {
        // Arrange - the dialog's GA_ROOTOWNER is the target
        _foreground = Dialog;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.Already);
        outcome.ForegroundHandle.Should().Be(Dialog);
        outcome.BlockingPopup.Should().Be(Dialog);
        outcome.BlockingPopupTitle.Should().Be("Dialog");
        _windows.Verify(w => w.GetAncestor(Dialog, WindowAncestor.RootOwner), Times.AtLeastOnce);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenAWpfOrWinFormsDialogOfTheTargetIsInFront_CountsAsInFrontByTheOwnerChain()
    {
        // Arrange - not WS_POPUP, so GA_ROOTOWNER is the dialog itself; only GW_OWNER leads to the target
        _foreground = WpfDialog;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.Already);
        outcome.ErrorCode.Should().Be(WindowErrorCode.None);
        outcome.ForegroundHandle.Should().Be(WpfDialog);
        outcome.BlockingPopup.Should().Be(WpfDialog);
        outcome.BlockingPopupTitle.Should().Be("WpfDialog");
        _calls.Should().BeEmpty();
        _sentInput.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenADialogOfADialogOfTheTargetIsInFront_FollowsTheWholeOwnerChain()
    {
        // Arrange - for example a message box over a WPF settings dialog
        _foreground = WpfNestedDialog;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.Already);
        outcome.BlockingPopup.Should().Be(WpfNestedDialog);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenAWindowWithAnotherRootOwnerIsInFront_DoesNotCountIt()
    {
        // Arrange - the terminal is its own root owner and has no owner
        _foreground = Terminal;
        _lock = ForegroundLock.Always;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.Method.Should().BeNull();
        _windows.Verify(w => w.GetAncestor(Terminal, WindowAncestor.RootOwner), Times.AtLeastOnce);
    }

    [Theory]
    [InlineData("Dialog", "Target")]
    [InlineData("Dialog", "OtherDialog")]
    [InlineData("Dialog", "WpfDialog")]
    [InlineData("WpfDialog", "Target")]
    [InlineData("WpfDialog", "Dialog")]
    [InlineData("WpfNestedDialog", "WpfDialog")]
    [InlineData("WpfNestedDialog", "Target")]
    public void EnsureForeground_WhenTheTargetIsADialog_NeitherItsOwnersNorASiblingDialogCountsAsInFront(string target, string inFront)
    {
        // Arrange - keyboard input would go to the owner or the other dialog, not to the target dialog
        _foreground = Window(inFront);
        var sut = CreateSut();

        // Act
        var outcome = sut.EnsureForeground(Window(target), timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.Method.Should().BeNull();
        sut.IsInFront(Window(target)).Should().BeFalse();
    }

    [Fact]
    public void EnsureForeground_WithAnInvalidHandle_ReturnsInvalidHandleAndActsOnNothing()
    {
        // Act
        var outcome = CreateSut().EnsureForeground(0x999, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        outcome.ForegroundHandle.Should().Be(Terminal);
        _calls.Should().BeEmpty();
        _activationThread.Calls.Should().Be(0);
    }

    [Fact]
    public void EnsureForeground_WhenTheTargetIsNotResponding_RefusesWithNotResponding()
    {
        // Arrange
        _windows.Setup(w => w.IsHungAppWindow(Target)).Returns(true);
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.NotResponding);
        outcome.Error.Should().Contain("not responding");
        _calls.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenTheTargetIsCloaked_RefusesWithCloaked()
    {
        // Arrange - for example a window on another virtual desktop
        _windows.Setup(w => w.IsCloaked(Target)).Returns(true);
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.Cloaked);
        _calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureForeground_WhenTheTargetIsHidden_RefusesWithWindowHidden(bool alreadyInFront)
    {
        // Arrange - an app that hid its window in the notification area; SetForegroundWindow can still make
        // it the foreground window, and keys would then go to a window the user cannot see
        _windows.Setup(w => w.IsWindowVisible(Target)).Returns(false);
        _foreground = alreadyInFront ? Target : Terminal;
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.WindowHidden);
        outcome.Error.Should().Contain("hidden");
        _calls.Should().BeEmpty();
        _sentInput.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenTheTargetIsAChildOfAHiddenWindow_RefusesWithWindowHidden()
    {
        // Arrange - the window that would be activated is the target's top-level window
        nint child = 0x110;
        _windows.Setup(w => w.IsWindow(child)).Returns(true);
        _windows.Setup(w => w.GetAncestor(child, WindowAncestor.Root)).Returns(Target);
        _windows.Setup(w => w.IsWindowVisible(Target)).Returns(false);
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(child, timeoutMs: 0);

        // Assert
        outcome.ErrorCode.Should().Be(WindowErrorCode.WindowHidden);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenThePolicyDeniesTheTarget_RefusesWithTargetDeniedByPolicy_BeforeAnyStep()
    {
        // Arrange - a minimized password manager: restoring and activating it would put it on screen
        _windowService.Setup(w => w.EvaluateTargetPolicy(Target)).Returns(TargetPolicyVerdict.Deny("KeePass", "KeePass is on the deny list"));
        _windows.Setup(w => w.IsIconic(Target)).Returns(true);
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        outcome.Error.Should().Contain("KeePass is on the deny list");
        _calls.Should().BeEmpty();
        _activationThread.Calls.Should().Be(0);
    }

    [Fact]
    public void EnsureForeground_WhenThePolicyDeniesTheModalDialogItWouldActivate_RefusesWithTargetDeniedByPolicy()
    {
        // Arrange - the target is disabled by a credential prompt of another process
        _windows.Setup(w => w.GetWindowLongPtr(Target, GWL_STYLE)).Returns(new nint(WsDisabled));
        _windows.Setup(w => w.GetWindow(Target, GW_ENABLEDPOPUP)).Returns(Dialog);
        _windowService.Setup(w => w.EvaluateTargetPolicy(Dialog))
            .Returns(TargetPolicyVerdict.Deny("CredentialUIBroker", "CredentialUIBroker is on the deny list"));
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.TargetDeniedByPolicy);
        _calls.Should().NotContain(call => call.StartsWith("SetForegroundWindow"));
    }

    #endregion

    #region The steps: lock refusal, attach, unlock

    [Fact]
    public void EnsureForeground_WhenSetForegroundWindowIsAllowed_StopsThere()
    {
        // Arrange
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.SetForeground);
        outcome.ForegroundHandle.Should().Be(Target);
        outcome.ForegroundTitle.Should().Be("Target");
        outcome.ForegroundProcess.Should().Be("app");
        outcome.ForegroundProcessId.Should().Be(TargetPid);
        _calls.Should().Equal("SetForegroundWindow(Target) -> True");
    }

    [Fact]
    public void EnsureForeground_WhenTheTargetIsMinimized_RestoresItBeforeActivatingIt()
    {
        // Arrange
        _windows.Setup(w => w.IsIconic(Target)).Returns(true);
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.Method.Should().Be(FocusMethod.SetForeground);
        _calls.Should().Equal($"ShowWindow(Target, {SW_RESTORE})", "SetForegroundWindow(Target) -> True");
    }

    [Fact]
    public void EnsureForeground_WhenTheLockRefuses_AttachesTheActivationThreadToTheForegroundThreadAndDetaches()
    {
        // Arrange
        _lock = ForegroundLock.UntilAttached;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - the activation thread's own ID first: attaching two foreign threads would not help
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.AttachThreadInput);
        _calls.Should().Equal(
            "SetForegroundWindow(Target) -> False",
            $"AttachThreadInput({ActivationThread}, {TerminalThread}, attach)",
            "BringWindowToTop(Target)",
            "SetForegroundWindow(Target) -> True",
            $"AttachThreadInput({ActivationThread}, {TerminalThread}, detach)");
        _sentInput.Should().BeEmpty();
        _activationThread.RetireRequests.Should().Be(0, "the detach worked, so the thread stays");
    }

    [Fact]
    public void EnsureForeground_WhenWindowsRefusesTheDetach_RetiresTheActivationThread()
    {
        // Arrange - the attach works but the detach does not: the long-lived activation thread could stay
        // attached to the terminal's input queue for the rest of the session
        _threads.Setup(t => t.AttachThreadInput(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>()))
            .Returns<uint, uint, bool>((from, to, attach) =>
            {
                _calls.Add($"AttachThreadInput({from}, {to}, {(attach ? "attach" : "detach")}) -> {attach}");
                _attached = true;
                return attach;
            });
        _lock = ForegroundLock.UntilAttached;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - the activation itself still counts; the next one starts on a new thread
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.AttachThreadInput);
        _calls.Should().EndWith($"AttachThreadInput({ActivationThread}, {TerminalThread}, detach) -> False");
        _activationThread.RetireRequests.Should().Be(1);
    }

    [Fact]
    public void EnsureForeground_WhenTheAttachDoesNotHelp_UsesTheInputUnlockLast()
    {
        // Arrange
        _lock = ForegroundLock.UntilInput;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.InputUnlock);
        _calls.Should().Equal(
            "SetForegroundWindow(Target) -> False",
            $"AttachThreadInput({ActivationThread}, {TerminalThread}, attach)",
            "BringWindowToTop(Target)",
            "SetForegroundWindow(Target) -> False",
            $"AttachThreadInput({ActivationThread}, {TerminalThread}, detach)",
            "SendInput(mouse move)",
            "SetForegroundWindow(Target) -> True");
    }

    [Fact]
    public void EnsureForeground_TheInputUnlock_IsOnlyAZeroDistanceMouseMove_NeverAKey()
    {
        // Arrange - every step fails, so every step runs
        _lock = ForegroundLock.Always;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - InputEvent.Mouse carries no distance; MOUSEEVENTF_MOVE without ABSOLUTE moves by 0, 0
        outcome.IsForeground.Should().BeFalse();
        _sentInput.Should().ContainSingle().Which.Should().Equal(InputEvent.Mouse(MOUSEEVENTF_MOVE));
        AltKeys(_sentInput).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ForegroundLock.Always)]
    [InlineData(ForegroundLock.Open)]
    [InlineData(ForegroundLock.UntilAttached)]
    [InlineData(ForegroundLock.UntilInput)]
    public void EnsureForeground_NeverSendsAnAltKey(ForegroundLock foregroundLock)
    {
        // Arrange - and the target is in menu mode once it is in front, so Esc is sent too
        _lock = foregroundLock;
        var menuMode = true;
        _threads.Setup(t => t.GetGuiThreadInfo(TargetThread)).Returns(() => MenuState(menuMode));
        _input.Setup(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns<IReadOnlyList<InputEvent>>(events =>
        {
            _sentInput.Add(events.ToList());
            _serverSentInput = true;
            menuMode = false;
            return (uint)events.Count;
        });

        // Act
        CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        _sentInput.Should().NotBeEmpty();
        AltKeys(_sentInput).Should().BeEmpty("an Alt tap reaches the window in front, often the agent's terminal");
    }

    [Fact]
    public void EnsureForeground_WhenTheForegroundWindowIsNotResponding_SkipsTheAttach()
    {
        // Arrange - attaching to a hung thread can hang the activation thread too
        _windows.Setup(w => w.IsHungAppWindow(Terminal)).Returns(true);
        _lock = ForegroundLock.UntilInput;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.Method.Should().Be(FocusMethod.InputUnlock);
        _calls.Should().NotContain(c => c.StartsWith("AttachThreadInput", StringComparison.Ordinal));
        _calls.Should().Equal("SetForegroundWindow(Target) -> False", "SendInput(mouse move)", "SetForegroundWindow(Target) -> True");
    }

    [Theory]
    [InlineData(IntegrityLevel.High, ProcessIntegrity.HighRid)]
    [InlineData(IntegrityLevel.System, ProcessIntegrity.SystemRid)]
    [InlineData(IntegrityLevel.LikelyElevated, null)]
    public void EnsureForeground_WhenTheForegroundProcessHasAHigherIntegrityLevel_SkipsTheAttach(IntegrityLevel level, int? rid)
    {
        // Arrange - UIPI refuses the attach across integrity levels
        _security.Setup(s => s.GetIntegrity(TerminalPid)).Returns(new ProcessIntegrity(TerminalPid, level, rid, true, null));
        _lock = ForegroundLock.Always;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        _calls.Should().NotContain(c => c.StartsWith("AttachThreadInput", StringComparison.Ordinal));
        outcome.Error.Should().Contain("higher integrity level");
    }

    [Fact]
    public void EnsureForeground_WhenActivatingThrowsWhileAttached_StillDetaches()
    {
        // Arrange
        _lock = ForegroundLock.Always;
        _windows.Setup(w => w.BringWindowToTop(Target)).Throws(new InvalidOperationException("refused"));
        var sut = CreateSut();

        // Act
        var act = () => sut.EnsureForeground(Target, timeoutMs: 0);

        // Assert
        act.Should().Throw<InvalidOperationException>();
        _calls.Should().EndWith($"AttachThreadInput({ActivationThread}, {TerminalThread}, detach)");
        _attached.Should().BeFalse();
    }

    [Fact]
    public void EnsureForeground_WhenWindowsRefusesTheAttach_DoesNotDetachAndMovesOn()
    {
        // Arrange
        _threads.Setup(t => t.AttachThreadInput(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>()))
            .Returns<uint, uint, bool>((from, to, attach) =>
            {
                _calls.Add($"AttachThreadInput({from}, {to}, {(attach ? "attach" : "detach")}) -> False");
                return false;
            });
        _lock = ForegroundLock.UntilInput;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.Method.Should().Be(FocusMethod.InputUnlock);
        _calls.Should().Equal(
            "SetForegroundWindow(Target) -> False",
            $"AttachThreadInput({ActivationThread}, {TerminalThread}, attach) -> False",
            "SendInput(mouse move)",
            "SetForegroundWindow(Target) -> True");
    }

    [Fact]
    public void EnsureForeground_WhenEveryStepFails_ReturnsFocusFailedWithWhatIsInFrontAndWhy()
    {
        // Arrange
        _lock = ForegroundLock.Always;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.Method.Should().BeNull();
        outcome.ErrorCode.Should().Be(WindowErrorCode.FocusFailed);
        outcome.ForegroundHandle.Should().Be(Terminal);
        outcome.ForegroundTitle.Should().Be("Terminal");
        outcome.ForegroundProcess.Should().Be("WindowsTerminal");
        outcome.Error.Should().Contain("'Terminal' (WindowsTerminal) is in front")
            .And.Contain("refused")
            .And.Contain("input unlock");
        _attached.Should().BeFalse();
    }

    [Fact]
    public void EnsureForeground_WhenTheInputDesktopIsNotAvailable_SkipsTheUnlock()
    {
        // Arrange - for example the lock screen or a UAC prompt
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(false);
        _lock = ForegroundLock.UntilInput;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.ErrorCode.Should().Be(WindowErrorCode.FocusFailed);
        outcome.Error.Should().Contain("input desktop is not available");
        _sentInput.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WaitsForTheActivationToShow()
    {
        // Arrange - the target's thread takes a few reads to process the activation
        _lock = ForegroundLock.Open;
        var readsUntilActive = 3;
        _windows.Setup(w => w.SetForegroundWindow(Target)).Returns(() =>
        {
            _calls.Add("SetForegroundWindow(Target) -> True");
            return true;
        });
        _windows.Setup(w => w.GetForegroundWindow()).Returns(() =>
            _calls.Count > 0 && --readsUntilActive <= 0 ? Target : Terminal);
        var sut = CreateSut(stepSettle: TimeSpan.FromSeconds(5));

        // Act
        var outcome = sut.EnsureForeground(Target, timeoutMs: 5000);

        // Assert - no attach, no unlock
        outcome.Method.Should().Be(FocusMethod.SetForeground);
        _calls.Should().Equal("SetForegroundWindow(Target) -> True");
    }

    #endregion

    #region Menu mode, modal dialogs

    [Fact]
    public void EnsureForeground_WhenTheWindowIsInMenuModeAfterActivation_SendsEscWhileItIsInFront()
    {
        // Arrange - a menu bar left active (for example by an earlier Alt key)
        _lock = ForegroundLock.Open;
        var menuMode = true;
        _threads.Setup(t => t.GetGuiThreadInfo(TargetThread)).Returns(() => MenuState(menuMode));
        _input.Setup(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns<IReadOnlyList<InputEvent>>(events =>
        {
            _sentInput.Add(events.ToList());
            _calls.Add("SendInput(" + string.Join(", ", events.Select(Describe)) + ") in front: " + Name(_foreground));
            menuMode = false;
            return (uint)events.Count;
        });

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.MenuClosed.Should().BeTrue();
        outcome.InMenuMode.Should().BeFalse();
        _calls.Should().Equal("SetForegroundWindow(Target) -> True", "SendInput(Esc down, Esc up) in front: Target");
    }

    [Fact]
    public void EnsureForeground_WhenTheMenuIsStillActiveAfterTheEsc_SendsNoSecondEscAndReportsInMenuMode()
    {
        // Arrange - a submenu under a dropdown, or a thread that has not processed the Esc yet. A second
        // Esc could arrive after the menu has closed and cancel a dialog (IDCANCEL)
        _lock = ForegroundLock.Open;
        _threads.Setup(t => t.GetGuiThreadInfo(TargetThread)).Returns(MenuState(inMenuMode: true));

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - in front, but keys would pick menu items, and the outcome says so
        outcome.IsForeground.Should().BeTrue();
        outcome.MenuClosed.Should().BeFalse();
        outcome.InMenuMode.Should().BeTrue();
        _calls.Should().Equal("SetForegroundWindow(Target) -> True", "SendInput(Esc down, Esc up)");
    }

    [Fact]
    public void EnsureForeground_WhenAModifierIsDownWhileTheWindowIsInMenuMode_SendsNoEscAndReportsInMenuMode()
    {
        // Arrange - the user holds Ctrl (or starts the pause chord): the Esc would become Ctrl+Esc (Start),
        // Alt+Esc (the next window) or Ctrl+Shift+Esc (Task Manager)
        _lock = ForegroundLock.Open;
        _threads.Setup(t => t.GetGuiThreadInfo(TargetThread)).Returns(MenuState(inMenuMode: true));
        _input.Setup(i => i.IsKeyDown(VK_LCONTROL)).Returns(true);

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - in front, the menu left open and reported, no key sent
        outcome.IsForeground.Should().BeTrue();
        outcome.MenuClosed.Should().BeFalse();
        outcome.InMenuMode.Should().BeTrue();
        _sentInput.Should().BeEmpty();
        _calls.Should().Equal("SetForegroundWindow(Target) -> True");
    }

    [Fact]
    public void EnsureForeground_WhenTheWindowLosesTheForegroundBeforeTheEsc_SendsNoEsc()
    {
        // Arrange - the terminal comes back between the activation and the menu check
        _lock = ForegroundLock.Open;
        _threads.Setup(t => t.GetGuiThreadInfo(TargetThread)).Returns(() =>
        {
            _foreground = Terminal;
            return MenuState(inMenuMode: true);
        });

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - Esc would have reached the terminal
        _sentInput.Should().BeEmpty();
        outcome.MenuClosed.Should().BeFalse();
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.FocusFailed);
    }

    [Fact]
    public void EnsureForeground_WhenTheWindowWasAlreadyInFront_LeavesItsMenuAlone()
    {
        // Arrange - a menu the user opened; nothing was activated
        _foreground = Target;
        _threads.Setup(t => t.GetGuiThreadInfo(TargetThread)).Returns(MenuState(inMenuMode: true));

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - left open, but reported: typed keys would pick menu items
        outcome.Method.Should().Be(FocusMethod.Already);
        outcome.MenuClosed.Should().BeFalse();
        outcome.InMenuMode.Should().BeTrue();
        _sentInput.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenAModalDialogDisablesTheTarget_ActivatesTheDialogAndReportsIt()
    {
        // Arrange
        _windows.Setup(w => w.GetWindowLongPtr(Target, GWL_STYLE)).Returns(new nint(WsDisabled));
        _windows.Setup(w => w.GetWindow(Target, GW_ENABLEDPOPUP)).Returns(Dialog);
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeTrue();
        outcome.ForegroundHandle.Should().Be(Dialog);
        outcome.BlockingPopup.Should().Be(Dialog);
        outcome.BlockingPopupTitle.Should().Be("Dialog");
        _calls.Should().Equal("SetForegroundWindow(Dialog) -> True");
    }

    [Fact]
    public void EnsureForeground_WhenAWpfModalDialogDisablesTheTarget_AcceptsTheDialogAtTheFirstStep()
    {
        // Arrange - ShowDialog in WPF or WinForms: the owner is disabled and the dialog is not WS_POPUP,
        // so its GA_ROOTOWNER is itself
        _windows.Setup(w => w.GetWindowLongPtr(Target, GWL_STYLE)).Returns(new nint(WsDisabled));
        _windows.Setup(w => w.GetWindow(Target, GW_ENABLEDPOPUP)).Returns(WpfDialog);
        _lock = ForegroundLock.Open;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert - no attach to another thread and no unlock input after the dialog is already in front
        outcome.IsForeground.Should().BeTrue();
        outcome.Method.Should().Be(FocusMethod.SetForeground);
        outcome.ForegroundHandle.Should().Be(WpfDialog);
        outcome.BlockingPopup.Should().Be(WpfDialog);
        outcome.BlockingPopupTitle.Should().Be("WpfDialog");
        _calls.Should().Equal("SetForegroundWindow(WpfDialog) -> True");
        _sentInput.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenFocusFailsOnATargetAModalDialogDisables_ReportsTheDialog()
    {
        // Arrange
        _windows.Setup(w => w.GetWindowLongPtr(Target, GWL_STYLE)).Returns(new nint(WsDisabled));
        _windows.Setup(w => w.GetWindow(Target, GW_ENABLEDPOPUP)).Returns(Dialog);
        _lock = ForegroundLock.Always;

        // Act
        var outcome = CreateSut().EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.BlockingPopup.Should().Be(Dialog);
    }

    #endregion

    #region The activation thread

    [Fact]
    public void EnsureForeground_RunsTheStepsOnTheActivationThread_WithTheTimeoutPlusAGrace()
    {
        // Arrange
        _lock = ForegroundLock.Open;
        var sut = CreateSut();

        // Act
        sut.EnsureForeground(Target, timeoutMs: 700);
        var huge = sut.EnsureForeground(Target, timeoutMs: int.MaxValue);

        // Assert
        _activationThread.Calls.Should().Be(2);
        _activationThread.LastTimeout.Should().Be(TimeSpan.FromMilliseconds(ForegroundService.MaxTimeoutMs) + Grace);
        huge.IsForeground.Should().BeTrue();
    }

    [Fact]
    public void EnsureForeground_WhenTheActivationThreadDoesNotFinishInTime_ReturnsNotResponding()
    {
        // Arrange - a window call blocked on the activation thread
        var sut = new ForegroundService(_windows.Object, _threads.Object, _input.Object, _security.Object,
            _windowService.Object, new TimingOutThread(), Timing(TimeSpan.Zero));

        // Act
        var outcome = sut.EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.NotResponding);
        outcome.Error.Should().Contain("did not answer");
        outcome.ForegroundHandle.Should().Be(Terminal);
    }

    [Fact]
    public void EnsureForeground_WhenAnotherActivationStillHoldsTheThread_ReportsFocusFailedNotNotResponding()
    {
        // Arrange - no window was slow; another focus call was still running
        var sut = new ForegroundService(_windows.Object, _threads.Object, _input.Object, _security.Object,
            _windowService.Object, new BusyThread(), Timing(TimeSpan.Zero));

        // Act
        var outcome = sut.EnsureForeground(Target, timeoutMs: 0);

        // Assert
        outcome.IsForeground.Should().BeFalse();
        outcome.ErrorCode.Should().Be(WindowErrorCode.FocusFailed);
        outcome.Error.Should().Contain("Another window activation was still running").And.NotContain("not responding");
        outcome.ForegroundHandle.Should().Be(Terminal);
        _calls.Should().BeEmpty();
    }

    [Fact]
    public void EnsureForeground_WhenRestoringBlocksPastTheDeadline_TakesNoFurtherStep()
    {
        // Arrange - ShowWindow on a slow window returns only after the caller stopped waiting
        _lock = ForegroundLock.Open;
        _windows.Setup(w => w.IsIconic(Target)).Returns(true);
        var thread = new CancellingThread();
        _windows.Setup(w => w.ShowWindow(Target, SW_RESTORE)).Returns(() =>
        {
            _calls.Add("ShowWindow(Target, 9)");
            thread.Cancel();
            return true;
        });
        var sut = new ForegroundService(_windows.Object, _threads.Object, _input.Object, _security.Object,
            _windowService.Object, thread, Timing(TimeSpan.Zero));

        // Act
        sut.EnsureForeground(Target, timeoutMs: 0);

        // Assert - once the caller has given up, no further step: no SetForegroundWindow, attach or input
        _calls.Should().Equal("ShowWindow(Target, 9)");
        _sentInput.Should().BeEmpty();
    }

    #endregion

    #region IsInFront (the cheap re-check)

    [Fact]
    public void IsInFront_UsesTheSameRuleAsEnsureForeground_AndOnlyReads()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        _foreground = Target;
        var targetInFront = sut.IsInFront(Target);
        _foreground = Dialog;
        var dialogOfTargetInFront = sut.IsInFront(Target);
        var siblingDialogInFront = sut.IsInFront(OtherDialog);
        _foreground = WpfNestedDialog;
        var wpfDialogOfTargetInFront = sut.IsInFront(Target);
        var wpfDialogOfWpfDialogInFront = sut.IsInFront(WpfDialog);
        _foreground = Terminal;
        var terminalInFront = sut.IsInFront(Target);

        // Assert
        targetInFront.Should().BeTrue();
        dialogOfTargetInFront.Should().BeTrue();
        siblingDialogInFront.Should().BeFalse("a sibling dialog takes the keys, not the target dialog");
        wpfDialogOfTargetInFront.Should().BeTrue("WPF and WinForms dialogs are owned windows too, found by GW_OWNER");
        wpfDialogOfWpfDialogInFront.Should().BeTrue();
        terminalInFront.Should().BeFalse();
        _calls.Should().BeEmpty();
        _activationThread.Calls.Should().Be(0, "a read needs no hop to the activation thread");
    }

    [Fact]
    public void IsInFront_CountsExactlyTheWindowsOfTheSharedOwnerChain()
    {
        // Arrange - the close guard, the host guard and the input guard's mouse rule decide with
        // WindowOwnerChain; activation and the input guard's keyboard re-check must agree with them
        var sut = CreateSut();
        var windows = new[] { Target, Terminal, Dialog, OtherDialog, WpfDialog, WpfNestedDialog };

        foreach (var foreground in windows)
        {
            _foreground = foreground;
            foreach (var target in windows)
            {
                // Act
                var inFront = sut.IsInFront(target);

                // Assert
                inFront.Should().Be(WindowOwnerChain.IsOwnedBy(_windows.Object, foreground, target),
                    $"{Name(foreground)} is in front and the target is {Name(target)}");
            }
        }
    }

    [Fact]
    public void IsInFront_ForAWindowThatIsGone_IsFalse()
    {
        _foreground = Target;

        CreateSut().IsInFront(0x999).Should().BeFalse();
    }

    #endregion

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    private ForegroundService CreateSut(TimeSpan? stepSettle = null) =>
        new(_windows.Object, _threads.Object, _input.Object, _security.Object, _windowService.Object, _activationThread,
            Timing(stepSettle ?? TimeSpan.Zero));

    private static ForegroundTiming Timing(TimeSpan stepSettle) =>
        new(StepSettle: stepSettle, PollInterval: TimeSpan.FromMilliseconds(1), MenuSettle: TimeSpan.Zero, ThreadGrace: Grace);

    /// <summary>The simulated foreground lock and SetForegroundWindow.</summary>
    private bool SetForegroundWindow(nint window)
    {
        var allowed = _lock switch
        {
            ForegroundLock.Open => true,
            ForegroundLock.UntilAttached => _attached,
            ForegroundLock.UntilInput => _serverSentInput,
            _ => false
        };
        if (allowed)
        {
            _foreground = window;
        }

        _calls.Add($"SetForegroundWindow({Name(window)}) -> {allowed}");
        return allowed;
    }

    private static GuiThreadInfo MenuState(bool inMenuMode) =>
        new(inMenuMode ? GuiThreadInfo.InMenuModeFlag : 0, Target, Target, 0, inMenuMode ? Target : 0);

    private static ProcessIntegrity Integrity(int pid, int rid) =>
        new(pid, ProcessIntegrity.LevelFromRid(rid), rid, false, null);

    private static IEnumerable<InputEvent> AltKeys(IEnumerable<IReadOnlyList<InputEvent>> batches) =>
        batches.SelectMany(batch => batch).Where(e =>
            e.Kind == InputEventKind.VirtualKey &&
            e.Code is VK_MENU or VK_LMENU or VK_RMENU);

    private static string Describe(InputEvent e) => e switch
    {
        { Kind: InputEventKind.Mouse, Flags: MOUSEEVENTF_MOVE } => "mouse move",
        { Kind: InputEventKind.VirtualKey, Code: VK_ESCAPE } => (e.Flags & KEYEVENTF_KEYUP) != 0 ? "Esc up" : "Esc down",
        _ => e.ToString()
    };

    private static string Name(nint window) =>
        window == Target ? "Target" : window == Terminal ? "Terminal" : window == Dialog ? "Dialog" :
        window == OtherDialog ? "OtherDialog" : window == WpfDialog ? "WpfDialog" :
        window == WpfNestedDialog ? "WpfNestedDialog" : $"0x{window:X}";

    private static string Title(nint window) => Name(window);

    private static nint Window(string name) =>
        new[] { Target, Terminal, Dialog, OtherDialog, WpfDialog, WpfNestedDialog }.Single(w => Name(w) == name);

    /// <summary>
    /// Runs the work inline with a token the test cancels, the way <see cref="ForegroundThread"/> cancels
    /// it when the caller stops waiting.
    /// </summary>
    private sealed class CancellingThread : IForegroundThread
    {
        private readonly CancellationTokenSource _cancellation = new();

        public void Cancel() => _cancellation.Cancel();

        public T Run<T>(Func<CancellationToken, T> work, TimeSpan timeout) => work(_cancellation.Token);

        public void RetireCurrentThread()
        {
        }
    }

    /// <summary>An activation thread whose work never finishes in time.</summary>
    private sealed class TimingOutThread : IForegroundThread
    {
        public T Run<T>(Func<CancellationToken, T> work, TimeSpan timeout) =>
            throw new TimeoutException("Window activation did not finish in time: a window did not answer.");

        public void RetireCurrentThread()
        {
        }
    }

    /// <summary>An activation thread that an earlier call still holds, so the work never starts.</summary>
    private sealed class BusyThread : IForegroundThread
    {
        public T Run<T>(Func<CancellationToken, T> work, TimeSpan timeout) =>
            throw new ForegroundThreadBusyException(
                "Another window activation was still running after 3000 ms, so this one did not start. Try again.");

        public void RetireCurrentThread()
        {
        }
    }
}
