using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The wait_for_element tool (roadmap M3.8): its arguments are checked before anything waits (InvalidArguments, or
/// InvalidControlType), and the request reaches the wait service as find_ui_elements' request model. The waiting itself is
/// ElementWaitServiceTests'. The service is a mock; nothing reads the real desktop.
/// </summary>
public class WaitForElementToolTests
{
    private static readonly WindowSelector Notepad = new() { ProcessName = "notepad" };

    private readonly Mock<IElementWaitService> _wait = new();
    private ElementWaitRequest? _request;

    public WaitForElementToolTests()
    {
        _wait.Setup(w => w.WaitAsync(It.IsAny<ElementWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback((ElementWaitRequest request, IProgress<OperationProgress>? _, CancellationToken _) => _request = request)
            .ReturnsAsync(new WaitForElementResult(true, true, false, 120, 2, WindowHandle: 0x1A0B2C));
    }

    private ElementWaitTools CreateSut() => new(_wait.Object);

    private async Task<ToolErrorException> RefusedAsync(Func<Task> call)
    {
        var error = (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;
        error.Message.Should().EndWith("Nothing was waited for.");
        _wait.Verify(w => w.WaitAsync(It.IsAny<ElementWaitRequest>(), It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        return error;
    }

    [Fact]
    public async Task Criteria_ReachTheService_AsFindUIElementsRequestModel()
    {
        var result = await CreateSut().WaitForElement(window: Notepad, name: "Save", nameMatch: ElementNameMatch.Contains,
            automationId: "SaveBtn", controlType: "Button, SplitButton", className: "Button", condition: ElementWaitCondition.Enabled,
            timeoutMs: 2500);

        result.Satisfied.Should().BeTrue();
        _request.Should().BeEquivalentTo(new ElementWaitRequest
        {
            Window = Notepad,
            Criteria = new ElementQuery(nint.Zero, null, "Save", ElementNameMatch.Contains, "SaveBtn", ["Button", "SplitButton"], "Button"),
            Condition = ElementWaitCondition.Enabled,
            TimeoutMs = 2500
        });
    }

    [Fact]
    public async Task TheDefaults_AreExists_And10Seconds()
    {
        await CreateSut().WaitForElement(window: Notepad, name: "Save");

        _request!.Condition.Should().Be(ElementWaitCondition.Exists);
        _request.TimeoutMs.Should().Be(10_000);
        _request.Expected.Should().BeNull();
    }

    [Fact]
    public async Task ARef_IsNormalized_AndNeedsNoWindow()
    {
        await CreateSut().WaitForElement(@ref: "W4120035E17", condition: ElementWaitCondition.ValueEquals, expected: "report.txt");

        _request.Should().BeEquivalentTo(new ElementWaitRequest
        {
            Ref = "w4120035e17", Condition = ElementWaitCondition.ValueEquals, Expected = "report.txt", TimeoutMs = 10_000
        });
    }

    [Fact]
    public async Task WithinRef_NamesTheWindow()
    {
        await CreateSut().WaitForElement(withinRef: "w4120035e3", controlType: "ListItem");

        _request!.Window.Should().BeNull();
        _request.Criteria!.WithinRef.Should().Be("w4120035e3");
    }

    [Fact]
    public async Task NoElement_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(window: Notepad, controlType: " , "));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("at least one of name, automationId, controlType and className");
    }

    [Fact]
    public async Task CriteriaWithoutAWindow_AreRefused()
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(name: "Save"));

        error.Message.Should().Contain("Pass window");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ARef_WithCriteriaOrAWindow_IsRefused(bool withName, bool withWindow, bool withWithinRef)
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(
            window: withWindow ? Notepad : null, @ref: "w1e2", name: withName ? "Save" : null, withinRef: withWithinRef ? "w1e1" : null));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task WithinRefAndAWindow_AreRefused()
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(window: Notepad, withinRef: "w1e1", name: "Save"));

        error.Message.Should().Contain("not both");
    }

    [Theory]
    [InlineData("x1e2")]
    [InlineData("Save")]
    public async Task SomethingThatIsNoRef_IsRefused(string notARef)
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(@ref: notARef));

        error.Message.Should().Contain("is not an element ref");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60_001)]
    public async Task ATimeoutOutOfRange_IsRefused(int timeoutMs)
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(window: Notepad, name: "Save", timeoutMs: timeoutMs));

        error.Message.Should().Contain("0-60000");
    }

    [Theory]
    [InlineData(ElementWaitCondition.NameEquals)]
    [InlineData(ElementWaitCondition.ValueEquals)]
    [InlineData(ElementWaitCondition.ToggleState)]
    public async Task AConditionThatComparesWithoutExpected_IsRefused(ElementWaitCondition condition)
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(window: Notepad, name: "Save", condition: condition));

        error.Message.Should().Contain("needs expected");
    }

    [Fact]
    public async Task ExpectedWithAnotherCondition_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(window: Notepad, name: "Save", expected: "Save"));

        error.Message.Should().Contain("expected applies only to");
    }

    [Fact]
    public async Task AToggleStateThatIsNone_IsRefused()
    {
        var error = await RefusedAsync(() =>
            CreateSut().WaitForElement(window: Notepad, name: "Wrap", condition: ElementWaitCondition.ToggleState, expected: "checked"));

        error.Message.Should().Contain("'on', 'off' or 'indeterminate'");
    }

    [Fact]
    public async Task EmptyExpected_IsAllowed_ForAnEmptyNameOrValue()
    {
        await CreateSut().WaitForElement(window: Notepad, automationId: "Search", condition: ElementWaitCondition.ValueEquals, expected: "");

        _request!.Expected.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInvalidRegex_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(window: Notepad, name: "(", nameMatch: ElementNameMatch.Regex));

        error.Message.Should().Contain("not a valid .NET regular expression");
    }

    [Fact]
    public async Task AnUnknownControlType_IsInvalidControlType_BeforeAnythingWaits()
    {
        var error = await RefusedAsync(() => CreateSut().WaitForElement(window: Notepad, controlType: "Buton"));

        error.ErrorCode.Should().Be("InvalidControlType");
    }
}
