namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Tests for the UI Automation adapter that need no window: they read only the UI Automation root
/// and look up a handle that is not a window. No window is read, focused or changed. They reach the
/// real client, so they run in the serial <see cref="RealUiAutomationCollection"/>; the tests with a
/// fake start gate are harmless there.
/// </summary>
[Collection(RealUiAutomationCollection.Name)]
public class UiaTreeTests
{
    // A handle that is extremely unlikely to be a real window
    private static readonly nint NotAWindow = new(999999999);

    [Theory]
    [InlineData(UiaComErrors.NotSupported, typeof(NotSupportedException))]
    [InlineData(UiaComErrors.NoClickablePoint, typeof(NotSupportedException))]
    [InlineData(UiaComErrors.Disconnected, typeof(UiaElementUnavailableException))]
    [InlineData(UiaComErrors.ElementNotEnabled, typeof(InvalidOperationException))]
    public void Translate_TurnsComErrorsIntoTheSameSeamExceptionsAsTheUia3Engine(int hresult, Type expected)
    {
        // The managed client passes most UI Automation HRESULTs on as a COMException; the UIA3 engine translates them
        var com = new System.Runtime.InteropServices.COMException("provider", hresult);

        var thrown = FluentActions.Invoking(() => UiaTree.Translate<int>(() => throw com)).Should().Throw<Exception>().Which;

        thrown.Should().BeOfType(expected);
        thrown.GetType().Should().Be(UiaComErrors.Translate(com)!.GetType());
    }

    [Fact]
    public void Translate_LeavesOtherComErrorsAsTheyAre()
    {
        FluentActions.Invoking(() => UiaTree.Translate<int>(() => throw new System.Runtime.InteropServices.COMException("E_FAIL", unchecked((int)0x80004005))))
            .Should().Throw<System.Runtime.InteropServices.COMException>();
    }

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
    public void IsAvailable_WhenTheClientCannotStart_IsFalse()
    {
        // Arrange - the start gate fails before any UI Automation call is made
        var sut = new UiaTree(new UiaClientStartup(() => throw new System.Runtime.InteropServices.COMException("E_FAIL")));

        // Act & Assert
        sut.IsAvailable().Should().BeFalse();
    }

    [Fact]
    public void EntryPoints_PassTheStartGateFirst()
    {
        // Arrange - a gate that refuses to start, so no UI Automation call follows it
        var startAttempts = 0;
        var sut = new UiaTree(new UiaClientStartup(() =>
        {
            startAttempts++;
            throw new InvalidOperationException("not started");
        }));

        // Act & Assert
        sut.Invoking(t => t.FromHandle(NotAWindow)).Should().Throw<InvalidOperationException>();
        sut.Invoking(t => t.FromPoint(0, 0)).Should().Throw<InvalidOperationException>();
        sut.Invoking(t => t.GetFocusedElement()).Should().Throw<InvalidOperationException>();
        startAttempts.Should().Be(3);
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
