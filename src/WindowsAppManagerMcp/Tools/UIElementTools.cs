using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class UIElementTools
{
    private readonly IUIElementService _uiElementService;

    private const int MaxTimeoutMs = 30000;
    private const int DefaultTimeoutMs = 10000;
    private const int MaxDepth = 10;
    private const int DefaultDepth = 5;

    public UIElementTools(IUIElementService uiElementService)
    {
        _uiElementService = uiElementService;
    }

    [McpServerTool(Name = "get_ui_elements")]
    [Description("Get UI element tree with bounding boxes for a window. Returns control names, types, automation IDs, and coordinates for programmatic interaction planning. Use this as a lightweight alternative to screenshots when only element coordinates are needed.")]
    public UIElementResult GetUIElements(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("Maximum tree depth to traverse (default: 5, max: 10). Higher values return more nested elements but take longer.")]
        int maxDepth = DefaultDepth,
        [Description("Comma-separated list of control types to include (e.g., 'Button,Edit,CheckBox'). Leave empty for all types. Common types: Button, Edit, CheckBox, ComboBox, ListItem, MenuItem, Text, Hyperlink, Image.")]
        string? controlTypes = null,
        [Description("Only return enabled/interactable elements (default: false)")]
        bool interactableOnly = false,
        [Description("Only return visible (not offscreen) elements (default: true)")]
        bool visibleOnly = true,
        [Description("Minimum width in pixels - filters out elements smaller than this (optional)")]
        int? minWidth = null,
        [Description("Minimum height in pixels - filters out elements smaller than this (optional)")]
        int? minHeight = null,
        [Description("Timeout in milliseconds (default: 10000, max: 30000)")]
        int timeoutMs = DefaultTimeoutMs)
    {
        // Clamp timeout to valid range
        timeoutMs = Math.Clamp(timeoutMs, 1000, MaxTimeoutMs);

        // Clamp max depth
        maxDepth = Math.Clamp(maxDepth, 1, MaxDepth);

        // Parse control types
        string[]? controlTypeArray = null;
        if (!string.IsNullOrWhiteSpace(controlTypes))
        {
            controlTypeArray = controlTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            if (controlTypeArray.Length == 0)
            {
                controlTypeArray = null;
            }
        }

        // Create filter
        var filter = new UIElementFilter(
            ControlTypes: controlTypeArray,
            InteractableOnly: interactableOnly,
            VisibleOnly: visibleOnly,
            MinWidth: minWidth,
            MinHeight: minHeight);

        // Create cancellation token with timeout
        using var cts = new CancellationTokenSource(timeoutMs);

        return _uiElementService.GetUIElements(
            (nint)handle,
            maxDepth,
            filter,
            cts.Token);
    }
}
