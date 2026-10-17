using ModelContextProtocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The wait_for_visual tool (roadmap M6.4): exactly one target, every value in its range (a value out of range is refused,
/// never clamped), and the request reaches the visual change service as given. The service is a mock; nothing reads pixels.
/// The waits themselves are VisualChangeServiceTests'.
/// </summary>
public class WaitForVisualToolTests
{
    private const nint Notepad = 0x1A0B2C;

    private readonly Mock<IVisualChangeService> _visual = new();
    private readonly Mock<IWindowService> _windows = new();
    private VisualWaitRequest? _request;

    public WaitForVisualToolTests()
    {
        _visual.Setup(v => v.WaitAsync(It.IsAny<VisualWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((VisualWaitRequest request, IProgress<OperationProgress>? _, CancellationToken _) => _request = request)
            .ReturnsAsync(new VisualWaitResult(Success: true, Satisfied: true, TimedOut: false, Condition: "changed", ElapsedMs: 412, Checks: 3,
                Reason: "pixels", ChangedPercent: 0.42, ChangedRegions: [new BoundsDto(288, 116, 64, 48)]));
        _windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo((int)Notepad, "notes.txt - Notepad", "notepad", 200)));
    }

    private VisualTools CreateTools() => new(_visual.Object, _windows.Object);

    [Fact]
    public async Task AHandle_IsAWindowTarget_WithItsFrame_AndTheDefaults()
    {
        var result = await CreateTools().WaitForVisual(handle: Notepad);

        result.Satisfied.Should().BeTrue();
        _request.Should().BeEquivalentTo(new VisualWaitRequest
        {
            Target = new VisualTarget.Window(Notepad, IncludeFrame: true),
            Condition = VisualWaitCondition.Changed,
            TimeoutMs = 10_000,
            PollMs = 200,
            StableMs = 500,
            MinChangedPercent = 0
        });
    }

    [Fact]
    public async Task AWindowSelector_IsResolvedToItsWindow()
    {
        await CreateTools().WaitForVisual(window: new WindowSelector { ProcessName = "notepad" }, includeFrame: false);

        _request!.Target.Should().Be(new VisualTarget.Window(Notepad, IncludeFrame: false));
    }

    [Fact]
    public async Task ARegion_AMonitor_AndSince_AreTheirTargets()
    {
        var tools = CreateTools();

        await tools.WaitForVisual(x: -10, y: 20, width: 300, height: 200);
        _request!.Target.Should().Be(new VisualTarget.Region(-10, 20, 300, 200));

        await tools.WaitForVisual(monitor: MonitorSelector.OfIndex(-1));
        _request!.Target.Should().Be(new VisualTarget.Monitor(-1));

        await tools.WaitForVisual(since: " c7-3fa91c ");
        _request!.Target.Should().Be(new VisualTarget.Since("c7-3fa91c"));
    }

    [Fact]
    public async Task TheValues_ReachTheService_AsGiven()
    {
        await CreateTools().WaitForVisual(VisualWaitCondition.Stable, handle: Notepad, timeoutMs: 60_000, pollMs: 2000, stableMs: 10_000,
            minChangedPercent: 1.5);

        _request!.Condition.Should().Be(VisualWaitCondition.Stable);
        _request.TimeoutMs.Should().Be(60_000);
        _request.PollMs.Should().Be(2000);
        _request.StableMs.Should().Be(10_000);
        _request.MinChangedPercent.Should().Be(1.5);
    }

    [Fact]
    public async Task TimeoutZero_WithSince_ComparesOnce()
    {
        await CreateTools().WaitForVisual(since: "c7-3fa91c", timeoutMs: 0);

        _request!.TimeoutMs.Should().Be(0);
    }

    public static TheoryData<string, Func<VisualTools, Task>> InvalidCalls => new()
    {
        { "one target", tools => tools.WaitForVisual() },
        { "one target only", tools => tools.WaitForVisual(handle: Notepad, monitor: MonitorSelector.OfIndex(0)) },
        { "one target only", tools => tools.WaitForVisual(since: "c7-3fa91c", handle: Notepad) },
        { "one target only", tools => tools.WaitForVisual(x: 0, y: 0, width: 10, height: 10, monitor: MonitorSelector.OfIndex(0)) },
        { "x, y, width and height", tools => tools.WaitForVisual(x: 0, y: 0, width: 10) },
        { "includeFrame", tools => tools.WaitForVisual(monitor: MonitorSelector.OfIndex(0), includeFrame: true) },
        { "includeFrame", tools => tools.WaitForVisual(since: "c7-3fa91c", includeFrame: false) },
        { "since", tools => tools.WaitForVisual(since: "  ") },
        { "timeoutMs", tools => tools.WaitForVisual(handle: Notepad, timeoutMs: -1) },
        { "timeoutMs", tools => tools.WaitForVisual(handle: Notepad, timeoutMs: 60_001) },
        { "pollMs", tools => tools.WaitForVisual(handle: Notepad, pollMs: 99) },
        { "pollMs", tools => tools.WaitForVisual(handle: Notepad, pollMs: 2001) },
        { "minChangedPercent", tools => tools.WaitForVisual(handle: Notepad, minChangedPercent: -0.1) },
        { "minChangedPercent", tools => tools.WaitForVisual(handle: Notepad, minChangedPercent: 100) },
        { "minChangedPercent", tools => tools.WaitForVisual(handle: Notepad, minChangedPercent: double.NaN) },
        { "stableMs", tools => tools.WaitForVisual(handle: Notepad, stableMs: 500) },
        { "stableMs", tools => tools.WaitForVisual(VisualWaitCondition.Stable, handle: Notepad, stableMs: -1) },
        { "stableMs", tools => tools.WaitForVisual(VisualWaitCondition.Stable, handle: Notepad, stableMs: 10_001, timeoutMs: 20_000) },
        { "stableMs", tools => tools.WaitForVisual(VisualWaitCondition.Stable, handle: Notepad, stableMs: 2000, timeoutMs: 1000) },
        { "timeoutMs 0", tools => tools.WaitForVisual(handle: Notepad, timeoutMs: 0) },
        { "timeoutMs 0", tools => tools.WaitForVisual(VisualWaitCondition.Stable, handle: Notepad, timeoutMs: 0, stableMs: 0) },
    };

    [Theory]
    [MemberData(nameof(InvalidCalls))]
    public async Task InvalidArguments_AreRefused_NotClamped_BeforeAnythingWaits(string says, Func<VisualTools, Task> call)
    {
        var act = () => call(CreateTools());

        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain(says).And.EndWith("Nothing was waited for.");
        _visual.Verify(v => v.WaitAsync(It.IsAny<VisualWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnOutOfRangeValue_IsRefused_BeforeTheSelectorIsResolved()
    {
        var act = () => CreateTools().WaitForVisual(window: new WindowSelector { ProcessName = "notepad" }, timeoutMs: 70_000);

        await act.Should().ThrowAsync<ToolErrorException>();
        _windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }
}
