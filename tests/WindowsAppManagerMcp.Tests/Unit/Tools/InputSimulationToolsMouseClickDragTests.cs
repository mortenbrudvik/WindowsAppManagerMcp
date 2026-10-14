using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// mouse_click and drag (roadmap M4.3) as tools: their arguments reach the input service, their ranges are refused before
/// anything is sent, and drag converts both points through space and captureId (roadmap M3.F28) against their own windows.
/// </summary>
public class InputSimulationToolsMouseClickDragTests
{
    private const nint Captured = 0x5150;
    private const nint Source = 0x6160;
    private const nint DropWindow = 0x7170;
    private const string CaptureId = "c3-abcdef";

    private readonly Mock<IInputSimulationService> _input = new();
    private readonly Mock<ICoordinateSpaceResolver> _resolver = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly SelectorRefusal _stale = new(nameof(InputErrorCode.StaleCapture), "moved");
    private DragRequest? _drag;
    private InputTarget? _dragTarget, _dropTarget;

    public InputSimulationToolsMouseClickDragTests()
    {
        _input.Setup(s => s.MouseClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<MouseModifier>>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, MouseButton button, int count, IReadOnlyList<MouseModifier> modifiers, InputTarget _, bool _, CancellationToken _) =>
                new ClickResult(true, x, y, button.ToString()) { ClickCount = count, Modifiers = modifiers.Select(m => m.ToString()).ToList() });
        _input.Setup(s => s.DragAsync(It.IsAny<DragRequest>(), It.IsAny<InputTarget>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns((DragRequest request, InputTarget target, InputTarget drop, bool _, CancellationToken _) =>
            {
                _drag = request;
                _dragTarget = target;
                _dropTarget = drop;
                return Task.FromResult(new DragResult(true, request.FromX, request.FromY, request.ToX, request.ToY) { Pressed = true });
            });

        // Image (x, y) of a capture of window 0x5150 is screen (108 + 2x, 231 + 2y); window points are offset by the handle's
        // own origin, (100, 200) for the source and (1000, 200) for the drop window
        _resolver.Setup(r => r.FromImage(CaptureId, It.IsAny<int>(), It.IsAny<int>()))
            .Returns((string _, int x, int y) => PointResolution.At(108 + 2 * x, 231 + 2 * y, () => _stale, Captured));
        _resolver.Setup(r => r.FromWindow(CoordinateSpace.Window, Source, It.IsAny<int>(), It.IsAny<int>()))
            .Returns((CoordinateSpace _, nint _, int x, int y) => PointResolution.At(100 + x, 200 + y, () => null));
        _resolver.Setup(r => r.FromWindow(CoordinateSpace.Window, DropWindow, It.IsAny<int>(), It.IsAny<int>()))
            .Returns((CoordinateSpace _, nint _, int x, int y) => PointResolution.At(1000 + x, 200 + y, () => null));
    }

    private InputSimulationTools CreateSut() => new(_input.Object, _windows.Object, _resolver.Object);

    #region mouse_click

    [Fact]
    public async Task MouseClick_PassesTheButtonCountAndModifiers()
    {
        var result = await CreateSut().MouseClick(300, 400, MouseButton.Middle, 3, [MouseModifier.Ctrl, MouseModifier.Alt],
            windowHandle: Source).Payload<ClickResult>();

        result.Success.Should().BeTrue();
        _input.Verify(s => s.MouseClick(300, 400, MouseButton.Middle, 3,
            It.Is<IReadOnlyList<MouseModifier>>(m => m.SequenceEqual(new[] { MouseModifier.Ctrl, MouseModifier.Alt })),
            InputTarget.Window(Source, false), false, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task MouseClick_Defaults_AreOnePrimaryClickWithoutModifiers()
    {
        await CreateSut().MouseClick(300, 400);

        _input.Verify(s => s.MouseClick(300, 400, MouseButton.Primary, 1, It.Is<IReadOnlyList<MouseModifier>>(m => m.Count == 0),
            InputTarget.None, false, It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task MouseClick_AClickCountOutsideOneToThree_IsInvalidArguments_AndSendsNothing(int count)
    {
        var act = () => CreateSut().MouseClick(300, 400, clickCount: count);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MouseClick_WithACaptureId_ConvertsThePoint_AndTargetsTheCapturedWindow()
    {
        var result = await CreateSut().MouseClick(50, 25, MouseButton.Secondary, captureId: CaptureId).Payload<ClickResult>();

        _input.Verify(s => s.MouseClick(208, 281, MouseButton.Secondary, 1, It.IsAny<IReadOnlyList<MouseModifier>>(),
            It.Is<InputTarget>(t => t.WindowHandle == Captured && t.PointRecheck != null), false, It.IsAny<CancellationToken>()));
        result.Requested.Should().Be(new RequestedPoint(50, 25, "image", CaptureId));
    }

    #endregion

    #region drag

    [Fact]
    public async Task Drag_ScreenPoints_GoAsGiven_WithWindowHandleAsTargetForBoth()
    {
        var result = await CreateSut().Drag(10, 20, 30, 40, MouseButton.Secondary, [MouseModifier.Shift], steps: 5, stepDelayMs: 7,
            holdBeforeMoveMs: 300, windowHandle: Source).Payload<DragResult>();

        result.Success.Should().BeTrue();
        _drag.Should().BeEquivalentTo(new DragRequest(10, 20, 30, 40, MouseButton.Secondary, [MouseModifier.Shift], 5, 7, 300));
        _dragTarget.Should().Be(InputTarget.Window(Source));
        _dropTarget.Should().Be(InputTarget.Window(Source));
        result.RequestedFrom.Should().BeNull();
        result.RequestedTo.Should().BeNull();
    }

    [Fact]
    public async Task Drag_Defaults_TenStepsOf20MsAfterAHoldOf100Ms()
    {
        await CreateSut().Drag(10, 20, 30, 40);

        _drag!.Steps.Should().Be(10);
        _drag.StepDelayMs.Should().Be(20);
        _drag.HoldBeforeMoveMs.Should().Be(100);
        _drag.Button.Should().Be(MouseButton.Primary);
        _drag.Modifiers.Should().BeEmpty();
        _dragTarget.Should().Be(InputTarget.None);
        _dropTarget.Should().Be(InputTarget.None);
    }

    [Fact]
    public async Task Drag_ToWindowHandle_IsTheDropTarget_AndToIsMeasuredFromIt()
    {
        // From File Explorer into an app: from is on windowHandle, to on toWindowHandle, each in its own window's space
        await CreateSut().Drag(5, 6, 7, 8, windowHandle: Source, toWindowHandle: DropWindow, allowHostTarget: true,
            space: CoordinateSpace.Window);

        _drag!.FromX.Should().Be(105);
        _drag.FromY.Should().Be(206);
        _drag.ToX.Should().Be(1007);
        _drag.ToY.Should().Be(208);
        _dragTarget!.Value.WindowHandle.Should().Be(Source);
        _dropTarget!.Value.WindowHandle.Should().Be(DropWindow);
        _dropTarget.Value.AllowHostTarget.Should().BeTrue("allowHostTarget applies to windows named by handle, and toWindowHandle is one");
    }

    [Fact]
    public async Task Drag_WithACaptureId_ConvertsBothPoints_TargetsTheCapturedWindow_AndEchoesThem()
    {
        var result = await CreateSut().Drag(50, 25, 60, 30, captureId: CaptureId).Payload<DragResult>();

        (_drag!.FromX, _drag.FromY, _drag.ToX, _drag.ToY).Should().Be((208, 281, 228, 291));
        _dragTarget!.Value.WindowHandle.Should().Be(Captured);
        _dropTarget!.Value.WindowHandle.Should().Be(Captured);
        _dragTarget.Value.PointRecheck.Should().NotBeNull("the guard checks the capture again before the press");
        _dropTarget.Value.PointRecheck.Should().NotBeNull("and before the drop");
        result.RequestedFrom.Should().Be(new RequestedPoint(50, 25, "image", CaptureId));
        result.RequestedTo.Should().Be(new RequestedPoint(60, 30, "image", CaptureId));
    }

    [Fact]
    public async Task Drag_ACaptureOfOneWindow_WithAnotherDropWindow_IsInvalidArguments()
    {
        // A point of window A's image never becomes input to window B: drop across windows with screen points
        var act = () => CreateSut().Drag(50, 25, 60, 30, toWindowHandle: DropWindow, captureId: CaptureId);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _drag.Should().BeNull();
    }

    [Fact]
    public async Task Drag_AToPointThatCannotBeConverted_FailsWithItsCode_AndSendsNothing()
    {
        _resolver.Setup(r => r.FromImage(CaptureId, 60, 30)).Returns(PointResolution.Fail(InputErrorCode.CoordinatesOutOfBounds, "outside the image"));

        var result = await CreateSut().Drag(50, 25, 60, 30, MouseButton.Primary, [MouseModifier.Ctrl], captureId: CaptureId)
            .Payload<DragResult>();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(InputErrorCode.CoordinatesOutOfBounds);
        result.Error.Should().Be("The to point: outside the image");
        result.Pressed.Should().BeFalse();
        result.Modifiers.Should().Equal("ctrl");
        result.RequestedTo.Should().Be(new RequestedPoint(60, 30, "image", CaptureId));
        _drag.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 20, 100)]
    [InlineData(101, 20, 100)]
    [InlineData(10, -1, 100)]
    [InlineData(10, 1001, 100)]
    [InlineData(10, 20, 2001)]
    [InlineData(100, 100, 1)] // 1 ms over the 10 s bound
    public async Task Drag_OutOfRange_IsInvalidArguments_AndSendsNothing(int steps, int stepDelayMs, int holdBeforeMoveMs)
    {
        var act = () => CreateSut().Drag(10, 20, 30, 40, steps: steps, stepDelayMs: stepDelayMs, holdBeforeMoveMs: holdBeforeMoveMs);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _drag.Should().BeNull();
    }

    #endregion
}
