namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// get_active_popups' arguments (roadmap M3.5): the window it lists, the timeout, and the refusals of a window argument. The
/// enumeration itself is PopupServiceTests'.
/// </summary>
public class PopupToolsTests
{
    private readonly Mock<IPopupService> _popups = new();
    private readonly Mock<IWindowService> _windows = new();
    private nint? _window;
    private TimeSpan? _timeout;

    public PopupToolsTests()
    {
        _popups.Setup(p => p.GetActivePopupsAsync(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Callback((nint window, TimeSpan timeout, CancellationToken _, TimeSpan? _) => (_window, _timeout) = (window, timeout))
            .ReturnsAsync((nint window, TimeSpan _, CancellationToken _, TimeSpan? _) => new ActivePopupsResult(true, (long)window, "W", true, [], 1));
    }

    private PopupTools CreateSut() => new(_popups.Object, _windows.Object);

    [Fact]
    public async Task GetActivePopups_WithoutAWindow_AsksForTheAppInFront()
    {
        await CreateSut().GetActivePopups();

        _window.Should().Be(nint.Zero);
        _timeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetActivePopups_WithASelector_ListsThePopupsOfTheWindowItResolvesTo()
    {
        _windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo(handleValue: 0x4400)));

        await CreateSut().GetActivePopups(window: new WindowSelector { ProcessName = "notepad" });

        _window.Should().Be((nint)0x4400);
    }

    [Theory]
    [InlineData(10, 1000)]
    [InlineData(99_000, 30_000)]
    public async Task GetActivePopups_ClampsTheTimeout(int timeoutMs, int expectedMs)
    {
        await CreateSut().GetActivePopups(handle: 0x4400, timeoutMs: timeoutMs);

        _timeout.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public async Task GetActivePopups_WithHandleAndWindow_IsRefused()
    {
        var call = () => CreateSut().GetActivePopups(handle: 0x4400, window: new WindowSelector { ProcessName = "notepad" });

        (await call.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _popups.Verify(p => p.GetActivePopupsAsync(It.IsAny<nint>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()), Times.Never);
    }
}
