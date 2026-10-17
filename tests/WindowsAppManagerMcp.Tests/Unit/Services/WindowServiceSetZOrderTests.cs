namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// set_window_zorder's service (roadmap M6.7, owner decision D14): SetWindowZOrder changes only the window's place in the
/// z-order, never activates or moves it, checks the result by reading it back (ZOrderNotApplied), and remembers the windows
/// it made topmost (<see cref="TopmostPins"/>) until it unpins them. Every window is a fake on a mocked native wrapper.
/// </summary>
public class WindowServiceSetZOrderTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTop = 0, HwndBottom = 1, HwndTopmost = -1, HwndNoTopmost = -2;

    private readonly FakeWindowDesktop _desktop = new();
    private readonly TopmostPins _pins = new();

    private WindowService CreateSut() =>
        new(TestDataFactory.CreateMockMonitorService().Object, _desktop.Native.Object, _desktop.HostGuard.Object, topmostPins: _pins);

    [Theory]
    [InlineData(ZOrderMode.Top, 0)]
    [InlineData(ZOrderMode.Bottom, 1)]
    [InlineData(ZOrderMode.Topmost, -1)]
    [InlineData(ZOrderMode.NoTopmost, -2)]
    public void EachMode_SendsItsInsertAfter_WithoutActivatingTheWindow(ZOrderMode mode, long insertAfter)
    {
        // Arrange
        _desktop.Add("Editor");
        var window = _desktop.Add("Reference");
        _desktop.AcceptZOrderChanges();

        // Act
        var result = CreateSut().SetWindowZOrder(window, mode);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        var call = _desktop.ZOrderCalls.Should().ContainSingle().Which;
        call.Window.Should().Be(window);
        call.InsertAfter.Should().Be((nint)insertAfter);
        (call.Flags & SwpNoActivate).Should().Be(SwpNoActivate, "a z-order change never takes the keyboard focus");
    }

    [Fact]
    public void Topmost_PinsTheWindow_AndNoTopmostUnpinsIt()
    {
        // Arrange
        var window = _desktop.Add("Reference");
        _desktop.AcceptZOrderChanges();
        var sut = CreateSut();

        // Act & Assert
        sut.SetWindowZOrder(window, ZOrderMode.Topmost).Success.Should().BeTrue();
        _desktop.IsTopmost(window).Should().BeTrue();
        _pins.Windows.Should().ContainSingle().Which.Window.Should().Be(window);

        sut.SetWindowZOrder(window, ZOrderMode.NoTopmost).Success.Should().BeTrue();
        _desktop.IsTopmost(window).Should().BeFalse();
        _pins.Windows.Should().BeEmpty();
    }

    [Fact]
    public void Bottom_UnpinsAWindowTheServerPinned()
    {
        // Arrange
        var window = _desktop.Add("Reference");
        _desktop.AcceptZOrderChanges();
        var sut = CreateSut();
        sut.SetWindowZOrder(window, ZOrderMode.Topmost);

        // Act
        sut.SetWindowZOrder(window, ZOrderMode.Bottom).Success.Should().BeTrue();

        // Assert
        _pins.Windows.Should().BeEmpty();
        _desktop.ZOrder[^1].Should().Be(window);
    }

    [Fact]
    public void Topmost_OnAWindowThatWasTopmostAlready_DoesNotPinIt()
    {
        // Arrange - the app made it topmost itself: the server did not pin it, so it does not report it as its own
        var window = _desktop.Add("Always on top", extendedStyle: FakeWindowDesktop.WsExTopmost);
        _desktop.AcceptZOrderChanges();

        // Act
        CreateSut().SetWindowZOrder(window, ZOrderMode.Topmost).Success.Should().BeTrue();

        // Assert
        _pins.Windows.Should().BeEmpty();
    }

    [Fact]
    public void Topmost_ThatTheAppTakesBack_IsZOrderNotApplied_AndNotPinned()
    {
        // Arrange
        var window = _desktop.Add("Game");
        _desktop.AcceptZOrderChanges(keepsOwnTopmost: true);

        // Act
        var result = CreateSut().SetWindowZOrder(window, ZOrderMode.Topmost);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(WindowErrorCode.ZOrderNotApplied);
        result.Error.Should().Contain("not topmost");
        _pins.Windows.Should().BeEmpty();
    }

    [Fact]
    public void NoTopmost_ThatTheAppTakesBack_IsZOrderNotApplied()
    {
        // Arrange
        var window = _desktop.Add("Overlay", extendedStyle: FakeWindowDesktop.WsExTopmost);
        _desktop.AcceptZOrderChanges(keepsOwnTopmost: true);

        // Act
        var result = CreateSut().SetWindowZOrder(window, ZOrderMode.NoTopmost);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.ZOrderNotApplied);
    }

    [Fact]
    public void Above_PlacesTheWindowDirectlyAboveTheOther_BehindTheWindowAboveIt()
    {
        // Arrange - front to back: editor, browser, terminal; the terminal goes above the browser
        var editor = _desktop.Add("Editor");
        var browser = _desktop.Add("Browser");
        var terminal = _desktop.Add("Terminal");
        _desktop.AcceptZOrderChanges();

        // Act
        var result = CreateSut().SetWindowZOrder(terminal, ZOrderMode.Above, browser);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _desktop.ZOrderCalls.Should().ContainSingle().Which.InsertAfter.Should().Be(editor);
        _desktop.ZOrder.Should().Equal(editor, terminal, browser);
    }

    [Fact]
    public void Above_TheFrontWindow_RaisesTheWindowToTheTop()
    {
        // Arrange
        var browser = _desktop.Add("Browser");
        var terminal = _desktop.Add("Terminal");
        _desktop.AcceptZOrderChanges();

        // Act
        CreateSut().SetWindowZOrder(terminal, ZOrderMode.Above, browser).Success.Should().BeTrue();

        // Assert
        _desktop.ZOrderCalls.Should().ContainSingle().Which.InsertAfter.Should().Be(HwndTop);
        _desktop.ZOrder.Should().Equal(terminal, browser);
    }

    [Fact]
    public void Above_ANormalWindowRightBelowTopmostOnes_UsesTopNeverATopmostWindow()
    {
        // Arrange - inserting behind a topmost window could make the window topmost; HWND_TOP keeps it a normal window
        var overlay = _desktop.Add("Overlay", extendedStyle: FakeWindowDesktop.WsExTopmost);
        var browser = _desktop.Add("Browser");
        var terminal = _desktop.Add("Terminal");
        _desktop.AcceptZOrderChanges();

        // Act
        CreateSut().SetWindowZOrder(terminal, ZOrderMode.Above, browser).Success.Should().BeTrue();

        // Assert
        _desktop.ZOrderCalls.Should().ContainSingle().Which.InsertAfter.Should().Be(HwndTop);
        _desktop.ZOrder.Should().Equal(overlay, terminal, browser);
        _desktop.IsTopmost(terminal).Should().BeFalse();
    }

    [Fact]
    public void Above_ATopmostOverlay_MakesTheWindowTopmost_JustAboveIt_AndPinsIt()
    {
        // Arrange - front to back: a topmost clock, a topmost overlay, the editor
        var clock = _desktop.Add("Clock", extendedStyle: FakeWindowDesktop.WsExTopmost);
        var overlay = _desktop.Add("Overlay", extendedStyle: FakeWindowDesktop.WsExTopmost);
        var editor = _desktop.Add("Editor");
        _desktop.AcceptZOrderChanges();

        // Act
        var result = CreateSut().SetWindowZOrder(editor, ZOrderMode.Above, overlay);

        // Assert
        result.Success.Should().BeTrue(result.Error);
        _desktop.ZOrderCalls.Select(c => c.InsertAfter).Should().Equal(HwndTopmost, clock);
        _desktop.ZOrder.Should().Equal(clock, editor, overlay);
        _pins.Windows.Should().ContainSingle().Which.Window.Should().Be(editor);
    }

    [Fact]
    public void Above_WhenTheWindowIsTopmostAndTheOtherIsNot_IsAboveAlready_AndSendsNothing()
    {
        // Arrange
        var pinned = _desktop.Add("Pinned", extendedStyle: FakeWindowDesktop.WsExTopmost);
        var browser = _desktop.Add("Browser");
        _desktop.AcceptZOrderChanges();

        // Act
        var result = CreateSut().SetWindowZOrder(pinned, ZOrderMode.Above, browser);

        // Assert
        result.Success.Should().BeTrue();
        _desktop.ZOrderCalls.Should().BeEmpty();
    }

    [Fact]
    public void Above_AWindowThatIsGone_IsInvalidHandle_AndSendsNothing()
    {
        // Arrange
        var terminal = _desktop.Add("Terminal");
        _desktop.AcceptZOrderChanges();

        // Act
        var result = CreateSut().SetWindowZOrder(terminal, ZOrderMode.Above, 0x7777);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
        result.Error.Should().Contain("aboveHandle");
        _desktop.ZOrderCalls.Should().BeEmpty();
    }

    [Fact]
    public void AChangeWindowsRefuses_IsAccessDenied()
    {
        // Arrange - User Interface Privilege Isolation: the window belongs to an app running as administrator
        var window = _desktop.Add("Elevated");
        _desktop.Native.Setup(n => n.SetWindowZOrder(window, It.IsAny<nint>(), It.IsAny<uint>())).Returns(Win32CallResult.Failure(5));

        // Act
        var result = CreateSut().SetWindowZOrder(window, ZOrderMode.Topmost);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.AccessDenied);
        _pins.Windows.Should().BeEmpty();
    }

    [Fact]
    public void AWindowThatIsGone_IsInvalidHandle()
    {
        // Act
        var result = CreateSut().SetWindowZOrder(0x4444, ZOrderMode.Top);

        // Assert
        result.ErrorCode.Should().Be(WindowErrorCode.InvalidHandle);
    }

    [Fact]
    public void FindWindows_ReportsIsTopmost()
    {
        // Arrange
        var overlay = _desktop.Add("Overlay", extendedStyle: FakeWindowDesktop.WsExTopmost);
        var editor = _desktop.Add("Editor");

        // Act
        var windows = CreateSut().FindWindows(new WindowQuery());

        // Assert
        windows.Single(w => w.Handle == overlay).IsTopmost.Should().BeTrue();
        windows.Single(w => w.Handle == editor).IsTopmost.Should().BeFalse();
    }
}
