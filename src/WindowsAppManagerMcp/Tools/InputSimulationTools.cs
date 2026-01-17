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
}
