namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the [Description] text that tools/list sends to every MCP client, including clients
/// that never load the plugin skill, does not teach destructive key chords, and the input tools
/// tell the agent to confirm the target window first.
/// These tests only reflect over the server assembly.
/// </summary>
public class ToolDescriptionTests
{
    [Fact]
    public void Catalog_FindsTheInputTools()
    {
        // Guards against the checks below silently running over an empty set.
        ServerToolCatalog.Tools.Keys.Should().Contain(
            new[] { "click", "right_click", "double_click", "mouse_move", "type_text", "send_keys" });
    }

    [Fact]
    public void NoToolDescription_TeachesTheAltF4Chord()
    {
        var offenders = ServerToolCatalog.AllDescriptions()
            .Where(d => d.Text.Contains("{F4}", StringComparison.OrdinalIgnoreCase) ||
                        d.Text.Contains("Alt+F4", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Source)
            .ToList();

        offenders.Should().BeEmpty(
            "Alt+F4 closes whatever window is in the foreground, often the agent's own terminal; " +
            "windows are closed with close_window");
    }

    [Theory]
    [InlineData("type_text")]
    [InlineData("send_keys")]
    public void KeyboardToolDescription_SaysInputGoesToTheForegroundWindowAndHowToConfirmIt(string toolName)
    {
        var description = ServerToolCatalog.Description(toolName);

        description.Should().Contain("foreground window",
            "keyboard input has no target window; it goes to whatever window is in the foreground");
        description.Should().Contain("get_foreground_window",
            "the agent must confirm the target immediately before typing");
    }

    [Fact]
    public void SendKeysDescription_PointsToCloseWindowForClosing()
    {
        ServerToolCatalog.Description("send_keys").Should().Contain("close_window");
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right_click")]
    [InlineData("double_click")]
    public void ClickToolDescription_SaysToConfirmTheTargetWindowFirst(string toolName)
    {
        ServerToolCatalog.Description(toolName).Should().Contain("get_foreground_window",
            "a click lands on whatever window is at that point");
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right_click")]
    [InlineData("double_click")]
    [InlineData("mouse_move")]
    [InlineData("type_text")]
    [InlineData("send_keys")]
    public void InputToolDescription_RecommendsWindowHandleAndNamesTheGuardResults(string toolName)
    {
        var description = ServerToolCatalog.Description(toolName);

        description.Should().Contain("Pass windowHandle", "the target window is how input is kept off the wrong window (roadmap M1.5)");
        description.Should().Contain("TargetIsAgentHost").And.Contain("receivingWindow");
        ParameterDescription(toolName, "windowHandle").Should().Contain("Strongly recommended");
        ParameterDescription(toolName, "allowHostTarget").Should().Contain("explicitly asked").And.Contain("windowHandle");
    }

    [Fact]
    public void TypeTextDescription_StatesTheBatchSizeTheServerUses()
    {
        // The description cannot interpolate the constant, so this keeps them in step
        ServerToolCatalog.Description("type_text").Should()
            .Contain($"every {InputSimulationService.TypeTextBatchSize} characters").And.Contain("TargetLostFocus");
    }

    [Fact]
    public void SendKeysDescription_SaysDestructiveChordsNeedWindowHandle()
    {
        ServerToolCatalog.Description("send_keys").Should().Contain("DestructiveChordNeedsTarget");
    }

    [Fact]
    public void SendKeysDescription_SaysAFocusChangingChordMustBeTheLastKey()
    {
        ServerToolCatalog.Description("send_keys").Should().Contain("KeysAfterFocusChange").And.Contain("Alt+Tab");
    }

    [Theory]
    [InlineData("click")]
    [InlineData("right_click")]
    [InlineData("double_click")]
    public void ClickDescription_NamesCursorNotAtPoint(string toolName)
    {
        ServerToolCatalog.Description(toolName).Should().Contain("CursorNotAtPoint");
    }

    [Fact]
    public void FocusWindowDescription_SaysToConfirmWithGetForegroundWindow()
    {
        ServerToolCatalog.Description("focus_window").Should().Contain("get_foreground_window",
            "Windows can refuse to change the foreground window");
    }

    [Fact]
    public void NoToolDescription_ClaimsABooleanReturnValue()
    {
        var offenders = ServerToolCatalog.AllDescriptions()
            .Where(d => d.Text.Contains("Returns true", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Source)
            .ToList();

        offenders.Should().BeEmpty("every tool returns a result object with success and details, not a bare bool");
    }

    [Fact]
    public void GetBrowserContentDescription_MarksPageContentAsUntrusted()
    {
        ServerToolCatalog.Description("get_browser_content").Should().Contain("untrusted",
            "web page text can contain prompt-injection instructions");
    }

    [Fact]
    public void LaunchBrowserWithDebugDescription_WarnsAboutTheUnauthenticatedPort()
    {
        ServerToolCatalog.Description("launch_browser_with_debug").Should().Contain("no authentication");
    }

    [Fact]
    public void LaunchBrowserWithDebugDescription_CleanupFindsWindowsOnOtherDesktopsAndChecksEveryProcess()
    {
        // A window on another virtual desktop is cloaked: find_windows leaves it out unless asked, and
        // list_processes without includeWindowless lists only processes with a window
        ServerToolCatalog.Description("launch_browser_with_debug").Should()
            .Contain("includeCloaked=true").And.Contain("includeWindowless=true");
    }

    [Theory]
    [InlineData("get_ui_elements", "controlTypes")]
    [InlineData("get_ui_elements", "interactableOnly")]
    [InlineData("get_ui_elements", "minWidth")]
    [InlineData("get_ui_elements", "minHeight")]
    [InlineData("capture_with_elements", "controlTypes")]
    [InlineData("capture_with_elements", "interactableOnly")]
    public void UIElementFilterDescription_SaysMatchesInsideLeftOutElementsAreStillReturned(string toolName, string parameterName)
    {
        // These filters apply to each element on its own: an element that fails one is left out,
        // and the elements inside it that pass are still returned (UIElementService).
        ParameterDescription(toolName, parameterName).Should().Contain("still returned")
            .And.NotContain("drops whole subtrees");
    }

    [Theory]
    [InlineData("get_ui_elements")]
    [InlineData("capture_with_elements")]
    public void VisibleOnlyDescription_SaysItDropsWholeSubtrees(string toolName)
    {
        // visibleOnly is the one filter that leaves out everything inside an offscreen element, so a
        // client that only sees tools/list must know to pass false for scrolled-out controls.
        ParameterDescription(toolName, "visibleOnly").Should().Contain("drops whole subtrees");
    }

    [Fact]
    public void ControlTypesDescription_SaysUnknownNamesFail()
    {
        ParameterDescription("get_ui_elements", "controlTypes").Should().Contain("InvalidControlType");
    }

    [Theory]
    [InlineData("get_focused_element")]
    [InlineData("get_element_at_point")]
    public void ElementInspectionDescription_MarksElementTextAsUntrustedAndMentionsTheTimeout(string toolName)
    {
        var description = ServerToolCatalog.Description(toolName);

        description.Should().Contain("untrusted", "element names and values come from the application");
        description.Should().Contain("timeout", "a hung application can block UI Automation");
    }

    [Fact]
    public void GetFocusedElementDescription_PointsToTheKeyboardTools()
    {
        ServerToolCatalog.Description("get_focused_element").Should().Contain("type_text").And.Contain("send_keys");
    }

    [Fact]
    public void GetElementAtPointDescription_SaysCoordinatesAreScreenPixelsNotImagePixels()
    {
        ServerToolCatalog.Description("get_element_at_point").Should().Contain("not image pixels");
    }

    private static string ParameterDescription(string toolName, string parameterName) =>
        ServerToolCatalog.AllDescriptions()
            .Single(d => d.Source == $"{toolName}.{parameterName}")
            .Text;
}
