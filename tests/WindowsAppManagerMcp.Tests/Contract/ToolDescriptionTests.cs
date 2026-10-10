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
}
