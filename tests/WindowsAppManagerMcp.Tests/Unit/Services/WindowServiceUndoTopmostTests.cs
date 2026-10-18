namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// set_window_zorder's 'above' makes a window topmost first when the other window is topmost, and makes it not topmost again
/// when the rest fails, so a failed call leaves no new topmost window (WindowService.PlaceAbove). When that undo fails too,
/// the failure says the window was left topmost and what to do, rather than a bare ZOrderNotApplied or NotResponding.
/// </summary>
public class WindowServiceUndoTopmostTests
{
    private static readonly nint HwndNoTopmost = -2;

    private readonly FakeWindowDesktop _desktop = new();
    private readonly TopmostPins _pins = new();

    private WindowService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _desktop.Native.Object, _desktop.HostGuard.Object, topmostPins: _pins);

    /// <summary>Front to back: a topmost clock, a topmost overlay and a normal editor; the editor goes above the overlay.</summary>
    private (nint Clock, nint Overlay, nint Editor) Desktop()
    {
        var clock = _desktop.Add("Clock", extendedStyle: FakeWindowDesktop.WsExTopmost);
        var overlay = _desktop.Add("Overlay", extendedStyle: FakeWindowDesktop.WsExTopmost);
        var editor = _desktop.Add("Editor");
        _desktop.AcceptZOrderChanges();
        return (clock, overlay, editor);
    }

    [Fact]
    public void AFailedAbove_WhoseUndoFails_SaysTheWindowWasLeftTopmost()
    {
        // Arrange - neither the step behind the clock nor the undo goes through
        var (clock, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, clock, It.IsAny<uint>())).Returns(Win32CallResult.Failure(0));
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndNoTopmost, It.IsAny<uint>())).Returns(Win32CallResult.Failure(0));

        // Act
        var result = CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        result.Success.Should().BeFalse();
        _desktop.IsTopmost(editor).Should().BeTrue();
        result.Error.Should().Contain("left topmost").And.Contain("notopmost");
    }

    [Fact]
    public void AFailedAbove_WhoseUndoWorks_SaysNothingAboutTopmost()
    {
        // Arrange
        var (clock, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, clock, It.IsAny<uint>())).Returns(Win32CallResult.Failure(0));

        // Act
        var result = CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        result.Success.Should().BeFalse();
        _desktop.IsTopmost(editor).Should().BeFalse();
        result.Error.Should().NotContain("left topmost");
    }

    [Fact]
    public void AnAboveThatHangs_WhoseUndoHangsToo_SaysTheWindowWasLeftTopmost()
    {
        // Arrange - the app stops answering after the window became topmost, and the undo does not answer either
        var (clock, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, clock, It.IsAny<uint>()))
            .Throws(new WindowNotRespondingException(editor, "The app is hung."));
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndNoTopmost, It.IsAny<uint>()))
            .Throws(new WindowNotRespondingException(editor, "The app is hung."));

        // Act
        var act = () => CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        act.Should().Throw<WindowNotRespondingException>()
            .Where(e => e.Message.Contains("The app is hung.") && e.Message.Contains("left topmost") && e.Window == editor);
        _pins.Windows.Should().ContainSingle().Which.Window.Should().Be(editor);
    }

    [Fact]
    public void AnAboveThatHangs_WhoseUndoWorks_KeepsTheMessage()
    {
        // Arrange
        var (clock, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, clock, It.IsAny<uint>()))
            .Throws(new WindowNotRespondingException(editor, "The app is hung."));

        // Act
        var act = () => CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        act.Should().Throw<WindowNotRespondingException>().WithMessage("The app is hung.");
        _desktop.IsTopmost(editor).Should().BeFalse();
    }
}
