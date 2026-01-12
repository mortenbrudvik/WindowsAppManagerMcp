using System.Diagnostics;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for WindowService.
/// Tests T2.1-T2.12 from BACKLOG-TECHNICAL.md.
///
/// Note: WindowService uses P/Invoke for window management.
/// These tests focus on behavior that can be safely tested without side effects.
/// Tests use the real NativeWindowWrapper to verify actual Windows API behavior.
/// </summary>
public class WindowServiceTests
{
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly INativeWindowWrapper _nativeWrapper;

    public WindowServiceTests()
    {
        _mockMonitorService = TestDataFactory.CreateMockMonitorService();
        _nativeWrapper = new NativeWindowWrapper();
    }

    private WindowService CreateSut() => new WindowService(_mockMonitorService.Object, _nativeWrapper);

    #region T2.1: GetAllWindows Tests

    [Fact]
    public void GetAllWindows_ReturnsNonEmptyList()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetAllWindows(includeMinimized: true);

        // Assert
        result.Should().NotBeEmpty("There should be at least some visible windows on the system");
    }

    [Fact]
    public void GetAllWindows_ReturnsWindowsWithValidHandles()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetAllWindows(includeMinimized: true);

        // Assert
        result.Should().OnlyContain(w => w.Handle != nint.Zero);
    }

    [Fact]
    public void GetAllWindows_ReturnsWindowsWithNonEmptyTitles()
    {
        // Arrange
        var sut = CreateSut();

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
        var sut = CreateSut();

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
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void FindWindows_WithProcessName_FiltersCorrectly()
    {
        // Arrange
        var sut = CreateSut();
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

    [Fact]
    public void FindWindows_WithNonMatchingFilter_ReturnsEmpty()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows(processName: "xyznonexistentprocess123");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void FindWindows_WithHandle_ReturnsMatchingWindow()
    {
        // Arrange
        var sut = CreateSut();
        var allWindows = sut.GetAllWindows(includeMinimized: true);
        if (!allWindows.Any()) return; // Skip if no windows
        var targetHandle = allWindows.First().Handle;

        // Act
        var result = sut.FindWindows(handle: targetHandle);

        // Assert
        result.Should().HaveCount(1);
        result[0].Handle.Should().Be(targetHandle);
    }

    #endregion

    #region T2.3: GetForegroundWindow Tests

    [Fact]
    public void GetForegroundWindow_ReturnsWindowOrNull()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetForegroundWindow();

        // Assert - May be null if no window has focus (rare but possible)
        // If not null, should have valid properties
        if (result != null)
        {
            result.Handle.Should().NotBe(nint.Zero);
            result.Title.Should().NotBeNullOrEmpty();
        }
    }

    #endregion

    #region T2.4: GetWindowInfo Tests

    [Fact]
    public void GetWindowInfo_WithValidHandle_ReturnsInfo()
    {
        // Arrange
        var sut = CreateSut();
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
        var sut = CreateSut();

        // Act
        var result = sut.GetWindowInfo(new nint(99999999));

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetWindowInfo_WithZeroHandle_ReturnsNull()
    {
        // Arrange
        var sut = CreateSut();

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
        var sut = CreateSut();
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
        var sut = CreateSut();

        // Act
        var result = sut.IsValidWindow(new nint(99999999));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidWindow_WithZeroHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.IsValidWindow(nint.Zero);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region T2.5-T2.8: Window Manipulation Tests (Safe Operations)

    [Fact]
    public void MoveWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.MoveWindow(new nint(99999999), 100, 100);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void ResizeWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.ResizeWindow(new nint(99999999), 800, 600);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void SetWindowBounds_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SetWindowBounds(new nint(99999999), 100, 100, 800, 600);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void SetWindowState_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SetWindowState(new nint(99999999), WindowState.Minimized);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void FocusWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.FocusWindow(new nint(99999999));

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region T2.9: SnapWindow Tests (Invalid Handle)

    [Fact]
    public void SnapWindow_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.SnapWindow(new nint(99999999), SnapPosition.LeftHalf);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region T2.10: MoveWindowToMonitor Tests (Invalid Handle)

    [Fact]
    public void MoveWindowToMonitor_WithInvalidHandle_ReturnsFalse()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.MoveWindowToMonitor(new nint(99999999), 0);

        // Assert
        result.Should().BeFalse();
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
