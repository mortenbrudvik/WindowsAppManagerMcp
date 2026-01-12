namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for WindowFinderTools.
/// Tests the MCP tool layer that exposes window finding functionality.
/// </summary>
public class WindowFinderToolsTests
{
    private readonly Mock<IWindowService> _mockWindowService;

    public WindowFinderToolsTests()
    {
        _mockWindowService = new Mock<IWindowService>();
    }

    private WindowFinderTools CreateSut() => new WindowFinderTools(_mockWindowService.Object);

    #region FindWindows Tests

    [Fact]
    public void FindWindows_CallsServiceWithCorrectParameters()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<nint?>(),
            It.IsAny<bool>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows(
            titleContains: "Test",
            processName: "notepad",
            processId: 1234,
            handle: 5678,
            visibleOnly: false);

        // Assert
        _mockWindowService.Verify(w => w.FindWindows(
            "Test",
            "notepad",
            1234,
            new nint(5678),
            false), Times.Once);
    }

    [Fact]
    public void FindWindows_ReturnsEmptyList_WhenNoWindowsFound()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<nint?>(),
            It.IsAny<bool>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void FindWindows_MapsWindowInfoToDto()
    {
        // Arrange
        var windowInfo = TestDataFactory.CreateWindowInfo(
            handleValue: 12345,
            title: "Test Window",
            processName: "testapp",
            processId: 999,
            x: 100, y: 200,
            width: 800, height: 600,
            state: WindowState.Normal,
            isVisible: true,
            monitorIndex: 1);

        _mockWindowService.Setup(w => w.FindWindows(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<nint?>(),
            It.IsAny<bool>()))
            .Returns(new List<WindowInfo> { windowInfo });
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result.Should().HaveCount(1);
        var dto = result[0];
        dto.Handle.Should().Be(12345);
        dto.Title.Should().Be("Test Window");
        dto.ProcessName.Should().Be("testapp");
        dto.ProcessId.Should().Be(999);
        dto.Bounds.X.Should().Be(100);
        dto.Bounds.Y.Should().Be(200);
        dto.Bounds.Width.Should().Be(800);
        dto.Bounds.Height.Should().Be(600);
        dto.State.Should().Be("normal");
        dto.IsVisible.Should().BeTrue();
        dto.MonitorIndex.Should().Be(1);
    }

    [Fact]
    public void FindWindows_ConvertsNullHandleCorrectly()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<nint?>(),
            It.IsAny<bool>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows(handle: null);

        // Assert
        _mockWindowService.Verify(w => w.FindWindows(
            null, null, null, null, true), Times.Once);
    }

    #endregion

    #region GetAllWindows Tests

    [Fact]
    public void GetAllWindows_CallsServiceWithIncludeMinimized()
    {
        // Arrange
        _mockWindowService.Setup(w => w.GetAllWindows(It.IsAny<bool>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.GetAllWindows(includeMinimized: false);

        // Assert
        _mockWindowService.Verify(w => w.GetAllWindows(false), Times.Once);
    }

    [Fact]
    public void GetAllWindows_DefaultsToIncludeMinimized()
    {
        // Arrange
        _mockWindowService.Setup(w => w.GetAllWindows(It.IsAny<bool>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.GetAllWindows();

        // Assert
        _mockWindowService.Verify(w => w.GetAllWindows(true), Times.Once);
    }

    [Fact]
    public void GetAllWindows_ReturnsMultipleWindows()
    {
        // Arrange
        var windows = TestDataFactory.CreateTypicalWindowSet();
        _mockWindowService.Setup(w => w.GetAllWindows(It.IsAny<bool>()))
            .Returns(windows);
        var sut = CreateSut();

        // Act
        var result = sut.GetAllWindows();

        // Assert
        result.Should().HaveCount(windows.Count);
    }

    #endregion

    #region GetForegroundWindow Tests

    [Fact]
    public void GetForegroundWindow_ReturnsNull_WhenNoForegroundWindow()
    {
        // Arrange
        _mockWindowService.Setup(w => w.GetForegroundWindow())
            .Returns((WindowInfo?)null);
        var sut = CreateSut();

        // Act
        var result = sut.GetForegroundWindow();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetForegroundWindow_ReturnsDto_WhenWindowExists()
    {
        // Arrange
        var windowInfo = TestDataFactory.CreateWindowInfo(title: "Foreground Window");
        _mockWindowService.Setup(w => w.GetForegroundWindow())
            .Returns(windowInfo);
        var sut = CreateSut();

        // Act
        var result = sut.GetForegroundWindow();

        // Assert
        result.Should().NotBeNull();
        result!.Title.Should().Be("Foreground Window");
    }

    #endregion

    #region WindowState Mapping Tests

    [Theory]
    [InlineData(WindowState.Normal, "normal")]
    [InlineData(WindowState.Minimized, "minimized")]
    [InlineData(WindowState.Maximized, "maximized")]
    public void FindWindows_MapsWindowStateToLowerCase(WindowState state, string expectedString)
    {
        // Arrange
        var windowInfo = TestDataFactory.CreateWindowInfo(state: state);
        _mockWindowService.Setup(w => w.FindWindows(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<nint?>(),
            It.IsAny<bool>()))
            .Returns(new List<WindowInfo> { windowInfo });
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result[0].State.Should().Be(expectedString);
    }

    #endregion
}
