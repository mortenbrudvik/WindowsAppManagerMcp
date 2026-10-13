namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Roadmap M3.11: image, window and client points converted to screen points, and the staleness checks that refuse a
/// point whose capture or window changed.
/// </summary>
public class CoordinateSpaceResolverTests
{
    private const nint Window = 0x5150;
    private const int Pid = 4242;

    private readonly CaptureRegistry _registry = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeHitTestWrapper> _hitTest = new();
    private readonly Mock<IScreenCaptureWrapper> _screen = new();

    // The window's rectangle, client area and the virtual screen, as the tests move them
    private (int Left, int Top, int Right, int Bottom) _rect = (100, 200, 916, 839);
    private WindowRect? _client = new(108, 231, 800, 600);
    private (int X, int Y, int Width, int Height) _virtualScreen = (-1920, 0, 3840, 1080);

    public CoordinateSpaceResolverTests()
    {
        _windows.Setup(w => w.IsWindow(Window)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns((uint)Pid);
        _windows.Setup(w => w.GetWindowRect(Window)).Returns(() => _rect);
        _hitTest.Setup(h => h.GetClientAreaOnScreen(Window)).Returns(() => _client);
        _screen.Setup(s => s.GetVirtualScreenBounds()).Returns(() => _virtualScreen);
    }

    private CoordinateSpaceResolver CreateSut() => new(_registry, _windows.Object, _hitTest.Object, _screen.Object);

    /// <summary>A capture of the window, scaled down by 2: image (0, 0) is screen (108, 231).</summary>
    private string WindowCapture(bool includeFrame = false) => _registry.Register(new CaptureGeometry(
        400, 300, new ImageToScreenTransform(108, 231, 2, 2), new CapturedRegion(108, 231, 800, 600),
        new CapturedWindow(Window, Pid, WindowRect.FromLTRB(100, 200, 916, 839), includeFrame), null)).CaptureId;

    /// <summary>A capture of the left monitor at full resolution.</summary>
    private string ScreenCapture() => _registry.Register(new CaptureGeometry(
        1920, 1080, new ImageToScreenTransform(-1920, 0, 1, 1), new CapturedRegion(-1920, 0, 1920, 1080), null,
        new VirtualScreenBounds(-1920, 0, 3840, 1080))).CaptureId;

    [Fact]
    public void FromImage_MapsThroughTheCapturesTransform_AndNamesTheWindow()
    {
        // Act
        var point = CreateSut().FromImage(WindowCapture(), 50, 25);

        // Assert - (108 + 50 x 2, 231 + 25 x 2)
        point.Resolved.Should().BeTrue(point.Error);
        (point.X, point.Y).Should().Be((208, 281));
        point.CapturedWindow.Should().Be(Window);
        point.Recheck!().Should().BeNull();
    }

    [Fact]
    public void FromImage_RoundsAsImageToScreenDoes()
    {
        var id = _registry.Register(new CaptureGeometry(1568, 882, new ImageToScreenTransform(0, 0, 2.44898, 2.44898),
            new CapturedRegion(0, 0, 3840, 2160), null, new VirtualScreenBounds(-1920, 0, 3840, 1080))).CaptureId;

        var point = CreateSut().FromImage(id, 100, 50);

        (point.X, point.Y).Should().Be((245, 122), "the documented example of Image coordinates");
        point.CapturedWindow.Should().BeNull();
    }

    [Fact]
    public void FromImage_UnknownId_IsCaptureNotFound()
    {
        var point = CreateSut().FromImage("c99-abcdef", 10, 10);

        point.ErrorCode.Should().Be(InputErrorCode.CaptureNotFound);
        point.Error.Should().Contain("c99-abcdef").And.Contain("10 minutes").And.Contain("capture again");
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(400, 0)]
    [InlineData(0, 300)]
    public void FromImage_OutsideTheImage_IsCoordinatesOutOfBounds(int x, int y)
    {
        var point = CreateSut().FromImage(WindowCapture(), x, y);

        point.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        point.Error.Should().Contain("400x300");
    }

    [Theory]
    [InlineData(2, 2, 2, 2)]
    [InlineData(-2, 0, -2, 0)]
    [InlineData(0, 0, 2, 1)]
    public void FromImage_AWindowThatMovedAtMostTwoPixels_IsNotStale(int left, int top, int right, int bottom)
    {
        _rect = (100 + left, 200 + top, 916 + right, 839 + bottom);
        _client = new WindowRect(108 + left, 231 + top, 800, 600);

        CreateSut().FromImage(WindowCapture(), 50, 25).Resolved.Should().BeTrue();
    }

    [Theory]
    [InlineData(3, 0, 3, 0)]
    [InlineData(0, -40, 0, -40)]
    [InlineData(0, 0, 100, 0)]
    public void FromImage_AWindowThatMovedOrResized_IsStaleCapture(int left, int top, int right, int bottom)
    {
        _rect = (100 + left, 200 + top, 916 + right, 839 + bottom);

        var point = CreateSut().FromImage(WindowCapture(includeFrame: true), 50, 25);

        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        point.Error.Should().Contain("moved or resized").And.Contain("capture it again");
    }

    [Fact]
    public void FromImage_AClientCapture_WhoseClientAreaMoved_IsStaleCapture()
    {
        // A toolbar appeared: the window stayed, its client area moved down
        _client = new WindowRect(108, 271, 800, 560);

        var point = CreateSut().FromImage(WindowCapture(includeFrame: false), 50, 25);

        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        point.Error.Should().Contain("client area");
    }

    [Fact]
    public void FromImage_AFrameCapture_IgnoresTheClientArea()
    {
        _client = new WindowRect(108, 271, 800, 560);

        CreateSut().FromImage(WindowCapture(includeFrame: true), 50, 25).Resolved.Should().BeTrue();
    }

    [Fact]
    public void FromImage_AClosedWindow_IsStaleCapture()
    {
        _windows.Setup(w => w.IsWindow(Window)).Returns(false);

        var point = CreateSut().FromImage(WindowCapture(), 50, 25);

        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        point.Error.Should().Contain("closed");
    }

    [Fact]
    public void FromImage_AReusedHandle_IsStaleCapture()
    {
        // The window closed and another process's window got its handle, at the same place
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(9999u);

        var point = CreateSut().FromImage(WindowCapture(), 50, 25);

        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        point.Error.Should().Contain("another process");
    }

    [Fact]
    public void FromImage_AMinimizedWindow_IsStaleCapture()
    {
        // restoreIfMinimized captured it and minimized it again
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);

        var point = CreateSut().FromImage(WindowCapture(), 50, 25);

        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        point.Error.Should().Contain("minimized");
    }

    [Fact]
    public void FromImage_TheRecheck_SeesAWindowThatMovedAfterTheConversion()
    {
        // Arrange - converted while the window was in place; then it moved while the call waited for the gate
        var point = CreateSut().FromImage(WindowCapture(), 50, 25);
        _rect = (300, 200, 1116, 839);

        // Act
        var refusal = point.Recheck!();

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(InputErrorCode.StaleCapture));
    }

    [Fact]
    public void FromImage_AScreenCapture_MapsWithoutAWindow()
    {
        var point = CreateSut().FromImage(ScreenCapture(), 100, 50);

        point.Resolved.Should().BeTrue(point.Error);
        (point.X, point.Y).Should().Be((-1820, 50));
        point.CapturedWindow.Should().BeNull();
    }

    [Fact]
    public void FromImage_AScreenCapture_AfterTheMonitorsChanged_IsStaleCapture()
    {
        var id = ScreenCapture();
        _virtualScreen = (0, 0, 1920, 1080);

        var point = CreateSut().FromImage(id, 100, 50);

        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        point.Error.Should().Contain("monitors");
    }

    [Fact]
    public void FromImage_AScreenCapture_AfterTwoMonitorsSwappedInsideTheSameVirtualScreen_IsStaleCapture()
    {
        // Arrange - three 1920 x 1080 monitors in a row; the user swaps the outer two, so the virtual screen stays the same
        // but the left third of the image now shows what is on the right
        MonitorInfo Monitor(int index, string name, int x) => new(index, name, x == 0, new MonitorRect(x, 0, 1920, 1080),
            new MonitorRect(x, 0, 1920, 1040), 1.0);
        var monitors = new List<MonitorInfo> { Monitor(0, "DISPLAY1", 0), Monitor(1, "DISPLAY2", -1920), Monitor(2, "DISPLAY3", 1920) };
        var monitorService = new Mock<IMonitorService>();
        monitorService.Setup(m => m.GetAllMonitors()).Returns(() => monitors);
        _virtualScreen = (-1920, 0, 5760, 1080);
        var id = _registry.Register(new CaptureGeometry(5760, 1080, new ImageToScreenTransform(-1920, 0, 1, 1),
            new CapturedRegion(-1920, 0, 5760, 1080), null, new VirtualScreenBounds(-1920, 0, 5760, 1080),
            CapturedMonitors.Of(monitors))).CaptureId;
        var sut = new CoordinateSpaceResolver(_registry, _windows.Object, _hitTest.Object, _screen.Object, monitorService.Object);
        sut.FromImage(id, 100, 50).Resolved.Should().BeTrue("nothing changed yet");
        monitors = [Monitor(0, "DISPLAY1", 0), Monitor(1, "DISPLAY2", 1920), Monitor(2, "DISPLAY3", -1920)];

        // Act
        var point = sut.FromImage(id, 100, 50);

        // Assert
        point.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        point.Error.Should().Contain("monitors");
    }

    [Fact]
    public void FromWindow_Window_IsRelativeToTheWindowRectangle()
    {
        var point = CreateSut().FromWindow(CoordinateSpace.Window, Window, 10, 5);

        point.Resolved.Should().BeTrue(point.Error);
        (point.X, point.Y).Should().Be((110, 205));
        point.CapturedWindow.Should().BeNull();
        point.Recheck!().Should().BeNull();
    }

    [Fact]
    public void FromWindow_Client_IsRelativeToTheClientAreaOnScreen()
    {
        // A mirrored window's client area is mapped with left < right too: x still counts to the right
        var point = CreateSut().FromWindow(CoordinateSpace.Client, Window, 10, 5);

        (point.X, point.Y).Should().Be((118, 236));
    }

    [Theory]
    [InlineData(CoordinateSpace.Window)]
    [InlineData(CoordinateSpace.Client)]
    public void FromWindow_AWindowThatCannotBeRead_IsInvalidHandle(CoordinateSpace space)
    {
        _windows.Setup(w => w.GetWindowRect(Window)).Returns(((int, int, int, int)?)null);
        _client = null;

        CreateSut().FromWindow(space, Window, 10, 5).ErrorCode.Should().Be(InputErrorCode.InvalidHandle);
    }

    [Theory]
    [InlineData(CoordinateSpace.Window)]
    [InlineData(CoordinateSpace.Client)]
    public void FromWindow_AMinimizedWindow_IsCoordinatesOutOfBounds(CoordinateSpace space)
    {
        _windows.Setup(w => w.IsIconic(Window)).Returns(true);

        var point = CreateSut().FromWindow(space, Window, 10, 5);

        point.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        point.Error.Should().Contain("minimized");
    }

    [Theory]
    [InlineData(CoordinateSpace.Window)]
    [InlineData(CoordinateSpace.Client)]
    public void FromWindow_TheRecheck_SeesAWindowThatMovedAfterTheConversion(CoordinateSpace space)
    {
        var point = CreateSut().FromWindow(space, Window, 10, 5);
        _rect = (400, 200, 1216, 839);
        _client = new WindowRect(408, 231, 800, 600);

        var refusal = point.Recheck!();

        refusal!.ErrorCode.Should().Be(nameof(InputErrorCode.StaleCapture));
        refusal.Error.Should().Contain("moved");
    }

    [Fact]
    public void FromWindow_Screen_IsNotAWindowSpace()
    {
        var act = () => CreateSut().FromWindow(CoordinateSpace.Screen, Window, 10, 5);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
