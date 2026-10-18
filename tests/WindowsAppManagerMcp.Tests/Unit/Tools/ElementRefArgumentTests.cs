using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The one check of a ref argument (<see cref="ElementRefs.Require"/>), and that every tool that takes a ref refuses one that is
/// no ref with the very message it always gave: element_action's ref and listRef, read_element_text's and read_table's ref,
/// wait_for_element's ref and withinRef, find_ui_elements' withinRef and capture_window's ref.
/// </summary>
public class ElementRefArgumentTests
{
    private const string Sources =
        "and come from find_ui_elements, get_ui_elements, get_focused_element, get_element_at_point or capture_with_elements";

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call)
    {
        var error = (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        return error;
    }

    [Theory]
    [InlineData("w1e7", "w1e7")]
    [InlineData("W4120035E17", "w4120035e17")]
    public void Require_ReturnsTheRefInLowerCase(string value, string expected)
    {
        ElementRefs.Require(value, "ref", "Nothing was read.").Should().Be(expected);
    }

    [Theory]
    [InlineData("save button")]
    [InlineData("w0e1")]
    [InlineData("")]
    [InlineData(null)]
    public void Require_RefusesWhatIsNoRef_WithInvalidArguments(string? value)
    {
        var call = () => ElementRefs.Require(value, "withinRef", "Nothing was searched.");

        var error = call.Should().Throw<ToolErrorException>().Which;
        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Be($"withinRef '{value}' is not an element ref: refs look like 'w4120035e17' {Sources}. Nothing was searched.");
    }

    [Fact]
    public void Require_SaysWhereRefsComeFrom_AsTheToolSays()
    {
        var call = () => ElementRefs.Require("list", "listRef", "Nothing was done.", "(find_ui_elements' virtualized.containers lists them)");

        call.Should().Throw<ToolErrorException>().Which.Message.Should()
            .Be("listRef 'list' is not an element ref: refs look like 'w4120035e17' (find_ui_elements' virtualized.containers lists them). " +
                "Nothing was done.");
    }

    #region The tools' messages, unchanged

    [Fact]
    public async Task ElementAction_Ref()
    {
        var sut = new ElementActionTools(new Mock<IElementActionService>().Object, new Mock<IElementSearchService>().Object,
            new Mock<IElementRegistry>().Object);

        var error = await RefusedAsync(() => sut.ElementAction(ElementActionKind.Invoke, @ref: "Save"));

        error.Message.Should().Be($"ref 'Save' is not an element ref: refs look like 'w4120035e17' {Sources}. Nothing was done.");
    }

    [Fact]
    public async Task ElementAction_ListRef()
    {
        var sut = new ElementActionTools(new Mock<IElementActionService>().Object, new Mock<IElementSearchService>().Object,
            new Mock<IElementRegistry>().Object);

        var error = await RefusedAsync(() => sut.ElementAction(ElementActionKind.Realize, name: "Item 7", maxScrolls: 3, listRef: "Files"));

        error.Message.Should().Be("listRef 'Files' is not an element ref: refs look like 'w4120035e17' (find_ui_elements' " +
                                  "virtualized.containers lists the window's lists). Nothing was done.");
    }

    [Fact]
    public async Task ReadElementText_And_ReadTable_Ref()
    {
        var sut = new ElementReadTools(new Mock<IElementReadService>().Object);
        var expected = $"ref 'Body' is not an element ref: refs look like 'w4120035e17' {Sources}. Nothing was read.";

        (await RefusedAsync(() => sut.ReadElementText(@ref: "Body"))).Message.Should().Be(expected);
        (await RefusedAsync(() => sut.ReadTable("Body"))).Message.Should().Be(expected);
    }

    [Fact]
    public async Task WaitForElement_Ref_And_WithinRef()
    {
        var sut = new ElementWaitTools(new Mock<IElementWaitService>().Object);

        (await RefusedAsync(() => sut.WaitForElement(@ref: "OK"))).Message.Should()
            .Be($"ref 'OK' is not an element ref: refs look like 'w4120035e17' {Sources}. Nothing was waited for.");
        (await RefusedAsync(() => sut.WaitForElement(withinRef: "Dialog", name: "OK"))).Message.Should()
            .Be($"withinRef 'Dialog' is not an element ref: refs look like 'w4120035e17' {Sources}. Nothing was waited for.");
    }

    [Fact]
    public async Task FindUIElements_WithinRef()
    {
        var sut = new UIElementTools(new Mock<IUIElementService>().Object, new Mock<IElementInspectionService>().Object, null,
            new Mock<IElementSearchService>().Object, new Mock<IElementRegistry>().Object);

        var error = await RefusedAsync(() => sut.FindUIElements(name: "Save", withinRef: "toolbar"));

        error.Message.Should().Be($"withinRef 'toolbar' is not an element ref: refs look like 'w4120035e17' {Sources}. Nothing was searched.");
    }

    [Fact]
    public async Task CaptureWindow_Ref()
    {
        var sut = new ScreenshotTools(TestDataFactory.CreateMockScreenshotService().Object, TestDataFactory.CreateMockUIElementService().Object,
            TestDataFactory.CreateMockWindowService().Object, new TargetPolicy(TargetPolicySettings.Default),
            elementRegistry: new Mock<IElementRegistry>().Object);

        var error = await RefusedAsync(() => sut.CaptureWindow(handle: 0x4242, @ref: "save-button"));

        error.Message.Should().Be("ref 'save-button' is not an element ref: refs look like 'w4120035e17' and come from " +
                                  "capture_with_elements, find_ui_elements, get_ui_elements, get_focused_element or get_element_at_point. " +
                                  "Nothing was captured.");
    }

    #endregion
}
