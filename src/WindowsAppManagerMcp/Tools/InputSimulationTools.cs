using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// MCP tools for simulating mouse input.
/// </summary>
[McpServerToolType]
public class InputSimulationTools
{
    private readonly IInputSimulationService _inputService;

    public InputSimulationTools(IInputSimulationService inputService)
    {
        _inputService = inputService;
    }

    [McpServerTool(Name = "click")]
    [Description("Perform a left mouse click at the specified screen coordinates. Use get_ui_elements to find element bounds, then calculate the center point for clicking.")]
    public ClickResult Click(
        [Description("X coordinate (screen position)")]
        int x,
        [Description("Y coordinate (screen position)")]
        int y)
    {
        return _inputService.Click(x, y);
    }

    [McpServerTool(Name = "right_click")]
    [Description("Perform a right mouse click at the specified screen coordinates. Use get_ui_elements to find element bounds, then calculate the center point for clicking.")]
    public ClickResult RightClick(
        [Description("X coordinate (screen position)")]
        int x,
        [Description("Y coordinate (screen position)")]
        int y)
    {
        return _inputService.RightClick(x, y);
    }

    [McpServerTool(Name = "double_click")]
    [Description("Perform a double left click at the specified screen coordinates. Useful for opening files, selecting words, or other double-click actions.")]
    public ClickResult DoubleClick(
        [Description("X coordinate (screen position)")]
        int x,
        [Description("Y coordinate (screen position)")]
        int y,
        [Description("Delay between clicks in milliseconds (default: 50, range: 10-500)")]
        int delayMs = 50)
    {
        return _inputService.DoubleClick(x, y, Math.Clamp(delayMs, 10, 500));
    }

    [McpServerTool(Name = "mouse_move")]
    [Description("Move the mouse cursor to the specified screen coordinates without clicking. Useful for hovering over elements to trigger tooltips or hover states.")]
    public MouseMoveResult MouseMove(
        [Description("X coordinate (screen position)")]
        int x,
        [Description("Y coordinate (screen position)")]
        int y)
    {
        return _inputService.MoveMouse(x, y);
    }

    [McpServerTool(Name = "type_text")]
    [Description("Type text by simulating keyboard input. Text is sent to the currently focused window using Unicode characters. Works with any language/characters.")]
    public TypeTextResult TypeText(
        [Description("The text to type")]
        string text,
        [Description("Delay between keystrokes in milliseconds (0-100, default 0). Use for visible typing effect.")]
        int delayMs = 0)
    {
        delayMs = Math.Clamp(delayMs, 0, 100);
        return _inputService.TypeText(text, delayMs);
    }

    [McpServerTool(Name = "send_keys")]
    [Description("Send key combinations to the focused window. Use {KEY} for special keys (ENTER, TAB, F1-F12, DELETE, etc.), ^ for Ctrl, % for Alt, + for Shift. Examples: '^c' (Ctrl+C), '{ENTER}' (Enter key), '%{F4}' (Alt+F4), '+{HOME}' (Shift+Home).")]
    public SendKeysResult SendKeys(
        [Description("Keys to send in SendKeys format. Special keys: {ENTER}, {TAB}, {ESC}, {BACKSPACE}, {DELETE}, {INSERT}, {UP}, {DOWN}, {LEFT}, {RIGHT}, {HOME}, {END}, {PGUP}, {PGDN}, {F1}-{F12}, {SPACE}. Modifiers: ^ (Ctrl), % (Alt), + (Shift).")]
        string keys)
    {
        return _inputService.SendKeys(keys);
    }
}
