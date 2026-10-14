using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The message of an argument a tool does not declare (roadmap M3.F42): it names the argument, lists the tool's
/// parameters and, for a near miss, the one most likely meant. Pure: the declared names are given, no server runs.
/// </summary>
public class UndeclaredArgumentRefusalTests
{
    private static readonly string[] GetWindowInfo = ["handle", "window", "detail"];
    private static readonly string[] Click =
        ["x", "y", "windowHandle", "window", "allowHostTarget", "releaseHeldModifiers", "space", "captureId", "observe"];
    private static readonly string[] TakeScreenshot =
        ["monitorIndex", "format", "quality", "maxDimension", "output", "outputPath", "overwrite", "saveToFile", "maxWidth"];

    [Fact]
    public void Refusal_WhenEveryArgumentIsDeclared_IsNull()
    {
        UndeclaredArgumentToolFilter.Refusal("get_window_info", ["handle", "detail"], GetWindowInfo).Should().BeNull();
    }

    [Fact]
    public void Refusal_NamesTheArgument_AndListsTheToolsParameters()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("get_window_info", ["handle", "bogusParam"], GetWindowInfo);

        message.Should().StartWith("Invalid arguments for tool 'get_window_info'.")
            .And.Contain("'bogusParam'")
            .And.Contain("Its parameters are handle, window, detail.")
            .And.Contain("Nothing was done")
            .And.NotContain("did you mean");
    }

    [Fact]
    public void Refusal_OfHandle_WhereTheToolTakesWindowHandle_SuggestsWindowHandle()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("click", ["x", "y", "handle"], Click);

        message.Should().Contain("'handle' (did you mean 'windowHandle'?)");
    }

    [Fact]
    public void Refusal_OfWindowHandle_WhereTheToolTakesHandle_SuggestsHandle()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("get_window_info", ["windowHandle"], GetWindowInfo);

        message.Should().Contain("'windowHandle' (did you mean 'handle'?)");
    }

    [Fact]
    public void Refusal_OfANameInAnotherLetterCase_SuggestsTheDeclaredSpelling()
    {
        // The SDK binds an argument only by its exact name (UndeclaredArgumentContractTests pins it), so Handle is not handle
        var message = UndeclaredArgumentToolFilter.Refusal("get_window_info", ["Handle"], GetWindowInfo);

        message.Should().Contain("'Handle' (did you mean 'handle'?)");
    }

    [Fact]
    public void Refusal_OfANameWithinTwoEdits_SuggestsTheDeclaredName()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("element_action", ["controlTypes"], ["action", "ref", "controlType"]);

        message.Should().Contain("'controlTypes' (did you mean 'controlType'?)");
    }

    [Fact]
    public void Refusal_OfANameThatEndsSeveralDeclaredNames_SuggestsNone()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("wait_for_element", ["ref"], ["listRef", "withinRef"]);

        message.Should().Contain("'ref'").And.NotContain("did you mean");
    }

    [Fact]
    public void Refusal_OfANameThatEndsInsideAWord_SuggestsNothing()
    {
        // "dth" ends maxWidth, but not at a word of it
        var message = UndeclaredArgumentToolFilter.Refusal("take_screenshot", ["dth"], TakeScreenshot);

        message.Should().NotContain("did you mean");
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("windowHandle")]
    [InlineData("window")]
    public void Refusal_OfAWindowForTakeScreenshot_PointsToCaptureWindow(string argument)
    {
        // Measured: take_screenshot with handle captured the whole primary monitor
        var message = UndeclaredArgumentToolFilter.Refusal("take_screenshot", [argument], TakeScreenshot);

        message.Should().Contain($"'{argument}'").And.Contain("capture_window");
    }

    [Fact]
    public void Refusal_ForAToolWithoutParameters_SaysItTakesNone()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("get_monitors", ["handle"], []);

        message.Should().Contain("'handle'").And.Contain("It takes no arguments.");
    }

    [Fact]
    public void Refusal_OfSeveralArguments_NamesEach()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("get_window_info", ["bogusParam", "windowHandle", "other"], GetWindowInfo);

        message.Should().Contain("'bogusParam', 'windowHandle' (did you mean 'handle'?) or 'other'");
    }

    [Fact]
    public void Refusal_OfANameThatIsNoIdentifier_DoesNotRepeatIt()
    {
        var message = UndeclaredArgumentToolFilter.Refusal("click", ["password=hunter2"], Click);

        message.Should().NotContain("hunter2").And.Contain("an argument whose name is not a parameter name");
    }
}
