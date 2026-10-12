using System.Text.Json;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// element_action's service (roadmap M3.6) over an in-memory UI Automation tree, the real element registry, an inline action
/// runner and mocked guard, input, activation and window probe: the checks before an action, the pattern each action uses,
/// the state before and after, the mouse and keyboard paths through the guarded input service only, a pending modal, and that a
/// password value is never read or returned. Nothing acts on a real element or sends input.
/// </summary>
public sealed class ElementActionServiceTests : IDisposable
{
    private static readonly nint Handle = 0x5100;
    private const int Pid = 4242;
    private static readonly WindowSummary Main = new(Handle, "Main", "app", Pid);

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;
    private readonly Mock<IInputTargetGuard> _guard = new();
    private readonly InputSession _session = new(new FakeInputWrapper());
    private readonly InlineActionRunner _runner = new();
    private readonly Mock<IAppWindowProbe> _probe = new();
    private readonly Mock<IInputSimulationService> _input = new(MockBehavior.Strict);
    private readonly Mock<IForegroundService> _foreground = new();
    private readonly Mock<IClipboardService> _clipboard = new();
    private readonly FakeUiaNode _window;

    public ElementActionServiceTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns((uint)Pid);
        _windows.Setup(w => w.RespondsWithin(It.IsAny<nint>(), It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.GetClassName(Handle)).Returns("AppWindow");
        _windows.Setup(w => w.GetAncestor(It.IsAny<nint>(), WindowAncestor.Root)).Returns(Handle);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, 1000, firstWindowNumber: 1, TimeProvider.System);
        _window = new FakeUiaNode("Window", "Main") { ProcessId = Pid, NativeWindowHandle = Handle, OtherPatterns = UiaPatterns.Window };
        _tree.WithWindow(Handle, _window);

        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>()))
            .Returns((InputTarget target) => InputGuardResult.Allow(target, Handle, Main, Handle));
        _guard.Setup(g => g.CheckElementWindow(It.IsAny<InputTarget>(), It.IsAny<nint>()))
            .Returns((InputTarget target, nint window) => InputGuardResult.Allow(target, window, Main, Handle));
        _probe.Setup(p => p.Snapshot(It.IsAny<IReadOnlyCollection<int>>()))
            .Returns((IReadOnlyCollection<int> ids) => new AppWindowSnapshot(ids.ToHashSet(), new HashSet<nint> { Handle }));
        _probe.Setup(p => p.OpenedSince(It.IsAny<AppWindowSnapshot>())).Returns([]);
        _foreground.Setup(f => f.EnsureForeground(Handle, It.IsAny<int>())).Returns(new FocusOutcome { Target = Handle, IsForeground = true });
    }

    public void Dispose() => _session.Dispose();

    /// <param name="toggleStateWait">How long a toggle waits for the state to change (30 ms by default, so a box that never
    /// changes fails fast); a test that needs several polls to pass must allow for the system timer, whose sleeps last up to
    /// about 16 ms.</param>
    private ElementActionService CreateSut(ToolsetSelection? toolsets = null, TimeSpan? toggleStateWait = null) => new(
        _tree, _worker, _registry, _guard.Object, _session, _runner, _probe.Object, _input.Object, _foreground.Object,
        _windows.Object, _clipboard.Object, toolsets)
    {
        SettleDelay = TimeSpan.Zero,
        GateTimeout = TimeSpan.FromSeconds(10),
        ToggleStateWait = toggleStateWait ?? TimeSpan.FromMilliseconds(30),
        ToggleStatePoll = TimeSpan.FromMilliseconds(1)
    };

    /// <summary>Puts <paramref name="node"/> in the window and gives it a ref, as a read tool would.</summary>
    private string RefOf(FakeUiaNode node, FakeUiaNode? parent = null)
    {
        // An element names its process, as UI Automation reports it: the application's unless the test says otherwise
        if (node.ProcessId == 0)
        {
            node.ProcessId = Pid;
        }

        (parent ?? _window).With(node);
        return _registry.Register(Handle, _tree.ReadElement(node), new ElementOrigin(_registry.NextGeneration()))!;
    }

    private Task<ElementActionResult> ActAsync(
        string elementRef, ElementActionKind action, string? value = null, ElementValueMode mode = ElementValueMode.Value,
        ElementScrollDirection? direction = null, ElementActionFallback fallback = ElementActionFallback.None) =>
        CreateSut().ActAsync(new ElementActionRequest(elementRef, action, value, mode, direction, Fallback: fallback));

    private static FakeUiaNode Button(string name, FakeInvokePattern? invoke = null)
    {
        var button = new FakeUiaNode("Button", name) { ActionPatterns = ["Invoke"], ProcessId = Pid, Bounds = new UiaRect(100, 200, 80, 24) };
        return button.WithPattern<IUiaInvokePattern>(invoke ?? new FakeInvokePattern());
    }

    private static FakeUiaNode CheckBox(FakeCyclingTogglePattern toggle) =>
        new FakeUiaNode("CheckBox", "Wrap lines") { ActionPatterns = ["Toggle"], ProcessId = Pid }.WithPattern<IUiaTogglePattern>(toggle);

    private static FakeUiaNode Edit(IUiaValuePattern value, bool password = false) =>
        new FakeUiaNode("Edit", "Name") { ActionPatterns = ["Value"], ProcessId = Pid, IsPassword = password, IsKeyboardFocusable = true }
            .WithPattern(value);

    #region The checks before an action

    [Fact]
    public async Task Invoke_ThroughTheInvokePattern_ReportsTheMethodAndTheStateBeforeAndAfter()
    {
        // Arrange
        var invoke = new FakeInvokePattern();
        var save = RefOf(Button("Save", invoke));

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Action.Should().Be("invoke");
        result.Ref.Should().Be(save);
        result.WindowHandle.Should().Be(Handle);
        result.Status.Should().Be(ElementActionStatus.Done);
        result.Method.Should().Be(ElementActionMethod.Invoke);
        result.Before!.Name.Should().Be("Save");
        result.After!.IsEnabled.Should().BeTrue();
        result.OutcomeVerified.Should().BeNull("an invoke has no state to read back");
        invoke.Invokes.Should().Be(1);
        _runner.Runs.Should().Be(1, "every pattern action runs on the action runner, never on the read worker");
    }

    [Fact]
    public async Task AWindowTheTargetChecksRefuse_IsNotRead_AndNothingIsDone()
    {
        // Arrange - the user's policy denies the app (a password manager)
        var invoke = new FakeInvokePattern();
        var save = RefOf(Button("Save", invoke));
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) =>
            InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "Denied by the policy.", target));
        var readsBefore = _tree.Reads.Count;

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("TargetDeniedByPolicy");
        invoke.Invokes.Should().Be(0);
        _tree.Reads.Count.Should().Be(readsBefore, "the element is not even read");
    }

    [Fact]
    public async Task TheTargetChecks_GetTheElementsWindow_WithAllowHostTarget()
    {
        // Arrange
        var save = RefOf(Button("Save"));

        // Act
        await CreateSut().ActAsync(new ElementActionRequest(save, ElementActionKind.Invoke, AllowHostTarget: true));

        // Assert
        _guard.Verify(g => g.CheckTarget(InputTarget.Window(Handle, true)), Times.Once);
    }

    [Fact]
    public async Task WhileInputIsPaused_NothingIsReadOrDone()
    {
        // Arrange
        var invoke = new FakeInvokePattern();
        var save = RefOf(Button("Save", invoke));
        _session.Pause(InputControlSources.Hotkey);
        var readsBefore = _tree.Reads.Count;

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("PausedByUser");
        invoke.Invokes.Should().Be(0);
        _tree.Reads.Count.Should().Be(readsBefore);
        _guard.Verify(g => g.CheckTarget(It.IsAny<InputTarget>()), Times.Never);
    }

    [Fact]
    public async Task APauseWhileTheElementIsRead_StopsTheCall_BeforeItActs()
    {
        // Arrange - the user presses the pause hotkey while a slow provider answers the read
        var invoke = new FakeInvokePattern();
        var button = Button("Save", invoke);
        var save = RefOf(button);
        button.OnRead = () => _session.Pause(InputControlSources.Hotkey);

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("PausedByUser");
        invoke.Invokes.Should().Be(0);
        _runner.Runs.Should().Be(0);
    }

    [Fact]
    public async Task AnElementOfAnotherProcess_HasItsOwnWindowChecked_AndIsRefusedWhenThePolicyDeniesIt()
    {
        // Arrange - a child window of a password manager inside the ref's window
        const nint vault = 0x5400;
        const int vaultPid = 7070;
        _windows.Setup(w => w.GetWindowThreadProcessId(vault)).Returns((uint)vaultPid);
        var host = new FakeUiaNode("Pane", "Vault") { NativeWindowHandle = vault, ProcessId = vaultPid };
        _window.With(host);
        var invoke = new FakeInvokePattern();
        var reveal = new FakeUiaNode("Button", "Reveal") { ActionPatterns = ["Invoke"], ProcessId = vaultPid }.WithPattern<IUiaInvokePattern>(invoke);
        var revealRef = RefOf(reveal, host);
        _guard.Setup(g => g.CheckElementWindow(It.IsAny<InputTarget>(), vault)).Returns((InputTarget target, nint _) =>
            InputGuardResult.Refuse(InputErrorCode.TargetDeniedByPolicy, "Denied by the policy.", target));

        // Act
        var result = await ActAsync(revealRef, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("TargetDeniedByPolicy");
        invoke.Invokes.Should().Be(0);
        _guard.Verify(g => g.CheckElementWindow(InputTarget.Window(Handle, false), vault), Times.Once);
    }

    [Fact]
    public async Task AnElementOfTheWindowsOwnProcess_NeedsNoSecondCheck()
    {
        await ActAsync(RefOf(Button("Save")), ElementActionKind.Invoke);

        _guard.Verify(g => g.CheckElementWindow(It.IsAny<InputTarget>(), It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public async Task AnElementOfAnotherProcess_WithoutAWindowOfThatProcess_IsRefused()
    {
        // Arrange - the process cannot be checked, so nothing is done
        var invoke = new FakeInvokePattern();
        var stranger = RefOf(new FakeUiaNode("Button", "Go") { ActionPatterns = ["Invoke"], ProcessId = 9999 }.WithPattern<IUiaInvokePattern>(invoke));

        // Act
        var result = await ActAsync(stranger, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("ElementAccessFailed");
        result.Error.Should().Contain("process 9999");
        invoke.Invokes.Should().Be(0);
    }

    [Fact]
    public async Task AnElementOfAMinimizedWindow_IsOffscreen_WithAPointerToRestoreTheWindow()
    {
        _windows.Setup(w => w.IsIconic(Handle)).Returns(true);
        var save = RefOf(new FakeUiaNode("Button", "Save") { ActionPatterns = ["Invoke"], IsOffscreen = true, ProcessId = Pid }
            .WithPattern<IUiaInvokePattern>(new FakeInvokePattern()));

        var result = await ActAsync(save, ElementActionKind.Invoke);

        result.ErrorCode.Should().Be("ElementOffscreen");
        result.Error.Should().Contain("minimized").And.Contain("set_window_state");
    }

    [Fact]
    public async Task AStaleRef_IsElementStale()
    {
        // Arrange
        var button = Button("Save");
        var save = RefOf(button);
        button.Vanish();

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("ElementStale");
    }

    [Fact]
    public async Task ARefTheServerNeverIssued_IsElementStale_WithoutTakingTheGate()
    {
        var result = await ActAsync("w99e1", ElementActionKind.Invoke);

        result.ErrorCode.Should().Be("ElementStale");
        _guard.Verify(g => g.CheckTarget(It.IsAny<InputTarget>()), Times.Never);
    }

    [Fact]
    public async Task ADisabledElement_IsRefused_WithElementNotEnabled()
    {
        // Arrange
        var invoke = new FakeInvokePattern();
        var save = RefOf(new FakeUiaNode("Button", "Save") { ActionPatterns = ["Invoke"], IsEnabled = false }.WithPattern<IUiaInvokePattern>(invoke));

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("ElementNotEnabled");
        result.Before!.IsEnabled.Should().BeFalse("the state is read before the refusal, so the agent sees it");
        invoke.Invokes.Should().Be(0);
    }

    [Fact]
    public async Task AnOffscreenElement_IsRefused_WithAPointerToScrollIntoView()
    {
        // Arrange
        var invoke = new FakeInvokePattern();
        var save = RefOf(new FakeUiaNode("Button", "Save") { ActionPatterns = ["Invoke"], IsOffscreen = true }.WithPattern<IUiaInvokePattern>(invoke));

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("ElementOffscreen");
        result.Error.Should().Contain("scroll_into_view");
        invoke.Invokes.Should().Be(0);
    }

    [Fact]
    public async Task ScrollIntoView_OfAnOffscreenElement_BringsItOnScreen_AndIsVerified()
    {
        // Arrange
        var item = new FakeUiaNode("ListItem", "Item 00042") { IsOffscreen = true, OtherPatterns = UiaPatterns.ScrollItem, ProcessId = Pid };
        item.WithPattern<IUiaScrollItemPattern>(new FakeScrollItemPattern { OnScrollIntoView = () => item.IsOffscreen = false });
        var itemRef = RefOf(item);

        // Act
        var result = await ActAsync(itemRef, ElementActionKind.ScrollIntoView);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.ScrollItem);
        result.Before!.IsOffscreen.Should().BeTrue();
        result.After!.IsOffscreen.Should().BeFalse();
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutThePattern_AndFallbackNone_IsPatternNotSupported_ListingThePatternsItHas()
    {
        // Arrange - a label with no action pattern
        var text = RefOf(new FakeUiaNode("Text", "Status") { OtherPatterns = UiaPatterns.Text });

        // Act
        var result = await ActAsync(text, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("PatternNotSupported");
        result.Error.Should().Contain("Text").And.Contain("fallback mouse");
    }

    [Fact]
    public async Task TheApplicationRefusingTheAction_IsActionFailed()
    {
        // Arrange
        var save = RefOf(Button("Save", new FakeInvokePattern { OnInvoke = () => throw new InvalidOperationException("not now") }));

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.ErrorCode.Should().Be("ActionFailed");
        result.Error.Should().Contain("not now");
    }

    [Fact]
    public async Task APatternAction_RunsInsideTheInputGate()
    {
        // Arrange
        var tag = InputGate.TagFlow();
        var heldDuringInvoke = false;
        var save = RefOf(Button("Save", new FakeInvokePattern { OnInvoke = () => heldDuringInvoke = InputGate.IsHeldBy(tag) }));

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        heldDuringInvoke.Should().BeTrue("no input or window call may run between the target checks and the action");
        InputGate.IsHeldBy(tag).Should().BeFalse();
    }

    #endregion

    #region Patterns

    [Fact]
    public async Task Invoke_WithoutAnInvokePattern_UsesTheMsaaDefaultAction_AndSaysSo()
    {
        // Arrange
        var legacy = new FakeLegacyPattern("Press");
        var control = RefOf(new FakeUiaNode("Pane", "Legacy") { OtherPatterns = UiaPatterns.LegacyIAccessible }
            .WithPattern<IUiaLegacyIAccessiblePattern>(legacy));

        // Act
        var result = await ActAsync(control, ElementActionKind.Invoke);

        // Assert
        result.Method.Should().Be(ElementActionMethod.LegacyIAccessible);
        legacy.DefaultActions.Should().Be(1);
        result.Warnings.Should().Contain(w => w.Contains("'Press'"));
    }

    [Theory]
    [InlineData(ElementActionKind.Invoke)]
    [InlineData(ElementActionKind.Click)]
    public async Task APasswordFieldsMsaaPattern_IsNeverRead_SoItsValueIsNot(ElementActionKind action)
    {
        // Arrange - MSAA's state of an element includes its value (accValue), which for a password field can be the password
        var legacy = new RecordingLegacyPattern();
        var password = RefOf(new FakeUiaNode("Edit", "Password") { ProcessId = Pid, IsPassword = true, OtherPatterns = UiaPatterns.LegacyIAccessible }
            .WithPattern<IUiaLegacyIAccessiblePattern>(legacy));

        // Act
        var result = await ActAsync(password, action);

        // Assert
        result.ErrorCode.Should().Be("PatternNotSupported");
        legacy.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Click_OnACheckBox_TogglesIt_AndIsVerifiedByTheNewState()
    {
        // Arrange
        var toggle = new FakeCyclingTogglePattern(UiaToggleState.Off);
        var box = RefOf(CheckBox(toggle));

        // Act
        var result = await ActAsync(box, ElementActionKind.Click);

        // Assert
        result.Method.Should().Be(ElementActionMethod.Toggle);
        result.Before!.ToggleState.Should().Be(UiaToggleState.Off);
        result.After!.ToggleState.Should().Be(UiaToggleState.On);
        result.OutcomeVerified.Should().BeTrue();
    }

    [Theory]
    [InlineData(UiaToggleState.Off, false, "on", 1)]
    [InlineData(UiaToggleState.Off, true, "indeterminate", 2)]
    [InlineData(UiaToggleState.On, false, "off", 1)]
    public async Task Toggle_ToAState_TogglesUntilItIsReached(UiaToggleState start, bool threeState, string goal, int toggles)
    {
        // Arrange
        var toggle = new FakeCyclingTogglePattern(start, threeState);
        var box = RefOf(CheckBox(toggle));

        // Act
        var result = await ActAsync(box, ElementActionKind.Toggle, goal);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        toggle.Toggles.Should().Be(toggles);
        result.After!.ToggleState.Should().Be(ElementActionService.ParseToggleState(goal));
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Toggle_ToTheStateItIsIn_DoesNothing_AndIsVerified()
    {
        // Arrange
        var toggle = new FakeCyclingTogglePattern(UiaToggleState.On);
        var box = RefOf(CheckBox(toggle));

        // Act
        var result = await ActAsync(box, ElementActionKind.Toggle, "on");

        // Assert
        result.Success.Should().BeTrue();
        result.Method.Should().Be(ElementActionMethod.None);
        result.OutcomeVerified.Should().BeTrue();
        toggle.Toggles.Should().Be(0);
        _runner.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Toggle_WhoseStateDoesNotChange_IsNotToggledAgain_AndFails()
    {
        // Arrange - an application that ignores the toggle: toggling again could only click once too often
        var toggle = new FakeCyclingTogglePattern(UiaToggleState.Off) { Stuck = true };
        var box = RefOf(CheckBox(toggle));

        // Act
        var result = await ActAsync(box, ElementActionKind.Toggle, "on");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ActionFailed");
        toggle.Toggles.Should().Be(1);
        result.After!.ToggleState.Should().Be(UiaToggleState.Off);
    }

    [Fact]
    public async Task Toggle_ToAState_WaitsForAStateThatUpdatesLate_BeforeItTogglesAgain()
    {
        // Arrange - Chromium and Electron toggle asynchronously: the old state is reported for a while
        var toggle = new FakeCyclingTogglePattern(UiaToggleState.Off) { StaleReads = 5 };
        var box = RefOf(CheckBox(toggle));

        // Act
        var result = await ActAsync(box, ElementActionKind.Toggle, "on");

        // Assert
        result.Success.Should().BeTrue(result.Error);
        toggle.Toggles.Should().Be(1, "a stale read must not lead to a second toggle that turns the box off again");
        result.After!.ToggleState.Should().Be(UiaToggleState.On);
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Toggle_OfAThreeStateBox_WaitsForEachChange_AndReachesTheState()
    {
        var toggle = new FakeCyclingTogglePattern(UiaToggleState.Off, threeState: true) { StaleReads = 3 };
        var box = RefOf(CheckBox(toggle));

        // The second toggle waits out three stale reads, 1 ms polls apart: up to about 50 ms with the system timer
        var result = await CreateSut(toggleStateWait: TimeSpan.FromMilliseconds(500))
            .ActAsync(new ElementActionRequest(box, ElementActionKind.Toggle, "indeterminate"));

        result.Success.Should().BeTrue(result.Error);
        toggle.Toggles.Should().Be(2);
        result.After!.ToggleState.Should().Be(UiaToggleState.Indeterminate);
    }

    [Fact]
    public async Task Click_OnAnItemThatCanBeSelectedAndInvoked_SelectsIt_AndDoesNotOpenIt()
    {
        // Arrange - an Explorer or ListView item: its Invoke is the double click that opens the file
        var invoke = new FakeInvokePattern();
        var toggle = new FakeCyclingTogglePattern(UiaToggleState.Off);
        var item = new FakeUiaNode("ListItem", "report.docx") { ActionPatterns = ["Invoke", "Toggle", "SelectionItem"], ProcessId = Pid }
            .WithPattern<IUiaInvokePattern>(invoke)
            .WithPattern<IUiaTogglePattern>(toggle)
            .WithPattern<IUiaSelectionItemPattern>(new FakeSelectionItemPattern(false));
        var itemRef = RefOf(item);

        // Act
        var result = await ActAsync(itemRef, ElementActionKind.Click);

        // Assert
        result.Method.Should().Be(ElementActionMethod.SelectionItem);
        result.After!.IsSelected.Should().BeTrue();
        result.OutcomeVerified.Should().BeTrue();
        invoke.Invokes.Should().Be(0, "a single click selects; it does not open the item");
        toggle.Toggles.Should().Be(0, "a click on the row is not a click on its check box");
    }

    [Fact]
    public async Task SetValue_ThroughTheValuePattern_ReadsTheValueBack()
    {
        // Arrange
        var value = new FakeValuePattern("old");
        var name = RefOf(Edit(value));

        // Act
        var result = await ActAsync(name, ElementActionKind.SetValue, "Ada Lovelace");

        // Assert
        result.Method.Should().Be(ElementActionMethod.Value);
        value.SetValues.Should().Equal("Ada Lovelace");
        result.Before!.Value.Should().Be("old");
        result.After!.Value.Should().Be("Ada Lovelace");
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task SetValue_ThatTheApplicationChanges_IsNotVerified_AndSaysToTryModeType()
    {
        // Arrange - a web input that ignores the Value pattern
        var value = new FakeValuePattern("") { Transform = _ => "" };
        var name = RefOf(Edit(value));

        // Act
        var result = await ActAsync(name, ElementActionKind.SetValue, "Ada");

        // Assert
        result.Success.Should().BeTrue("the action was done; its effect is reported, not hidden");
        result.OutcomeVerified.Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("mode type"));
    }

    [Fact]
    public async Task SetValue_OnAReadOnlyField_IsElementReadOnly()
    {
        var value = new FakeValuePattern("fixed", isReadOnly: true);
        var name = RefOf(Edit(value));

        var result = await ActAsync(name, ElementActionKind.SetValue, "x");

        result.ErrorCode.Should().Be("ElementReadOnly");
        value.SetValues.Should().BeEmpty();
    }

    [Fact]
    public async Task SetValue_OnAPasswordField_NeverReadsOrReturnsItsValue()
    {
        // Arrange - the field counts every read of its value
        var value = new UnreadableValuePattern();
        var password = RefOf(Edit(value, password: true));

        // Act
        var result = await ActAsync(password, ElementActionKind.SetValue, "hunter2");

        // Assert
        result.Success.Should().BeTrue(result.Error);
        value.Set.Should().Be("hunter2");
        value.Reads.Should().Be(0);
        result.Before!.IsPassword.Should().BeTrue();
        result.Before.Value.Should().BeNull();
        result.After!.Value.Should().BeNull();
        result.OutcomeVerified.Should().BeNull("a password field's value is never read, so it cannot be checked");
        JsonSerializer.Serialize(result).Should().NotContain("hunter2");
    }

    [Theory]
    [InlineData(ElementActionKind.SetValue)]
    [InlineData(ElementActionKind.AppendText)]
    [InlineData(ElementActionKind.Focus)]
    public async Task AnElementWhoseIdentityCannotBeRead_IsNotActedOn_AndItsValueIsNeverRead(ElementActionKind action)
    {
        // Arrange - without the identity it is not known whether this is a password field
        var value = new UnreadableValuePattern();
        var password = new FakeUiaNode("Edit", "Password")
        {
            ActionPatterns = ["Value"], ProcessId = Pid, IsPassword = true, IsKeyboardFocusable = true,
            IdentityException = new InvalidOperationException("RPC failed")
        }.WithPattern<IUiaValuePattern>(value);
        var passwordRef = RefOf(password);

        // Act
        var result = await ActAsync(passwordRef, action, action == ElementActionKind.Focus ? null : "x");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ElementAccessFailed");
        result.Error.Should().Contain("password field");
        value.Reads.Should().Be(0);
        value.Set.Should().BeNull();
        JsonSerializer.Serialize(result).Should().NotContain("hunter2");
    }

    [Fact]
    public async Task AppendText_AddsToTheWholeValue()
    {
        // Arrange
        var value = new FakeValuePattern("Hello");
        var name = RefOf(Edit(value));

        // Act
        var result = await ActAsync(name, ElementActionKind.AppendText, ", world");

        // Assert
        value.SetValues.Should().Equal("Hello, world");
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task AppendText_ToAPasswordField_WithModeValue_IsRefused()
    {
        var password = RefOf(Edit(new UnreadableValuePattern(), password: true));

        var result = await ActAsync(password, ElementActionKind.AppendText, "x");

        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("mode type");
    }

    [Fact]
    public async Task Expand_ThenCollapse_ReportTheNewState()
    {
        // Arrange
        var pattern = new FakeExpandCollapsePattern(UiaExpandCollapseState.Collapsed);
        var combo = RefOf(new FakeUiaNode("ComboBox", "Font") { ActionPatterns = ["ExpandCollapse"], ProcessId = Pid }
            .WithPattern<IUiaExpandCollapsePattern>(pattern));

        // Act
        var expanded = await ActAsync(combo, ElementActionKind.Expand);
        var collapsed = await ActAsync(combo, ElementActionKind.Collapse);

        // Assert
        expanded.After!.ExpandCollapseState.Should().Be(UiaExpandCollapseState.Expanded);
        expanded.OutcomeVerified.Should().BeTrue();
        collapsed.After!.ExpandCollapseState.Should().Be(UiaExpandCollapseState.Collapsed);
        collapsed.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Expand_OfALeafNode_IsPatternNotSupported()
    {
        var leaf = RefOf(new FakeUiaNode("TreeItem", "readme.md") { ActionPatterns = ["ExpandCollapse"] }
            .WithPattern<IUiaExpandCollapsePattern>(new FakeExpandCollapsePattern(UiaExpandCollapseState.LeafNode)));

        var result = await ActAsync(leaf, ElementActionKind.Expand);

        result.ErrorCode.Should().Be("PatternNotSupported");
    }

    [Fact]
    public async Task Expand_ThatOpensAPopup_ReportsItAsOpenedPopup()
    {
        // Arrange - the dropdown is a new top-level window of the app
        var combo = RefOf(new FakeUiaNode("ComboBox", "Font") { ActionPatterns = ["ExpandCollapse"], ProcessId = Pid }
            .WithPattern<IUiaExpandCollapsePattern>(new FakeExpandCollapsePattern(UiaExpandCollapseState.Collapsed)));
        var dropdown = new OpenedWindow(0x7700, "", "ComboLBox", Pid, "app");
        _probe.Setup(p => p.OpenedSince(It.IsAny<AppWindowSnapshot>())).Returns([dropdown]);

        // Act
        var result = await ActAsync(combo, ElementActionKind.Expand);

        // Assert
        result.OpenedPopup.Should().Be(dropdown);
        _probe.Verify(p => p.Snapshot(It.Is<IReadOnlyCollection<int>>(ids => ids.Contains(Pid))), Times.Once);
    }

    [Theory]
    [InlineData(ElementActionKind.Select, false, true)]
    [InlineData(ElementActionKind.AddToSelection, false, true)]
    [InlineData(ElementActionKind.RemoveFromSelection, true, false)]
    public async Task SelectionActions_ReportTheSelection(ElementActionKind action, bool before, bool after)
    {
        var item = RefOf(new FakeUiaNode("ListItem", "Bravo") { ActionPatterns = ["SelectionItem"] }
            .WithPattern<IUiaSelectionItemPattern>(new FakeSelectionItemPattern(before)));

        var result = await ActAsync(item, action);

        result.Method.Should().Be(ElementActionMethod.SelectionItem);
        result.After!.IsSelected.Should().Be(after);
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Scroll_UsesTheNearestScrollableAncestor_AndReportsThePercentAndItsRef()
    {
        // Arrange - an item inside a list that scrolls
        var scroll = new FakeScrollPattern(vertical: 0);
        var list = new FakeUiaNode("List", "Files") { OtherPatterns = UiaPatterns.Scroll, ProcessId = Pid }.WithPattern<IUiaScrollPattern>(scroll);
        _window.With(list);
        var item = RefOf(new FakeUiaNode("ListItem", "a.txt") { ProcessId = Pid }, list);

        // Act
        var result = await ActAsync(item, ElementActionKind.Scroll, direction: ElementScrollDirection.Down);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.Scroll);
        scroll.Scrolls.Should().Equal((UiaScrollAmount.NoAmount, UiaScrollAmount.LargeIncrement));
        result.Scroll!.VerticalPercent.Should().Be(25);
        result.Scroll.AtEnd.Should().BeFalse();
        result.Scroll.ContainerRef.Should().NotBeNull().And.NotBe(item);
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Scroll_AtTheEnd_DoesNothing_AndSaysAtEnd()
    {
        var scroll = new FakeScrollPattern(vertical: 100);
        var list = RefOf(new FakeUiaNode("List", "Files") { OtherPatterns = UiaPatterns.Scroll }.WithPattern<IUiaScrollPattern>(scroll));

        var result = await ActAsync(list, ElementActionKind.Scroll, direction: ElementScrollDirection.Down);

        result.Method.Should().Be(ElementActionMethod.None);
        result.Scroll!.AtEnd.Should().BeTrue();
        scroll.Scrolls.Should().BeEmpty();
    }

    [Fact]
    public async Task SetRangeValue_SetsTheNumber_AndRefusesOneOutsideTheRange()
    {
        // Arrange
        var range = new FakeRangeValuePattern(10);
        var slider = RefOf(new FakeUiaNode("Slider", "Volume") { ActionPatterns = ["RangeValue"] }.WithPattern<IUiaRangeValuePattern>(range));

        // Act
        var set = await ActAsync(slider, ElementActionKind.SetRangeValue, "42.5");
        var outside = await ActAsync(slider, ElementActionKind.SetRangeValue, "150");

        // Assert
        set.After!.RangeValue.Should().Be(42.5);
        set.OutcomeVerified.Should().BeTrue();
        outside.ErrorCode.Should().Be("InvalidArguments");
        range.Value.Should().Be(42.5);
    }

    [Fact]
    public async Task WindowClose_OfATopLevelWindow_IsRefused_PointingToCloseWindow()
    {
        // Arrange - the element is the window itself
        var pattern = new FakeWindowPattern();
        _window.WithPattern<IUiaWindowPattern>(pattern);
        var window = _registry.Register(Handle, _tree.ReadElement(_window), new ElementOrigin(_registry.NextGeneration()))!;

        // Act
        var result = await ActAsync(window, ElementActionKind.WindowClose);

        // Assert
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("close_window");
        pattern.Closes.Should().Be(0, "the close guards of close_window must not be bypassed");
    }

    [Fact]
    public async Task WindowClose_OfAWindowInsideTheApplication_ClosesIt_AndIsVerifiedByItsGoing()
    {
        // Arrange - an MDI child: a window of its own, under the main window
        var pattern = new FakeWindowPattern();
        var child = new FakeUiaNode("Window", "report.txt") { OtherPatterns = UiaPatterns.Window, NativeWindowHandle = 0x5200, ProcessId = Pid };
        child.WithPattern<IUiaWindowPattern>(pattern);
        pattern.OnClose = child.Vanish;
        var childRef = RefOf(child);

        // Act
        var result = await ActAsync(childRef, ElementActionKind.WindowClose);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.Window);
        pattern.Closes.Should().Be(1);
        result.After.Should().BeNull();
        result.OutcomeVerified.Should().BeTrue();
    }

    [Fact]
    public async Task WindowClose_OfTheWindowsOwnElement_WithoutAHandleOfItsOwn_IsRefused()
    {
        // Arrange - the provider reports no native handle for the root element: it is still the top-level window
        var pattern = new FakeWindowPattern();
        var root = new FakeUiaNode("Window", "Main") { ProcessId = Pid, OtherPatterns = UiaPatterns.Window }.WithPattern<IUiaWindowPattern>(pattern);
        _tree.WithWindow(Handle, root);
        var rootRef = _registry.Register(Handle, _tree.ReadElement(root), new ElementOrigin(_registry.NextGeneration()))!;

        // Act
        var result = await ActAsync(rootRef, ElementActionKind.WindowClose);

        // Assert
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("close_window");
        pattern.Closes.Should().Be(0);
    }

    [Fact]
    public async Task WindowClose_OfAUwpAppsCoreWindow_IsRefused_PointingToItsFrameWindow()
    {
        // Arrange - a CoreWindow is a child of its ApplicationFrameWindow, but closing it ends the app
        const nint coreWindow = 0x5300;
        _windows.Setup(w => w.GetClassName(coreWindow)).Returns("Windows.UI.Core.CoreWindow");
        var pattern = new FakeWindowPattern();
        var core = new FakeUiaNode("Window", "Calculator") { OtherPatterns = UiaPatterns.Window, NativeWindowHandle = coreWindow, ProcessId = Pid };
        core.WithPattern<IUiaWindowPattern>(pattern);
        var coreRef = RefOf(core);

        // Act
        var result = await ActAsync(coreRef, ElementActionKind.WindowClose);

        // Assert
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain($"close_window (handle {(long)Handle})");
        pattern.Closes.Should().Be(0);
    }

    [Fact]
    public async Task Realize_OfAnItemThatIsNotVirtualized_DoesNothing_AndSaysSo()
    {
        var item = RefOf(new FakeUiaNode("ListItem", "Item 00001"));

        var result = await ActAsync(item, ElementActionKind.Realize);

        result.Method.Should().Be(ElementActionMethod.None);
        result.Warnings.Should().Contain(w => w.Contains("not a virtualized item"));
    }

    #endregion

    #region realize (roadmap M3.10)

    /// <summary>A 10,000-item virtual list in the window, its first 30 items realized, and the ref of an item it has not realized.</summary>
    private (FakeVirtualList List, string ItemRef) VirtualList(int index)
    {
        var list = new FakeVirtualList(10_000, realized: 30, i => $"Item {i:D5}", Pid);
        _window.With(list.List);
        var itemRef = _registry.Register(Handle, _tree.ReadElement(list.Items[index]), new ElementOrigin(_registry.NextGeneration()))!;
        return (list, itemRef);
    }

    [Fact]
    public async Task Realize_OfAnItemTheListHasNotRealized_RealizesIt_ThenScrollsItIntoView_OnTheActionRunner()
    {
        // Arrange
        var (list, item) = VirtualList(5000);

        // Act
        var result = await ActAsync(item, ElementActionKind.Realize);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.VirtualizedItem);
        result.Before!.IsOffscreen.Should().BeTrue();
        result.After!.IsOffscreen.Should().BeFalse();
        result.OutcomeVerified.Should().BeTrue();
        list.VirtualizedOf(5000).Realizes.Should().Be(1);
        list.ScrollItemOf(5000).Calls.Should().Be(1, "the fake's Realize leaves the item offscreen, as a provider that does not scroll");
        list.IsRealized(5000).Should().BeTrue();
        _runner.Runs.Should().Be(1, "Realize and ScrollIntoView are one pattern action, on the action runner");
    }

    [Fact]
    public async Task Realize_ThatBringsTheItemIntoViewItself_IsNotScrolledAgain()
    {
        // Arrange - WPF's Realize scrolls the item into view
        var (list, item) = VirtualList(5000);
        var node = list.Items[5000];
        list.VirtualizedOf(5000).OnRealize = () =>
        {
            list.List.With(node);
            node.IsOffscreen = false;
        };

        // Act
        var result = await ActAsync(item, ElementActionKind.Realize);

        // Assert
        result.OutcomeVerified.Should().BeTrue();
        list.ScrollItemOf(5000).Calls.Should().Be(0);
    }

    [Fact]
    public async Task Realize_ThatLeavesTheItemOffscreen_IsNotVerified()
    {
        var (list, item) = VirtualList(5000);
        list.ScrollItemOf(5000).OnScrollIntoView = null;

        var result = await ActAsync(item, ElementActionKind.Realize);

        result.Success.Should().BeTrue();
        result.OutcomeVerified.Should().BeFalse();
        result.Warnings.Should().Contain(w => w.Contains("does not show its effect"));
    }

    [Fact]
    public async Task Realize_OfAnOffscreenItemThatIsNotVirtualized_ScrollsItIntoView()
    {
        var scrollItem = new FakeScrollItemPattern();
        var node = new FakeUiaNode("ListItem", "Row 80") { IsOffscreen = true, OtherPatterns = UiaPatterns.ScrollItem };
        scrollItem.OnScrollIntoView = () => node.IsOffscreen = false;
        var item = RefOf(node.WithPattern<IUiaScrollItemPattern>(scrollItem));

        var result = await ActAsync(item, ElementActionKind.Realize);

        result.Method.Should().Be(ElementActionMethod.ScrollItem);
        result.OutcomeVerified.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Contains("only scrolled into view"));
    }

    [Fact]
    public async Task Realize_IsRefused_WhenTheTargetChecksRefuse_OrInputIsPaused_AndNothingIsRealized()
    {
        // Arrange
        var (list, item) = VirtualList(5000);
        _guard.Setup(g => g.CheckTarget(It.IsAny<InputTarget>())).Returns((InputTarget target) =>
            InputGuardResult.Refuse(InputErrorCode.TargetIsAgentHost, "The agent's own window.", target));

        // Act
        var refused = await ActAsync(item, ElementActionKind.Realize);
        _session.Pause(InputControlSources.Hotkey);
        var paused = await ActAsync(item, ElementActionKind.Realize);

        // Assert
        refused.ErrorCode.Should().Be("TargetIsAgentHost");
        paused.ErrorCode.Should().Be("PausedByUser");
        list.VirtualizedOf(5000).Realizes.Should().Be(0);
        list.ScrollItemOf(5000).Calls.Should().Be(0);
    }

    [Fact]
    public async Task SearchVirtualized_ThenRealize_PutsTheItemInTheTreeAndInView_WhereAPlainSearchFindsIt()
    {
        // Arrange - find_ui_elements' search with the same tree and registry
        var list = new FakeVirtualList(10_000, realized: 30, i => $"Item {i:D5}", Pid);
        _window.With(list.List);
        var search = new ElementSearchService(_tree, _worker, _registry, _windows.Object);
        var query = new ElementQuery(Handle, Name: "Item 07500", Timeout: TimeSpan.FromSeconds(5));
        (await search.FindAsync(query)).MatchCount.Should().Be(0);
        var found = await search.FindAsync(query with { SearchVirtualized = true });
        var item = found.Elements.Should().ContainSingle().Subject;
        item.Virtualized.Should().BeTrue();

        // Act
        var result = await ActAsync(item.Ref!, ElementActionKind.Realize);

        // Assert - realized and on screen: the plain search (visible elements only) finds it, under the same ref
        result.Success.Should().BeTrue(result.Error);
        result.OutcomeVerified.Should().BeTrue();
        var after = await search.FindAsync(query);
        after.Elements.Should().ContainSingle().Which.Ref.Should().Be(item.Ref);
    }

    #endregion

    #region Blocking actions

    [Fact]
    public async Task AnActionStillBlockedAtTheTimeout_WithANewWindow_IsInvokedPendingModal()
    {
        // Arrange - a Win32 Invoke that waits for the dialog it opened
        _runner.Outcome = UiaActionStatus.Pending;
        var save = RefOf(Button("Save As..."));
        var dialog = new OpenedWindow(0x7800, "Save As", "#32770", Pid, "app");
        _probe.Setup(p => p.OpenedSince(It.IsAny<AppWindowSnapshot>())).Returns([dialog]);

        // Act
        var result = await ActAsync(save, ElementActionKind.Invoke);

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be(ElementActionStatus.InvokedPendingModal);
        result.OpenedPopup.Should().Be(dialog);
        result.OutcomeVerified.Should().BeNull();
        result.Warnings.Should().Contain(w => w.Contains("Save As"));
    }

    [Fact]
    public async Task AnActionStillBlockedAtTheTimeout_WithoutANewWindow_IsActionTimedOut()
    {
        _runner.Outcome = UiaActionStatus.Pending;
        var save = RefOf(Button("Save"));

        var result = await ActAsync(save, ElementActionKind.Invoke);

        result.ErrorCode.Should().Be("ActionTimedOut");
        result.Error.Should().Contain("may still take effect");
    }

    [Fact]
    public async Task WhenTooManyActionsAreBlocked_TheActionIsNotStarted()
    {
        _runner.Outcome = UiaActionStatus.Refused;
        var save = RefOf(Button("Save"));

        var result = await ActAsync(save, ElementActionKind.Invoke);

        result.ErrorCode.Should().Be("ActionsPending");
    }

    #endregion

    #region Mouse and keyboard

    [Fact]
    public async Task FallbackMouse_ActivatesTheWindow_AndClicksAtTheClickPoint_ThroughTheGuardedInput_OutsideTheGate()
    {
        // Arrange - a control with no pattern at all
        var tag = InputGate.TagFlow();
        var node = new FakeUiaNode("Custom", "Canvas button") { Bounds = new UiaRect(100, 200, 80, 24), ClickablePoint = new UiaPoint(130, 210) };
        var target = RefOf(node);
        var heldDuringClick = true;
        _input.Setup(i => i.Click(130, 210, InputTarget.Window(Handle, false), false, It.IsAny<CancellationToken>()))
            .Callback(() => heldDuringClick = InputGate.IsHeldBy(tag))
            .Returns(new ClickResult(true, 130, 210, "left") { ReceivingWindow = Main });

        // Act
        var result = await ActAsync(target, ElementActionKind.Click, fallback: ElementActionFallback.Mouse);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.Mouse);
        result.ReceivingWindow.Should().Be(Main);
        _foreground.Verify(f => f.EnsureForeground(Handle, It.IsAny<int>()), Times.Once);
        heldDuringClick.Should().BeFalse("the input service takes the gate itself; holding it here would deadlock");
    }

    [Fact]
    public async Task RightClick_IsAlwaysAGuardedMouseClick_AtTheCentreWhenTheProviderHasNoClickPoint()
    {
        // Arrange
        var target = RefOf(Button("Save"));
        _input.Setup(i => i.RightClick(140, 212, It.IsAny<InputTarget>(), false, It.IsAny<CancellationToken>()))
            .Returns(new ClickResult(true, 140, 212, "right") { ReceivingWindow = Main });

        // Act
        var result = await ActAsync(target, ElementActionKind.RightClick);

        // Assert
        result.Method.Should().Be(ElementActionMethod.Mouse);
        _input.Verify(i => i.RightClick(140, 212, InputTarget.Window(Handle, false), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AGuardedClickTheInputGuardRefuses_ReportsItsCode()
    {
        var target = RefOf(Button("Save"));
        _input.Setup(i => i.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), 50, It.IsAny<InputTarget>(), false, It.IsAny<CancellationToken>()))
            .Returns(new ClickResult(false, 140, 212, "double", "Covered by another window.", InputErrorCode.TargetOccluded));

        var result = await ActAsync(target, ElementActionKind.DoubleClick);

        result.ErrorCode.Should().Be("TargetOccluded");
        result.Method.Should().Be(ElementActionMethod.Mouse);
    }

    [Fact]
    public async Task AWindowThatCannotBeActivated_GetsNoClick()
    {
        var target = RefOf(Button("Save"));
        _foreground.Setup(f => f.EnsureForeground(Handle, It.IsAny<int>())).Returns(new FocusOutcome
        {
            Target = Handle, IsForeground = false, ErrorCode = WindowErrorCode.FocusFailed, Error = "The lock refused it."
        });

        var result = await ActAsync(target, ElementActionKind.RightClick);

        result.ErrorCode.Should().Be("TargetNotForeground");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetValue_ModeType_FocusesTheElement_SelectsAll_AndTypes_ThroughTheGuardedInput()
    {
        // Arrange
        var value = new FakeValuePattern("old");
        var name = RefOf(Edit(value));
        var keys = new List<string>();
        _input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), InputTarget.Window(Handle, false), false, It.IsAny<CancellationToken>()))
            .Callback((string k, InputTarget _, bool _, CancellationToken _) => keys.Add(k))
            .ReturnsAsync((string k, InputTarget _, bool _, CancellationToken _) => new SendKeysResult(true, k) { ReceivingWindow = Main });
        _input.Setup(i => i.TypeTextAsync("Ada", 0, InputTarget.Window(Handle, false), false, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                keys.Add("Ada");
                value.Value = "Ada";
            })
            .ReturnsAsync(new TypeTextResult(true, "Ada", 3) { ReceivingWindow = Main });

        // Act
        var result = await ActAsync(name, ElementActionKind.SetValue, "Ada", ElementValueMode.Type);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.Keyboard);
        keys.Should().Equal("^a", "Ada");
        _tree.Reads.Should().Contain("SetFocus:Name");
        _foreground.Verify(f => f.EnsureForeground(Handle, It.IsAny<int>()), Times.Once);
        value.SetValues.Should().BeEmpty("mode type never uses the Value pattern");
        result.OutcomeVerified.Should().BeTrue();
        result.ReceivingWindow.Should().Be(Main);
    }

    [Fact]
    public async Task SetValue_ModeType_WhenFocusDoesNotLandOnTheElement_SendsNoKey()
    {
        // Arrange - select-all would reach whatever control has focus
        var name = RefOf(Edit(new FakeValuePattern("old")));
        _tree.SetFocusMovesFocus = false;

        // Act
        var result = await ActAsync(name, ElementActionKind.SetValue, "Ada", ElementValueMode.Type);

        // Assert
        result.ErrorCode.Should().Be("FocusNotOnElement");
        _input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("List", ElementValueMode.Type)]   // Ctrl+A, Delete: every file of a folder to the Recycle Bin
    [InlineData("DataGrid", ElementValueMode.Type)]
    [InlineData("Pane", ElementValueMode.Paste)]
    [InlineData("Button", ElementValueMode.Type)] // a space would press it
    public async Task SetValue_ModeTypeOrPaste_OnAnElementThatIsNotATextField_SendsNoKey(string controlType, ElementValueMode mode)
    {
        // Arrange
        var container = RefOf(new FakeUiaNode(controlType, "Items View") { ProcessId = Pid, IsKeyboardFocusable = true });

        // Act
        var result = await ActAsync(container, ElementActionKind.SetValue, "", mode);

        // Assert
        result.ErrorCode.Should().Be("PatternNotSupported");
        result.Error.Should().Contain("text field");
        _input.VerifyNoOtherCalls();
        _tree.Reads.Should().NotContain(r => r.StartsWith("SetFocus:"));
    }

    [Fact]
    public async Task SetValue_ModeType_OnAGridCellWithAWritableValue_SendsNoKey()
    {
        // A grid's Ctrl+A selects every row, and Delete can delete them: only text controls take keys
        var cell = RefOf(new FakeUiaNode("DataItem", "Amount") { ActionPatterns = ["Value"], ProcessId = Pid, IsKeyboardFocusable = true }
            .WithPattern<IUiaValuePattern>(new FakeValuePattern("42")));

        var result = await ActAsync(cell, ElementActionKind.SetValue, "", ElementValueMode.Type);

        result.ErrorCode.Should().Be("PatternNotSupported");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetValue_ModeType_WhenFocusLandsOnAControlInsideTheElement_SendsNoKey()
    {
        // Arrange - an Edit whose SetFocus puts focus on a button inside it (a clear button): the keys would go there
        var edit = Edit(new FakeValuePattern("old"));
        var name = RefOf(edit);
        var clear = new FakeUiaNode("Button", "Clear") { ProcessId = Pid, IsKeyboardFocusable = true };
        edit.With(clear);
        _tree.SetFocusMovesFocus = false;
        _tree.Focused = clear;

        // Act
        var result = await ActAsync(name, ElementActionKind.SetValue, "Ada", ElementValueMode.Type);

        // Assert
        result.ErrorCode.Should().Be("FocusNotOnElement");
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetValue_ModeType_OnAnEditableComboBox_TypesIntoItsEdit()
    {
        // Arrange - an editable combo box takes its text in the Edit that is its child
        var combo = new FakeUiaNode("ComboBox", "Font") { ActionPatterns = ["Value"], ProcessId = Pid, IsKeyboardFocusable = true }
            .WithPattern<IUiaValuePattern>(new FakeValuePattern("Arial"));
        var comboRef = RefOf(combo);
        var text = new FakeUiaNode("Edit", "Font") { ProcessId = Pid, IsKeyboardFocusable = true };
        combo.With(text);
        _tree.SetFocusMovesFocus = false;
        _tree.Focused = text;
        _input.Setup(i => i.SendKeysAsync("^a", It.IsAny<InputTarget>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendKeysResult(true, "^a"));
        _input.Setup(i => i.TypeTextAsync("Calibri", 0, It.IsAny<InputTarget>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TypeTextResult(true, "Calibri", 7));

        // Act
        var result = await ActAsync(comboRef, ElementActionKind.SetValue, "Calibri", ElementValueMode.Type);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Method.Should().Be(ElementActionMethod.Keyboard);
    }

    [Fact]
    public async Task SetValue_ModePaste_IsRefused_WhenTheUserTurnedClipboardWritesOff()
    {
        // Arrange - WAM_EXCLUDE_TOOLS=set_clipboard: the user's clipboard must not be replaced
        var toolsets = ToolsetSelection.FromEnvironment(
            name => name == ToolsetSelection.ExcludeToolsVariable ? "set_clipboard" : null, Toolsets.Server);
        var name = RefOf(Edit(new FakeValuePattern("old")));

        // Act
        var result = await CreateSut(toolsets).ActAsync(new ElementActionRequest(name, ElementActionKind.SetValue, "Ada", ElementValueMode.Paste));

        // Assert
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("set_clipboard").And.Contain("mode type");
        _clipboard.Verify(c => c.SetText(It.IsAny<string>()), Times.Never);
        _guard.Verify(g => g.CheckTarget(It.IsAny<InputTarget>()), Times.Never);
    }

    [Fact]
    public async Task SetValue_ModePaste_TakesNoMoreThanSetClipboard()
    {
        var name = RefOf(Edit(new FakeValuePattern("old")));

        var result = await ActAsync(name, ElementActionKind.SetValue, new string('x', ClipboardService.MaxTextLength + 1), ElementValueMode.Paste);

        result.ErrorCode.Should().Be("InvalidArguments");
        _clipboard.Verify(c => c.SetText(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SetValue_ModePaste_IntoAPasswordField_IsRefused_SoThePasswordNeverReachesTheClipboard()
    {
        var password = RefOf(Edit(new UnreadableValuePattern(), password: true));

        var result = await ActAsync(password, ElementActionKind.SetValue, "hunter2", ElementValueMode.Paste);

        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("clipboard");
        _clipboard.Verify(c => c.SetText(It.IsAny<string>()), Times.Never);
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetValue_ModePaste_PutsTheTextOnTheClipboardInsideTheGate_ThenPastesThroughTheGuardedInput()
    {
        // Arrange
        var tag = InputGate.TagFlow();
        var name = RefOf(Edit(new FakeValuePattern("old")));
        var heldWhileSet = false;
        _clipboard.Setup(c => c.SetText("Ada")).Callback(() => heldWhileSet = InputGate.IsHeldBy(tag)).Returns(new SetClipboardResult(true, 3));
        var keys = new List<string>();
        _input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), false, It.IsAny<CancellationToken>()))
            .Callback((string k, InputTarget _, bool _, CancellationToken _) => keys.Add(k))
            .ReturnsAsync((string k, InputTarget _, bool _, CancellationToken _) => new SendKeysResult(true, k));

        // Act
        var result = await ActAsync(name, ElementActionKind.SetValue, "Ada", ElementValueMode.Paste);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        heldWhileSet.Should().BeTrue("set_clipboard replaces the clipboard inside the gate too");
        keys.Should().Equal("^a", "^v");
    }

    [Fact]
    public async Task AppendText_ModeType_GoesToTheEnd_InsteadOfSelectingAll()
    {
        var name = RefOf(Edit(new FakeValuePattern("Hello")));
        var keys = new List<string>();
        _input.Setup(i => i.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), false, It.IsAny<CancellationToken>()))
            .Callback((string k, InputTarget _, bool _, CancellationToken _) => keys.Add(k))
            .ReturnsAsync((string k, InputTarget _, bool _, CancellationToken _) => new SendKeysResult(true, k));
        _input.Setup(i => i.TypeTextAsync(", world", 0, It.IsAny<InputTarget>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TypeTextResult(true, ", world", 7));

        var result = await ActAsync(name, ElementActionKind.AppendText, ", world", ElementValueMode.Type);

        result.Success.Should().BeTrue(result.Error);
        keys.Should().Equal("^{END}");
    }

    [Fact]
    public async Task Focus_ActivatesTheWindow_AndSetsFocus()
    {
        var name = RefOf(Edit(new FakeValuePattern("")));

        var result = await ActAsync(name, ElementActionKind.Focus);

        result.Method.Should().Be(ElementActionMethod.Focus);
        result.After!.HasKeyboardFocus.Should().BeTrue();
        result.OutcomeVerified.Should().BeTrue();
        _foreground.Verify(f => f.EnsureForeground(Handle, It.IsAny<int>()), Times.Once);
    }

    #endregion

    #region Arguments

    [Theory]
    [InlineData(ElementActionKind.SetValue, null, ElementValueMode.Value, null, "set_value needs value")]
    [InlineData(ElementActionKind.AppendText, "", ElementValueMode.Value, null, "append_text needs value")]
    [InlineData(ElementActionKind.SetRangeValue, "lots", ElementValueMode.Value, null, "a number")]
    [InlineData(ElementActionKind.Toggle, "maybe", ElementValueMode.Value, null, "on, off or indeterminate")]
    [InlineData(ElementActionKind.Scroll, null, ElementValueMode.Value, null, "needs direction")]
    [InlineData(ElementActionKind.Invoke, "x", ElementValueMode.Value, null, "value is for")]
    [InlineData(ElementActionKind.Invoke, null, ElementValueMode.Type, null, "mode is for")]
    [InlineData(ElementActionKind.Invoke, null, ElementValueMode.Value, ElementScrollDirection.Up, "direction is for")]
    [InlineData(ElementActionKind.MenuPath, null, ElementValueMode.Value, null, "menu_path needs value")]
    [InlineData(ElementActionKind.MenuPath, "File>>Save", ElementValueMode.Value, null, "empty item")]
    public void ArgumentError_NamesWhatDoesNotFitTheAction(
        ElementActionKind action, string? value, ElementValueMode mode, ElementScrollDirection? direction, string expected)
    {
        ElementActionService.ArgumentError(new ElementActionRequest("w1e1", action, value, mode, direction))
            .Should().Contain(expected);
    }

    [Fact]
    public void ArgumentError_RefusesFallbackMouse_ForAnActionAClickDoesNotDo()
    {
        ElementActionService.ArgumentError(new ElementActionRequest("w1e1", ElementActionKind.Expand, Fallback: ElementActionFallback.Mouse))
            .Should().Contain("fallback mouse");
        ElementActionService.ArgumentError(new ElementActionRequest("w1e1", ElementActionKind.Click, Fallback: ElementActionFallback.Mouse))
            .Should().BeNull();

        // menu_path's items are expanded and invoked through their patterns only: a mouse fallback is refused, not ignored
        ElementActionService.ArgumentError(new ElementActionRequest("w1e1", ElementActionKind.MenuPath, "File>Save As",
                Fallback: ElementActionFallback.Mouse))
            .Should().Contain("fallback mouse");
    }

    [Fact]
    public async Task MenuPath_IsNoActionOnOneElement_ForTheService_AndIsRefusedBeforeAnythingIsRead()
    {
        ElementActionService.ArgumentError(new ElementActionRequest("w1e1", ElementActionKind.MenuPath, "File>Save As")).Should().BeNull();

        var result = await ActAsync("w1e1", ElementActionKind.MenuPath, "File>Save As");

        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Error.Should().Contain("item by item");
        _guard.Verify(g => g.CheckTarget(It.IsAny<InputTarget>()), Times.Never);
    }

    [Fact]
    public async Task ActAsync_WithArgumentsThatDoNotFit_DoesNothing()
    {
        var result = await ActAsync("w1e1", ElementActionKind.SetValue);

        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _guard.Verify(g => g.CheckTarget(It.IsAny<InputTarget>()), Times.Never);
    }

    #endregion

    /// <summary>A password field's Value pattern: it counts the reads of its value, which must never happen.</summary>
    private sealed class UnreadableValuePattern : IUiaValuePattern
    {
        public string? Set { get; private set; }

        public int Reads { get; private set; }

        public string? Value
        {
            get
            {
                Reads++;
                return "hunter2-before";
            }
        }

        public bool IsReadOnly => false;

        public void SetValue(string value) => Set = value;
    }

    /// <summary>An MSAA pattern that records every member used, which must not happen for a password field.</summary>
    private sealed class RecordingLegacyPattern : IUiaLegacyIAccessiblePattern
    {
        public List<string> Calls { get; } = [];

        public string? DefaultAction
        {
            get
            {
                Calls.Add(nameof(DefaultAction));
                return "Press";
            }
        }

        public void DoDefaultAction() => Calls.Add(nameof(DoDefaultAction));
    }
}
