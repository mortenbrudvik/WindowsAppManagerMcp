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
    [InlineData("scroll")]
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

    [Theory]
    [InlineData("move_window")]
    [InlineData("set_window_bounds")]
    [InlineData("snap_window")]
    [InlineData("move_window_to_monitor")]
    public void BringToFrontDescription_SaysTheWindowIsNotActivated(string toolName)
    {
        ParameterDescription(toolName, "bringToFront").Should().Contain("without activating")
            .And.Contain("focus_window", "an agent that wants to type into the window must activate it first");
    }

    /// <summary>
    /// These tools restore a minimized window with SW_RESTORE before placing it, and SW_RESTORE activates
    /// (WindowService.SetWindowBounds and MoveWindowToMonitor); move_window_to_monitor's 'maximize' uses
    /// SW_MAXIMIZE, which activates and ignores bringToFront. The descriptions must not promise more.
    /// </summary>
    [Theory]
    [InlineData("set_window_bounds")]
    [InlineData("snap_window")]
    [InlineData("move_window_to_monitor")]
    [InlineData("set_windows_bounds_batch.placements")]
    [InlineData("snap_windows_batch.placements")]
    public void BringToFrontDescription_NamesTheCasesThatStillActivate(string source)
    {
        var text = source.Contains('.')
            ? ServerToolCatalog.AllDescriptions().Single(d => d.Source == source).Text
            : ParameterDescription(source, "bringToFront");

        text.Should().Contain("minimized").And.Contain("can activate it");
        if (source == "move_window_to_monitor")
            text.Should().Contain("'maximize'");
    }

    [Fact]
    public void MoveWindowBringToFrontDescription_HasNoActivatingCase()
    {
        // move_window only calls SetWindowPos (SWP_NOACTIVATE), never ShowWindow
        ParameterDescription("move_window", "bringToFront").Should().NotContain("minimized");
    }

    [Theory]
    [InlineData("close_windows_batch")]
    [InlineData("close_windows_batch.confirm")]
    public void CloseWindowsBatchDescriptions_NameTheConfirmThresholdTheToolEnforces(string source)
    {
        // The threshold is written out in the [Description] strings, which cannot interpolate the int constant
        var text = ServerToolCatalog.AllDescriptions().Single(d => d.Source == source).Text;

        text.Should().ContainEquivalentOf($"more than {WindowsAppManagerMcp.Tools.WindowManagerTools.MaxCloseBatchWithoutConfirm} windows");
    }

    [Fact]
    public void TheWaitsTheServerClamps_StateTheirRange()
    {
        // A batch's delay and a launch's wait for its window are waited out inside the input gate, so the server
        // clamps them; the agent reads the range in tools/list
        var waits = ServerToolCatalog.AllDescriptions()
            .Where(d => d.Source.EndsWith(".delayBetweenMs", StringComparison.Ordinal) ||
                        d.Source.EndsWith(".waitTimeoutMs", StringComparison.Ordinal))
            .ToList();

        waits.Select(d => d.Source).Should().BeEquivalentTo(
            "close_windows_batch.delayBetweenMs", "set_windows_bounds_batch.delayBetweenMs", "snap_windows_batch.delayBetweenMs",
            "set_windows_state_batch.delayBetweenMs", "launch_application.waitTimeoutMs", "launch_applications_batch.waitTimeoutMs");
        foreach (var (source, text) in waits)
        {
            var range = source.EndsWith(".delayBetweenMs", StringComparison.Ordinal)
                ? $"0-{WindowService.MaxDelayBetweenMs}"
                : $"0-{WindowsAppManagerMcp.Tools.AppLauncherTools.MaxWaitTimeoutMs}";
            text.Should().Contain($"({range}, default: ", source);
        }
    }

    private static string ParameterDescription(string toolName, string parameterName) =>
        ServerToolCatalog.AllDescriptions()
            .Single(d => d.Source == $"{toolName}.{parameterName}")
            .Text;
}
