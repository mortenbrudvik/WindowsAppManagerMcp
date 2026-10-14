using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The guards the harness puts in front of the server's native wrappers, tested with mocked wrappers
/// and a fake desktop. Nothing here reaches a real window, a real process or sends input.
/// </summary>
public class WrapperGuardTests
{
    private const int FixturePid = FakeDesktop.FixturePid;
    private const int OtherPid = FakeDesktop.OtherPid;
    private static readonly nint FixtureWindow = FakeDesktop.FixtureWindow;
    private static readonly nint OtherWindow = FakeDesktop.OtherWindow;

    /// <summary>Members a later wave might add to a wrapper, to check that the guard fails closed.</summary>
    public interface IFutureWrapperMembers
    {
        bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

        void ReleaseHeldKeys();

        /// <summary>Acts, despite a name that reads like a capture.</summary>
        nint CaptureMouse(nint hWnd);

        bool SetWindowOrder(nint hWnd, nint hWndInsertAfter);
    }

    #region Which interfaces are guarded

    [Theory]
    [InlineData(typeof(INativeWindowWrapper), true)]
    [InlineData(typeof(INativeInputWrapper), true)]
    [InlineData(typeof(INativeProcessWrapper), true)]
    [InlineData(typeof(IProcessHandle), true)]
    [InlineData(typeof(INativeMonitorWrapper), true)]
    [InlineData(typeof(ITcpPortOwnerResolver), true)]
    [InlineData(typeof(IUIAutomationWrapper), true)]
    [InlineData(typeof(IScreenCaptureWrapper), true)]
    [InlineData(typeof(INativeThreadInputWrapper), true)]
    [InlineData(typeof(IForegroundService), false)]
    [InlineData(typeof(IWindowService), false)]
    [InlineData(typeof(IInputSimulationService), false)]
    [InlineData(typeof(IExecutableResolver), false)]
    [InlineData(typeof(ICdpService), false)]
    [InlineData(typeof(IFutureWrapperMembers), false)]
    public void IsNativeWrapper_RecognizesTheServersWrapperInterfaces(Type type, bool expected)
    {
        WrapperGuard.IsNativeWrapper(type).Should().Be(expected);
    }

    [Fact]
    public void GuardException_IsNotAnInvalidOperationException()
    {
        // The server reads some InvalidOperationExceptions as "the process has exited" (kill_process then
        // reports success); a refusal must never be mistaken for that.
        typeof(E2EGuardException).Should().NotBeAssignableTo<InvalidOperationException>();
    }

    #endregion

    #region Read-only policy (DesktopNoInput)

    [Fact]
    public void ReadOnlyGuard_PassesReadsThrough()
    {
        // Arrange
        var inner = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.GetForegroundWindow()).Returns(FixtureWindow);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        // Act & Assert
        guarded.GetForegroundWindow().Should().Be(FixtureWindow);
    }

    [Fact]
    public void ReadOnlyGuard_RefusesWindowChangesBeforeTheyReachTheWrapper()
    {
        // Arrange - a strict mock throws MockException for any call it was not set up for
        var inner = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        // Act
        var act = () => guarded.SetForegroundWindow(FixtureWindow);

        // Assert
        act.Should().Throw<E2EGuardException>().WithMessage("*SetForegroundWindow*DesktopNoInput*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void ReadOnlyGuard_RefusesInput()
    {
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        var act = () => guarded.SendInput([InputEvent.Unicode('x', 0)]);

        act.Should().Throw<E2EGuardException>();
        inner.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A wrapper member with an out parameter. No server wrapper has one since the browser reads moved onto IUiaTree
    /// (roadmap M3.F24), but the guard must still pass one back should a wrapper gain it.
    /// </summary>
    public interface IOutParameterWrapper
    {
        bool TryGetElementFromHandle(nint hwnd, out object? element);
    }

    /// <summary>Allows every call, for a test of what the guard passes through rather than what it refuses.</summary>
    private sealed class AllowAllCallPolicy : IWrapperCallPolicy
    {
        public string? Check(WrapperCall call) => null;
    }

    [Fact]
    public void Guard_PassesOutParametersBack()
    {
        // Arrange
        object? element = "the element";
        var inner = new Mock<IOutParameterWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.TryGetElementFromHandle(FixtureWindow, out element)).Returns(true);
        var guarded = WrapperGuard.Create(inner.Object, new AllowAllCallPolicy());

        // Act
        var found = guarded.TryGetElementFromHandle(FixtureWindow, out var result);

        // Assert
        found.Should().BeTrue();
        result.Should().Be("the element");
    }

    [Fact]
    public void Guard_RethrowsTheWrappersOwnExceptionUnwrapped()
    {
        var inner = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.GetWindowText(FixtureWindow)).Throws(new InvalidOperationException("boom"));
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        var act = () => guarded.GetWindowText(FixtureWindow);

        act.Should().Throw<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public void ReadOnlyGuard_RefusesAMemberTheCatalogDoesNotClassify_EvenWithAReadLikeName()
    {
        // Arrange - a strict mock throws MockException if the call gets through
        var inner = new Mock<IFutureWrapperMembers>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        // Act
        var act = () => guarded.CaptureMouse(FixtureWindow);

        // Assert
        act.Should().Throw<E2EGuardException>().WithMessage("*does not know*CaptureMouse*WrapperCatalog*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void ReadOnlyGuard_RefusesProcessStarts()
    {
        var inner = new Mock<INativeProcessWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        var start = () => guarded.StartProcess(new ProcessStartInfo("app.exe"));
        var where = () => guarded.FindWithWhereExe("app.exe");

        start.Should().Throw<E2EGuardException>().WithMessage("*StartProcess*DesktopNoInput*");
        where.Should().Throw<E2EGuardException>().WithMessage("*FindWithWhereExe*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void ReadOnlyGuard_GuardsTheProcessHandleOpenProcessReturns()
    {
        // Arrange
        var handle = ProcessHandle(FixturePid, FixtureContract.ProcessName);
        handle.Setup(h => h.Dispose());
        var inner = new Mock<INativeProcessWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.OpenProcess(FixturePid)).Returns(handle.Object);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        // Act
        var opened = guarded.OpenProcess(FixturePid)!;
        var name = opened.ProcessName;
        var close = () => opened.CloseMainWindow();
        var kill = () => opened.Kill(entireProcessTree: false);
        opened.Dispose();

        // Assert - reads and releasing the handle pass, closing and killing do not
        opened.Should().BeAssignableTo<GuardProxy>();
        name.Should().Be(FixtureContract.ProcessName);
        close.Should().Throw<E2EGuardException>().WithMessage("*IProcessHandle.CloseMainWindow*");
        kill.Should().Throw<E2EGuardException>().WithMessage("*IProcessHandle.Kill*");
        handle.Verify(h => h.Dispose(), Times.Once);
        handle.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        handle.Verify(h => h.CloseMainWindow(), Times.Never);
    }

    [Fact]
    public void Guards_ThePatternObjectsTheUiaTreeHandsOut_AndRefusesTheirActions_WhenTheElementCannotBeIdentified()
    {
        // Arrange - the widened IUiaTree (roadmap M3.2) hands out control patterns through a generic method; this tree
        // cannot say which process the element belongs to (ReadIdentity is not set up), so even the input tier refuses
        var node = new Mock<IUiaNode>().Object;
        var value = new Mock<IUiaValuePattern>(MockBehavior.Strict);
        value.SetupGet(v => v.IsReadOnly).Returns(false);
        var tree = new Mock<IUiaTree>(MockBehavior.Strict);
        tree.Setup(t => t.GetPattern<IUiaValuePattern>(node)).Returns(value.Object);
        var noInput = WrapperGuard.Create(tree.Object, new ReadOnlyCallPolicy());
        var fixtureOnly = WrapperGuard.Create(tree.Object, FixtureOnly(new FakeDesktop()));

        foreach (var guarded in new[] { noInput, fixtureOnly })
        {
            // Act
            var pattern = guarded.GetPattern<IUiaValuePattern>(node)!;
            var readOnly = pattern.IsReadOnly;
            var setValue = () => pattern.SetValue("typed");

            // Assert - the read passes; the action is refused before it reaches the pattern
            pattern.Should().BeAssignableTo<GuardProxy>();
            readOnly.Should().BeFalse();
            setValue.Should().Throw<E2EGuardException>().WithMessage("*IUiaValuePattern.SetValue*");
        }

        value.Verify(v => v.SetValue(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(FixturePid, true)]
    [InlineData(OtherPid, false)]
    public void FixtureOnlyGuard_AllowsAPatternAction_OnlyOnAnElementOfTheFixture(int elementProcess, bool allowed)
    {
        // Arrange - element_action (roadmap M3.6) invokes through the pattern the guarded tree handed out
        var node = new Mock<IUiaNode>().Object;
        var invoke = new Mock<IUiaInvokePattern>();
        var tree = new Mock<IUiaTree>(MockBehavior.Strict);
        tree.Setup(t => t.GetPattern<IUiaInvokePattern>(node)).Returns(invoke.Object);
        tree.Setup(t => t.ReadIdentity(node)).Returns(new UiaNodeIdentity("Button", "WPF", elementProcess, 0, false));
        var guarded = WrapperGuard.Create(tree.Object, FixtureOnly(new FakeDesktop()));
        var pattern = guarded.GetPattern<IUiaInvokePattern>(node)!;

        // Act
        var act = () => pattern.Invoke();

        // Assert
        if (allowed)
        {
            act.Should().NotThrow();
            invoke.Verify(i => i.Invoke(), Times.Once);
        }
        else
        {
            act.Should().Throw<E2EGuardException>().WithMessage($"*process {OtherPid} is not the fixture*");
            invoke.Verify(i => i.Invoke(), Times.Never);
        }
    }

    [Theory]
    [InlineData(FixturePid, true)]
    [InlineData(OtherPid, false)]
    public void FixtureOnlyGuard_ReadsAnUnrealizedItemsProcess_PropertyByProperty(int elementProcess, bool allowed)
    {
        // Arrange - element_action's realize (roadmap M3.10) acts on an item its list has not realized: WPF's placeholder
        // fails the identity read as a whole, but still names its process when read property by property
        var node = new Mock<IUiaNode>().Object;
        var realize = new Mock<IUiaVirtualizedItemPattern>();
        var tree = new Mock<IUiaTree>(MockBehavior.Strict);
        tree.Setup(t => t.GetPattern<IUiaVirtualizedItemPattern>(node)).Returns(realize.Object);
        tree.Setup(t => t.ReadIdentity(node)).Throws(new UiaElementUnavailableException("The element is no longer available."));
        tree.Setup(t => t.ReadVirtualizedItem(node)).Returns(VirtualizedItem(node, elementProcess));
        var guarded = WrapperGuard.Create(tree.Object, FixtureOnly(new FakeDesktop()));
        var pattern = guarded.GetPattern<IUiaVirtualizedItemPattern>(node)!;

        // Act
        var act = () => pattern.Realize();

        // Assert
        if (allowed)
        {
            act.Should().NotThrow();
            realize.Verify(r => r.Realize(), Times.Once);
        }
        else
        {
            act.Should().Throw<E2EGuardException>().WithMessage($"*process {OtherPid} is not the fixture*");
            realize.Verify(r => r.Realize(), Times.Never);
        }
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesAnAction_WhenNeitherReadNamesTheElementsProcess()
    {
        // Arrange - the element is gone: both the identity read and the property-by-property read fail
        var node = new Mock<IUiaNode>().Object;
        var realize = new Mock<IUiaVirtualizedItemPattern>();
        var tree = new Mock<IUiaTree>(MockBehavior.Strict);
        tree.Setup(t => t.GetPattern<IUiaVirtualizedItemPattern>(node)).Returns(realize.Object);
        tree.Setup(t => t.ReadIdentity(node)).Throws(new UiaElementUnavailableException("The element is no longer available."));
        tree.Setup(t => t.ReadVirtualizedItem(node)).Throws(new UiaElementUnavailableException("The element is gone."));
        var guarded = WrapperGuard.Create(tree.Object, FixtureOnly(new FakeDesktop()));
        var pattern = guarded.GetPattern<IUiaVirtualizedItemPattern>(node)!;

        // Act
        var act = () => pattern.Realize();

        // Assert
        act.Should().Throw<E2EGuardException>().WithMessage("*cannot tell which process*");
        realize.Verify(r => r.Realize(), Times.Never);
    }

    private static UiaVirtualizedItem VirtualizedItem(IUiaNode node, int processId) =>
        new(new UiaElement(
                node,
                new UiaNodeProperties("Item 07500", "ListItem", null, UiaRect.Empty, IsEnabled: true, IsOffscreen: true, "WPF"),
                "ListBoxItem",
                IsKeyboardFocusable: false,
                UiaPatterns.None,
                [7, 42],
                ControlTypeId: null,
                Virtualized: true),
            processId,
            IsPassword: null);

    [Theory]
    [InlineData(FixturePid, true)]
    [InlineData(OtherPid, false)]
    public void FixtureOnlyGuard_AllowsSetFocus_OnlyOnAnElementOfTheFixture(int elementProcess, bool allowed)
    {
        // Arrange
        var node = new Mock<IUiaNode>().Object;
        var tree = new Mock<IUiaTree>();
        tree.Setup(t => t.ReadIdentity(node)).Returns(new UiaNodeIdentity("TextBox", "WPF", elementProcess, 0, false));
        var guarded = WrapperGuard.Create(tree.Object, FixtureOnly(new FakeDesktop()));

        // Act
        var act = () => guarded.SetFocus(node);

        // Assert
        if (allowed)
        {
            act.Should().NotThrow();
        }
        else
        {
            act.Should().Throw<E2EGuardException>();
        }

        tree.Verify(t => t.SetFocus(node), allowed ? Times.Once() : Times.Never());
    }

    [Fact]
    public void ReadOnlyGuard_RefusesSetFocus()
    {
        var node = new Mock<IUiaNode>().Object;
        var tree = new Mock<IUiaTree>();
        var guarded = WrapperGuard.Create(tree.Object, new ReadOnlyCallPolicy());

        guarded.Invoking(t => t.SetFocus(node)).Should().Throw<E2EGuardException>().WithMessage("*UiaAction*");
        tree.Verify(t => t.SetFocus(It.IsAny<IUiaNode>()), Times.Never);
    }

    #endregion

    #region Fixture-only policy (Desktop input tier)

    [Fact]
    public void FixtureOnlyGuard_AllowsWindowCallsOnFixtureWindows()
    {
        // Arrange
        var inner = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SetForegroundWindow(FixtureWindow)).Returns(true);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop()));

        // Act & Assert
        guarded.SetForegroundWindow(FixtureWindow).Should().BeTrue();
        inner.Verify(w => w.SetForegroundWindow(FixtureWindow), Times.Once);
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesWindowCallsOnOtherWindows()
    {
        // Arrange
        var inner = new Mock<INativeWindowWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop()));

        // Act
        var act = () => guarded.PostMessage(OtherWindow, 0x0010, 0, 0);

        // Assert
        act.Should().Throw<E2EGuardException>()
            .WithMessage($"*PostMessage*process {OtherPid}*not the fixture (process {FixturePid})*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyGuard_AllowsKeyboardInputWhileTheFixtureIsInFront()
    {
        // Arrange
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(2u);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop { Foreground = FixtureWindow }));

        // Act & Assert
        guarded.SendInput([InputEvent.Unicode('x', 0), InputEvent.Unicode('x', 2)]).Should().Be(2u);
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesKeyboardInputWhileAnotherWindowIsInFront()
    {
        // Arrange - for example the terminal that runs the tests
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop { Foreground = OtherWindow }));

        // Act
        var act = () => guarded.SendInput([InputEvent.Key(0x41, 0)]);

        // Assert
        act.Should().Throw<E2EGuardException>().WithMessage("*keyboard input*foreground*'Other window'*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyGuard_ChecksEveryBatch_SoTypingStopsWhenTheFixtureLosesTheForeground()
    {
        // Arrange
        var desktop = new FakeDesktop { Foreground = FixtureWindow };
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(2u);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act
        guarded.SendInput([InputEvent.Unicode('a', 0), InputEvent.Unicode('a', 2)]);
        desktop.Foreground = OtherWindow;
        var act = () => guarded.SendInput([InputEvent.Unicode('b', 0), InputEvent.Unicode('b', 2)]);

        // Assert
        act.Should().Throw<E2EGuardException>();
        inner.Verify(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Once);
    }

    [Fact]
    public void FixtureOnlyGuard_AllowsMouseInputOverTheFixture()
    {
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(1u);
        var desktop = new FakeDesktop { Cursor = (10, 10), WindowAtCursor = FixtureWindow };
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        guarded.SendInput([InputEvent.Mouse(0x0002)]).Should().Be(1u);
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesMouseInputOverAnotherWindow_EvenWhenTheFixtureIsInFront()
    {
        // Arrange - a click lands on the window under the cursor, not on the foreground window
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var desktop = new FakeDesktop { Foreground = FixtureWindow, Cursor = (10, 10), WindowAtCursor = OtherWindow };
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act
        var act = () => guarded.SendInput([InputEvent.Mouse(0x0002)]);

        // Assert
        act.Should().Throw<E2EGuardException>().WithMessage("*mouse input at (10, 10)*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesMouseInputWhenTheCursorCannotBeRead()
    {
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop { Cursor = null }));

        var act = () => guarded.SendInput([InputEvent.Mouse(0x0002)]);

        act.Should().Throw<E2EGuardException>();
    }

    [Fact]
    public void FixtureOnlyGuard_MovesTheCursorOnlyOverTheFixture()
    {
        // Arrange
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SetCursorPos(5, 5)).Returns(true);
        var desktop = new FakeDesktop();
        desktop.WindowsAtPoints[(5, 5)] = FixtureWindow;
        desktop.WindowsAtPoints[(900, 900)] = OtherWindow;
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act
        var overFixture = guarded.SetCursorPos(5, 5);
        var overOther = () => guarded.SetCursorPos(900, 900);

        // Assert
        overFixture.Should().BeTrue();
        overOther.Should().Throw<E2EGuardException>();
    }

    [Fact]
    public void FixtureOnlyPolicy_RefusesAMemberTheCatalogDoesNotClassify()
    {
        // A later wave may add members to a wrapper; they stay refused until someone classifies them
        var policy = FixtureOnly(new FakeDesktop { Foreground = FixtureWindow });
        var method = Method(nameof(IFutureWrapperMembers.ReleaseHeldKeys));

        policy.Check(new WrapperCall(typeof(INativeInputWrapper), method, [], null, Member: null))
            .Should().Contain("does not know");
    }

    [Fact]
    public void FixtureOnlyPolicy_RefusesAnInputMemberItHasNoRuleFor()
    {
        // Classified as input, but the policy cannot tell where it would land
        var policy = FixtureOnly(new FakeDesktop { Foreground = FixtureWindow });
        var method = Method(nameof(IFutureWrapperMembers.ReleaseHeldKeys));

        policy.Check(new WrapperCall(typeof(INativeInputWrapper), method, [], null, WrapperMember.Input))
            .Should().Contain("does not know");
    }

    [Fact]
    public void FixtureOnlyPolicy_RefusesAWindowActionThatNamesNoWindow()
    {
        var policy = FixtureOnly(new FakeDesktop());
        var method = Method(nameof(IFutureWrapperMembers.AttachThreadInput));

        policy.Check(new WrapperCall(typeof(INativeWindowWrapper), method, [1u, 2u, true], null, WrapperMember.WindowAction()))
            .Should().Contain("names no window");
    }

    [Fact]
    public void FixtureOnlyPolicy_ChecksEveryWindowAWindowActionNames()
    {
        // Arrange - a call that names two windows; the second one must be checked too
        var policy = FixtureOnly(new FakeDesktop());
        var method = Method(nameof(IFutureWrapperMembers.SetWindowOrder));
        var member = WrapperMember.WindowAction("hWnd", "hWndInsertAfter");

        // Act
        var bothFixture = policy.Check(new WrapperCall(typeof(INativeWindowWrapper), method, [FixtureWindow, FixtureWindow], null, member));
        var secondOther = policy.Check(new WrapperCall(typeof(INativeWindowWrapper), method, [FixtureWindow, OtherWindow], null, member));

        // Assert
        bothFixture.Should().BeNull();
        secondOther.Should().Contain("hWndInsertAfter").And.Contain("not the fixture");
    }

    [Fact]
    public void FixtureOnlyPolicy_RefusesAWindowActionWhoseWindowParameterDoesNotExist()
    {
        var policy = FixtureOnly(new FakeDesktop());
        var method = Method(nameof(IFutureWrapperMembers.SetWindowOrder));

        policy.Check(new WrapperCall(typeof(INativeWindowWrapper), method, [FixtureWindow, FixtureWindow], null,
                WrapperMember.WindowAction("hwndParent")))
            .Should().Contain("no window handle parameter 'hwndParent'");
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesProcessStarts()
    {
        var inner = new Mock<INativeProcessWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop { Foreground = FixtureWindow }));

        var start = () => guarded.StartProcess(new ProcessStartInfo(FixtureContract.ProcessName + ".exe"));
        var where = () => guarded.FindWithWhereExe("app.exe");

        start.Should().Throw<E2EGuardException>().WithMessage("*may not start processes*");
        where.Should().Throw<E2EGuardException>().WithMessage("*may not start processes*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyGuard_ClosesAndKillsTheFixtureProcess()
    {
        // Arrange
        var handle = ProcessHandle(FixturePid, FixtureContract.ProcessName);
        handle.Setup(h => h.CloseMainWindow()).Returns(true);
        handle.Setup(h => h.Kill(false));
        var guarded = OpenGuarded(handle.Object, FixtureOnly(new FakeDesktop()));

        // Act
        var closed = guarded.CloseMainWindow();
        guarded.Kill(entireProcessTree: false);

        // Assert
        closed.Should().BeTrue();
        handle.Verify(h => h.Kill(false), Times.Once);
    }

    [Theory]
    [InlineData(OtherPid, "notepad")]
    [InlineData(FixturePid, "notepad")] // the fixture's pid, reused by another process after the fixture exited
    public void FixtureOnlyGuard_RefusesClosingOrKillingAnyOtherProcess(int processId, string processName)
    {
        // Arrange
        var handle = ProcessHandle(processId, processName);
        var guarded = OpenGuarded(handle.Object, FixtureOnly(new FakeDesktop()));

        // Act
        var close = () => guarded.CloseMainWindow();
        var kill = () => guarded.Kill(entireProcessTree: true);

        // Assert
        close.Should().Throw<E2EGuardException>().WithMessage($"*process {processId} '{processName}' is not the fixture*");
        kill.Should().Throw<E2EGuardException>();
        handle.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        handle.Verify(h => h.CloseMainWindow(), Times.Never);
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesKillingAProcessItCannotIdentify()
    {
        // Arrange - the process exited, so its name can no longer be read
        var handle = new Mock<IProcessHandle>(MockBehavior.Strict);
        handle.SetupGet(h => h.Id).Returns(FixturePid);
        handle.SetupGet(h => h.ProcessName).Throws(new InvalidOperationException("No process is associated with this object."));
        var guarded = OpenGuarded(handle.Object, FixtureOnly(new FakeDesktop()));

        // Act
        var kill = () => guarded.Kill(entireProcessTree: false);

        // Assert
        kill.Should().Throw<E2EGuardException>().WithMessage("*cannot tell which process*");
        handle.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void FixtureOnlyPolicy_RequiresAProcessId()
    {
        var act = () => new FixtureOnlyCallPolicy(new FakeDesktop(), 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region Verified activation (EnsureForeground)

    [Fact]
    public void ReadOnlyGuard_RefusesAttachingThreadInput()
    {
        var inner = new Mock<INativeThreadInputWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        var act = () => guarded.AttachThreadInput(1111, 2222, attach: true);

        act.Should().Throw<E2EGuardException>().WithMessage("*AttachThreadInput*DesktopNoInput*ThreadInputAttach*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void ReadOnlyGuard_PassesThreadReadsThrough()
    {
        var inner = new Mock<INativeThreadInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.GetWindowThreadId(OtherWindow)).Returns(2222u);
        inner.Setup(w => w.GetGuiThreadInfo(2222u)).Returns((GuiThreadInfo?)null);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

        guarded.GetWindowThreadId(OtherWindow).Should().Be(2222u);
        guarded.GetGuiThreadInfo(2222u).Should().BeNull();
    }

    [Fact]
    public void FixtureOnlyGuard_LetsTheCallingThreadAttachToAndDetachFromAnotherThread()
    {
        // Arrange - the activation thread attaches to the foreground thread, which may be the terminal's
        var inner = new Mock<INativeThreadInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.AttachThreadInput(1111, 2222, It.IsAny<bool>())).Returns(true);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop { CurrentThreadId = 1111 }));

        // Act & Assert
        guarded.AttachThreadInput(1111, 2222, attach: true).Should().BeTrue();
        guarded.AttachThreadInput(1111, 2222, attach: false).Should().BeTrue();
    }

    [Theory]
    [InlineData(3333u, 2222u)] // two other threads: the attach that does not help SetForegroundWindow
    [InlineData(1111u, 1111u)] // a thread to itself
    [InlineData(1111u, 0u)]
    public void FixtureOnlyGuard_RefusesAnyOtherAttach(uint attachThreadId, uint attachToThreadId)
    {
        var inner = new Mock<INativeThreadInputWrapper>(MockBehavior.Strict);
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(new FakeDesktop { CurrentThreadId = 1111 }));

        var act = () => guarded.AttachThreadInput(attachThreadId, attachToThreadId, attach: true);

        act.Should().Throw<E2EGuardException>().WithMessage("*AttachThreadInput*");
        inner.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeitherTier_RegistersAGlobalHotkey(bool inputTier)
    {
        // A registered hotkey takes its chord from every other application; the pause hotkey's own tests use fakes
        var inner = new Mock<INativeHotkeyWrapper>(MockBehavior.Strict);
        IWrapperCallPolicy policy = inputTier
            ? FixtureOnly(new FakeDesktop { Foreground = FixtureWindow })
            : new ReadOnlyCallPolicy();
        var guarded = WrapperGuard.Create(inner.Object, policy);

        var register = () => guarded.RegisterHotKey(1, 0x4007, 0x13);
        var wait = () => guarded.WaitForMessage();
        var openSharedPause = () => guarded.OpenDesktopPause();

        register.Should().Throw<E2EGuardException>().WithMessage("*RegisterHotKey*");
        wait.Should().Throw<E2EGuardException>().WithMessage("*WaitForMessage*");
        openSharedPause.Should().Throw<E2EGuardException>().WithMessage("*OpenDesktopPause*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyPolicy_RefusesAThreadInputAttachMemberItHasNoRuleFor()
    {
        var policy = FixtureOnly(new FakeDesktop());
        var method = Method(nameof(IFutureWrapperMembers.ReleaseHeldKeys));

        policy.Check(new WrapperCall(typeof(INativeThreadInputWrapper), method, [], null, WrapperMember.ThreadInputAttach))
            .Should().Contain("no rule");
    }

    [Fact]
    public void FixtureOnlyGuard_AllowsAZeroDistanceMouseMoveOverAnyWindow()
    {
        // Arrange - the input unlock moves nothing and presses nothing, wherever the cursor is
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(1u);
        var desktop = new FakeDesktop { Foreground = OtherWindow, Cursor = (10, 10), WindowAtCursor = OtherWindow };
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act & Assert
        guarded.SendInput([InputEvent.Mouse(FixtureOnlyCallPolicy.MouseMoveFlag)]).Should().Be(1u);
    }

    [Theory]
    [InlineData(0x0001u | 0x8000u)] // an absolute move goes to a point
    [InlineData(0x0001u | 0x0002u)] // a move with a button press
    public void FixtureOnlyGuard_ChecksAnyOtherMouseEventOverAnotherWindow(uint flags)
    {
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var desktop = new FakeDesktop { Cursor = (10, 10), WindowAtCursor = OtherWindow };
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        var act = () => guarded.SendInput([InputEvent.Mouse(flags)]);

        act.Should().Throw<E2EGuardException>().WithMessage("*mouse input at (10, 10)*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyGuard_ChecksABatchThatMixesAZeroDistanceMoveWithKeys()
    {
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var desktop = new FakeDesktop { Foreground = OtherWindow, Cursor = (10, 10), WindowAtCursor = FixtureWindow };
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        var act = () => guarded.SendInput([InputEvent.Mouse(FixtureOnlyCallPolicy.MouseMoveFlag), InputEvent.Key(0x1B, 0)]);

        act.Should().Throw<E2EGuardException>().WithMessage("*keyboard input*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void InputEvent_CarriesOnlyADistance_WhichTheMoveRulesRead()
    {
        // The zero-distance and destination rules read Dx and Dy; MouseData only carries how far a wheel event turns,
        // which does not change where it goes (the wheel rule checks that), and Scan only a key's scan code, which does not
        // change which window a key goes to. An InputEvent that gains other data must revisit FixtureOnlyCallPolicy.
        typeof(InputEvent).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            nameof(InputEvent.Kind), nameof(InputEvent.Code), nameof(InputEvent.Flags), nameof(InputEvent.Dx), nameof(InputEvent.Dy),
            nameof(InputEvent.MouseData), nameof(InputEvent.Scan));
    }

    [Fact]
    public void FixtureOnlyGuard_AllowsAScrollOverTheFixture_WhileTheFixtureIsInFront()
    {
        // Arrange - the server's scroll: a move to the point, then the wheel notches (1920x1080 desktop)
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(3u);
        var desktop = new FakeDesktop { Foreground = FixtureWindow, Cursor = (500, 300), WindowAtCursor = FixtureWindow };
        desktop.WindowsAtPoints[(500, 300)] = FixtureWindow;
        var batch = new InputBatchBuilder(new VirtualScreenBounds(0, 0, 1920, 1080), buttonsSwapped: false)
            .MoveTo(500, 300).Wheel(deltaY: 2, deltaX: 0).Build();
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act & Assert
        guarded.SendInput(batch.Events).Should().Be(3u);
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesAScroll_WhileAnotherWindowIsInFront()
    {
        // The wheel can follow the keyboard focus ("scroll inactive windows" off), so it would scroll the window in front
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var desktop = new FakeDesktop { Foreground = OtherWindow, Cursor = (500, 300), WindowAtCursor = FixtureWindow };
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        var act = () => guarded.SendInput([InputEvent.Wheel(-120)]);

        act.Should().Throw<E2EGuardException>().WithMessage("*mouse wheel input*");
        inner.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeitherTier_OpensTheClipboard(bool inputTier)
    {
        // The clipboard is the user's: a test must neither read what they copied nor replace it
        var inner = new Mock<INativeClipboardWrapper>(MockBehavior.Strict);
        inner.Setup(c => c.GetSequenceNumber()).Returns(7u);
        inner.Setup(c => c.GetOwnerProcessId()).Returns(1234);
        IWrapperCallPolicy policy = inputTier
            ? FixtureOnly(new FakeDesktop { Foreground = FixtureWindow })
            : new ReadOnlyCallPolicy();
        var guarded = WrapperGuard.Create(inner.Object, policy);

        var read = () => guarded.Read(ClipboardFormat.Text, 100, privateContentSequence: 0);
        var write = () => guarded.WriteText("x");

        read.Should().Throw<E2EGuardException>().WithMessage("*Read*");
        write.Should().Throw<E2EGuardException>().WithMessage("*WriteText*");
        guarded.GetSequenceNumber().Should().Be(7u, "the sequence number opens nothing");
        guarded.GetOwnerProcessId().Should().Be(1234, "the owner opens nothing either");
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesARelativeMoveWithADistance_EvenOverTheFixture()
    {
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var desktop = new FakeDesktop { Foreground = FixtureWindow, Cursor = (10, 10), WindowAtCursor = FixtureWindow };
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        var act = () => guarded.SendInput([new InputEvent(InputEventKind.Mouse, 0, FixtureOnlyCallPolicy.MouseMoveFlag, 50, 0)]);

        act.Should().Throw<E2EGuardException>().WithMessage("*cannot work out*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyGuard_AllowsAClickWhoseAbsoluteMoveEndsOverTheFixture()
    {
        // Arrange - the server's click: a move to the point, button down, button up (1920x1080 desktop)
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(3u);
        var desktop = new FakeDesktop { Cursor = (10, 10), WindowAtCursor = FixtureWindow };
        desktop.WindowsAtPoints[(500, 300)] = FixtureWindow;
        var batch = new InputBatchBuilder(new VirtualScreenBounds(0, 0, 1920, 1080), buttonsSwapped: false)
            .MoveTo(500, 300).Click(MouseButton.Primary).Build();
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act & Assert
        batch.Events.Skip(1).Should().Equal(InputEvent.Mouse(0x0002), InputEvent.Mouse(0x0004));
        guarded.SendInput(batch.Events).Should().Be(3u);
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesAClickWhoseAbsoluteMoveEndsOverAnotherWindow()
    {
        // Arrange - the cursor is over the fixture, but the click's own move takes it to another window
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var desktop = new FakeDesktop { Cursor = (10, 10), WindowAtCursor = FixtureWindow };
        desktop.WindowsAtPoints[(960, 540)] = OtherWindow;
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act
        var act = () => guarded.SendInput([InputEvent.MouseMoveAbsolute(32768, 32768), InputEvent.Mouse(0x0002)]);

        // Assert
        act.Should().Throw<E2EGuardException>().WithMessage("*after the move to (960, 540)*");
        inner.VerifyNoOtherCalls();
    }

    [Fact]
    public void FixtureOnlyGuard_AllowsAMoveOnlyBatchThatEndsOverTheFixture_WhereverTheCursorIs()
    {
        // Arrange - mouse_move's batch (roadmap M4.2) is judged as SetCursorPos is: by where it puts the pointer
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.SendInput(It.IsAny<IReadOnlyList<InputEvent>>())).Returns(1u);
        var desktop = new FakeDesktop { Cursor = (10, 10), WindowAtCursor = OtherWindow };
        desktop.WindowsAtPoints[(500, 300)] = FixtureWindow;
        var batch = new InputBatchBuilder(new VirtualScreenBounds(0, 0, 1920, 1080), buttonsSwapped: false).MoveTo(500, 300).Build();
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        // Act & Assert
        guarded.SendInput(batch.Events).Should().Be(1u);
    }

    [Fact]
    public void FixtureOnlyGuard_RefusesAMoveOnlyBatchThatEndsOverAnotherWindow()
    {
        var inner = new Mock<INativeInputWrapper>(MockBehavior.Strict);
        var desktop = new FakeDesktop { Cursor = (10, 10), WindowAtCursor = FixtureWindow };
        desktop.WindowsAtPoints[(960, 540)] = OtherWindow;
        var guarded = WrapperGuard.Create(inner.Object, FixtureOnly(desktop));

        var act = () => guarded.SendInput([InputEvent.MouseMoveAbsolute(32768, 32768)]);

        act.Should().Throw<E2EGuardException>().WithMessage("*after the move to (960, 540)*");
        inner.VerifyNoOtherCalls();
    }

    #endregion

    #region Refused services

    [Fact]
    public void RefusingGuard_RefusesEveryCall()
    {
        var cdp = (ICdpService)WrapperGuard.CreateRefusing(typeof(ICdpService), "no CDP target in E2E tests");

        Action targets = () => _ = cdp.GetPageTargetsAsync(9222, TimeSpan.FromSeconds(1));
        Action find = () => _ = cdp.FindTargetForWindowAsync(9222, 1, "title", TimeSpan.FromSeconds(1));

        targets.Should().Throw<E2EGuardException>().WithMessage("*ICdpService.GetPageTargetsAsync*no CDP target*");
        find.Should().Throw<E2EGuardException>();
    }

    [Fact]
    public void RefuseService_ReplacesTheRegistrationWithoutCreatingTheService()
    {
        // Arrange
        var services = new ServiceCollection();
        var created = false;
        services.AddSingleton<ICdpService>(_ =>
        {
            created = true;
            return Mock.Of<ICdpService>(MockBehavior.Strict);
        });

        // Act
        WrapperGuard.RefuseService(services, typeof(ICdpService), "refused");
        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ICdpService>();

        // Assert
        resolved.Should().BeAssignableTo<GuardProxy>();
        created.Should().BeFalse();
    }

    [Fact]
    public void RefuseService_FailsWhenTheServiceIsNotRegistered()
    {
        var act = () => WrapperGuard.RefuseService(new ServiceCollection(), typeof(ICdpService), "refused");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not registered*");
    }

    #endregion

    #region Registration

    [Fact]
    public void GuardNativeWrappers_ReplacesEveryWrapperTheServerRegisters()
    {
        // Arrange - the server's own registrations, as Program.cs makes them
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(Path.GetTempPath()));

        // Act
        var guarded = WrapperGuard.GuardNativeWrappers(services, new ReadOnlyCallPolicy());

        // Assert
        guarded.Should().Contain(new[]
        {
            typeof(INativeWindowWrapper),
            typeof(INativeInputWrapper),
            typeof(IUIAutomationWrapper),
            typeof(IScreenCaptureWrapper)
        });
        services.Where(d => WrapperGuard.IsNativeWrapper(d.ServiceType))
            .Should().OnlyContain(d => d.ImplementationFactory != null && d.ImplementationType == null);
    }

    #endregion

    private static FixtureOnlyCallPolicy FixtureOnly(IDesktopProbe desktop) => new(desktop, FixturePid);

    private static MethodInfo Method(string name) => typeof(IFutureWrapperMembers).GetMethod(name)!;

    /// <summary>A strict process handle mock that answers who it is and nothing else.</summary>
    private static Mock<IProcessHandle> ProcessHandle(int processId, string processName)
    {
        var handle = new Mock<IProcessHandle>(MockBehavior.Strict);
        handle.SetupGet(h => h.Id).Returns(processId);
        handle.SetupGet(h => h.ProcessName).Returns(processName);
        return handle;
    }

    /// <summary>The guarded handle the server gets when it opens <paramref name="handle"/>'s process.</summary>
    private static IProcessHandle OpenGuarded(IProcessHandle handle, IWrapperCallPolicy policy)
    {
        var wrapper = new Mock<INativeProcessWrapper>(MockBehavior.Strict);
        wrapper.Setup(w => w.OpenProcess(It.IsAny<int>())).Returns(handle);
        return WrapperGuard.Create(wrapper.Object, policy).OpenProcess(handle.Id)!;
    }
}
