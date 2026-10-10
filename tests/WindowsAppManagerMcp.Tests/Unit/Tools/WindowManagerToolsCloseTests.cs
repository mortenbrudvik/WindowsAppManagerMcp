using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// close_window and close_windows_batch at the tool layer (roadmap M1.7): force reaches the service, the
/// service's refusals come back with their error code, and a batch of more than 3 windows needs confirm.
/// The window service is a mock; the guard itself is tested in WindowServiceCloseTests.
/// </summary>
public class WindowManagerToolsCloseTests
{
    private readonly Mock<IWindowService> _windowService = new();

    public WindowManagerToolsCloseTests()
    {
        _windowService.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        _windowService.Setup(w => w.CloseWindowsBatch(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns((IReadOnlyList<nint> handles, bool _, int _) => handles.Select(WindowCloseResult.Sent).ToList());
    }

    private WindowManagerTools CreateSut() => new(_windowService.Object, Mock.Of<IForegroundService>());

    private static WindowCloseRequest[] Requests(params long[] handles) => handles.Select(h => new WindowCloseRequest(h)).ToArray();

    #region close_window

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseWindow_PassesForceToTheService(bool force)
    {
        // Arrange
        var handle = new nint(12345);
        _windowService.Setup(w => w.CloseWindow(handle, force)).Returns(WindowCloseResult.Sent(handle));

        // Act
        var result = CreateSut().CloseWindow(12345, force);

        // Assert
        result.Should().Be(new WindowOperationResult(Success: true));
        _windowService.Verify(w => w.CloseWindow(handle, force), Times.Once);
    }

    [Fact]
    public void CloseWindow_ByDefault_DoesNotForce()
    {
        // Arrange
        var handle = new nint(12345);
        _windowService.Setup(w => w.CloseWindow(handle, false)).Returns(WindowCloseResult.Sent(handle));

        // Act
        CreateSut().CloseWindow(12345);

        // Assert
        _windowService.Verify(w => w.CloseWindow(handle, false), Times.Once);
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
    }

    [Theory]
    [InlineData(WindowErrorCode.ProtectedWindow)]
    [InlineData(WindowErrorCode.AccessDenied)]
    [InlineData(WindowErrorCode.CloseFailed)]
    public void CloseWindow_Refused_ReturnsTheServiceErrorAndCode(WindowErrorCode code)
    {
        // Arrange
        var handle = new nint(12345);
        _windowService.Setup(w => w.CloseWindow(handle, false))
            .Returns(WindowCloseResult.Failed(handle, code, "why"));

        // Act
        var result = CreateSut().CloseWindow(12345);

        // Assert
        result.Should().Be(new WindowOperationResult(Success: false, Error: "why", ErrorCode: code.ToString()));
    }

    #endregion

    #region close_windows_batch

    [Fact]
    public void CloseWindowsBatch_MoreThanThreeWindowsWithoutConfirm_IsRefused_AndClosesNothing()
    {
        // Act
        var act = () => CreateSut().CloseWindowsBatch(Requests(1, 2, 3, 4));

        // Assert
        act.Should().Throw<ToolErrorException>()
            .Where(e => e.ErrorCode == nameof(WindowErrorCode.ConfirmationRequired))
            .WithMessage("*4 windows*confirm=true*Nothing was closed*");
        _windowService.Verify(w => w.CloseWindowsBatch(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Never);
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void CloseWindowsBatch_MoreThanThreeWindowsWithConfirm_ClosesThem()
    {
        // Act
        var result = CreateSut().CloseWindowsBatch(Requests(1, 2, 3, 4), delayBetweenMs: 0, confirm: true);

        // Assert
        result.TotalRequested.Should().Be(4);
        result.Succeeded.Should().Be(4);
        _windowService.Verify(w => w.CloseWindowsBatch(
            It.Is<IReadOnlyList<nint>>(h => h.SequenceEqual(new nint[] { 1, 2, 3, 4 })), false, 0), Times.Once);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CloseWindowsBatch_UpToThreeWindows_NeedsNoConfirm(int count)
    {
        // Act
        var result = CreateSut().CloseWindowsBatch(Requests(Enumerable.Range(1, count).Select(i => (long)i).ToArray()));

        // Assert
        result.Succeeded.Should().Be(count);
    }

    [Fact]
    public void CloseWindowsBatch_PassesForceToTheService()
    {
        // Act
        CreateSut().CloseWindowsBatch(Requests(1, 2), force: true);

        // Assert
        _windowService.Verify(w => w.CloseWindowsBatch(It.IsAny<IReadOnlyList<nint>>(), true, 50), Times.Once);
    }

    [Fact]
    public void CloseWindowsBatch_ReportsEachRefusalWithItsErrorCode()
    {
        // Arrange
        _windowService.Setup(w => w.CloseWindowsBatch(It.IsAny<IReadOnlyList<nint>>(), false, It.IsAny<int>()))
            .Returns(
            [
                WindowCloseResult.Sent(1),
                WindowCloseResult.Failed(2, WindowErrorCode.ProtectedWindow, "the desktop"),
                WindowCloseResult.Failed(3, WindowErrorCode.InvalidHandle, "Invalid window handle")
            ]);

        // Act
        var result = CreateSut().CloseWindowsBatch(Requests(1, 2, 3));

        // Assert
        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(2);
        result.Results.Should().Equal(
            new WindowResultItem(1, true),
            new WindowResultItem(2, false, Error: "the desktop", ErrorCode: "ProtectedWindow"),
            new WindowResultItem(3, false, Error: "Invalid window handle", ErrorCode: "InvalidHandle"));
    }

    #endregion
}
