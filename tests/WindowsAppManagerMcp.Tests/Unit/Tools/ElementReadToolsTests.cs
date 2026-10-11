namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// read_element_text's and read_table's arguments (roadmap M3.9): what the tools refuse before they read, and the request
/// they pass on, criteria as find_ui_elements' own query. The reads themselves are ElementReadService*Tests'.
/// </summary>
public class ElementReadToolsTests
{
    private static readonly nint Handle = 0x8200;

    private readonly Mock<IElementReadService> _reads = new();
    private readonly Mock<IWindowService> _windows = new();
    private ElementTextRequest? _textRequest;
    private ElementTableRequest? _tableRequest;

    public ElementReadToolsTests()
    {
        _reads.Setup(r => r.ReadTextAsync(It.IsAny<ElementTextRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementTextRequest request, CancellationToken _) => _textRequest = request)
            .ReturnsAsync(new ElementTextResult(true, "w1e1", Handle, "Edit", "text", ElementTextSource.Value, 0, 4, false, null, 1));
        _reads.Setup(r => r.ReadTableAsync(It.IsAny<ElementTableRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ElementTableRequest request, CancellationToken _) => _tableRequest = request)
            .ReturnsAsync(new ElementTableResult(true, "w1e2", Handle, ElementTableSource.Grid, null, [], 0, 0, 0, false, 1));
    }

    private ElementReadTools CreateSut() => new(_reads.Object, _windows.Object);

    private static WindowSelector Window => new() { Handle = Handle };

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    #region read_element_text

    [Fact]
    public async Task ReadText_ByRef_PassesTheRefNormalized_AndTheClampedLimits()
    {
        await CreateSut().ReadElementText(@ref: "W4E17", offset: 100, maxLength: 999_999, timeoutMs: 50);

        _textRequest!.Ref.Should().Be("w4e17");
        _textRequest.Query.Should().BeNull();
        _textRequest.Offset.Should().Be(100);
        _textRequest.MaxLength.Should().Be(999_999, "the service clamps it to its limit");
        _textRequest.Timeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReadText_ByCriteria_SendsFindUIElementsQuery_ForTheWindow()
    {
        // Act
        await CreateSut().ReadElementText(window: Window, name: "body", nameMatch: ElementNameMatch.Contains, controlType: "Document, Edit");

        // Assert
        var query = _textRequest!.Query!;
        _textRequest.Ref.Should().BeNull();
        query.WindowHandle.Should().Be(Handle);
        query.Name.Should().Be("body");
        query.NameMatch.Should().Be(ElementNameMatch.Contains);
        query.ControlTypes.Should().Equal("Document", "Edit");
        query.VisibleOnly.Should().BeTrue();
        query.MaxResults.Should().Be(5, "enough to list the candidates when several match");
    }

    [Fact]
    public async Task ReadText_WithNeitherRefNorCriteria_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadElementText());

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("Pass ref");
        _reads.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadText_WithAWindowButNoCriterion_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadElementText(window: Window, controlType: " , "));

        error.Message.Should().Contain("at least one of name, automationId, controlType and className");
    }

    [Fact]
    public async Task ReadText_WithCriteriaButNoWindow_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadElementText(name: "Body"));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task ReadText_WithARefAndCriteria_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadElementText(@ref: "w1e1", window: Window, name: "Body"));

        error.Message.Should().Contain("not both");
    }

    [Fact]
    public async Task ReadText_WithSomethingThatIsNoRef_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadElementText(@ref: "the body"));

        error.Message.Should().Contain("is not an element ref");
    }

    [Fact]
    public async Task ReadText_WithANegativeOffset_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadElementText(@ref: "w1e1", offset: -1));

        error.Message.Should().Contain("offset");
    }

    [Fact]
    public async Task ReadText_WithAnInvalidRegex_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadElementText(window: Window, name: "(", nameMatch: ElementNameMatch.Regex));

        error.Message.Should().Contain("not a valid .NET regular expression");
    }

    #endregion

    #region read_table

    [Fact]
    public async Task ReadTable_PassesTheRefNormalized_AndThePage()
    {
        await CreateSut().ReadTable("W9E3", startRow: 50, maxRows: 25, maxColumns: 4, timeoutMs: 60_000);

        _tableRequest.Should().Be(new ElementTableRequest("w9e3", 50, 25, 4, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ReadTable_WithANegativeStartRow_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadTable("w1e1", startRow: -5));

        error.Message.Should().Contain("startRow");
        _reads.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadTable_WithSomethingThatIsNoRef_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().ReadTable("Processes"));

        error.Message.Should().Contain("is not an element ref");
    }

    #endregion
}
