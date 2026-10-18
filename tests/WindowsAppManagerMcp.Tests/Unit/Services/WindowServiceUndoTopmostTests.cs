namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// set_window_zorder's 'above' makes a window topmost first when the other window is topmost, and makes it not topmost again
/// when the rest fails, so a failed call leaves no new topmost window (WindowService.PlaceAbove). When that undo fails too,
/// the failure says the window was left topmost and what to do, rather than a bare ZOrderNotApplied or NotResponding.
/// </summary>
public class WindowServiceUndoTopmostTests
{
    private static readonly nint HwndTopmost = -1;
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
    public void AFailedAbove_WhoseUndoFails_LeadsWithTheWindowsState_BeforeTheStepsOwnNothingWasChanged()
    {
        // Arrange - the step behind the clock is refused (access denied, "Nothing was changed"), and so is the undo
        var (clock, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, clock, It.IsAny<uint>())).Returns(Win32CallResult.Failure(5));
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndNoTopmost, It.IsAny<uint>())).Returns(Win32CallResult.Failure(5));

        // Act
        var result = CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert - the window is topmost now: that comes first, so "Nothing was changed" reads as the failed step's
        result.ErrorCode.Should().Be(WindowErrorCode.AccessDenied);
        result.Error.Should().StartWith("The window was left topmost").And.Contain("Nothing was changed");
    }

    [Fact]
    public void AnAboveThatHangs_WhoseUndoHangsToo_LeadsWithTheWindowsState()
    {
        // Arrange
        var (clock, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, clock, It.IsAny<uint>()))
            .Throws(new WindowNotRespondingException(editor, "The app did not answer, so it was not changed."));
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndNoTopmost, It.IsAny<uint>()))
            .Throws(new WindowNotRespondingException(editor, "The app is hung."));

        // Act
        var act = () => CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        act.Should().Throw<WindowNotRespondingException>().Where(e => e.Message.StartsWith("The window was left topmost"));
    }

    [Fact]
    public void AnAboveWhoseFirstStepHangs_SaysTheWindowMayBecomeTopmost()
    {
        // Arrange - the call that makes it topmost (to go above the topmost overlay) was made and given up on: it can still
        // happen once the app answers, after any undo
        var (_, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndTopmost, It.IsAny<uint>()))
            .Throws(new WindowNotRespondingException(editor, "The app is hung."));

        // Act
        var act = () => CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        act.Should().Throw<WindowNotRespondingException>()
            .Where(e => e.Window == editor && e.Message.StartsWith("The app is hung.") &&
                        e.Message.Contains("may be left topmost") && e.Message.Contains("notopmost"));
        _desktop.IsTopmost(editor).Should().BeFalse();
    }

    [Fact]
    public void AnAboveWhoseFirstStepAnswersLate_IsUndone_AndKeepsTheMessage()
    {
        // Arrange - the window became topmost after all, as the call was given up on; the undo works
        var (_, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndTopmost, It.IsAny<uint>()))
            .Callback(() => _desktop.SetTopmost(editor, true))
            .Throws(new WindowNotRespondingException(editor, "The app is hung."));

        // Act
        var act = () => CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        act.Should().Throw<WindowNotRespondingException>().WithMessage("The app is hung.");
        _desktop.IsTopmost(editor).Should().BeFalse();
        _pins.Windows.Should().BeEmpty();
    }

    [Fact]
    public void AnAboveWhoseFirstStepAnswersLate_AndWhoseUndoFails_SaysTheWindowWasLeftTopmost()
    {
        // Arrange
        var (_, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndTopmost, It.IsAny<uint>()))
            .Callback(() => _desktop.SetTopmost(editor, true))
            .Throws(new WindowNotRespondingException(editor, "The app is hung."));
        _desktop.Native.Setup(n => n.SetWindowZOrder(editor, HwndNoTopmost, It.IsAny<uint>())).Returns(Win32CallResult.Failure(0));

        // Act
        var act = () => CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        act.Should().Throw<WindowNotRespondingException>()
            .Where(e => e.Message.StartsWith("The window was left topmost") && e.Message.Contains("The app is hung."));
        _pins.Windows.Should().ContainSingle().Which.Window.Should().Be(editor);
    }

    [Fact]
    public void AnAboveWhoseFirstStepIsRefusedBeforeItIsSent_KeepsTheMessage()
    {
        // Arrange - Windows reports the app hung, so nothing is sent and nothing can become topmost later
        var (_, overlay, editor) = Desktop();
        _desktop.Native.Setup(n => n.IsHungAppWindow(editor)).Returns(true);

        // Act
        var act = () => CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        act.Should().Throw<WindowNotRespondingException>().Where(e => e.Message.Contains("not changed") && !e.Message.Contains("topmost"));
        _desktop.Native.Verify(n => n.SetWindowZOrder(editor, It.IsAny<nint>(), It.IsAny<uint>()), Times.Never);
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
