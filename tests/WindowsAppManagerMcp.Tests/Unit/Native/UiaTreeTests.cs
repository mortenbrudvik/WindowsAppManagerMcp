namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Tests for the UI Automation adapter that need no window: they read only the UI Automation root
/// and look up a handle that is not a window. No window is read, focused or changed.
/// </summary>
public class UiaTreeTests
{
    // A handle that is extremely unlikely to be a real window
    private static readonly nint NotAWindow = new(999999999);

    [Fact]
    public void IsAvailable_OnWindows_IsTrue()
    {
        new UiaTree().IsAvailable().Should().BeTrue();
    }

    [Fact]
    public void FromHandle_WithAHandleThatIsNotAWindow_ReturnsNull()
    {
        // UIElementService turns this into WindowNotFound
        new UiaTree().FromHandle(NotAWindow).Should().BeNull();
    }

    [Fact]
    public void Reads_RejectNodesFromAnotherTree()
    {
        // Arrange
        var sut = new UiaTree();
        var foreign = new FakeUiaNode("Button", "Fake");

        // Act & Assert
        sut.Invoking(t => t.Read(foreign)).Should().Throw<ArgumentException>();
        sut.Invoking(t => t.GetChildren(foreign, CancellationToken.None)).Should().Throw<ArgumentException>();
        sut.Invoking(t => t.GetParent(foreign)).Should().Throw<ArgumentException>();
        sut.Invoking(t => t.ReadValue(foreign)).Should().Throw<ArgumentException>();
    }
}
