namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
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
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows(
            titleContains: "Test",
            processName: "notepad",
            processId: 1234,
            handle: 5678,
            visibleOnly: false,
            className: "Shell_TrayWnd",
            includeCloaked: true,
            includeToolWindows: true,
            includeShell: true);

        // Assert
        _mockWindowService.Verify(w => w.FindWindows(new WindowQuery
        {
            TitleContains = "Test",
            ProcessName = "notepad",
            ProcessId = 1234,
            Handle = new nint(5678),
            VisibleOnly = false,
            ClassName = "Shell_TrayWnd",
            IncludeCloaked = true,
            IncludeToolWindows = true,
            IncludeShell = true
        }), Times.Once);
    }

    [Fact]
    public void FindWindows_ReportsTheVisibleFrame_BesideTheWindowRect()
    {
        // Arrange - the window rect has the invisible resize borders (7 px left, right and bottom); set_window_bounds
        // takes the visible frame, so an agent that copies a rect must be able to copy that one
        var window = TestDataFactory.CreateWindowInfo(0x100, x: 93, y: 100, width: 814, height: 607) with
        {
            VisibleBounds = new WindowRect(100, 100, 800, 600)
        };
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([window, window with { Handle = 0x200, VisibleBounds = null }]);

        // Act
        var result = CreateSut().FindWindows();

        // Assert
        result[0].Bounds.Should().Be(new BoundsDto(93, 100, 814, 607));
        result[0].VisibleBounds.Should().Be(new BoundsDto(100, 100, 800, 600));
        result[1].VisibleBounds.Should().BeNull("DWM could not report it");
    }

    [Fact]
    public void FindWindows_ByDefault_AsksForVisibleUserFacingWindows()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows();

        // Assert - the default query keeps the user-facing filter
        _mockWindowService.Verify(w => w.FindWindows(It.Is<WindowQuery>(q =>
            q == new WindowQuery() && q.VisibleOnly && !q.SkipsUserFacingFilter)), Times.Once);
    }

    [Fact]
    public void FindWindows_MapsWindowIdentityToDto()
    {
        // Arrange - a UWP frame owned by another window, reported under the app's process
        var windowInfo = TestDataFactory.CreateWindowInfo(processName: "CalculatorApp", processId: 600) with
        {
            ClassName = "ApplicationFrameWindow",
            IsCloaked = true,
            OwnerHandle = new nint(777),
            HostProcessName = "ApplicationFrameHost",
            HostProcessId = 500
        };
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo> { windowInfo });
        var sut = CreateSut();

        // Act
        var dto = sut.FindWindows().Should().ContainSingle().Which;

        // Assert
        dto.ClassName.Should().Be("ApplicationFrameWindow");
        dto.IsCloaked.Should().BeTrue();
        dto.OwnerHandle.Should().Be(777);
        dto.ProcessName.Should().Be("CalculatorApp");
        dto.HostProcessName.Should().Be("ApplicationFrameHost");
        dto.HostProcessId.Should().Be(500);
    }

    [Fact]
    public void FindWindows_LeavesOwnerAndHostNull_ForAnOrdinaryWindow()
    {
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo> { TestDataFactory.CreateWindowInfo() with { ClassName = "Notepad" } });

        var dto = CreateSut().FindWindows().Should().ContainSingle().Which;

        dto.ClassName.Should().Be("Notepad");
        dto.IsCloaked.Should().BeFalse();
        dto.OwnerHandle.Should().BeNull();
        dto.HostProcessName.Should().BeNull();
        dto.HostProcessId.Should().BeNull();
    }

    [Fact]
    public void FindWindows_ReturnsEmptyList_WhenNoWindowsFound()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
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

        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
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
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows(handle: null);

        // Assert
        _mockWindowService.Verify(w => w.FindWindows(It.Is<WindowQuery>(q => q.Handle == null)), Times.Once);
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
    public void GetForegroundWindow_WhenNoWindowIsInTheForeground_IsANoForegroundWindowToolError()
    {
        // Arrange
        _mockWindowService.Setup(w => w.GetForegroundWindow())
            .Returns((WindowInfo?)null);
        var sut = CreateSut();

        // Act
        var act = () => sut.GetForegroundWindow();

        // Assert - roadmap M2.1: never a null result, which a client receives as empty content
        act.Should().Throw<WindowsAppManagerMcp.Infrastructure.ToolErrorException>()
            .Where(e => e.ErrorCode == nameof(WindowErrorCode.NoForegroundWindow) && e.Message.Contains("No window is in the foreground"));
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
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo> { windowInfo });
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result[0].State.Should().Be(expectedString);
    }

    #endregion

    #region Bounded titles (roadmap M3.F14)

    private static readonly string LongTitle = "Inbox - " + new string('a', 5000);

    private static string CutTitle => LongTitle[..(AppText.MaxLength - 1)] + "…";

    [Fact]
    public void FindWindows_CutsALongTitle_WithAnEllipsis()
    {
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns([TestDataFactory.CreateWindowInfo(title: LongTitle), TestDataFactory.CreateWindowInfo(title: "Untitled - Notepad")]);

        var result = CreateSut().FindWindows(titleContains: "Inbox");

        result.Select(w => w.Title).Should().Equal(CutTitle, "Untitled - Notepad");
    }

    [Fact]
    public void GetAllWindows_AndGetForegroundWindow_CutALongTitle()
    {
        var window = TestDataFactory.CreateWindowInfo(title: LongTitle);
        _mockWindowService.Setup(w => w.GetAllWindows(It.IsAny<bool>())).Returns([window]);
        _mockWindowService.Setup(w => w.GetForegroundWindow()).Returns(window);

        CreateSut().GetAllWindows().Single().Title.Should().Be(CutTitle);
        CreateSut().GetForegroundWindow().Title.Should().Be(CutTitle);
    }

    [Theory]
    [InlineData(WindowDetail.Compact)]
    [InlineData(WindowDetail.Standard)]
    [InlineData(WindowDetail.Full)]
    public void GetWindowInfo_CutsALongTitle_AtEveryDetail(WindowDetail detail)
    {
        var window = TestDataFactory.CreateWindowInfo(title: LongTitle);
        _mockWindowService.Setup(w => w.IsValidWindow(window.Handle)).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        _mockWindowService.Setup(w => w.GetWindowDetails(window.Handle))
            .Returns(new WindowDetails(window, false, 3, false, true, true, 96));

        var result = CreateSut().GetWindowInfo(handle: window.Handle, detail: detail);

        result.Title.Should().Be(CutTitle);
    }

    [Fact]
    public void GetOwnedWindows_CutsALongTitle()
    {
        var owner = TestDataFactory.CreateWindowInfo(handleValue: 0x100);
        var dialog = TestDataFactory.CreateWindowInfo(handleValue: 0x200, title: LongTitle);
        _mockWindowService.Setup(w => w.IsValidWindow(owner.Handle)).Returns(true);
        _mockWindowService.Setup(w => w.GetOwnedWindows(owner.Handle, false)).Returns([new OwnedWindowInfo(dialog, true, true)]);

        var result = CreateSut().GetOwnedWindows(handle: owner.Handle);

        result.Windows!.Single().Title.Should().Be(CutTitle);
    }

    #endregion
}
