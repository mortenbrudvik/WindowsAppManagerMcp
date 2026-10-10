using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// MCP tools for simulating mouse and keyboard input.
/// </summary>
[McpServerToolType]
public class InputSimulationTools
{
    private const string XDescription =
        "X coordinate in physical virtual-desktop pixels (negative on monitors left of the primary monitor)";

    private const string YDescription =
        "Y coordinate in physical virtual-desktop pixels (negative on monitors above the primary monitor)";

    private const string ClickTargetNote =
        " The click goes to whatever window is at that point, so first bring the target window to the front with focus_window and confirm it with get_foreground_window." +
        " Take coordinates from get_ui_elements bounds or capture_with_elements screenBounds, not from positions measured on a screenshot image.";

    private const string KeyboardTargetNote =
        " There is no target window parameter: input goes to the current foreground window, whichever it is, and that is often the agent's own terminal or chat window." +
        " Call get_foreground_window immediately before this call and continue only if its handle is the intended window; otherwise call focus_window and check again.";

    private readonly IInputSimulationService _inputService;

    public InputSimulationTools(IInputSimulationService inputService)
    {
        _inputService = inputService;
    }

    [McpServerTool(Name = "click")]
    [Description("Perform a left mouse click at the specified screen coordinates. To click an element, take its bounds from get_ui_elements and click the center point." + ClickTargetNote)]
    public ClickResult Click(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y)
    {
        return _inputService.Click(x, y);
    }

    [McpServerTool(Name = "right_click")]
    [Description("Perform a right mouse click at the specified screen coordinates. To click an element, take its bounds from get_ui_elements and click the center point." + ClickTargetNote)]
    public ClickResult RightClick(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y)
    {
        return _inputService.RightClick(x, y);
    }

    [McpServerTool(Name = "double_click")]
    [Description("Perform a double left click at the specified screen coordinates. Useful for opening files, selecting words, or other double-click actions." + ClickTargetNote)]
    public ClickResult DoubleClick(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y,
        [Description("Delay between clicks in milliseconds (default: 50, range: 10-500)")]
        int delayMs = 50)
    {
        return _inputService.DoubleClick(x, y, Math.Clamp(delayMs, 10, 500));
    }

    [McpServerTool(Name = "mouse_move")]
    [Description("Move the mouse cursor to the specified screen coordinates without clicking. Useful for hovering over elements to trigger tooltips or hover states.")]
    public MouseMoveResult MouseMove(
        [Description(XDescription)]
        int x,
        [Description(YDescription)]
        int y)
    {
        return _inputService.MoveMouse(x, y);
    }

    [McpServerTool(Name = "type_text")]
    [Description("Type text by simulating Unicode keyboard input. Works with any language/characters." + KeyboardTargetNote)]
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
    [Description("Send keys and key combinations. Use {KEY} for special keys (ENTER, TAB, F1-F12, DELETE, etc.), ^ for Ctrl, % for Alt, + for Shift. Examples: '^c' (Ctrl+C), '{TAB}' (Tab key), '+{HOME}' (Shift+Home)." + KeyboardTargetNote +
                 " Never send keys that close, quit, submit or delete without that check. To close a window, use close_window with its handle instead of a key combination.")]
    public SendKeysResult SendKeys(
        [Description("Keys to send in SendKeys format. Special keys: {ENTER}, {TAB}, {ESC}, {BACKSPACE}, {DELETE}, {INSERT}, {UP}, {DOWN}, {LEFT}, {RIGHT}, {HOME}, {END}, {PGUP}, {PGDN}, {F1}-{F12}, {SPACE}. Modifiers: ^ (Ctrl), % (Alt), + (Shift).")]
        string keys)
    {
        return _inputService.SendKeys(keys);
    }
}
