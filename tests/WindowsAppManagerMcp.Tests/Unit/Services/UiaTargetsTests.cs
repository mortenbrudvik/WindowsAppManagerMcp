namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The target policy's check of a UI Automation read (<see cref="UiaTargets.ReadVerdict"/>): the window's process and, for a
/// UWP frame, the app it hosts; failing closed when the process cannot be named. Every window is a fake on a mocked
/// <see cref="INativeWindowWrapper"/>; nothing is read.
/// </summary>
public class UiaTargetsTests
{
    private static readonly nint KeePass = 0x300;
    private static readonly nint Notepad = 0x200;
    private static readonly nint Frame = 0x400;
    private static readonly nint CoreWindow = 0x401;

    private readonly Mock<INativeWindowWrapper> _windows = new();

    public UiaTargetsTests()
    {
        Window(KeePass, "WindowsForms10.Window.8.app.0.1", 3001, "KeePass");
        Window(Notepad, "Notepad", 2001, "notepad");
    }

    private static TargetPolicy Policy(string? deny = null, string? allow = null) =>
        new(TargetPolicySettings.FromEnvironment(name => name switch
        {
            TargetPolicySettings.DenyVariable => deny,
            TargetPolicySettings.AllowVariable => allow,
            _ => null
        }));

    private void Window(nint handle, string className, uint processId, string? processName)
    {
        _windows.Setup(w => w.IsWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetClassName(handle)).Returns(className);
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns(processId);
        _windows.Setup(w => w.GetProcessName(processId)).Returns(processName);
    }

    /// <summary>A UWP frame of ApplicationFrameHost that hosts the CoreWindow of <paramref name="app"/>.</summary>
    private void UwpFrame(string app)
    {
        Window(Frame, "ApplicationFrameWindow", 500, "ApplicationFrameHost");
        Window(CoreWindow, "Windows.UI.Core.CoreWindow", 600, app);
        _windows.Setup(w => w.EnumChildWindows(Frame, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> callback) => callback(CoreWindow));
    }

    [Fact]
    public void ReadVerdict_WindowOfADeniedApp_IsRefused_AndOfAnotherAppAllowed()
    {
        // Act
        var keePass = UiaTargets.ReadVerdict(_windows.Object, Policy(), KeePass);
        var notepad = UiaTargets.ReadVerdict(_windows.Object, Policy(), Notepad);

        // Assert
        keePass.Allowed.Should().BeFalse();
        keePass.ProcessName.Should().Be("KeePass");
        notepad.Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("SecHealthUI")]
    [InlineData("CHXSmartScreen")]
    public void ReadVerdict_UwpFrameOfTheWindowsSecurityAppOrTheSmartScreenPrompt_IsRefusedByDefault(string app)
    {
        // Arrange
        UwpFrame(app);

        // Act
        var verdict = UiaTargets.ReadVerdict(_windows.Object, Policy(), Frame);

        // Assert - the frame belongs to ApplicationFrameHost, which no list names: the app it hosts decides
        verdict.Allowed.Should().BeFalse();
        verdict.ProcessName.Should().Be(app);
    }

    /// <summary>
    /// A window of ApplicationFrameHost of another class than ApplicationFrameWindow that hosts the CoreWindow of
    /// <paramref name="app"/>, as a prompt may come up (roadmap M3.F7).
    /// </summary>
    private void FrameOfAnotherClass(string app)
    {
        UwpFrame(app);
        Window(Frame, "PromptFrameWindow", 500, "ApplicationFrameHost");
    }

    [Fact]
    public void ReadVerdict_FrameHostWindowOfAnotherClassThatHostsADeniedPrompt_IsRefusedByDefault()
    {
        // Arrange
        FrameOfAnotherClass("CredDialogHost");

        // Act
        var verdict = UiaTargets.ReadVerdict(_windows.Object, Policy(), Frame);

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.ProcessName.Should().Be("CredDialogHost");
    }

    [Fact]
    public void NotRespondingReason_OfAFrameHostWindowOfAnotherClassWhoseAppIsHung_IsGiven()
    {
        // Arrange
        FrameOfAnotherClass("CredDialogHost");
        _windows.Setup(w => w.RespondsWithin(Frame, It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.RespondsWithin(CoreWindow, It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.IsHungAppWindow(CoreWindow)).Returns(true);

        // Act & Assert
        UiaTargets.NotRespondingReason(_windows.Object, Frame).Should().Contain("not responding");
    }

    [Fact]
    public void ReadVerdict_TheSecurityPromptOfExplorer_IsRefusedByDefault()
    {
        // Arrange - Windows' "Open File - Security Warning" in explorer.exe, recognized by its controls (roadmap M3.F5)
        var prompt = new nint(0x900);
        Window(prompt, "#32770", 5001, "explorer");
        SecurityPromptWindows.Add(_windows, prompt);
        var policy = new TargetPolicy(TargetPolicySettings.Default, _windows.Object);

        // Act
        var verdict = UiaTargets.ReadVerdict(_windows.Object, policy, prompt);

        // Assert
        verdict.Allowed.Should().BeFalse();
        UiaTargets.PolicyRefusal(_windows.Object, policy, prompt).Should().Contain("Open File - Security Warning")
            .And.Contain("Nothing was read");
        UiaTargets.ReadVerdict(_windows.Object, policy, Notepad).Allowed.Should().BeTrue();
    }

    [Fact]
    public void ReadVerdict_WindowWhoseProcessCannotBeNamed_FailsClosed_WhileThePolicyDeniesAnything()
    {
        // Arrange - the process name cannot be read
        Window(0x500, "Vault", 5001, processName: null);

        // Act & Assert
        UiaTargets.ReadVerdict(_windows.Object, Policy(), 0x500).Should().Match<TargetPolicyVerdict>(v =>
            !v.Allowed && v.Reason!.Contains("cannot be read"));
        UiaTargets.ReadVerdict(_windows.Object, Policy(deny: "none"), 0x500).Allowed.Should().BeTrue(
            "a policy that denies nothing has nothing to check");
    }

    [Fact]
    public void ReadVerdict_WithAnAllowList_RefusesAnUnlistedApp()
    {
        UiaTargets.ReadVerdict(_windows.Object, Policy(allow: "KeePass"), Notepad).Allowed.Should().BeFalse();
        UiaTargets.ReadVerdict(_windows.Object, Policy(deny: "none", allow: "notepad"), Notepad).Allowed.Should().BeTrue();
    }

    [Fact]
    public void ReadVerdict_OfAHandleThatIsNoWindow_IsLeftToTheRead()
    {
        // The read reports WindowNotFound (or the ref's window closed): not the policy's verdict
        UiaTargets.ReadVerdict(_windows.Object, Policy(), 0x9999).Allowed.Should().BeTrue();
    }

    [Fact]
    public void ReadVerdict_WithoutAPolicy_ReadsNothing()
    {
        // Act
        var verdict = UiaTargets.ReadVerdict(_windows.Object, policy: null, KeePass);

        // Assert
        verdict.Allowed.Should().BeTrue();
        _windows.Verify(w => w.GetProcessName(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public void ReadVerdict_ACheckThatThrows_FailsClosed()
    {
        _windows.Setup(w => w.GetWindowThreadProcessId(KeePass)).Throws(new InvalidOperationException("no access"));

        UiaTargets.ReadVerdict(_windows.Object, Policy(), KeePass).Allowed.Should().BeFalse();
    }

    [Fact]
    public void NotRespondingReason_OfAUwpFrameWhoseAppIsHung_IsGiven_ThoughTheFrameHostAnswers()
    {
        // Arrange - the frame belongs to ApplicationFrameHost, which answers; the app's UI thread, behind its CoreWindow, is hung
        UwpFrame("CalculatorApp");
        _windows.Setup(w => w.RespondsWithin(Frame, It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.RespondsWithin(CoreWindow, It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.IsHungAppWindow(CoreWindow)).Returns(true);

        // Act
        var reason = UiaTargets.NotRespondingReason(_windows.Object, Frame);

        // Assert
        reason.Should().Contain("not responding");
    }

    [Fact]
    public void NotRespondingReason_OfAUwpFrameWhoseAppDoesNotAnswer_IsGiven()
    {
        // Arrange
        UwpFrame("CalculatorApp");
        _windows.Setup(w => w.RespondsWithin(Frame, It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.RespondsWithin(CoreWindow, It.IsAny<uint>())).Returns(false);

        // Act
        var reason = UiaTargets.NotRespondingReason(_windows.Object, Frame);

        // Assert
        reason.Should().Contain($"did not answer a message within {UiaTargets.RespondTimeoutMs} ms");
    }

    [Fact]
    public void NotRespondingReason_OfAUwpFrameWithoutItsCoreWindow_IsNone()
    {
        // Arrange - a minimized or suspended app's CoreWindow is not in the frame; the frame itself answers
        Window(Frame, "ApplicationFrameWindow", 500, "ApplicationFrameHost");
        _windows.Setup(w => w.RespondsWithin(Frame, It.IsAny<uint>())).Returns(true);

        // Act & Assert
        UiaTargets.NotRespondingReason(_windows.Object, Frame).Should().BeNull();
    }

    [Fact]
    public void NotRespondingReason_OfAnOrdinaryWindow_ListsNoChildren()
    {
        _windows.Setup(w => w.RespondsWithin(Notepad, It.IsAny<uint>())).Returns(true);

        UiaTargets.NotRespondingReason(_windows.Object, Notepad).Should().BeNull();
        _windows.Verify(w => w.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Fact]
    public void PolicyRefusal_NamesTheWindowAndTheReason_AndSaysNothingWasRead()
    {
        // Act
        var refusal = UiaTargets.PolicyRefusal(_windows.Object, Policy(), KeePass);

        // Assert
        refusal.Should().Contain($"Window {(long)KeePass}").And.Contain("'KeePass' is on the server's default deny list")
            .And.Contain("Nothing was read");
        UiaTargets.PolicyRefusal(_windows.Object, Policy(), Notepad).Should().BeNull();
    }
}
