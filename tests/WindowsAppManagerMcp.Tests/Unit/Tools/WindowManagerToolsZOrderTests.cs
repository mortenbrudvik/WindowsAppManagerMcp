using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// set_window_zorder (roadmap M6.7, owner decision D14): the tool checks its arguments, refuses a window the policy denies
/// before anything is sent, and reports isTopmost and zOrder read back. The window service is a mock.
/// </summary>
public class WindowManagerToolsZOrderTests
{
    private static readonly nint Handle = 0x5100;

    private readonly Mock<IWindowService> _windows = new();

    public WindowManagerToolsZOrderTests()
    {
        _windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.EvaluateTargetPolicy(It.IsAny<nint>())).Returns(TargetPolicyVerdict.Allow);
        _windows.Setup(w => w.SetWindowZOrder(It.IsAny<nint>(), It.IsAny<ZOrderMode>(), It.IsAny<nint?>()))
            .Returns((nint h, ZOrderMode _, nint? _) => WindowChangeResult.Done(h));
        _windows.Setup(w => w.GetWindowDetails(Handle)).Returns(new WindowDetails(TestDataFactory.CreateWindowInfo(handleValue: (int)Handle),
            IsForeground: false, ZOrder: 3, IsTopmost: true, IsEnabled: true, IsResponding: true, Dpi: 96));
    }

    private WindowManagerTools CreateSut() => new(_windows.Object, Mock.Of<IForegroundService>());

    [Fact]
    public async Task SetWindowZOrder_ChangesTheZOrder_AndReportsItReadBack()
    {
        // Act
        var result = await CreateSut().SetWindowZOrder(ZOrderMode.Topmost, handle: Handle);

        // Assert
        result.Should().Be(new ZOrderResult(true, Handle, IsTopmost: true, ZOrder: 3));
        _windows.Verify(w => w.SetWindowZOrder(Handle, ZOrderMode.Topmost, null), Times.Once);
    }

    [Fact]
    public async Task SetWindowZOrder_Above_PassesTheOtherWindow()
    {
        // Act
        await CreateSut().SetWindowZOrder(ZOrderMode.Above, handle: Handle, aboveHandle: 0x6200);

        // Assert
        _windows.Verify(w => w.SetWindowZOrder(Handle, ZOrderMode.Above, (nint)0x6200), Times.Once);
    }

    [Theory]
    [InlineData(ZOrderMode.Above, null, "aboveHandle")]
    [InlineData(ZOrderMode.Top, 0x6200L, "only with mode 'above'")]
    [InlineData(ZOrderMode.Above, 0x5100L, "itself")]
    public async Task SetWindowZOrder_WithAboveHandleMissingOrMisplaced_IsInvalidArguments(ZOrderMode mode, long? aboveHandle, string message)
    {
        // Act
        var act = () => CreateSut().SetWindowZOrder(mode, handle: Handle, aboveHandle: aboveHandle);

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain(message);
        _windows.Verify(w => w.SetWindowZOrder(It.IsAny<nint>(), It.IsAny<ZOrderMode>(), It.IsAny<nint?>()), Times.Never);
    }

    [Fact]
    public async Task SetWindowZOrder_OnAWindowThePolicyDenies_SendsNothing()
    {
        // Arrange
        _windows.Setup(w => w.EvaluateTargetPolicy(Handle)).Returns(TargetPolicyVerdict.Deny("KeePass", "KeePass is on the deny list"));

        // Act
        var result = await CreateSut().SetWindowZOrder(ZOrderMode.Bottom, handle: Handle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.TargetDeniedByPolicy));
        _windows.Verify(w => w.SetWindowZOrder(It.IsAny<nint>(), It.IsAny<ZOrderMode>(), It.IsAny<nint?>()), Times.Never);
    }

    [Fact]
    public async Task SetWindowZOrder_WhenTheAppDoesNotAnswer_IsNotResponding()
    {
        // Arrange
        _windows.Setup(w => w.SetWindowZOrder(Handle, ZOrderMode.Top, null)).Throws(new WindowNotRespondingException(Handle, "The app is hung."));

        // Act
        var result = await CreateSut().SetWindowZOrder(ZOrderMode.Top, handle: Handle);

        // Assert
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.NotResponding));
        result.Error.Should().Be("The app is hung.");
    }

    [Fact]
    public async Task SetWindowZOrder_ThatWasNotApplied_ReportsTheCode_AndTheWindowAsItIs()
    {
        // Arrange
        _windows.Setup(w => w.SetWindowZOrder(Handle, ZOrderMode.NoTopmost, null))
            .Returns(WindowChangeResult.Failed(Handle, WindowErrorCode.ZOrderNotApplied, "still topmost"));

        // Act
        var result = await CreateSut().SetWindowZOrder(ZOrderMode.NoTopmost, handle: Handle);

        // Assert
        result.Should().Be(new ZOrderResult(false, Handle, IsTopmost: true, ZOrder: 3, Error: "still topmost",
            ErrorCode: nameof(WindowErrorCode.ZOrderNotApplied)));
    }
}
