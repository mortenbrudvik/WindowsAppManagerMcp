using ModelContextProtocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The wait_for_window tool (roadmap M3.7): its arguments are checked before anything waits (InvalidArguments), the
/// request reaches the window wait service as given, and the service's outcome becomes the result: satisfied with the
/// window (or the candidates when several windows satisfy it), or not satisfied with its errorCode (WaitTimedOut on
/// timeout), which the tool-error filter turns into isError. The service is a mock; nothing reads the real desktop.
/// </summary>
public class WaitForWindowToolTests
{
    private static readonly WindowSelector Notepad = new() { ProcessName = "notepad" };

    private static readonly WindowInfo NotepadWindow =
        TestDataFactory.CreateWindowInfo(0x1A0B2C, "notes.txt - Notepad", "notepad", 200) with { ClassName = "Notepad" };

    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IWindowWaitService> _wait = new();
    private WindowConditionRequest? _request;

    public WaitForWindowToolTests()
    {
        Returns(new WindowConditionResult { Satisfied = true, Window = NotepadWindow, MatchCount = 1, ElapsedMs = 420 });
    }

    private void Returns(WindowConditionResult result) =>
        _wait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((WindowConditionRequest request, IProgress<OperationProgress>? _, CancellationToken _) => _request = request)
            .ReturnsAsync(result);

    private WindowFinderTools CreateTools() => new(_windows.Object, windowWait: _wait.Object);

    [Fact]
    public async Task ASatisfiedWait_ReturnsTheWindow()
    {
        var result = await CreateTools().WaitForWindow(Notepad);

        result.Success.Should().BeTrue();
        result.Satisfied.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.ElapsedMs.Should().Be(420);
        result.Window!.Handle.Should().Be(0x1A0B2C);
        result.Window.Title.Should().Be("notes.txt - Notepad");
        result.MatchCount.Should().BeNull("one window needs no count");
        result.Candidates.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task TheRequest_ReachesTheService_AsGiven()
    {
        await CreateTools().WaitForWindow(Notepad, WindowWaitCondition.Foreground, excludeHandles: [0x10, 0x20], timeoutMs: 2500, stableMs: 0);

        _request.Should().BeEquivalentTo(new WindowConditionRequest
        {
            Window = Notepad,
            Condition = WindowWaitCondition.Foreground,
            ExcludeHandles = [(nint)0x10, (nint)0x20],
            TimeoutMs = 2500,
            StableMs = 0
        });
    }

    [Fact]
    public async Task TheDefaults_AreExists_10Seconds_And300Ms()
    {
        await CreateTools().WaitForWindow(Notepad);

        _request!.Condition.Should().Be(WindowWaitCondition.Exists);
        _request.TimeoutMs.Should().Be(10_000);
        _request.StableMs.Should().Be(300);
        _request.NewOnly.Should().BeFalse();
        _request.ExcludeHandles.Should().BeEmpty();
    }

    [Fact]
    public async Task SeveralSatisfyingWindows_AreReportedAsCandidates_WithoutAWindow()
    {
        Returns(new WindowConditionResult
        {
            Satisfied = true,
            MatchCount = 7,
            Candidates = [NotepadWindow, NotepadWindow with { Handle = 0x2000, Title = "Untitled - Notepad" }]
        });

        var result = await CreateTools().WaitForWindow(Notepad);

        result.Success.Should().BeTrue();
        result.Window.Should().BeNull();
        result.MatchCount.Should().Be(7);
        result.Candidates!.Select(c => c.Handle).Should().Equal(0x1A0B2C, 0x2000);
    }

    [Fact]
    public async Task ATimeout_IsAFailure_WithWaitTimedOut_AndTheWindowAsLastSeen()
    {
        Returns(new WindowConditionResult
        {
            TimedOut = true,
            ElapsedMs = 10_000,
            Window = NotepadWindow,
            PreviousTitle = "*notes.txt - Notepad",
            ErrorCode = nameof(WindowErrorCode.WaitTimedOut),
            Error = "The title of window 1706796 is still '*notes.txt - Notepad' after 10000 ms."
        });

        var result = await CreateTools().WaitForWindow(new WindowSelector { Handle = 0x1A0B2C }, WindowWaitCondition.TitleChanged);

        result.Success.Should().BeFalse("a wait that was not satisfied is a failure, so the client gets isError");
        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.Error.Should().Contain("still");
        result.Window!.Handle.Should().Be(0x1A0B2C);
        result.PreviousTitle.Should().Be("*notes.txt - Notepad");
    }

    [Fact]
    public async Task AnEarlyEnd_KeepsItsErrorCode_AndCandidates()
    {
        Returns(new WindowConditionResult
        {
            ErrorCode = nameof(WindowErrorCode.AmbiguousWindow),
            Error = "2 windows match processName 'chrome'.",
            MatchCount = 2,
            Candidates = [NotepadWindow, NotepadWindow with { Handle = 0x2000 }]
        });

        var result = await CreateTools().WaitForWindow(Notepad, WindowWaitCondition.InputIdle);

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be("AmbiguousWindow");
        result.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task ASelectorTheServiceCannotUse_IsInvalidArguments()
    {
        Returns(new WindowConditionResult { ErrorCode = "InvalidArguments", Error = "window.titleContains is blank." });

        var act = () => CreateTools().WaitForWindow(new WindowSelector { TitleContains = " " });

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task InputIdle_ReportsHowReadinessWasChecked()
    {
        Returns(new WindowConditionResult { Satisfied = true, Window = NotepadWindow, MatchCount = 1, IdleCheck = IdleCheck.RespondingOnly });

        var result = await CreateTools().WaitForWindow(Notepad, WindowWaitCondition.InputIdle);

        result.IdleCheck.Should().Be("respondingOnly");
    }

    [Fact]
    public async Task AGoneWaitWhoseSelectorMatchedNothing_CarriesTheWarning()
    {
        Returns(new WindowConditionResult { Satisfied = true, Warning = "No window matched titleEquals 'Sav As' when the wait started." });

        var result = await CreateTools().WaitForWindow(new WindowSelector { TitleEquals = "Sav As" }, WindowWaitCondition.Gone);

        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("No window matched");
    }

    [Fact]
    public async Task Progress_ReachesTheClient()
    {
        // Arrange
        _wait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WindowConditionRequest _, IProgress<OperationProgress>? progress, CancellationToken _) =>
            {
                progress!.Report(new OperationProgress(1000, 10_000, "waiting for exists: 1000 of 10000 ms"));
                return new WindowConditionResult { Satisfied = true, Window = NotepadWindow, MatchCount = 1 };
            });
        var client = new RecordingProgress<ProgressNotificationValue>();

        // Act
        await CreateTools().WaitForWindow(Notepad, progress: client);

        // Assert
        client.Reports.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ProgressNotificationValue { Progress = 1000, Total = 10_000, Message = "waiting for exists: 1000 of 10000 ms" });
    }

    [Fact]
    public async Task Cancellation_ReachesTheService()
    {
        using var cancel = new CancellationTokenSource();
        CancellationToken seen = default;
        _wait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((WindowConditionRequest _, IProgress<OperationProgress>? _, CancellationToken token) => seen = token)
            .ReturnsAsync(new WindowConditionResult { Satisfied = true });

        await CreateTools().WaitForWindow(Notepad, cancellationToken: cancel.Token);

        seen.Should().Be(cancel.Token);
    }

    public static TheoryData<string, Func<WindowFinderTools, Task>> InvalidCalls => new()
    {
        { "window", tools => tools.WaitForWindow(null!) },
        { "foreground", tools => tools.WaitForWindow(new WindowSelector { Foreground = true }) },
        { "timeoutMs", tools => tools.WaitForWindow(Notepad, timeoutMs: -1) },
        { "timeoutMs", tools => tools.WaitForWindow(Notepad, timeoutMs: 60_001) },
        { "stableMs", tools => tools.WaitForWindow(Notepad, stableMs: -1) },
        { "stableMs", tools => tools.WaitForWindow(Notepad, stableMs: 10_001) },
        { "excludeHandles", tools => tools.WaitForWindow(Notepad, excludeHandles: [0]) },
        { "excludeHandles", tools => tools.WaitForWindow(Notepad, excludeHandles: Enumerable.Range(1, 257).Select(i => (long)i).ToArray()) },
        { "newOnly", tools => tools.WaitForWindow(Notepad, WindowWaitCondition.Gone, newOnly: true) },
        { "newOnly", tools => tools.WaitForWindow(Notepad, WindowWaitCondition.TitleChanged, newOnly: true) },
        { "excludeHandles", tools => tools.WaitForWindow(Notepad, WindowWaitCondition.TitleChanged, excludeHandles: [0x10]) },
        { "previousTitle", tools => tools.WaitForWindow(Notepad, WindowWaitCondition.Exists, previousTitle: "Untitled - Notepad") },
    };

    [Theory]
    [MemberData(nameof(InvalidCalls))]
    public async Task InvalidArguments_AreRefused_BeforeAnythingWaits(string parameter, Func<WindowFinderTools, Task> call)
    {
        var act = () => call(CreateTools());

        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain(parameter);
        _wait.Verify(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
