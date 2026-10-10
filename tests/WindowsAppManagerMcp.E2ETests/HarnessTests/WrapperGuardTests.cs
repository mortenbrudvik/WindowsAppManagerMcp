using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The guards the harness puts in front of the server's native wrappers, tested with mocked wrappers
/// and a fake desktop. Nothing here reaches a real window or sends input.
/// </summary>
public class WrapperGuardTests
{
    private const int FixturePid = 4242;
    private const int OtherPid = 777;
    private static readonly nint FixtureWindow = 0x1000;
    private static readonly nint OtherWindow = 0x2000;

    /// <summary>Members a later wave might add to a wrapper, to check that the guard fails closed.</summary>
    public interface IFutureWrapperMembers
    {
        bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

        void ReleaseHeldKeys();
    }

    #region Read classification

    [Theory]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.EnumWindows), true)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.GetWindowRect), true)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.IsWindow), true)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.GetForegroundWindow), true)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.SetForegroundWindow), false)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.BringWindowToTop), false)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.SetWindowPos), false)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.ShowWindow), false)]
    [InlineData(typeof(INativeWindowWrapper), nameof(INativeWindowWrapper.PostMessage), false)]
    [InlineData(typeof(INativeInputWrapper), nameof(INativeInputWrapper.GetCursorPos), true)]
    [InlineData(typeof(INativeInputWrapper), nameof(INativeInputWrapper.SendInput), false)]
    [InlineData(typeof(INativeInputWrapper), nameof(INativeInputWrapper.SetCursorPos), false)]
    [InlineData(typeof(IUIAutomationWrapper), nameof(IUIAutomationWrapper.TryGetElementFromHandle), true)]
    [InlineData(typeof(IScreenCaptureWrapper), nameof(IScreenCaptureWrapper.CaptureWindow), true)]
    [InlineData(typeof(IFutureWrapperMembers), nameof(IFutureWrapperMembers.AttachThreadInput), false)]
    [InlineData(typeof(IFutureWrapperMembers), nameof(IFutureWrapperMembers.ReleaseHeldKeys), false)]
    public void IsRead_ClassifiesWrapperMembersByName(Type type, string method, bool expected)
    {
        WrapperGuard.IsRead(type.GetMethod(method)!).Should().Be(expected);
    }

    [Theory]
    [InlineData(typeof(INativeWindowWrapper), true)]
    [InlineData(typeof(INativeInputWrapper), true)]
    [InlineData(typeof(INativeProcessWrapper), true)]
    [InlineData(typeof(IUIAutomationWrapper), true)]
    [InlineData(typeof(IScreenCaptureWrapper), true)]
    [InlineData(typeof(IWindowService), false)]
    [InlineData(typeof(IInputSimulationService), false)]
    [InlineData(typeof(IFutureWrapperMembers), false)]
    public void IsNativeWrapper_RecognizesTheServersWrapperInterfaces(Type type, bool expected)
    {
        WrapperGuard.IsNativeWrapper(type).Should().Be(expected);
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

    [Fact]
    public void Guard_PassesOutParametersBack()
    {
        // Arrange
        object? element = "the element";
        var inner = new Mock<IUIAutomationWrapper>(MockBehavior.Strict);
        inner.Setup(w => w.TryGetElementFromHandle(FixtureWindow, out element)).Returns(true);
        var guarded = WrapperGuard.Create(inner.Object, new ReadOnlyCallPolicy());

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
    public void FixtureOnlyPolicy_RefusesAnInputMemberItDoesNotKnow()
    {
        // A later wave may add members to INativeInputWrapper; they stay refused until the policy knows them
        var policy = FixtureOnly(new FakeDesktop { Foreground = FixtureWindow });
        var method = typeof(IFutureWrapperMembers).GetMethod(nameof(IFutureWrapperMembers.ReleaseHeldKeys))!;

        policy.Check(typeof(INativeInputWrapper), method, []).Should().Contain("does not know");
    }

    [Fact]
    public void FixtureOnlyPolicy_RefusesAWindowMemberThatNamesNoWindow()
    {
        var policy = FixtureOnly(new FakeDesktop());
        var method = typeof(IFutureWrapperMembers).GetMethod(nameof(IFutureWrapperMembers.AttachThreadInput))!;

        policy.Check(typeof(INativeWindowWrapper), method, [1u, 2u, true]).Should().Contain("names no window");
    }

    [Fact]
    public void FixtureOnlyPolicy_RequiresAProcessId()
    {
        var act = () => new FixtureOnlyCallPolicy(new FakeDesktop(), 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
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

    /// <summary>A desktop with one fixture window and one other window.</summary>
    private sealed class FakeDesktop : IDesktopProbe
    {
        public nint Foreground { get; set; }

        public (int X, int Y)? Cursor { get; set; } = (0, 0);

        public nint WindowAtCursor { get; set; }

        public Dictionary<(int X, int Y), nint> WindowsAtPoints { get; } = new();

        public nint GetForegroundWindow() => Foreground;

        public nint WindowFromPoint(int x, int y) =>
            WindowsAtPoints.TryGetValue((x, y), out var window) ? window
            : Cursor == (x, y) ? WindowAtCursor
            : 0;

        public (int X, int Y)? GetCursorPos() => Cursor;

        public int GetWindowProcessId(nint hwnd) =>
            hwnd == FixtureWindow ? FixturePid : hwnd == OtherWindow ? OtherPid : 0;

        public string GetWindowTitle(nint hwnd) =>
            hwnd == FixtureWindow ? "Fixture window" : hwnd == OtherWindow ? "Other window" : "";
    }
}
