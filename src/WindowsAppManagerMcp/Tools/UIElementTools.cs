using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class UIElementTools
{
    private readonly IUIElementService _uiElementService;

    private const int MinTimeoutMs = 1000;
    private const int MaxTimeoutMs = 30000;
    private const int DefaultTimeoutMs = 10000;
    private const int MaxDepth = 10;
    private const int DefaultDepth = 5;

    // Shared with capture_with_elements (ScreenshotTools), whose parameter descriptions start with
    // "Only include enabled elements. Default: false. " and "Only include elements that are not
    // offscreen. Default: true. ". The filters are applied per element (UIElementService): an element
    // that fails one is left out and the elements inside it that pass take its place; only visibleOnly
    // drops whole subtrees.
    internal const string ControlTypesDescription =
        "Comma-separated control types to keep (e.g., 'Button,Edit'). Leave empty for all types. " +
        "Matching elements inside containers of other types are still returned: the containers are left out and " +
        "their matches take their place. Unknown names fail with InvalidControlType, which lists the valid names. " +
        "Common types: Button, Edit, CheckBox, ComboBox, ListItem, MenuItem, TabItem, Text, Hyperlink, Image, Pane, Group.";

    internal const string InteractableOnlyCaution =
        "Stricter than enabled: an element must also be keyboard-focusable or support an action pattern " +
        "(invoke, toggle, value, selection, expand/collapse or range). Labels and containers are left out, but " +
        "interactable elements inside them are still returned.";

    internal const string VisibleOnlyCaution =
        "This filter drops whole subtrees: everything inside an element reported as offscreen is left out too. " +
        "Pass false if the control may be scrolled out of view.";

    public UIElementTools(IUIElementService uiElementService)
    {
        _uiElementService = uiElementService;
    }

    [McpServerTool(Name = "get_ui_elements")]
    [Description("Get UI element tree with bounding boxes for a window. Returns control names, types, automation IDs, and coordinates for programmatic interaction planning. Use this as a lightweight alternative to screenshots when only element coordinates are needed. If the timeout passes, the elements read so far are returned with truncated=true.")]
    public UIElementResult GetUIElements(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("How many levels below the window to read (default: 5, max: 10). 1 returns only the window's direct children. When depthReached in the result equals depth, there may be more elements below.")]
        int maxDepth = DefaultDepth,
        [Description(ControlTypesDescription)]
        string? controlTypes = null,
        [Description("Only return interactable elements (default: false). " + InteractableOnlyCaution)]
        bool interactableOnly = false,
        [Description("Only return elements that are not offscreen (default: true). " + VisibleOnlyCaution)]
        bool visibleOnly = true,
        [Description("Minimum element width in pixels (optional). Narrower elements and elements without bounds are left out, but wide enough elements inside them are still returned.")]
        int? minWidth = null,
        [Description("Minimum element height in pixels (optional). Shorter elements and elements without bounds are left out, but tall enough elements inside them are still returned.")]
        int? minHeight = null,
        [Description("Timeout in milliseconds (default: 10000, max: 30000). When it passes, the elements read so far are returned with truncated=true.")]
        int timeoutMs = DefaultTimeoutMs)
    {
        // Clamp timeout to valid range
        timeoutMs = Math.Clamp(timeoutMs, MinTimeoutMs, MaxTimeoutMs);

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
