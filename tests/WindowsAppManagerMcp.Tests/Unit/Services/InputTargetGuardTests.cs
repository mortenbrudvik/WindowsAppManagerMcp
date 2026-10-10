namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for the input guard (roadmap M1.1, M1.5, M1.12): every rule and error code, and the order in
/// which the rules run. Every window, process, probe and the foreground service are mocks, so nothing is
/// read from or done to the real desktop; the only "action" the guard can take, EnsureForeground, is a
/// mock call.
/// </summary>
public class InputTargetGuardTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const uint GwOwner = 4;
    private const uint GwEnabledPopup = 6;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsOverlappedWindow = 0x00CF0000; // includes WS_CAPTION
    private const long WsPopup = 0x80000000;
    private const long WsVisible = 0x10000000;
    private const long WsDisabled = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    private static readonly nint Terminal = 0x100;       // the agent's host
    private static readonly nint Notepad = 0x200;
    private static readonly nint NotepadEdit = 0x201;    // a child control of Notepad
    private static readonly nint NotepadDialog = 0x210;  // owned by Notepad without WS_POPUP (GW_OWNER only)
    private static readonly nint NotepadMenu = 0x220;    // an unowned popup of Notepad's process
    private static readonly nint SecondNotepad = 0x230;  // another main window of Notepad's process
    private static readonly nint KeePass = 0x300;        // on the default deny list
    private static readonly nint TaskManager = 0x400;    // elevated
    private static readonly nint Outlook = 0x600;
    private static readonly nint WebViewPopup = 0x610;   // unowned popup of msedgewebview2, a child process of Outlook
    private static readonly nint Calculator = 0x700;     // unrelated
    private static readonly nint StrangerPopup = 0x710;  // unowned popup of an unrelated process
    private static readonly nint CredentialPrompt = 0x800; // CredentialUIBroker dialog owned by Notepad

    private const int TerminalPid = 1001;
    private const int NotepadPid = 2001;
    private const int KeePassPid = 3001;
    private const int TaskManagerPid = 4001;
    private const int OutlookPid = 6001;
    private const int WebViewPid = 6101;
    private const int CalculatorPid = 7001;
    private const int StrangerPid = 7101;
    private const int CredentialPid = 8001;

    private static readonly ProcessIntegrity Medium = new(0, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null);
    private static readonly ProcessIntegrity High = new(0, IntegrityLevel.High, ProcessIntegrity.HighRid, true, null);

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeHitTestWrapper> _hitTest = new();
    private readonly Mock<INativeThreadInputWrapper> _threads = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Mock<IWindowService> _windowService = new();
    private readonly Mock<IForegroundService> _foreground = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();
    private readonly Mock<IHostGuard> _hostGuard = new();
    private readonly List<nint> _topLevelWindows = [];
    private TargetPolicySettings _policy = TargetPolicySettings.Default;
    private nint _inFront;

    public InputTargetGuardTests()
    {
        Window(Terminal, TerminalPid, "WindowsTerminal", "pwsh");
        Window(Notepad, NotepadPid, "notepad", "Untitled - Notepad");
        _windows.Setup(w => w.IsWindow(NotepadEdit)).Returns(true);
        _windows.Setup(w => w.GetAncestor(NotepadEdit, WindowAncestor.Root)).Returns(Notepad);
        Window(NotepadDialog, NotepadPid, "notepad", "Save As", owner: Notepad);
        Window(NotepadMenu, NotepadPid, "notepad", "", style: WsPopup | WsVisible, exStyle: WsExToolWindow);
        Window(SecondNotepad, NotepadPid, "notepad", "Other - Notepad");
        Window(KeePass, KeePassPid, "KeePass", "Database - KeePass");
        Window(TaskManager, TaskManagerPid, "Taskmgr", "Task Manager");
        Window(Outlook, OutlookPid, "olk", "Inbox - Outlook");
        Window(WebViewPopup, WebViewPid, "msedgewebview2", "", style: WsPopup | WsVisible, exStyle: WsExToolWindow);
        Window(Calculator, CalculatorPid, "CalculatorApp", "Calculator");
        Window(StrangerPopup, StrangerPid, "stranger", "", style: WsPopup | WsVisible);
        Window(CredentialPrompt, CredentialPid, "CredentialUIBroker", "Windows Security", owner: Notepad);

        _processes.Setup(p => p.GetAncestorsOf(WebViewPid)).Returns([OutlookPid, 1, 4]);
        _processes.Setup(p => p.GetAncestorsOf(It.Is<int>(pid => pid != WebViewPid))).Returns([]);

        _hostGuard.Setup(g => g.IsHostWindow(Terminal)).Returns(true);

        _security.Setup(s => s.ServerIntegrity).Returns(Medium);
        _security.Setup(s => s.GetIntegrity(It.IsAny<int>())).Returns(Medium);
        _security.Setup(s => s.GetIntegrity(TaskManagerPid)).Returns(High);
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(true);

        _windows.Setup(w => w.GetForegroundWindow()).Returns(() => _inFront);
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>()))
            .Callback((Func<nint, bool> callback) =>
            {
                foreach (var hWnd in _topLevelWindows)
                {
                    if (!callback(hWnd))
                    {
                        break;
                    }
                }
            });

        // EnsureForeground brings the target to the front; IsInFront follows the owner chain
        _foreground.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) =>
            {
                _inFront = handle == NotepadEdit ? Notepad : handle;
                return new FocusOutcome { Target = handle, IsForeground = true, Method = FocusMethod.SetForeground, ForegroundHandle = _inFront };
            });
        _foreground.Setup(f => f.IsInFront(It.IsAny<nint>()))
            .Returns((nint handle) => _inFront != 0 && WindowOwnerChain.IsOwnedBy(_windows.Object, _inFront, RootOf(handle)));
    }

    private nint RootOf(nint handle) => handle == NotepadEdit ? Notepad : handle;

    private void Window(nint handle, int processId, string processName, string title, nint owner = 0,
        long style = WsOverlappedWindow | WsVisible, long exStyle = 0)
    {
        _topLevelWindows.Add(handle);
        _windows.Setup(w => w.IsWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetAncestor(handle, WindowAncestor.Root)).Returns(handle);
        _windows.Setup(w => w.GetAncestor(handle, WindowAncestor.RootOwner)).Returns(handle);
        _windows.Setup(w => w.GetWindow(handle, GwOwner)).Returns(owner);
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns((uint)processId);
        _windows.Setup(w => w.GetWindowText(handle)).Returns(title);
        _windows.Setup(w => w.GetProcessName((uint)processId)).Returns(processName);
        Style(handle, style, exStyle);
        _windowService.Setup(s => s.GetWindowInfo(handle)).Returns(
            TestDataFactory.CreateWindowInfo((int)handle, title, processName, processId));
        _threads.Setup(t => t.GetWindowThreadId(handle)).Returns((uint)processId + 1);
    }

    private void Style(nint handle, long style, long exStyle = 0)
    {
        _windows.Setup(w => w.GetWindowLongPtr(handle, GwlStyle)).Returns(new nint(style));
        _windows.Setup(w => w.GetWindowLongPtr(handle, GwlExStyle)).Returns(new nint(exStyle));
    }

    /// <summary>Puts <paramref name="hit"/> at (500, 300); <paramref name="root"/> is its top-level window.</summary>
    private void At(nint hit, nint? root = null)
    {
        _hitTest.Setup(h => h.WindowFromPoint(500, 300)).Returns(hit);
        _hitTest.Setup(h => h.GetRootWindow(hit)).Returns(root ?? hit);
    }

    private void InMenuMode(nint window)
    {
        var threadId = _threads.Object.GetWindowThreadId(window);
        _threads.Setup(t => t.GetGuiThreadInfo(threadId)).Returns(new GuiThreadInfo(GuiThreadInfo.InMenuModeFlag, window, window, 0, window));
    }

    private InputTargetGuard CreateSut(InputGuardOptions? options = null) => new(
        _windows.Object, _hitTest.Object, _threads.Object, _processes.Object, _windowService.Object, _foreground.Object,
        _security.Object, _hostGuard.Object, new TargetPolicy(_policy), options ?? InputGuardOptions.Default);

    private static InputTarget To(nint handle, bool allowHostTarget = false) => InputTarget.Window(handle, allowHostTarget);

    private void VerifyNoActivation() =>
        _foreground.Verify(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);

    #region Keyboard without a target: the host guard (M1.1)

    [Fact]
    public void Keyboard_NoTarget_TerminalInFront_IsRefusedAsTheAgentHost()
    {
        // Arrange - the situation that closed the user's terminal: nothing named, the terminal has focus
        _inFront = Terminal;

        // Act
        var result = CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);

        // Assert
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
        result.ReceivingWindow.Should().Be(new WindowSummary(Terminal, "pwsh", "WindowsTerminal", TerminalPid));
        result.Error.Should().Contain("agent's own host").And.Contain("windowHandle").And.Contain("allowHostTarget=true");
        VerifyNoActivation();
    }

    [Fact]
    public void Keyboard_NoTarget_DialogOfTheHostInFront_IsRefused()
    {
        // The host guard answers for the window in front; it follows the owner chain on its own
        var hostDialog = new nint(0x110);
        Window(hostDialog, TerminalPid, "WindowsTerminal", "Settings", owner: Terminal);
        _hostGuard.Setup(g => g.IsHostWindow(hostDialog)).Returns(true);
        _inFront = hostDialog;

        CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Keys, SendKeysParser.Parse("{ENTER}"))
            .ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
    }

    [Fact]
    public void Keyboard_NoTarget_AllowHostTargetWithoutWindowHandle_IsStillRefused()
    {
        // Arrange
        _inFront = Terminal;

        // Act
        var result = CreateSut().CheckKeyboard(new InputTarget(null, AllowHostTarget: true), KeyboardInputKind.Text, null);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
        result.Error.Should().Contain("no effect without windowHandle");
    }

    [Fact]
    public void Keyboard_NoTarget_OrdinaryWindowInFront_IsAllowedAndAnchoredToIt()
    {
        // Arrange
        _inFront = Notepad;

        // Act
        var result = CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);

        // Assert
        result.Allowed.Should().BeTrue();
        result.Receiving.Should().Be(Notepad);
        result.Anchor.Should().Be(Notepad);
        result.ReceivingWindow!.ProcessName.Should().Be("notepad");
        VerifyNoActivation();
    }

    [Fact]
    public void Keyboard_NoTarget_NothingInFront_IsTargetNotForeground()
    {
        _inFront = nint.Zero;

        var result = CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);

        result.ErrorCode.Should().Be(InputErrorCode.TargetNotForeground);
        result.Error.Should().Contain("No window is in the foreground");
    }

    #endregion

    #region Destructive chords

    [Theory]
    [InlineData("%{F4}")]
    [InlineData("^w")]
    [InlineData("^+w")]
    [InlineData("^q")]
    [InlineData("^+{ESC}")]
    public void Keyboard_DestructiveChordWithoutTarget_IsRefusedEvenWhenAnOrdinaryWindowIsInFront(string keys)
    {
        // Arrange
        _inFront = Notepad;

        // Act
        var result = CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Keys, SendKeysParser.Parse(keys));

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.DestructiveChordNeedsTarget);
        result.Error.Should().Contain("windowHandle").And.Contain("close_window");
        result.ReceivingWindow!.Handle.Should().Be(Notepad, "the result says where the keys would have gone");
    }

    [Fact]
    public void Keyboard_DestructiveChordWithTarget_IsAllowed()
    {
        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Keys, SendKeysParser.Parse("^w"));

        result.Allowed.Should().BeTrue(result.Error);
        result.Receiving.Should().Be(Notepad);
    }

    [Fact]
    public void Keyboard_ConfiguredChordList_IsUsed()
    {
        // Arrange - WAM_DESTRUCTIVE_CHORDS="^{F4}"
        _inFront = Notepad;
        var options = InputGuardOptions.FromEnvironment(name => name == InputGuardOptions.DestructiveChordsVariable ? "^{F4}" : null);
        var sut = CreateSut(options);

        // Act & Assert
        sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Keys, SendKeysParser.Parse("^{F4}"))
            .ErrorCode.Should().Be(InputErrorCode.DestructiveChordNeedsTarget);
        sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Keys, SendKeysParser.Parse("^w")).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Keyboard_OrdinaryKeysWithoutTarget_AreAllowed()
    {
        _inFront = Notepad;

        CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Keys, SendKeysParser.Parse("^c{TAB}")).Allowed.Should().BeTrue();
    }

    #endregion

    #region Probes

    [Fact]
    public void Keyboard_SessionLocked_IsRefusedFirst()
    {
        // Arrange - the input desktop is gone too; the more specific reason wins
        _security.Setup(s => s.IsSessionLocked()).Returns(true);
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(false);

        // Act
        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.SessionLocked);
        VerifyNoActivation();
    }

    [Fact]
    public void Mouse_InputDesktopUnavailable_IsDesktopUnavailable()
    {
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(false);
        At(Notepad);

        var result = CreateSut().CheckMouse(500, 300, InputTarget.None);

        result.ErrorCode.Should().Be(InputErrorCode.DesktopUnavailable);
        result.Error.Should().Contain("UAC prompt");
    }

    [Fact]
    public void Keyboard_ElevatedWindowInFront_IsTargetElevated()
    {
        _inFront = TaskManager;

        var result = CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);

        result.ErrorCode.Should().Be(InputErrorCode.TargetElevated);
        result.Error.Should().Contain("High").And.Contain("administrator");
        result.ReceivingWindow!.ProcessName.Should().Be("Taskmgr");
    }

    [Fact]
    public void Keyboard_ElevatedTarget_IsRefusedBeforeItIsActivated()
    {
        CreateSut().CheckKeyboard(To(TaskManager), KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetElevated);
        VerifyNoActivation();
    }

    [Theory]
    [InlineData(IntegrityLevel.Restricted)]
    [InlineData(IntegrityLevel.Unknown)]
    public void Keyboard_IntegrityThatCannotBeCompared_IsNotRefused(IntegrityLevel level)
    {
        // Restricted means "access denied, no sign of elevation": at most a possible cause, never a refusal
        _security.Setup(s => s.GetIntegrity(NotepadPid)).Returns(new ProcessIntegrity(NotepadPid, level, null, null, "denied"));
        _inFront = Notepad;

        CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null).Allowed.Should().BeTrue();
    }

    #endregion

    #region Policy (M1.12) and the order of the rules

    [Fact]
    public void Keyboard_DeniedProcessInFront_IsTargetDeniedByPolicy()
    {
        // Arrange
        _inFront = KeePass;

        // Act
        var result = CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("KeePass").And.Contain(TargetPolicySettings.DenyVariable).And.Contain("ask the user");
    }

    [Fact]
    public void Keyboard_DeniedTarget_IsRefusedBeforeItIsActivated()
    {
        CreateSut().CheckKeyboard(To(KeePass), KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
        VerifyNoActivation();
    }

    [Fact]
    public void Keyboard_TargetsOwnDialogFromADeniedProcess_IsRefused()
    {
        // Arrange - Notepad opened a Windows Security prompt (CredentialUIBroker) that it owns
        _foreground.Setup(f => f.EnsureForeground(Notepad, It.IsAny<int>())).Returns(() =>
        {
            _inFront = CredentialPrompt;
            return new FocusOutcome { Target = Notepad, IsForeground = true, Method = FocusMethod.Already, ForegroundHandle = CredentialPrompt };
        });

        // Act
        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        // Assert - the keys would go to the prompt, which the policy denies
        result.ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
        result.ReceivingWindow!.Handle.Should().Be(CredentialPrompt);
    }

    [Fact]
    public void Order_ElevationBeforePolicyBeforeHost()
    {
        // Arrange - one window that is elevated, denied and the host at once
        _security.Setup(s => s.GetIntegrity(KeePassPid)).Returns(High);
        _hostGuard.Setup(g => g.IsHostWindow(KeePass)).Returns(true);
        _inFront = KeePass;
        var sut = CreateSut();

        // Act & Assert - each rule reports only once the earlier ones pass
        sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetElevated);
        _security.Setup(s => s.GetIntegrity(KeePassPid)).Returns(Medium);
        sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
        _policy = TargetPolicySettings.FromEnvironment(name => name == TargetPolicySettings.DenyVariable ? "none" : null);
        CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
    }

    [Fact]
    public void Order_PolicyBeforeTheHostRule_EvenWithAllowHostTarget()
    {
        // The model cannot override the policy: allowHostTarget lifts only the host rule
        _hostGuard.Setup(g => g.IsHostWindow(KeePass)).Returns(true);

        CreateSut().CheckKeyboard(To(KeePass, allowHostTarget: true), KeyboardInputKind.Text, null)
            .ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
    }

    [Fact]
    public void Policy_AllowList_RefusesEveryOtherWindow()
    {
        // Arrange - WAM_ALLOW_PROCESSES=notepad
        _policy = TargetPolicySettings.FromEnvironment(name => name == TargetPolicySettings.AllowVariable ? "notepad" : null);
        var sut = CreateSut();

        // Act & Assert
        _inFront = Notepad;
        sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null).Allowed.Should().BeTrue();
        _inFront = Calculator;
        var refused = sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);
        refused.ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
        refused.Error.Should().Contain(TargetPolicySettings.AllowVariable);
    }

    [Fact]
    public void Policy_DenyListWinsOverTheAllowList()
    {
        _policy = TargetPolicySettings.FromEnvironment(name => name == TargetPolicySettings.AllowVariable ? "KeePass, notepad" : null);
        _inFront = KeePass;

        CreateSut().CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
    }

    #endregion

    #region Keyboard with a target (M1.5)

    [Fact]
    public void Keyboard_Target_IsBroughtToTheFrontAndReceivesTheKeys()
    {
        // Arrange - the terminal is in front when the call starts
        _inFront = Terminal;

        // Act
        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        // Assert
        result.Allowed.Should().BeTrue(result.Error);
        result.Receiving.Should().Be(Notepad);
        result.Anchor.Should().Be(Notepad);
        _foreground.Verify(f => f.EnsureForeground(Notepad, It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void Keyboard_ChildControlAsTarget_StandsForItsTopLevelWindow()
    {
        var result = CreateSut().CheckKeyboard(To(NotepadEdit), KeyboardInputKind.Text, null);

        result.Allowed.Should().BeTrue(result.Error);
        result.Receiving.Should().Be(Notepad);
    }

    [Fact]
    public void Keyboard_Target_CannotBeBroughtToTheFront_IsTargetNotForeground()
    {
        // Arrange
        _inFront = Terminal;
        _foreground.Setup(f => f.EnsureForeground(Notepad, It.IsAny<int>())).Returns(new FocusOutcome
        {
            Target = Notepad,
            IsForeground = false,
            ForegroundHandle = Terminal,
            ErrorCode = WindowErrorCode.FocusFailed,
            Error = "SetForegroundWindow was refused (foreground lock)."
        });

        // Act
        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        // Assert - the window that is in front instead is named, and nothing more is tried
        result.ErrorCode.Should().Be(InputErrorCode.TargetNotForeground);
        result.ReceivingWindow!.Handle.Should().Be(Terminal);
        result.Error.Should().Contain("foreground lock").And.Contain("FocusFailed");
    }

    [Fact]
    public void Keyboard_Target_InFrontButLostAgain_IsTargetNotForeground()
    {
        // EnsureForeground reports success, but another window took the front before the keys
        _foreground.Setup(f => f.EnsureForeground(Notepad, It.IsAny<int>())).Returns(() =>
        {
            _inFront = Calculator;
            return new FocusOutcome { Target = Notepad, IsForeground = true, Method = FocusMethod.SetForeground, ForegroundHandle = Notepad };
        });

        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        result.ErrorCode.Should().Be(InputErrorCode.TargetNotForeground);
        result.ReceivingWindow!.Handle.Should().Be(Calculator);
    }

    [Fact]
    public void Keyboard_Target_ItsOwnedDialogInFront_ReceivesTheKeys()
    {
        // A WPF dialog (GW_OWNER only) of the target counts as the target, as in IForegroundService
        _foreground.Setup(f => f.EnsureForeground(Notepad, It.IsAny<int>())).Returns(() =>
        {
            _inFront = NotepadDialog;
            return new FocusOutcome { Target = Notepad, IsForeground = true, Method = FocusMethod.Already, ForegroundHandle = NotepadDialog };
        });

        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        result.Allowed.Should().BeTrue(result.Error);
        result.Receiving.Should().Be(NotepadDialog);
        result.Anchor.Should().Be(Notepad);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(0x9999L)]
    public void Keyboard_InvalidTarget_IsInvalidHandle(long handle)
    {
        var result = CreateSut().CheckKeyboard(To(new nint(handle)), KeyboardInputKind.Text, null);

        result.ErrorCode.Should().Be(InputErrorCode.InvalidHandle);
        result.Error.Should().Contain("find_windows");
        VerifyNoActivation();
    }

    [Fact]
    public void Keyboard_TargetDisabledByAModalDialog_IsBlockedByModal_WithTheDialog()
    {
        // Arrange - the dialog is GW_ENABLEDPOPUP of the target
        Style(Notepad, WsOverlappedWindow | WsVisible | WsDisabled);
        _windows.Setup(w => w.GetWindow(Notepad, GwEnabledPopup)).Returns(NotepadDialog);

        // Act
        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.BlockedByModal);
        result.BlockingWindow.Should().Be(new WindowSummary(NotepadDialog, "Save As", "notepad", NotepadPid));
        result.ReceivingWindow!.Handle.Should().Be(Notepad);
        result.Error.Should().Contain("Save As").And.Contain("windowHandle");
        VerifyNoActivation();
    }

    [Fact]
    public void Keyboard_TargetDisabled_WithoutAnEnabledPopup_FindsTheOwnedDialogInZOrder()
    {
        // Arrange - GetWindow(GW_ENABLEDPOPUP) returns the window itself; the WPF dialog is found by its owner
        Style(Notepad, WsOverlappedWindow | WsVisible | WsDisabled);
        _windows.Setup(w => w.GetWindow(Notepad, GwEnabledPopup)).Returns(Notepad);
        Style(CredentialPrompt, WsOverlappedWindow | WsVisible | WsDisabled); // disabled itself: not the blocker

        // Act
        var result = CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.BlockedByModal);
        result.BlockingWindow!.Handle.Should().Be(NotepadDialog);
    }

    [Fact]
    public void Keyboard_TargetIsTheHost_WithoutAllowHostTarget_IsRefused()
    {
        var result = CreateSut().CheckKeyboard(To(Terminal), KeyboardInputKind.Text, null);

        result.ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
        result.Error.Should().Contain("allowHostTarget was not set");
        VerifyNoActivation();
    }

    [Fact]
    public void Keyboard_TargetIsTheHost_WithAllowHostTarget_IsAllowed()
    {
        var result = CreateSut().CheckKeyboard(To(Terminal, allowHostTarget: true), KeyboardInputKind.Text, null);

        result.Allowed.Should().BeTrue(result.Error);
        result.Receiving.Should().Be(Terminal);
    }

    [Fact]
    public void Keyboard_Text_IntoAnActiveMenu_IsTargetInMenuMode_ButKeysAreAllowed()
    {
        // Arrange
        _inFront = Notepad;
        InMenuMode(Notepad);
        var sut = CreateSut();

        // Act & Assert - letters would pick menu items; {ESC} or arrows are how the menu is used
        sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetInMenuMode);
        sut.CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetInMenuMode);
        sut.CheckKeyboard(To(Notepad), KeyboardInputKind.Keys, SendKeysParser.Parse("{ESC}")).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Keyboard_Text_MenuStillOpenAfterActivation_IsTargetInMenuMode()
    {
        _foreground.Setup(f => f.EnsureForeground(Notepad, It.IsAny<int>())).Returns(() =>
        {
            _inFront = Notepad;
            return new FocusOutcome { Target = Notepad, IsForeground = true, Method = FocusMethod.SetForeground, ForegroundHandle = Notepad, InMenuMode = true };
        });

        CreateSut().CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null).ErrorCode.Should().Be(InputErrorCode.TargetInMenuMode);
    }

    #endregion

    #region Re-check before later batches

    [Fact]
    public void RecheckKeyboard_SameWindowInFront_ReturnsTheApproval()
    {
        // Arrange
        _inFront = Notepad;
        var sut = CreateSut();
        var approved = sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);
        _windowService.Invocations.Clear();

        // Act
        var again = sut.RecheckKeyboard(approved);

        // Assert - cheap: nothing is read but the foreground window
        again.Should().BeSameAs(approved);
        _windowService.Verify(s => s.GetWindowInfo(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void RecheckKeyboard_AnotherWindowCameToTheFront_IsTargetLostFocus()
    {
        // Arrange - typing started in Notepad, then the terminal came to the front
        _inFront = Notepad;
        var sut = CreateSut();
        var approved = sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);
        _inFront = Terminal;

        // Act
        var result = sut.RecheckKeyboard(approved);

        // Assert - the window that got the typed text is still reported
        result.ErrorCode.Should().Be(InputErrorCode.TargetLostFocus);
        result.ReceivingWindow!.Handle.Should().Be(Notepad);
        result.Error.Should().Contain("pwsh");
    }

    [Fact]
    public void RecheckKeyboard_TargetOpenedADialog_ContinuesIntoIt()
    {
        // Arrange
        var sut = CreateSut();
        var approved = sut.CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);
        _inFront = NotepadDialog;

        // Act
        var result = sut.RecheckKeyboard(approved);

        // Assert
        result.Allowed.Should().BeTrue(result.Error);
        result.Receiving.Should().Be(NotepadDialog);
        result.Anchor.Should().Be(Notepad);
    }

    [Fact]
    public void RecheckKeyboard_TargetOpenedADeniedDialog_IsRefused()
    {
        var sut = CreateSut();
        var approved = sut.CheckKeyboard(To(Notepad), KeyboardInputKind.Text, null);
        _inFront = CredentialPrompt;

        sut.RecheckKeyboard(approved).ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
    }

    #endregion

    #region Mouse without a target (M1.1)

    [Fact]
    public void Mouse_NoTarget_PointOnTheTerminal_IsRefusedAsTheAgentHost()
    {
        // Arrange - a child control of the terminal is at the point
        var terminalPane = new nint(0x101);
        At(terminalPane, root: Terminal);

        // Act
        var result = CreateSut().CheckMouse(500, 300, InputTarget.None);

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
        result.ReceivingWindow!.Handle.Should().Be(Terminal);
    }

    [Fact]
    public void Mouse_NoTarget_PointOnAnOrdinaryWindow_IsAllowedWithItsTopLevelWindow()
    {
        At(NotepadEdit, root: Notepad);

        var result = CreateSut().CheckMouse(500, 300, InputTarget.None);

        result.Allowed.Should().BeTrue(result.Error);
        result.Receiving.Should().Be(Notepad);
        result.ReceivingWindow!.Title.Should().Be("Untitled - Notepad");
    }

    [Fact]
    public void Mouse_NoTarget_PointOnADeniedWindow_IsTargetDeniedByPolicy()
    {
        At(KeePass);

        CreateSut().CheckMouse(500, 300, InputTarget.None).ErrorCode.Should().Be(InputErrorCode.TargetDeniedByPolicy);
    }

    [Fact]
    public void Mouse_NoTarget_NoWindowAtThePoint_IsAllowedWithoutAReceivingWindow()
    {
        At(nint.Zero);

        var result = CreateSut().CheckMouse(500, 300, InputTarget.None);

        result.Allowed.Should().BeTrue();
        result.ReceivingWindow.Should().BeNull();
    }

    #endregion

    #region Mouse with a target (M1.5)

    [Fact]
    public void Mouse_Target_PointOnItsChildControl_IsAllowed()
    {
        At(NotepadEdit, root: Notepad);

        var result = CreateSut().CheckMouse(500, 300, To(Notepad));

        result.Allowed.Should().BeTrue(result.Error);
        result.Anchor.Should().Be(Notepad);
    }

    [Fact]
    public void Mouse_Target_PointOnAWindowItOwns_IsAllowed()
    {
        At(NotepadDialog);

        CreateSut().CheckMouse(500, 300, To(Notepad)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Mouse_Target_PointOnAnotherApplication_IsTargetOccluded()
    {
        // Arrange
        At(Calculator);

        // Act
        var result = CreateSut().CheckMouse(500, 300, To(Notepad));

        // Assert
        result.ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
        result.ReceivingWindow!.Handle.Should().Be(Calculator);
        result.Error.Should().Contain("(500, 300)").And.Contain("Calculator").And.Contain("focus_window");
    }

    [Fact]
    public void Mouse_Target_PointOnAnotherMainWindowOfTheSameProcess_IsTargetOccluded()
    {
        // One process often has several main windows (Notepad, Explorer, browsers): the process rule is for popups only
        At(SecondNotepad);

        CreateSut().CheckMouse(500, 300, To(Notepad)).ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
    }

    [Fact]
    public void Mouse_Target_PointOnAnUnownedPopupOfItsProcess_IsAllowed()
    {
        At(NotepadMenu);

        CreateSut().CheckMouse(500, 300, To(Notepad)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Mouse_Target_PointOnAPopupOfAChildProcess_IsAllowed()
    {
        // A WebView2 dropdown: msedgewebview2.exe, started by the target, creates it unowned
        At(WebViewPopup);

        var result = CreateSut().CheckMouse(500, 300, To(Outlook));

        result.Allowed.Should().BeTrue(result.Error);
        result.ReceivingWindow!.ProcessName.Should().Be("msedgewebview2");
    }

    [Fact]
    public void Mouse_Target_PointOnAnAppWindowOfAChildProcess_IsTargetOccluded()
    {
        Style(WebViewPopup, WsOverlappedWindow | WsVisible, WsExAppWindow);
        At(WebViewPopup);

        CreateSut().CheckMouse(500, 300, To(Outlook)).ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
    }

    [Fact]
    public void Mouse_Target_PointOnAPopupOfAnUnrelatedProcess_IsTargetOccluded()
    {
        At(StrangerPopup);

        CreateSut().CheckMouse(500, 300, To(Notepad)).ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
    }

    [Fact]
    public void Mouse_Target_PointOnTheTerminal_IsOccludedEvenWithAllowHostTarget()
    {
        // allowHostTarget lifts the host rule only for the window that is the target
        At(Terminal);

        CreateSut().CheckMouse(500, 300, To(Notepad, allowHostTarget: true)).ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
    }

    [Fact]
    public void Mouse_TargetIsTheHost_OnlyWithAllowHostTarget()
    {
        // Arrange
        At(Terminal);
        var sut = CreateSut();

        // Act & Assert
        sut.CheckMouse(500, 300, To(Terminal)).ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
        sut.CheckMouse(500, 300, To(Terminal, allowHostTarget: true)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Mouse_TargetDisabledByAModalDialog_IsBlockedByModal()
    {
        // A click on a disabled window is silently ignored; report the dialog instead
        Style(Notepad, WsOverlappedWindow | WsVisible | WsDisabled);
        _windows.Setup(w => w.GetWindow(Notepad, GwEnabledPopup)).Returns(NotepadDialog);
        At(Notepad);

        var result = CreateSut().CheckMouse(500, 300, To(Notepad));

        result.ErrorCode.Should().Be(InputErrorCode.BlockedByModal);
        result.BlockingWindow!.Handle.Should().Be(NotepadDialog);
    }

    [Fact]
    public void Mouse_Target_PointOnItsElevatedOwnedWindow_IsTargetElevated()
    {
        // The window at the point belongs to the target, but runs elevated
        var elevatedDialog = new nint(0x240);
        Window(elevatedDialog, TaskManagerPid, "Taskmgr", "Elevated helper", owner: Notepad);
        At(elevatedDialog);

        CreateSut().CheckMouse(500, 300, To(Notepad)).ErrorCode.Should().Be(InputErrorCode.TargetElevated);
    }

    [Fact]
    public void RecheckMouse_SameWindowAtThePoint_ReturnsTheApproval()
    {
        At(Notepad);
        var sut = CreateSut();
        var approved = sut.CheckMouse(500, 300, To(Notepad));

        sut.RecheckMouse(500, 300, approved).Should().BeSameAs(approved);
    }

    [Fact]
    public void RecheckMouse_AnotherWindowAppearedAtThePoint_IsTargetOccluded()
    {
        // Arrange - moving the cursor opened a window of another app over the point
        At(Notepad);
        var sut = CreateSut();
        var approved = sut.CheckMouse(500, 300, To(Notepad));
        At(Calculator);

        // Act & Assert
        sut.RecheckMouse(500, 300, approved).ErrorCode.Should().Be(InputErrorCode.TargetOccluded);
    }

    [Fact]
    public void RecheckMouse_WithoutTarget_TheHostAppearedAtThePoint_IsRefused()
    {
        At(Notepad);
        var sut = CreateSut();
        var approved = sut.CheckMouse(500, 300, InputTarget.None);
        At(Terminal);

        sut.RecheckMouse(500, 300, approved).ErrorCode.Should().Be(InputErrorCode.TargetIsAgentHost);
    }

    #endregion

    #region Describing windows

    [Fact]
    public void DescribeForeground_UsesTheWindowServiceAndFallsBackToTheWrapper()
    {
        // Arrange - a window the window service cannot read (its rectangle is gone)
        var popup = new nint(0x900);
        _windows.Setup(w => w.GetWindowThreadProcessId(popup)).Returns(4242u);
        _windows.Setup(w => w.GetProcessName(4242u)).Returns("helper");
        _windows.Setup(w => w.GetWindowText(popup)).Returns("Tooltip");
        var sut = CreateSut();

        // Act & Assert
        _inFront = Notepad;
        sut.DescribeForeground().Should().Be(new WindowSummary(Notepad, "Untitled - Notepad", "notepad", NotepadPid));
        _inFront = popup;
        sut.DescribeForeground().Should().Be(new WindowSummary(popup, "Tooltip", "helper", 4242));
        _inFront = nint.Zero;
        sut.DescribeForeground().Should().BeNull();
    }

    #endregion

    #region Reads only

    [Fact]
    public void Guard_NeverChangesAWindow_ExceptThroughEnsureForeground()
    {
        // Arrange - run a mix of checks
        _inFront = Notepad;
        At(Notepad);
        var sut = CreateSut();

        // Act
        sut.CheckKeyboard(InputTarget.None, KeyboardInputKind.Text, null);
        sut.CheckKeyboard(To(Notepad), KeyboardInputKind.Keys, SendKeysParser.Parse("^w"));
        sut.CheckMouse(500, 300, To(Notepad));
        sut.CheckMouse(500, 300, InputTarget.None);

        // Assert - no window action on the wrapper, and no attach
        _windows.Verify(w => w.SetForegroundWindow(It.IsAny<nint>()), Times.Never);
        _windows.Verify(w => w.SetWindowPos(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
        _windows.Verify(w => w.ShowWindow(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        _windows.Verify(w => w.BringWindowToTop(It.IsAny<nint>()), Times.Never);
        _windows.Verify(w => w.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
        _threads.Verify(t => t.AttachThreadInput(It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion
}
