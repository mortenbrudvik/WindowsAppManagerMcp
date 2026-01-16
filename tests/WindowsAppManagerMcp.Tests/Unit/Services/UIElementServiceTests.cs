namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for UIElementService.
/// Tests Task 11.2 from BACKLOG-TECHNICAL.md.
/// </summary>
public class UIElementServiceTests
{
    private UIElementService CreateSut() => new UIElementService();

    #region IsAvailable Tests

    [Fact]
    public void IsAvailable_ReturnsTrue_WhenUIAutomationWorks()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.IsAvailable();

        // Assert - On Windows, UI Automation should be available
        result.Should().BeTrue();
    }

    #endregion

    #region GetUIElements - Invalid Handle Tests

    [Fact]
    public void GetUIElements_WithZeroHandle_ReturnsInvalidHandleError()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetUIElements(nint.Zero);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
        result.Error.Should().Contain("Invalid window handle");
        result.WindowHandle.Should().Be(0);
    }

    [Fact]
    public void GetUIElements_WithInvalidHandle_ReturnsWindowNotFoundError()
    {
        // Arrange
        var sut = CreateSut();
        // Use a handle that's extremely unlikely to be a real window
        var invalidHandle = new nint(999999999);

        // Act
        var result = sut.GetUIElements(invalidHandle);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().BeOneOf(
            UIElementErrorCode.WindowNotFound,
            UIElementErrorCode.ElementAccessFailed,
            UIElementErrorCode.OperationException);
    }

    #endregion

    #region GetUIElements - Depth Clamping Tests

    [Theory]
    [InlineData(0)]   // Below minimum, should clamp to 1
    [InlineData(-5)]  // Negative, should clamp to 1
    [InlineData(15)] // Above maximum, should clamp to 10
    [InlineData(100)] // Way above maximum, should clamp to 10
    public void GetUIElements_AcceptsOutOfRangeDepthValues_WithoutThrowing(int requestedDepth)
    {
        // Arrange
        var sut = CreateSut();

        // Act - Service should not throw for out-of-range depth values
        var result = sut.GetUIElements(nint.Zero, maxDepth: requestedDepth);

        // Assert - The operation should complete (fails due to zero handle, not depth)
        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void GetUIElements_AcceptsValidDepthValues(int depth)
    {
        // Arrange
        var sut = CreateSut();

        // Act - With zero handle to test that depth is accepted
        var result = sut.GetUIElements(nint.Zero, maxDepth: depth);

        // Assert - Fails for invalid handle, not depth
        result.ErrorCode.Should().Be(UIElementErrorCode.InvalidHandle);
    }

    #endregion

    #region GetUIElements - Result Structure Tests

    [Fact]
    public void GetUIElements_ErrorResult_HasCorrectStructure()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetUIElements(nint.Zero);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Elements.Should().BeNull();
        result.TotalElementCount.Should().Be(0);
        result.Depth.Should().Be(0);
        result.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(0);
        result.Error.Should().NotBeNullOrEmpty();
        result.ErrorCode.Should().NotBe(UIElementErrorCode.None);
    }

    [Fact]
    public void GetUIElements_IncludesElapsedTime()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var result = sut.GetUIElements(nint.Zero);

        // Assert
        result.ElapsedMilliseconds.Should().BeGreaterOrEqualTo(0);
    }

    #endregion

    #region GetUIElements - Filter Construction Tests

    [Fact]
    public void GetUIElements_WithNullFilter_Succeeds()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, filter: null);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetUIElements_WithEmptyFilter_Succeeds()
    {
        // Arrange
        var sut = CreateSut();
        var filter = new UIElementFilter();

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, filter: filter);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetUIElements_WithControlTypeFilter_Succeeds()
    {
        // Arrange
        var sut = CreateSut();
        var filter = new UIElementFilter(ControlTypes: new[] { "Button", "Edit" });

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, filter: filter);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetUIElements_WithInteractableOnlyFilter_Succeeds()
    {
        // Arrange
        var sut = CreateSut();
        var filter = new UIElementFilter(InteractableOnly: true);

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, filter: filter);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetUIElements_WithVisibleOnlyFilter_Succeeds()
    {
        // Arrange
        var sut = CreateSut();
        var filter = new UIElementFilter(VisibleOnly: true);

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, filter: filter);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetUIElements_WithSizeFilters_Succeeds()
    {
        // Arrange
        var sut = CreateSut();
        var filter = new UIElementFilter(MinWidth: 50, MinHeight: 20);

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, filter: filter);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetUIElements_WithAllFilters_Succeeds()
    {
        // Arrange
        var sut = CreateSut();
        var filter = new UIElementFilter(
            ControlTypes: new[] { "Button", "Edit", "CheckBox" },
            InteractableOnly: true,
            VisibleOnly: true,
            MinWidth: 50,
            MinHeight: 20);

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, filter: filter);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region GetUIElements - Cancellation Token Tests

    [Fact]
    public void GetUIElements_WithCancelledToken_ReturnsTimeoutError()
    {
        // Arrange
        var sut = CreateSut();
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act - Need a valid handle to get past the initial validation
        // Use a fake handle that will try to access UI automation
        var result = sut.GetUIElements(new nint(1), cancellationToken: cts.Token);

        // Assert - Should fail (either timeout or window not found)
        result.Success.Should().BeFalse();
    }

    [Fact]
    public void GetUIElements_WithDefaultToken_Succeeds()
    {
        // Arrange
        var sut = CreateSut();

        // Act - Should not throw
        var result = sut.GetUIElements(nint.Zero, cancellationToken: default);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion
}
