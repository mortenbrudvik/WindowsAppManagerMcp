namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The target policy's check for window captures (roadmap M1.12), moved out of ScreenshotTools so that the capture tools
/// and the raw pixel reads of roadmap M6 share it: a denied window is TargetDeniedByPolicy, a window that cannot be checked
/// is refused while the policy denies anything, and a handle that is no window is left to the capture (InvalidHandle).
/// </summary>
public class CaptureTargetPolicyTests
{
    private static readonly nint Handle = new(0x300);

    private readonly Mock<IWindowService> _windows = new();

    private static TargetPolicy Policy(string? deny = null) =>
        new(TargetPolicySettings.FromEnvironment(name => name == TargetPolicySettings.DenyVariable ? deny : null));

    private CaptureTargetPolicy CreateSut(string processName, string? deny = null)
    {
        _windows.Setup(w => w.IsValidWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowInfo(Handle))
            .Returns(TestDataFactory.CreateWindowInfo((int)Handle, processName + " window", processName, 3001));
        return new CaptureTargetPolicy(_windows.Object, Policy(deny));
    }

    [Fact]
    public void Check_OfADeniedProcess_IsTargetDeniedByPolicy_WithTheCaptureToolsMessage()
    {
        // Act
        var refusal = CreateSut("KeePass").Check(Handle);

        // Assert - the message capture_window has always given
        refusal.Should().NotBeNull();
        refusal!.Code.Should().Be(ScreenshotErrorCode.TargetDeniedByPolicy);
        refusal.Message.Should().StartWith("The window is refused by the server's target policy: ")
            .And.Contain("KeePass")
            .And.EndWith(". Nothing was captured. The policy is set by the user in the server's environment; no tool argument changes it.");
    }

    [Fact]
    public void Check_OfAnAllowedProcess_IsNull()
    {
        CreateSut("notepad").Check(Handle).Should().BeNull();
    }

    [Fact]
    public void Check_OfAHandleThatIsNoWindow_IsLeftToTheCapture()
    {
        // Arrange
        var sut = CreateSut("KeePass");
        _windows.Setup(w => w.GetWindowInfo(Handle)).Returns((WindowInfo?)null);
        _windows.Setup(w => w.IsValidWindow(Handle)).Returns(false);

        // Act + Assert
        sut.Check(Handle).Should().BeNull();
    }

    [Fact]
    public void Check_OfAWindowThatCannotBeRead_IsRefused_WhileThePolicyDeniesAnything()
    {
        // Arrange - the window exists, but its information (process included) cannot be read
        var sut = CreateSut("notepad");
        _windows.Setup(w => w.GetWindowInfo(Handle)).Returns((WindowInfo?)null);

        // Act
        var refusal = sut.Check(Handle);

        // Assert
        refusal!.Code.Should().Be(ScreenshotErrorCode.TargetDeniedByPolicy);
        refusal.Message.Should().Contain("cannot be read");
    }

    [Fact]
    public void Check_OfAWindowWhoseProcessCannotBeNamed_IsAllowed_WhenThePolicyDeniesNothing()
    {
        CreateSut(WindowInfo.UnknownProcessName, deny: "none").Check(Handle).Should().BeNull();
    }

    [Fact]
    public void Verdict_OfNoWindowInformation_IsUnreadable_WhileThePolicyDeniesAnything_AndAllowedOtherwise()
    {
        new CaptureTargetPolicy(_windows.Object, Policy()).Verdict(null).Should().Be(TargetPolicyVerdict.Unreadable);
        new CaptureTargetPolicy(_windows.Object, Policy(deny: "none")).Verdict(null).Should().Be(TargetPolicyVerdict.Allow);
    }
}
