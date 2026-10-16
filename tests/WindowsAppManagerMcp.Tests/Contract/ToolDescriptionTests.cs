namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the [Description] text that tools/list sends to every MCP client, including clients
/// that never load the plugin skill, does not teach destructive key chords, and the input tools
/// tell the agent to confirm the target window first.
/// These tests only reflect over the server assembly. The tools roadmap M6.1 deprecated (click, right_click, double_click,
/// move_window, resize_window and the others of the deprecated toolset) carry a one-line description that sends the agent
/// to their replacement instead (DeprecatedToolsContractTests), so the rules here hold for the replacements.
/// </summary>
public class ToolDescriptionTests
{
    [Fact]
    public void Catalog_FindsTheInputTools()
    {
        // Guards against the checks below silently running over an empty set.
        ServerToolCatalog.Tools.Keys.Should().Contain(
            new[] { "mouse_click", "drag", "mouse_move", "type_text", "send_keys" });
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
    [InlineData("press_keys")]
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
    [InlineData("mouse_click")]
    [InlineData("mouse_move")]
    public void MouseToolDescription_PointsToRefsAndImagePoints_NotToScreenBoundsTheLegendLeavesOut(string toolName)
    {
        // capture_with_elements returns screenBounds only with detail full; its legend centres are image points
        var description = ServerToolCatalog.Description(toolName);

        description.Should().NotContain("screenBounds");
        description.Should().Contain("captureId", "a point seen on a capture is passed with its captureId");
        if (toolName != "mouse_move")
        {
            description.Should().Contain("element_action", "acting on a ref needs no point at all");
        }
    }

    [Fact]
    public void GetUiElementsDescription_SaysItReturnsRefs()
    {
        ServerToolCatalog.Description("get_ui_elements").Should().Contain("ref");
    }

    [Theory]
    [InlineData("mouse_click")]
    public void ClickToolDescription_SaysToConfirmTheTargetWindowFirst(string toolName)
    {
        ServerToolCatalog.Description(toolName).Should().Contain("get_foreground_window",
            "a click lands on whatever window is at that point");
    }

    [Theory]
    [InlineData("mouse_click")]
    [InlineData("drag")]
    [InlineData("mouse_move")]
    [InlineData("scroll")]
    [InlineData("type_text")]
    [InlineData("send_keys")]
    [InlineData("press_keys")]
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
        ServerToolCatalog.Description("press_keys").Should().Contain("DestructiveChordNeedsTarget").And.Contain("KeysAfterFocusChange");
    }

    [Fact]
    public void SendKeysDescription_SaysAFocusChangingChordMustBeTheLastKey()
    {
        ServerToolCatalog.Description("send_keys").Should().Contain("KeysAfterFocusChange").And.Contain("Alt+Tab");
    }

    [Theory]
    [InlineData("mouse_click")]
    public void ClickDescription_NamesCursorNotAtPoint(string toolName)
    {
        ServerToolCatalog.Description(toolName).Should().Contain("CursorNotAtPoint");
    }

    [Fact]
    public void MouseClickDescription_StandsAlone_WithEveryCheckOfAClick()
    {
        // Roadmap M6.1: mouse_click replaces click, right_click and double_click, so it cannot send the agent to their text
        var description = ServerToolCatalog.Description("mouse_click");

        description.Should().NotContain("as for click").And.NotContain("as click");
        foreach (var code in new[]
                 {
                     "TargetIsAgentHost", "TargetOccluded", "BlockedByModal", "CursorNotAtPoint", "CursorClamped", "MouseInUse",
                     "PasteNotAllowed", "TargetElevated", "TargetDeniedByPolicy", "SessionLocked", "DesktopUnavailable", "PausedByUser",
                     "ModifiersHeld", "InputBusy"
                 })
        {
            description.Should().Contain(code);
        }

        description.Should().Contain("get_element_at_point").And.Contain("clickPoint")
            .And.Contain("PuTTY", "a right click pastes in a terminal, as right_click said");
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
    public void LaunchBrowserWithDebugDescription_SendsTheCleanupToCloseDebugBrowser()
    {
        // Roadmap M5.4: close_debug_browser closes the whole browser by its own process and port, on every virtual
        // desktop, and deletes its temporary profile; closing its windows one by one (the old advice) left the profile
        ServerToolCatalog.Description("launch_browser_with_debug").Should()
            .Contain("close it with close_debug_browser").And.NotContain("includeWindowless");
    }

    [Fact]
    public void CloseDebugBrowserDescription_SaysItNeverClosesTheUsersBrowser_AndHowItIsConfirmed()
    {
        ServerToolCatalog.Description("close_debug_browser").Should()
            .Contain("NotADebugBrowser").And.Contain("never closed")
            .And.Contain("ConfirmationDeclined").And.Contain("ConfirmationRequired");
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
    public void FindWindowsDescription_SaysForceOnTheHostNeedsConfirmWhenTheClientCannotAsk()
    {
        // Since M2.F1 force alone no longer closes a protected window: the user answers, or confirm stands in when the
        // client cannot ask (close_window's ConfirmationRequired)
        ServerToolCatalog.Description("find_windows").Should().Contain("unless force=true").And.Contain("confirm=true");
    }

    [Fact]
    public void ExpectWindowDescription_SaysReuseExistingMatchesItToo()
    {
        // The hint is not only the window wait's: reuseExisting reuses only an open window that matches it, and a web address
        // can only be reused by its titleContains
        ParameterDescription("launch_application", "expectWindow").Should().Contain("With reuseExisting").And.Contain("titleContains");
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
    /// Since roadmap M2.3 the placements restore a minimized or maximized window with SW_SHOWNOACTIVATE, which does not
    /// activate it, so only move_window_to_monitor's 'maximize' still activates (SW_MAXIMIZE has no non-activating form).
    /// The descriptions must say so, and no longer warn that restoring activates.
    /// </summary>
    [Theory]
    [InlineData("set_window_bounds")]
    [InlineData("snap_window")]
    [InlineData("move_window_to_monitor")]
    [InlineData("set_windows_bounds_batch")]
    public void PlacementDescriptions_SayAMinimizedOrMaximizedWindowIsRestoredWithoutActivation(string toolName)
    {
        ServerToolCatalog.Description(toolName).Should().Contain("minimized or maximized window is restored to normal first, without activating it");
    }

    [Fact]
    public void MoveWindowToMonitorBringToFront_NamesMaximizeAsTheCaseThatActivates()
    {
        ParameterDescription("move_window_to_monitor", "bringToFront").Should().Contain("'maximize'").And.Contain("activates the window");
    }

    [Theory]
    [InlineData("move_window")]
    [InlineData("set_window_bounds")]
    [InlineData("snap_window")]
    [InlineData("set_windows_bounds_batch.placements")]
    [InlineData("snap_windows_batch.placements")]
    public void NoPlacementDescription_SaysRestoringActivates(string source)
    {
        var text = source.Contains('.')
            ? ServerToolCatalog.AllDescriptions().Single(d => d.Source == source).Text
            : ParameterDescription(source, "bringToFront");

        text.Should().NotContain("can activate it");
    }

    [Theory]
    [InlineData("set_window_bounds")]
    [InlineData("set_window_state")]
    [InlineData("snap_window")]
    [InlineData("move_window_to_monitor")]
    public void GeometryDescriptions_NameTheResultFields_AndTheVisibleFrame(string toolName)
    {
        ServerToolCatalog.Description(toolName).Should().Contain("bounds (its visible frame)").And.Contain("outerBounds")
            .And.Contain("state").And.Contain("monitorIndex").And.NotContain("newBounds");
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
        // clamps them; the agent reads the range in tools/list. press_keys refuses a delayBetweenMs out of its range instead
        var waits = ServerToolCatalog.AllDescriptions()
            .Where(d => (d.Source.EndsWith(".delayBetweenMs", StringComparison.Ordinal) && !d.Source.StartsWith("press_keys.", StringComparison.Ordinal)) ||
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
