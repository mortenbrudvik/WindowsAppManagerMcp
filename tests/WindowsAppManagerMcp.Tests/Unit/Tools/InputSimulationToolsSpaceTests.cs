using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// Roadmap M3.11 in the mouse tools: space and captureId convert the point before the guarded input path, the captured
/// window becomes the expected target, and the result echoes the requested point.
/// </summary>
public class InputSimulationToolsSpaceTests
{
    private const nint Captured = 0x5150;
    private const nint Other = 0x6160;
    private const string CaptureId = "c3-abcdef";

    private readonly Mock<IInputSimulationService> _input = new();
    private readonly Mock<ICoordinateSpaceResolver> _resolver = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly SelectorRefusal _stale = new(nameof(InputErrorCode.StaleCapture), "moved");
    private InputTarget? _sentTarget;
    private (int X, int Y)? _sentPoint;

    public InputSimulationToolsSpaceTests()
    {
        _input.Setup(s => s.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget target, bool _, CancellationToken _) => Sent(x, y, target, new ClickResult(true, x, y, "left")));
        _input.Setup(s => s.RightClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget target, bool _, CancellationToken _) => Sent(x, y, target, new ClickResult(true, x, y, "right")));
        _input.Setup(s => s.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget target, bool _, CancellationToken _) => Sent(x, y, target, new ClickResult(true, x, y, "double")));
        _input.Setup(s => s.MoveMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget target, bool _, CancellationToken _) => Sent(x, y, target, new MouseMoveResult(true, x, y)));
        _input.Setup(s => s.Scroll(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int? x, int? y, int dy, int dx, InputTarget target, bool _, CancellationToken _) =>
                Sent(x ?? 0, y ?? 0, target, new ScrollResult(true, x ?? 0, y ?? 0, dy, dx)));

        // Image (50, 25) of a capture of window 0x5150 is screen (208, 281); window points are offset by (100, 200)
        _resolver.Setup(r => r.FromImage(CaptureId, It.IsAny<int>(), It.IsAny<int>()))
            .Returns((string _, int x, int y) => PointResolution.At(108 + 2 * x, 231 + 2 * y, () => _stale, Captured));
        _resolver.Setup(r => r.FromWindow(CoordinateSpace.Window, It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((CoordinateSpace _, nint _, int x, int y) => PointResolution.At(100 + x, 200 + y, () => _stale));
        _resolver.Setup(r => r.FromWindow(CoordinateSpace.Client, It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((CoordinateSpace _, nint _, int x, int y) => PointResolution.At(108 + x, 231 + y, () => _stale));
    }

    private T Sent<T>(int x, int y, InputTarget target, T result)
    {
        _sentPoint = (x, y);
        _sentTarget = target;
        return result;
    }

    private InputSimulationTools CreateSut() => new(_input.Object, _windows.Object, _resolver.Object);

    [Fact]
    public async Task Click_WithoutSpace_SendsTheScreenPointAsBefore()
    {
        var result = await CreateSut().Click(300, 400).Payload<ClickResult>();

        _sentPoint.Should().Be((300, 400));
        result.Requested.Should().BeNull("a screen point needs no echo");
        _resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Click_WithACaptureId_ConvertsTheImagePoint_AndTargetsTheCapturedWindow()
    {
        // Act - space is left out: a captureId means image coordinates
        var result = await CreateSut().Click(50, 25, captureId: CaptureId).Payload<ClickResult>();

        // Assert
        _sentPoint.Should().Be((208, 281));
        _sentTarget!.Value.WindowHandle.Should().Be(Captured);
        _sentTarget.Value.AllowHostTarget.Should().BeFalse();
        _sentTarget.Value.PointRecheck!().Should().Be(_stale, "the guard checks the capture again inside the gate");
        result.X.Should().Be(208);
        result.Y.Should().Be(281);
        result.Requested.Should().Be(new RequestedPoint(50, 25, "image", CaptureId));
    }

    [Fact]
    public async Task Click_ImageSpace_WithTheCapturedWindowAsWindowHandle_KeepsItsHostRule()
    {
        await CreateSut().Click(50, 25, windowHandle: Captured, allowHostTarget: true, space: CoordinateSpace.Image, captureId: CaptureId);

        _sentTarget.Should().Be(InputTarget.Window(Captured, allowHostTarget: true));
    }

    [Fact]
    public async Task Click_ImageSpace_WithAnotherWindowHandle_IsRefused_AndSendsNothing()
    {
        // A capture of window A must never become a click on window B
        var act = () => CreateSut().Click(50, 25, windowHandle: Other, captureId: CaptureId);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _sentPoint.Should().BeNull();
    }

    [Fact]
    public async Task Click_ImageSpace_WithAllowHostTargetButNoHandle_DoesNotLiftTheHostGuard()
    {
        await CreateSut().Click(50, 25, allowHostTarget: true, captureId: CaptureId);

        _sentTarget.Should().Be(InputTarget.Window(Captured, allowHostTarget: false));
    }

    [Fact]
    public async Task Click_ImageSpace_OfAScreenCapture_KeepsTheCallsTarget()
    {
        // A monitor capture names no window: the click has the usual rules for the point, plus the recheck
        _resolver.Setup(r => r.FromImage("c4-abcdef", 100, 50)).Returns(PointResolution.At(-1820, 50, () => null));

        await CreateSut().Click(100, 50, captureId: "c4-abcdef");

        _sentPoint.Should().Be((-1820, 50));
        _sentTarget.Should().Be(InputTarget.None);
        _sentTarget!.Value.PointRecheck.Should().NotBeNull();
    }

    [Theory]
    [InlineData(InputErrorCode.CaptureNotFound)]
    [InlineData(InputErrorCode.StaleCapture)]
    [InlineData(InputErrorCode.CoordinatesOutOfBounds)]
    public async Task Click_AnImagePointThatCannotBeConverted_FailsWithItsCode_AndSendsNothing(InputErrorCode code)
    {
        _resolver.Setup(r => r.FromImage(CaptureId, 50, 25)).Returns(PointResolution.Fail(code, "why"));

        var result = await CreateSut().Click(50, 25, captureId: CaptureId).Payload<ClickResult>();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(code);
        result.Error.Should().Be("why");
        result.Requested.Should().Be(new RequestedPoint(50, 25, "image", CaptureId));
        _sentPoint.Should().BeNull();
    }

    [Fact]
    public async Task Click_ImageSpaceWithoutACaptureId_IsInvalidArguments()
    {
        var act = () => CreateSut().Click(50, 25, space: CoordinateSpace.Image);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain("captureId");
    }

    [Theory]
    [InlineData(CoordinateSpace.Screen)]
    [InlineData(CoordinateSpace.Window)]
    [InlineData(CoordinateSpace.Client)]
    public async Task Click_ACaptureIdWithAnotherSpace_IsInvalidArguments(CoordinateSpace space)
    {
        var act = () => CreateSut().Click(50, 25, windowHandle: Captured, space: space, captureId: CaptureId);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _sentPoint.Should().BeNull();
    }

    [Theory]
    [InlineData(CoordinateSpace.Window)]
    [InlineData(CoordinateSpace.Client)]
    public async Task Click_WindowOrClientSpace_WithoutAWindow_IsInvalidArguments(CoordinateSpace space)
    {
        var act = () => CreateSut().Click(10, 5, space: space);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.Message.Should().Contain("windowHandle");
    }

    [Theory]
    [InlineData(CoordinateSpace.Window, 110, 205)]
    [InlineData(CoordinateSpace.Client, 118, 236)]
    public async Task Click_WindowOrClientSpace_IsRelativeToTheTarget(CoordinateSpace space, int screenX, int screenY)
    {
        // Act
        var result = await CreateSut().Click(10, 5, windowHandle: Captured, space: space).Payload<ClickResult>();

        // Assert
        _resolver.Verify(r => r.FromWindow(space, Captured, 10, 5), Times.Once);
        _sentPoint.Should().Be((screenX, screenY));
        _sentTarget.Should().Be(InputTarget.Window(Captured));
        _sentTarget!.Value.PointRecheck!().Should().Be(_stale);
        result.Requested.Should().Be(new RequestedPoint(10, 5, ToolEnum<CoordinateSpace>.Name(space)));
    }

    [Fact]
    public async Task Click_WindowSpace_WithASelector_IsRelativeToTheWindowItNames()
    {
        // Arrange
        var selector = new WindowSelector { ProcessName = "notepad" };
        _windows.Setup(w => w.ResolveWindow(selector)).Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo(handleValue: (int)Captured)));

        // Act
        await CreateSut().Click(10, 5, window: selector, space: CoordinateSpace.Window);

        // Assert - the selector's own recheck stays, the point's is added
        _resolver.Verify(r => r.FromWindow(CoordinateSpace.Window, Captured, 10, 5), Times.Once);
        _sentTarget!.Value.WindowHandle.Should().Be(Captured);
        _sentTarget.Value.SelectorRecheck.Should().NotBeNull();
        _sentTarget.Value.PointRecheck.Should().NotBeNull();
    }

    [Fact]
    public async Task RightClick_DoubleClick_AndMouseMove_ConvertTheirPoints()
    {
        var sut = CreateSut();

        (await sut.RightClick(50, 25, captureId: CaptureId).Payload<ClickResult>()).Requested!.Space.Should().Be("image");
        _sentPoint.Should().Be((208, 281));
        (await sut.DoubleClick(10, 5, windowHandle: Captured, space: CoordinateSpace.Window).Payload<ClickResult>()).X.Should().Be(110);
        _sentPoint.Should().Be((110, 205));
        sut.MouseMove(10, 5, windowHandle: Captured, space: CoordinateSpace.Client).Requested.Should().Be(new RequestedPoint(10, 5, "client"));
        _sentPoint.Should().Be((118, 236));
    }

    [Fact]
    public void MouseMove_ACaptureThatCannotBeConverted_FailsWithItsCode()
    {
        _resolver.Setup(r => r.FromImage(CaptureId, 1, 1)).Returns(PointResolution.Fail(InputErrorCode.StaleCapture, "moved"));

        var result = CreateSut().MouseMove(1, 1, captureId: CaptureId);

        result.ErrorCode.Should().Be(InputErrorCode.StaleCapture);
        _sentPoint.Should().BeNull();
    }

    [Fact]
    public void Scroll_AtAnImagePoint_ScrollsTheCapturedWindowThere()
    {
        // Act
        var result = CreateSut().Scroll(x: 50, y: 25, deltaY: 3, captureId: CaptureId);

        // Assert
        _sentPoint.Should().Be((208, 281));
        _sentTarget!.Value.WindowHandle.Should().Be(Captured);
        result.Requested.Should().Be(new RequestedPoint(50, 25, "image", CaptureId));
    }

    [Fact]
    public void Scroll_AConvertedSpaceWithoutAPoint_IsInvalidArguments()
    {
        var act = () => CreateSut().Scroll(windowHandle: Captured, deltaY: 3, space: CoordinateSpace.Client);

        act.Should().Throw<ToolErrorException>().Which.Message.Should().Contain("x and y");
    }

    [Fact]
    public void Scroll_ACaptureThatCannotBeConverted_FailsWithItsCode()
    {
        _resolver.Setup(r => r.FromImage(CaptureId, 1, 1)).Returns(PointResolution.Fail(InputErrorCode.CaptureNotFound, "gone"));

        var result = CreateSut().Scroll(x: 1, y: 1, deltaY: 1, captureId: CaptureId);

        result.ErrorCode.Should().Be(InputErrorCode.CaptureNotFound);
        result.DeltaY.Should().Be(1);
        _sentPoint.Should().BeNull();
    }
}
