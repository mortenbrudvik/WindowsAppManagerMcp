using System.Diagnostics;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for WindowService.
/// Tests T2.1-T2.12 from BACKLOG-TECHNICAL.md.
///
/// Note: WindowService uses P/Invoke for window management.
/// Read-only tests (enumerating and querying windows) use the real NativeWindowWrapper to
/// verify actual Windows API behavior, wrapped in <see cref="ReadOnlyNativeWindowWrapper"/>: it
/// throws instead of moving, resizing, showing, focusing or closing a window, so even a test that
/// calls a mutating method by mistake cannot change the desktop. Tests of methods that change
/// windows use a mocked wrapper.
/// </summary>
public class WindowServiceTests
{
    private readonly Mock<IMonitorService> _mockMonitorService;

    public WindowServiceTests()
    {
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
    }

    /// <summary>
    /// A WindowService over the real windows of the desktop that can only read them: every window
    /// change throws (see <see cref="ReadOnlyNativeWindowWrapper"/>).
    /// </summary>
    private WindowService CreateReadOnlySut() => new WindowService(_mockMonitorService.Object, new ReadOnlyNativeWindowWrapper());

    private const int InvalidHandleValue = 99999999;

    /// <summary>
    /// Creates a WindowService whose native wrapper reports every handle as invalid. With the
    /// real wrapper, a window that happened to own the handle would be moved, minimized or focused.
    /// </summary>
    private (WindowService Sut, Mock<INativeWindowWrapper> Native) CreateSutWithNoValidWindows()
    {
        var native = new Mock<INativeWindowWrapper>();
        native.Setup(n => n.IsWindow(It.IsAny<nint>())).Returns(false);
        return (new WindowService(_mockMonitorService.Object, native.Object), native);
    }

    private static void VerifyNoWindowWasChanged(Mock<INativeWindowWrapper> native)
    {
        native.Verify(n => n.SetWindowPos(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()), Times.Never);
        native.Verify(n => n.ShowWindow(It.IsAny<nint>(), It.IsAny<int>()), Times.Never);
        native.Verify(n => n.SetForegroundWindow(It.IsAny<nint>()), Times.Never);
        native.Verify(n => n.BringWindowToTop(It.IsAny<nint>()), Times.Never);
        native.Verify(n => n.PostMessage(It.IsAny<nint>(), It.IsAny<uint>(),
            It.IsAny<nint>(), It.IsAny<nint>()), Times.Never);
    }

    #region T2.1: GetAllWindows Tests

    [Fact]
    public void GetAllWindows_ReturnsNonEmptyList()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.GetAllWindows(includeMinimized: true);

        // Assert
        result.Should().NotBeEmpty("There should be at least some visible windows on the system");
    }

    /// <summary>
    /// T2.2.3: Test GetAllWindows with includeMinimized: false excludes minimized windows.
    /// Uses mock to ensure predictable test data with minimized windows.
    /// </summary>
    [Fact]
    public void GetAllWindows_WithIncludeMinimizedFalse_ExcludesMinimizedWindows()
    {
        // Arrange - Create test windows with one minimized
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Normal Window", "app1", 1001, state: WindowState.Normal),
            TestDataFactory.CreateWindowInfo(1002, "Minimized Window", "app2", 1002, state: WindowState.Minimized),
            TestDataFactory.CreateWindowInfo(1003, "Maximized Window", "app3", 1003, state: WindowState.Maximized)
        };
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper(testWindows);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act
        var result = sut.GetAllWindows(includeMinimized: false);

        // Assert - Should exclude the minimized window
        result.Should().HaveCount(2);
        result.Should().NotContain(w => w.State == WindowState.Minimized);
        result.Should().Contain(w => w.Title == "Normal Window");
        result.Should().Contain(w => w.Title == "Maximized Window");
    }

    [Fact]
    public void GetAllWindows_ReturnsWindowsWithValidHandles()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.GetAllWindows(includeMinimized: true);

        // Assert
        result.Should().OnlyContain(w => w.Handle != nint.Zero);
    }

    [Fact]
    public void GetAllWindows_ReturnsWindowsWithNonEmptyTitles()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.GetAllWindows(includeMinimized: true);

        // Assert
        // All returned windows should have non-empty titles (the service filters these)
        result.Should().OnlyContain(w => !string.IsNullOrEmpty(w.Title));
    }

    [Fact]
    public void GetAllWindows_ReturnsWindowsWithProcessInfo()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.GetAllWindows(includeMinimized: true);

        // Assert
        result.Should().OnlyContain(w => w.ProcessId > 0);
    }

    #endregion

    #region T2.2: FindWindows Tests

    [Fact]
    public void FindWindows_WithNoFilters_ReturnsWindows()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result.Should().NotBeEmpty();
    }

    /// <summary>
    /// T2.3.1: Test FindWindows with titleContains filter.
    /// Uses mock to ensure predictable test data with specific titles.
    /// </summary>
    [Fact]
    public void FindWindows_WithTitleContains_FiltersCorrectly()
    {
        // Arrange - Create test windows with distinctive titles
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Document.txt - Notepad", "notepad", 1001),
            TestDataFactory.CreateWindowInfo(1002, "Google Chrome - Search", "chrome", 1002),
            TestDataFactory.CreateWindowInfo(1003, "Document.docx - Word", "WINWORD", 1003),
            TestDataFactory.CreateWindowInfo(1004, "Terminal", "WindowsTerminal", 1004)
        };
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper(testWindows);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act - Search for windows with "Document" in title
        var result = sut.FindWindows(titleContains: "Document");

        // Assert - Should find windows with "Document" in title
        result.Should().HaveCount(2);
        result.Should().OnlyContain(w => w.Title.Contains("Document", StringComparison.OrdinalIgnoreCase));
        result.Should().Contain(w => w.Title.Contains("Notepad"));
        result.Should().Contain(w => w.Title.Contains("Word"));
    }

    [Fact]
    public void FindWindows_WithProcessName_UsesTheProcessNamesFromTheNativeWrapper()
    {
        // Arrange - the names come from the mocked wrapper, not from whichever real processes
        // happen to own the fake process IDs
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Document.txt - Notepad", "notepad", 5001),
            TestDataFactory.CreateWindowInfo(1002, "Google Chrome - Search", "chrome", 5002)
        };
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper(testWindows);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act
        var result = sut.FindWindows(processName: "notepad");

        // Assert
        result.Should().ContainSingle().Which.ProcessName.Should().Be("notepad");
    }

    [Fact]
    public void GetWindowInfo_WhenTheProcessNameCannotBeRead_ReportsUnknown()
    {
        // Arrange - the process exited or cannot be opened
        var window = TestDataFactory.CreateWindowInfo(1001, "Orphaned Window", "gone", 5001);
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper([window]);
        mockNativeWrapper.Setup(n => n.GetProcessName(5001u)).Returns((string?)null);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act
        var result = sut.GetWindowInfo(window.Handle);

        // Assert
        result.Should().NotBeNull();
        result!.ProcessName.Should().Be("Unknown");
    }

    [Fact]
    public void ReadOnlyWrapper_MakesWindowChangesThrowBeforeTheyReachTheWrapper()
    {
        // Arrange - the read-only wrapper over a mock that reports a valid window, the situation
        // in which a read-only test calling MoveWindow by mistake would move a real window
        var window = TestDataFactory.CreateWindowInfo(1001, "Some Window", "app", 5001);
        var inner = TestDataFactory.CreateMockNativeWindowWrapper([window]);
        var sut = new WindowService(_mockMonitorService.Object, new ReadOnlyNativeWindowWrapper(inner.Object));

        // Act
        var act = () => sut.MoveWindow(window.Handle, 10, 10);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("SetWindowPos*read-only*");
        VerifyNoWindowWasChanged(inner);
    }

    [Fact]
    public void FindWindows_WithProcessName_FiltersCorrectly()
    {
        // Arrange
        var sut = CreateReadOnlySut();
        // Get all windows first and use a process name that we know has windows
        var allWindows = sut.GetAllWindows(includeMinimized: true);
        if (!allWindows.Any()) return; // Skip if no windows
        var targetProcessName = allWindows.First().ProcessName;

        // Act
        var result = sut.FindWindows(processName: targetProcessName);

        // Assert - Should only contain windows matching the process name filter
        result.Should().NotBeEmpty();
        result.Should().OnlyContain(w =>
            w.ProcessName.Contains(targetProcessName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// T2.3.3: Test FindWindows with processId filter.
    /// Uses mock to ensure predictable test data with specific process IDs.
    /// </summary>
    [Fact]
    public void FindWindows_WithProcessId_FiltersCorrectly()
    {
        // Arrange - Create test windows with different process IDs
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Window 1", "app1", 5001),
            TestDataFactory.CreateWindowInfo(1002, "Window 2", "app2", 5002),
            TestDataFactory.CreateWindowInfo(1003, "Window 3", "app1", 5001), // Same process as Window 1
            TestDataFactory.CreateWindowInfo(1004, "Window 4", "app3", 5003)
        };
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper(testWindows);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act - Search for windows with specific process ID
        var result = sut.FindWindows(processId: 5001);

        // Assert - Should find windows belonging to process 5001
        result.Should().HaveCount(2);
        result.Should().OnlyContain(w => w.ProcessId == 5001);
        result.Should().Contain(w => w.Title == "Window 1");
        result.Should().Contain(w => w.Title == "Window 3");
    }

    [Fact]
    public void FindWindows_WithNonMatchingFilter_ReturnsEmpty()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.FindWindows(processName: "xyznonexistentprocess123");

        // Assert
        result.Should().BeEmpty();
    }

    /// <summary>
    /// T2.3.5: Test FindWindows with multiple filters uses AND logic.
    /// All filters must match for a window to be included in results.
    /// </summary>
    [Fact]
    public void FindWindows_WithMultipleFilters_UsesAndLogic()
    {
        // Arrange - Create windows where some match partial filters
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Project - Notepad", "notepad", 5001),     // Matches title AND processId
            TestDataFactory.CreateWindowInfo(1002, "Project - Code", "code", 5002),           // Matches title only
            TestDataFactory.CreateWindowInfo(1003, "Document - Notepad", "notepad", 5001),   // Matches processId only
            TestDataFactory.CreateWindowInfo(1004, "Other Window", "other", 5003)             // Matches nothing
        };
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper(testWindows);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act - Search with both title and processId filters
        var result = sut.FindWindows(titleContains: "Project", processId: 5001);

        // Assert - Should only find windows matching BOTH filters
        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Project - Notepad");
        result[0].ProcessId.Should().Be(5001);
    }

    [Fact]
    public void FindWindows_WithHandle_ReturnsMatchingWindow()
    {
        // Arrange
        var sut = CreateReadOnlySut();
        var allWindows = sut.GetAllWindows(includeMinimized: true);
        if (!allWindows.Any()) return; // Skip if no windows
        var targetHandle = allWindows.First().Handle;

        // Act
        var result = sut.FindWindows(handle: targetHandle);

        // Assert
        result.Should().HaveCount(1);
        result[0].Handle.Should().Be(targetHandle);
    }

    /// <summary>
    /// T2.3.6: Test FindWindows with visibleOnly: false includes invisible windows.
    /// When visibleOnly is false, windows with IsVisible=false should be included.
    /// </summary>
    [Fact]
    public void FindWindows_WithVisibleOnlyFalse_IncludesInvisibleWindows()
    {
        // Arrange - Create windows with mixed visibility
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Visible Window 1", "app1", 5001, isVisible: true),
            TestDataFactory.CreateWindowInfo(1002, "Hidden Window", "app2", 5002, isVisible: false),
            TestDataFactory.CreateWindowInfo(1003, "Visible Window 2", "app3", 5003, isVisible: true)
        };
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper(testWindows);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act - Search with visibleOnly: false
        var result = sut.FindWindows(visibleOnly: false);

        // Assert - Should include the invisible window
        result.Should().HaveCount(3);
        result.Should().Contain(w => w.Title == "Hidden Window");
    }

    /// <summary>
    /// Complementary test: visibleOnly: true should exclude invisible windows.
    /// </summary>
    [Fact]
    public void FindWindows_WithVisibleOnlyTrue_ExcludesInvisibleWindows()
    {
        // Arrange - Create windows with mixed visibility
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1001, "Visible Window 1", "app1", 5001, isVisible: true),
            TestDataFactory.CreateWindowInfo(1002, "Hidden Window", "app2", 5002, isVisible: false),
            TestDataFactory.CreateWindowInfo(1003, "Visible Window 2", "app3", 5003, isVisible: true)
        };
        var mockNativeWrapper = TestDataFactory.CreateMockNativeWindowWrapper(testWindows);
        var sut = new WindowService(_mockMonitorService.Object, mockNativeWrapper.Object);

        // Act - Search with visibleOnly: true (default)
        var result = sut.FindWindows(visibleOnly: true);

        // Assert - Should NOT include the invisible window
        result.Should().HaveCount(2);
        result.Should().NotContain(w => w.Title == "Hidden Window");
    }

    #endregion

    #region T2.3: GetForegroundWindow Tests

    [Fact]
    public void GetForegroundWindow_ReturnsWindowOrNull()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.GetForegroundWindow();

        // Assert - May be null if no window has focus (rare but possible)
        // If not null, should have valid properties. The title can be empty (a menu, the taskbar),
        // but every window has a class.
        if (result != null)
        {
            result.Handle.Should().NotBe(nint.Zero);
            result.ClassName.Should().NotBeNullOrEmpty();
        }
    }

    #endregion

    #region T2.4: GetWindowInfo Tests

    [Fact]
    public void GetWindowInfo_WithValidHandle_ReturnsInfo()
    {
        // Arrange
        var sut = CreateReadOnlySut();
        var allWindows = sut.GetAllWindows(includeMinimized: true);
        if (!allWindows.Any()) return;
        var targetHandle = allWindows.First().Handle;

        // Act
        var result = sut.GetWindowInfo(targetHandle);

        // Assert
        result.Should().NotBeNull();
        result!.Handle.Should().Be(targetHandle);
    }

    [Fact]
    public void GetWindowInfo_WithInvalidHandle_ReturnsNull()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.GetWindowInfo(new nint(99999999));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetWindowInfo_WithZeroHandle_ReturnsNull()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.GetWindowInfo(nint.Zero);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region T2.12: IsValidWindow Tests

    [Fact]
    public void IsValidWindow_WithValidHandle_ReturnsTrue()
    {
        // Arrange
        var sut = CreateReadOnlySut();
        var allWindows = sut.GetAllWindows(includeMinimized: true);
        if (!allWindows.Any()) return;
        var validHandle = allWindows.First().Handle;

        // Act
        var result = sut.IsValidWindow(validHandle);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsValidWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.IsValidWindow(new nint(99999999));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidWindow_WithZeroHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateReadOnlySut();

        // Act
        var result = sut.IsValidWindow(nint.Zero);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region T2.5-T2.8: Window Manipulation Tests (Mocked Native Wrapper)

    [Fact]
    public void MoveWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var (sut, native) = CreateSutWithNoValidWindows();

        // Act
        var result = sut.MoveWindow(new nint(InvalidHandleValue), 100, 100);

        // Assert
        result.Should().BeFalse();
        VerifyNoWindowWasChanged(native);
    }

    [Fact]
    public void ResizeWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var (sut, native) = CreateSutWithNoValidWindows();

        // Act
        var result = sut.ResizeWindow(new nint(InvalidHandleValue), 800, 600);

        // Assert
        result.Should().BeFalse();
        VerifyNoWindowWasChanged(native);
    }

    [Fact]
    public void SetWindowBounds_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var (sut, native) = CreateSutWithNoValidWindows();

        // Act
        var result = sut.SetWindowBounds(new nint(InvalidHandleValue), 100, 100, 800, 600);

        // Assert
        result.Should().BeFalse();
        VerifyNoWindowWasChanged(native);
    }

    [Fact]
    public void SetWindowState_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var (sut, native) = CreateSutWithNoValidWindows();

        // Act
        var result = sut.SetWindowState(new nint(InvalidHandleValue), WindowState.Minimized);

        // Assert
        result.Should().BeFalse();
        VerifyNoWindowWasChanged(native);
    }

    #endregion

    #region T2.9: SnapWindow Tests (Invalid Handle)

    [Fact]
    public void SnapWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var (sut, native) = CreateSutWithNoValidWindows();

        // Act
        var result = sut.SnapWindow(new nint(InvalidHandleValue), SnapPosition.LeftHalf);

        // Assert
        result.Should().BeFalse();
        VerifyNoWindowWasChanged(native);
    }

    #endregion

    #region T2.10: MoveWindowToMonitor Tests (Invalid Handle)

    [Fact]
    public void MoveWindowToMonitor_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var (sut, native) = CreateSutWithNoValidWindows();

        // Act
        var result = sut.MoveWindowToMonitor(new nint(InvalidHandleValue), 0);

        // Assert
        result.Should().BeFalse();
        VerifyNoWindowWasChanged(native);
    }

    #endregion

    #region Window State Enumeration Tests

    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.Maximized)]
    public void WindowState_HasExpectedValues(WindowState state)
    {
        // Assert - Verify enum values exist
        Enum.IsDefined(typeof(WindowState), state).Should().BeTrue();
    }

    [Theory]
    [InlineData(SnapPosition.LeftHalf)]
    [InlineData(SnapPosition.RightHalf)]
    [InlineData(SnapPosition.TopHalf)]
    [InlineData(SnapPosition.BottomHalf)]
    [InlineData(SnapPosition.TopLeftQuarter)]
    [InlineData(SnapPosition.TopRightQuarter)]
    [InlineData(SnapPosition.BottomLeftQuarter)]
    [InlineData(SnapPosition.BottomRightQuarter)]
    [InlineData(SnapPosition.LeftThird)]
    [InlineData(SnapPosition.CenterThird)]
    [InlineData(SnapPosition.RightThird)]
    [InlineData(SnapPosition.LeftTwoThirds)]
    [InlineData(SnapPosition.RightTwoThirds)]
    [InlineData(SnapPosition.Fullscreen)]
    public void SnapPosition_HasExpectedValues(SnapPosition position)
    {
        // Assert - Verify enum values exist
        Enum.IsDefined(typeof(SnapPosition), position).Should().BeTrue();
    }

    #endregion
}
