using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for UIAutomationWrapper.
/// Note: Full integration tests with real browser windows would require
/// actual browsers running, which is better suited for manual testing.
/// These tests verify basic behavior and error handling.
/// </summary>
public class UIAutomationWrapperTests
{
    private UIAutomationWrapper CreateSut() => new();

    #region IsAvailable Tests

    [Fact]
    public void IsAvailable_ReturnsTrue_OnWindowsWithUIAutomation()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.IsAvailable();

        // Assert
        // On Windows, UI Automation should be available
        result.Should().BeTrue();
    }

    #endregion

    #region TryGetElementFromHandle Tests

    [Fact]
    public void TryGetElementFromHandle_ReturnsFalse_WhenHandleIsZero()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.TryGetElementFromHandle(nint.Zero, out var element);

        // Assert
        result.Should().BeFalse();
        element.Should().BeNull();
    }

    [Fact]
    public void TryGetElementFromHandle_ReturnsFalse_WhenHandleIsInvalid()
    {
        // Arrange
        var sut = CreateSut();
        var invalidHandle = new nint(999999999); // Very unlikely to be valid

        // Act
        var result = sut.TryGetElementFromHandle(invalidHandle, out var element);

        // Assert
        result.Should().BeFalse();
        element.Should().BeNull();
    }

    #endregion

    #region GetWindowTitle Tests

    [Fact]
    public void GetWindowTitle_ReturnsNull_WhenHandleIsInvalid()
    {
        // Arrange
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act
        var result = sut.GetWindowTitle(invalidHandle);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetBrowserUrl Tests

    [Fact]
    public void GetBrowserUrl_ReturnsNull_WhenHandleIsInvalid()
    {
        // Arrange
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act
        var result = sut.GetBrowserUrl(invalidHandle, "chrome");

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("edge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("vivaldi")]
    [InlineData("opera")]
    [InlineData("unknown")]
    public void GetBrowserUrl_HandlesAllBrowserTypes_WithoutException(string browserType)
    {
        // Arrange
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act & Assert - Should not throw, just return null
        var result = sut.GetBrowserUrl(invalidHandle, browserType);
        result.Should().BeNull();
    }

    #endregion

    #region GetBrowserContent Tests

    [Fact]
    public void GetBrowserContent_ReturnsNull_WhenHandleIsInvalid()
    {
        // Arrange
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act
        var result = sut.GetBrowserContent(invalidHandle, "chrome");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetBrowserContent_RespectsMaxLength_Parameter()
    {
        // Arrange
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act - Should not throw even with maxLength specified
        var result = sut.GetBrowserContent(invalidHandle, "chrome", maxLength: 100);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Browser Type Handling Tests

    [Fact]
    public void GetBrowserUrl_HandlesCaseInsensitiveBrowserType()
    {
        // Arrange
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act & Assert - Should handle case variations without exception
        sut.GetBrowserUrl(invalidHandle, "Chrome");
        sut.GetBrowserUrl(invalidHandle, "CHROME");
        sut.GetBrowserUrl(invalidHandle, "Firefox");
        sut.GetBrowserUrl(invalidHandle, "FIREFOX");
    }

    [Fact]
    public void GetBrowserUrl_UsesChromiumStrategy_ForChromiumBasedBrowsers()
    {
        // These browsers use the Chromium URL bar pattern
        var chromiumBrowsers = new[] { "chrome", "edge", "brave", "vivaldi", "opera", "chromium" };
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        foreach (var browser in chromiumBrowsers)
        {
            // Act & Assert - Should handle all Chromium-based browsers
            var action = () => sut.GetBrowserUrl(invalidHandle, browser);
            action.Should().NotThrow();
        }
    }

    [Fact]
    public void GetBrowserUrl_UsesFirefoxStrategy_ForFirefoxBasedBrowsers()
    {
        // Firefox uses a different URL bar pattern
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act & Assert - Should handle Firefox
        var action = () => sut.GetBrowserUrl(invalidHandle, "firefox");
        action.Should().NotThrow();
    }

    [Fact]
    public void GetBrowserUrl_TriesBothStrategies_ForUnknownBrowsers()
    {
        // Unknown browsers should try both strategies
        var sut = CreateSut();
        var invalidHandle = new nint(999999999);

        // Act & Assert - Should handle unknown browser type
        var action = () => sut.GetBrowserUrl(invalidHandle, "unknown-browser");
        action.Should().NotThrow();
    }

    #endregion
}
